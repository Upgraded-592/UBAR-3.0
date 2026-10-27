using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Ubar3;

/// <summary>
/// Live app selector: shows one button per running app (like a mini taskbar).
/// Click a button to focus that app. Drag one app's button onto another to
/// snap the two side-by-side (left / right halves of the screen) and merge
/// the buttons into a single half-and-half icon. Click the merged button to
/// re-apply the split; right-click it to ungroup.
/// </summary>
public sealed class AppSwitcherModule : IUbarModule
{
    private sealed class AppEntry
    {
        public required string Key;          // "pid:exePath"
        public required uint Pid;
        public required string ExePath;
        public required string Name;
        public required List<IntPtr> Windows; // top-level hwnds, topmost first
        public IntPtr LatestWindow => Windows.Count > 0 ? Windows[0] : IntPtr.Zero;
    }

    private sealed class AppGroup
    {
        public required AppEntry Left;
        public required AppEntry Right;
    }

    private readonly List<AppEntry> _singles = new();
    private readonly List<AppGroup> _groups = new();
    private StackPanel? _panel;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private string _signature = "";
    private const int MaxSingleButtons = 8;

    public string Id => "appswitcher";
    public string DisplayName => "App Selector";
    public string Glyph => "\uE7C4"; // TaskView (verified in Segoe Fluent Icons)
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Left;
    public int DefaultOrder => 1;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += (_, _) => Refresh();

