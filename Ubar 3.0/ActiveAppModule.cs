using System;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ubar3;

/// <summary>
/// Shows the currently active (foreground) app: icon + name, with the full
/// window title in the tooltip. Hidden by default — enable it from the bar's
/// right-click menu (Modules list) or the Settings window.
///
/// Updates instantly via the EVENT_SYSTEM_FOREGROUND WinEvent hook, with a
/// slow poll as a safety net. Zero-cost while disabled (contract: OnDisabled
/// unhooks and stops the timer).
/// </summary>
public sealed class ActiveAppModule : IUbarModule
{
    private Border? _host;
    private Image? _icon;
    private TextBlock? _name;
    private DispatcherQueue? _ui;
    private DispatcherQueueTimer? _timer;
    private IntPtr _hook;
    private readonly Win32Interop.WinEventDelegate _hookProc; // field keeps the delegate alive
    private IntPtr _lastHwnd;

    public string Id => "activeapp";
    public string DisplayName => "Active App";
    public string Glyph => "\uE7F4"; // TVMonitor (verified in Segoe Fluent Icons)
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Right;
    public int DefaultOrder => 16;
    public bool DefaultVisible => false; // hidden until the user turns it on

    public ActiveAppModule() => _hookProc = OnForegroundChanged;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        _ui = DispatcherQueue.GetForCurrentThread();

        _host = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 32, 32, 32)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        _icon = new Image
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_icon);

        _name = new TextBlock
        {
            Text = "—",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            MaxWidth = 140,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_name);

        _host.Child = panel;

        // Safety-net poll (the WinEvent hook is the primary, instant source).
        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += (_, _) => UpdateFromForeground();

        UpdateFromForeground();
        return _host;
    }

    public void OnEnabled()
    {
        InstallHook();
        _timer?.Start();
        UpdateFromForeground();
    }

    public void OnDisabled()
    {
        RemoveHook();
        _timer?.Stop();
    }

    public void Dispose()
    {
        RemoveHook();
        _timer?.Stop();
        _timer = null;
    }

    // ------------------------------------------------------------------ foreground tracking

    private void InstallHook()
    {
        if (_hook != IntPtr.Zero) return;
        try
        {
            _hook = Win32Interop.SetWinEventHook(
                Win32Interop.EVENT_SYSTEM_FOREGROUND, Win32Interop.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _hookProc, 0, 0, Win32Interop.WINEVENT_OUTOFCONTEXT);
            if (_hook == IntPtr.Zero)
                Logger.Log("[ActiveApp] SetWinEventHook failed; relying on the 5s poll.");
        }
        catch (Exception ex)
        {
            Logger.Log($"[ActiveApp] SetWinEventHook error: {ex.Message}");
        }
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero) return;
        _ = Win32Interop.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime)
    {
        // WINEVENT_OUTOFCONTEXT callbacks arrive on the hooking (UI) thread;
        // marshal defensively anyway.
        _ui?.TryEnqueue(() => UpdateFromForeground(hwnd));
    }

    private void UpdateFromForeground() => UpdateFromForeground(Win32Interop.GetForegroundWindow());

    private void UpdateFromForeground(IntPtr hwnd)
    {
        if (_icon == null || _name == null || _host == null) return;
        if (hwnd == IntPtr.Zero || hwnd == _lastHwnd) return;
        _lastHwnd = hwnd;

        // Ignore UBAR's own windows (bar, flyout overlay, music player, settings).
        _ = Win32Interop.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId)
        {
            _icon.Source = null;
            _name.Text = "—";
            ToolTipService.SetToolTip(_host, "Active App");
            return;
        }

        var info = AppIconHelper.GetProcessInfo(pid);
        _icon.Source = AppIconHelper.GetIconImageSource(info.Exe);
        _name.Text = string.IsNullOrEmpty(info.Name) ? "Desktop" : info.Name;

        // Tooltip shows the live window title.
        var sb = new StringBuilder(512);
        if (Win32Interop.GetWindowText(hwnd, sb, 512) > 0)
            ToolTipService.SetToolTip(_host, sb.ToString());
    }
}
