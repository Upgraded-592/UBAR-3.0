using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Windows.Foundation;

namespace Ubar3;

/// <summary>
/// Hosts flyouts in a separate transparent Window so they can extend beyond the bar's 60px height.
/// This avoids the Popup visual tree issues by using a proper Window with XamlRoot.
/// </summary>
internal static class FlyoutHost
{
    private static Window? _window;
    private static Canvas? _root;
    private static Border? _anchor;
    private static IntPtr _hwnd;
    private static FlyoutBase? _currentFlyout;

    public static bool IsOpen => _currentFlyout != null;

    /// <summary>
    /// Initializes the flyout host. Call once during app startup.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            System.IO.File.AppendAllText(@"C:\temp\flyout_direct.log", $"[{DateTime.Now:HH:mm:ss.fff}] FlyoutHost.Initialize() called.\r\n");
        }
        catch { }

        Logger.Log("[FlyoutHost] Initialize() called.");
        if (_window != null) return;

        Logger.Log("[FlyoutHost] Creating root canvas...");
        _root = new Canvas
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _anchor = new Border
        {
            Width = 1,
            Height = 1,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        _root.Children.Add(_anchor);

        Logger.Log("[FlyoutHost] Creating Window...");
        _window = new Window
        {
            SystemBackdrop = null,
            Title = "Ubar Flyout Host",
            Content = _root,
        };

        Logger.Log("[FlyoutHost] Resizing to 1x1...");
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));

        Logger.Log("[FlyoutHost] Activating window...");
        _window.Activate();

        Logger.Log("[FlyoutHost] Getting HWND...");
        _hwnd = WindowNative.GetWindowHandle(_window);
        Logger.Log($"[FlyoutHost] HWND = 0x{_hwnd:X}");

        Logger.Log("[FlyoutHost] Configuring window styles...");
        ConfigureWindowStyles();
        Logger.Log("[FlyoutHost] Hiding window...");
        Win32Interop.ShowWindow(_hwnd, Win32Interop.SW_HIDE);

        Logger.Log($"[FlyoutHost] Host window ready (hwnd=0x{_hwnd:X}).");
    }

    /// <summary>
    /// Shows a flyout anchored to a bar element, rendered on the overlay window so it can extend below the bar.
    /// </summary>
    public static bool ShowFlyout(FlyoutBase flyout, FrameworkElement barAnchor, Point? pointInAnchor = null)
    {
        System.Diagnostics.Debug.WriteLine($"[FlyoutHost] ShowFlyout called - window={_window != null}, anchor={_anchor != null}, currentFlyout={_currentFlyout != null}, flyout={flyout?.GetType().Name}, barAnchor={barAnchor?.GetType().Name}");
        Logger.Log($"[FlyoutHost] ShowFlyout called - flyout={flyout?.GetType().Name}, anchor={barAnchor?.GetType().Name}");

        if (_window == null || _root == null || _anchor == null)
        {
            Logger.Log("[FlyoutHost] Not initialized.");
            return false;
        }

        if (_currentFlyout != null)
        {
            Logger.Log("[FlyoutHost] Another flyout already open.");
            return false;
        }

        try
        {
            // Compute the screen position where the flyout should appear
            Logger.Log($"[FlyoutHost] Computing screen point for anchor={barAnchor?.GetType().Name}");
            var screenPoint = ComputeScreenPoint(barAnchor, pointInAnchor);
            if (screenPoint == null)
            {
                Logger.Log("[FlyoutHost] Could not resolve anchor position.");
                return false;
            }

            Logger.Log($"[FlyoutHost] Screen point computed: {screenPoint.Value.X},{screenPoint.Value.Y}");

            var wa = GetWorkArea();
            if (wa == null)
            {
                Logger.Log("[FlyoutHost] Could not get work area.");
                return false;
            }
            var work = wa.Value;

            // Size the overlay to the bar monitor's work area (everything below the bar).
            Logger.Log($"[FlyoutHost] Resizing overlay to work area: {work.Width}x{work.Height} at {work.Left},{work.Top}");
            Win32Interop.SetWindowPos(
                _hwnd, IntPtr.Zero,
                work.Left, work.Top, work.Width, work.Height,
                Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_NOZORDER);

            // Convert the screen-space anchor point to the overlay's DIP space.
            uint dpi = Win32Interop.GetDpiForWindow(_hwnd);
            if (dpi == 0) dpi = 96;
            double scale = dpi / 96.0;
            double ox = (screenPoint.Value.X - work.Left) / scale;
            double oy = (screenPoint.Value.Y - work.Top) / scale;

            Canvas.SetLeft(_anchor, ox);
            Canvas.SetTop(_anchor, oy);

            // Show the host window (transparent, click-through except content).
            Logger.Log("[FlyoutHost] Showing host window...");
            Win32Interop.ShowWindow(_hwnd, Win32Interop.SW_SHOWNOACTIVATE);

            // Show the flyout.
            _currentFlyout = flyout;
            flyout.Closed -= OnFlyoutClosed;
            flyout.Closed += OnFlyoutClosed;

            // Ensure the flyout uses the secondary window's XamlRoot
            if (_root?.XamlRoot != null)
            {
                flyout.XamlRoot = _root.XamlRoot;
            }

            Logger.Log("[FlyoutHost] Calling flyout.ShowAt...");
            flyout.ShowAt(_anchor, new FlyoutShowOptions
            {
                Position = new Point(0, 0),
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            });

            Logger.Log($"[FlyoutHost] Flyout shown at screen {screenPoint.Value.X},{screenPoint.Value.Y}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"[FlyoutHost] ShowFlyout EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            Cleanup();
            return false;
        }
    }

    /// <summary>Closes the currently open flyout (if any) and hides the overlay.</summary>
    public static void CloseCurrent()
    {
        if (_currentFlyout == null) return;
        var flyout = _currentFlyout;
        _currentFlyout = null;
        try { flyout.Hide(); }
        catch { }
    }

    private static void OnFlyoutClosed(object? sender, object e)
    {
        if (sender is FlyoutBase f) f.Closed -= OnFlyoutClosed;
        Cleanup();
    }

    private static void Cleanup()
    {
        _currentFlyout = null;
        if (_hwnd != IntPtr.Zero)
            Win32Interop.ShowWindow(_hwnd, Win32Interop.SW_HIDE);
    }

    /// <summary>
    /// Computes the anchor point in physical screen pixels: the bar element's
    /// position (or a point inside it) mapped through the bar window's rect,
    /// accounting for the bar window's DPI.
    /// </summary>
    private static Point? ComputeScreenPoint(FrameworkElement barAnchor, Point? pointInAnchor)
    {
        var bar = App.MainWindowInstance;
        if (bar == null) return null;

        try
        {
            var barHwnd = WindowNative.GetWindowHandle(bar);
            if (!Win32Interop.GetWindowRect(barHwnd, out var barRect)) return null;

            uint dpi = Win32Interop.GetDpiForWindow(barHwnd);
            if (dpi == 0) dpi = 96;
            double scale = dpi / 96.0;

            var toRoot = barAnchor.TransformToVisual(null);
            var local = pointInAnchor ?? new Point(0, barAnchor.ActualHeight);
            var inBar = toRoot.TransformPoint(local);
            return new Point(barRect.Left + inBar.X * scale, barRect.Top + inBar.Y * scale);
        }
        catch (Exception ex)
        {
            Logger.Log($"[FlyoutHost] Anchor transform failed: {ex.Message}");
            return null;
        }
    }

    private static Win32Interop.RECT? GetWorkArea()
    {
        var bar = App.MainWindowInstance;
        var barHwnd = bar != null ? WindowNative.GetWindowHandle(bar) : IntPtr.Zero;
        if (barHwnd != IntPtr.Zero)
        {
            var monitor = Win32Interop.MonitorFromWindow(barHwnd, Win32Interop.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && TryGetWorkArea(monitor, out var work)) return work;
        }

        var mon = Win32Interop.MonitorFromWindow(_hwnd, Win32Interop.MONITOR_DEFAULTTOPRIMARY);
        if (mon != IntPtr.Zero && TryGetWorkArea(mon, out var fallback)) return fallback;

        return null;
    }

    private static bool TryGetWorkArea(IntPtr monitor, out Win32Interop.RECT work)
    {
        var mi = new Win32Interop.MONITORINFO
        {
            cbSize = Marshal.SizeOf<Win32Interop.MONITORINFO>(),
        };
        if (Win32Interop.GetMonitorInfo(monitor, ref mi))
        {
            work = mi.rcWork;
            return true;
        }
        work = default;
        return false;
    }

    private static void ConfigureWindowStyles()
    {
        // Strip caption/border so the window is a plain rectangle.
        int style = Win32Interop.GetWindowStyle(_hwnd);
        style &= ~(Win32Interop.WS_CAPTION | Win32Interop.WS_THICKFRAME |
                   Win32Interop.WS_SYSMENU | Win32Interop.WS_MINIMIZEBOX |
                   Win32Interop.WS_MAXIMIZEBOX);
        Win32Interop.SetWindowStyle(_hwnd, style);

        // Hide from taskbar / alt-tab.
        int ex = Win32Interop.GetWindowExStyle(_hwnd);
        ex |= Win32Interop.WS_EX_TOOLWINDOW;
        ex |= Win32Interop.WS_EX_NOACTIVATE;
        ex &= ~(Win32Interop.WS_EX_APPWINDOW | Win32Interop.WS_EX_LAYERED);
        Win32Interop.SetWindowExStyle(_hwnd, ex);

        // Float above normal windows.
        Win32Interop.SetWindowPos(
            _hwnd, (IntPtr)Win32Interop.HWND_TOPMOST, 0, 0, 0, 0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE |
            Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_FRAMECHANGED);
    }
}