        Refresh();
        _timer.Start();
        return _panel;
    }

    public void OnEnabled() => _timer?.Start();
    public void OnDisabled() => _timer?.Stop();
    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }

    // ------------------------------------------------------------------ refresh

    private void Refresh()
    {
        if (_panel == null) return;
        try
        {
            var fresh = EnumerateApps();

            // Dissolve groups whose members went away; keep the rest up to date.
            for (int i = _groups.Count - 1; i >= 0; i--)
            {
                var g = _groups[i];
                var l = fresh.FirstOrDefault(a => a.Key == g.Left.Key);
                var r = fresh.FirstOrDefault(a => a.Key == g.Right.Key);
                if (l == null || r == null)
                {
                    _groups.RemoveAt(i);
                    continue;
                }
                g.Left = l;
                g.Right = r;
            }

            var groupedKeys = new HashSet<string>();
            foreach (var g in _groups)
            {
                groupedKeys.Add(g.Left.Key);
                groupedKeys.Add(g.Right.Key);
            }

            _singles.Clear();
            _singles.AddRange(fresh.Where(a => !groupedKeys.Contains(a.Key)));

            // Rebuild only when the set of visible buttons actually changes —
            // keeps drags alive across refreshes.
            var sig = string.Join("|",
                _groups.Select(g => g.Left.Key + "+" + g.Right.Key)
                       .Concat(_singles.Select(s => s.Key)));
            if (sig != _signature)
            {
                _signature = sig;
                RebuildPanel();
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[AppSwitcher] Refresh failed: {ex.Message}");
        }
    }

    private void RebuildPanel()
    {
        if (_panel == null) return;
        _panel.Children.Clear();
        foreach (var g in _groups) _panel.Children.Add(BuildGroupButton(g));
        foreach (var a in _singles.Take(MaxSingleButtons)) _panel.Children.Add(BuildAppButton(a));
    }

    // ------------------------------------------------------------------ window enumeration

    private List<AppEntry> EnumerateApps()
    {
        var byKey = new Dictionary<string, AppEntry>();
        var result = new List<AppEntry>();
        uint myPid = (uint)Environment.ProcessId;

        Win32Interop.EnumWindows((hwnd, lParam) =>
        {
            try
            {
                if (!Win32Interop.IsWindowVisible(hwnd)) return true;
                if ((Win32Interop.GetWindowExStyle(hwnd) & Win32Interop.WS_EX_TOOLWINDOW) != 0) return true;
                if (Win32Interop.GetWindow(hwnd, Win32Interop.GW_OWNER) != IntPtr.Zero) return true;

                var sb = new StringBuilder(256);
                if (Win32Interop.GetWindowText(hwnd, sb, 256) == 0) return true;

                Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0 || pid == myPid) return true;

                // Cloaked windows: UWP ghost frames, windows on other virtual desktops.
                if (Win32Interop.DwmGetWindowAttribute(hwnd, Win32Interop.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                    && cloaked != 0) return true;

                if (!Win32Interop.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0) return true;

                var info = AppIconHelper.GetProcessInfo(pid);
                string key = pid + ":" + info.Exe;
                if (!byKey.TryGetValue(key, out var entry))
                {
                    entry = new AppEntry
                    {
                        Key = key,
                        Pid = pid,
                        ExePath = info.Exe,
                        Name = info.Name,
                        Windows = new List<IntPtr>(),
                    };
                    byKey[key] = entry;
                    result.Add(entry);
                }
                entry.Windows.Add(hwnd);
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    // ------------------------------------------------------------------ UI

    private Button BuildAppButton(AppEntry entry)
    {
        var btn = new Button
        {
            Padding = new Thickness(4),
            MinWidth = 34,
            MinHeight = 34,
            CornerRadius = new CornerRadius(8),
            Background = null,
            BorderThickness = new Thickness(0),
            Content = BuildIconContent(entry.ExePath),
            CanDrag = true,
            AllowDrop = true,
        };
        ToolTipService.SetToolTip(btn, entry.Name);
        btn.Click += (_, _) => FocusEntry(entry);

        btn.DragStarting += (s, e) =>
        {
            e.Data.SetText(entry.Key);
            e.Data.RequestedOperation = DataPackageOperation.Move;
        };
        btn.DragOver += (s, e) => { e.AcceptedOperation = DataPackageOperation.Move; };
        btn.Drop += async (s, e) =>
        {
            var deferral = e.GetDeferral();
            try
            {
                var srcKey = await e.Data.GetView().GetTextAsync();
                SnapAndMerge(srcKey, entry);
            }
            finally
            {
                deferral.Complete();
            }
        };
        return btn;
    }

    private Button BuildGroupButton(AppGroup g)
    {
        var btn = new Button
        {
            Padding = new Thickness(4),
            MinWidth = 34,
            MinHeight = 34,
            CornerRadius = new CornerRadius(8),
            Background = null,
            BorderThickness = new Thickness(0),
            Content = BuildMergedIconContent(g),
            CanDrag = false,
            AllowDrop = false,
        };
        ToolTipService.SetToolTip(btn, $"{g.Left.Name} | {g.Right.Name} (split screen — right-click to ungroup)");
        btn.Click += (_, _) =>
        {
            if (!ApplySplit(g.Left, g.Right))
                Logger.Log("[AppSwitcher] Re-applying split failed (window closed?)");
        };
        btn.RightTapped += (_, _) =>
        {
            _groups.Remove(g);
            _signature = ""; // force rebuild
            Refresh();
        };
        return btn;
    }

    private FrameworkElement BuildIconContent(string exePath)
    {
        var src = AppIconHelper.GetIconImageSource(exePath);
        if (src != null) return new Image { Source = src, Width = 22, Height = 22 };
        return new FontIcon
        {
            Glyph = "\uE7C4",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
        };
    }

    private FrameworkElement BuildMergedIconContent(AppGroup g)
    {
        var src = AppIconHelper.GetMergedIconSource(g.Left.ExePath, g.Right.ExePath);
        if (src != null) return new Image { Source = src, Width = 22, Height = 22 };
        return new FontIcon
        {
            Glyph = "\uE8CB", // MultiSelect fallback
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
        };
    }

    // ------------------------------------------------------------------ behavior

    private static void FocusEntry(AppEntry entry)
    {
        var hwnd = entry.LatestWindow;
        if (hwnd == IntPtr.Zero) return;
        RestoreIfMinimized(hwnd);
        _ = Win32Interop.SetForegroundWindow(hwnd);
    }

    private void SnapAndMerge(string srcKey, AppEntry target)
    {
        if (srcKey == target.Key) return;
        var src = _singles.FirstOrDefault(a => a.Key == srcKey);
        if (src == null)
        {
            Logger.Log($"[AppSwitcher] Drop source '{srcKey}' not found (closed or already grouped?).");
            return;
        }
        if (!ApplySplit(src, target)) return;

        _groups.Add(new AppGroup { Left = src, Right = target });
        _singles.Remove(src);
        _singles.Remove(target);
        _signature = ""; // force rebuild
        Refresh();
        Logger.Log($"[AppSwitcher] Snapped + merged '{src.Name}' | '{target.Name}'");
    }

    /// <summary>Snaps the two apps' windows to the left / right halves of the right app's monitor.</summary>
    private static bool ApplySplit(AppEntry left, AppEntry right)
    {
        var hwndL = left.LatestWindow;
        var hwndR = right.LatestWindow;
        if (hwndL == IntPtr.Zero || hwndR == IntPtr.Zero || hwndL == hwndR) return false;
        return SnapPair(hwndL, hwndR);
    }

    private static bool SnapPair(IntPtr hwndL, IntPtr hwndR)
    {
        try
        {
            var monitor = Win32Interop.MonitorFromWindow(hwndR, Win32Interop.MONITOR_DEFAULTTONEAREST);
            var mi = new Win32Interop.MONITORINFO { cbSize = Marshal.SizeOf<Win32Interop.MONITORINFO>() };
            if (!Win32Interop.GetMonitorInfo(monitor, ref mi)) return false;
            var work = mi.rcWork;
            int halfW = work.Width / 2;

            RestoreIfMinimized(hwndL);
            RestoreIfMinimized(hwndR);

            bool okL = Win32Interop.SetWindowPos(hwndL, IntPtr.Zero,
                work.Left, work.Top, halfW, work.Height,
                Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_NOZORDER | Win32Interop.SWP_FRAMECHANGED);
            bool okR = Win32Interop.SetWindowPos(hwndR, IntPtr.Zero,
                work.Left + halfW, work.Top, work.Width - halfW, work.Height,
                Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_NOZORDER | Win32Interop.SWP_FRAMECHANGED);

            _ = Win32Interop.SetForegroundWindow(hwndL);
            return okL && okR;
        }
        catch (Exception ex)
        {
            Logger.Log($"[AppSwitcher] SnapPair failed: {ex.Message}");
            return false;
        }
    }

    private static void RestoreIfMinimized(IntPtr hwnd)
    {
        if (Win32Interop.IsIconic(hwnd)) _ = Win32Interop.ShowWindow(hwnd, Win32Interop.SW_RESTORE);
    }
}
