using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Ubar3;



/// <summary>
/// Manages the desktop appbar: registers with the shell via
/// <c>SHAppBarMessage</c>, reserves the top 60 px of the monitor's work
/// area, positions the WinUI window over that strip, handles monitor /
/// resolution changes, and re-registers if Explorer restarts.
/// </summary>
internal sealed class AppBarManager : IDisposable
{
    private readonly Window _window;
    private readonly IntPtr _taskbarCreatedMsg;
    private IntPtr _hwnd;
    private uint _callbackMsg;
    private bool _registered;
    private WindowSubclass? _subclass;
    private bool _updatingPosition;

    public AppBarManager(Window window)
    {
        _window = window;
        _hwnd = WindowNative.GetWindowHandle(window);

        _callbackMsg = (uint)Win32Interop.RegisterWindowMessage("Ubar3AppBarCallback");
        _taskbarCreatedMsg = Win32Interop.RegisterWindowMessage("TaskbarCreated");
    }

    // ------------------------------------------------------------------
    public void Register()
    {
        Logger.Log($"AppBarManager.Register() called. _registered={_registered}");
        if (_registered) return;

        StripWindowChrome();
        InstallSubclass();

        var abd = NewAppBarData();
        uint res = Win32Interop.SHAppBarMessage(Win32Interop.ABM_NEW, ref abd);
        Logger.Log($"SHAppBarMessage(ABM_NEW) result: {res}");
        if (res == 0)
        {
            Logger.Log("ABM_NEW returned 0, attempting ABM_REMOVE and ABM_NEW again.");
            var removeRes = Win32Interop.SHAppBarMessage(Win32Interop.ABM_REMOVE, ref abd);
            Logger.Log($"SHAppBarMessage(ABM_REMOVE) result: {removeRes}");
            res = Win32Interop.SHAppBarMessage(Win32Interop.ABM_NEW, ref abd);
            Logger.Log($"SHAppBarMessage(ABM_NEW) retry result: {res}");
        }

        _registered = true;
        UpdatePosition();
    }

    public void Unregister()
    {
        Logger.Log($"AppBarManager.Unregister() called. _registered={_registered}");
        if (!_registered) return;

        var abd = NewAppBarData();
        uint res = Win32Interop.SHAppBarMessage(Win32Interop.ABM_REMOVE, ref abd);
        Logger.Log($"SHAppBarMessage(ABM_REMOVE) result: {res}");
        _registered = false;
        _subclass?.Dispose();
        _subclass = null;
        Logger.Log("AppBarManager.Unregister() finished.");
    }

    private const int BarHeightDip = 60;

    private int GetBarHeightPx()
    {
        uint dpi = Win32Interop.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96;
        return (int)(BarHeightDip * dpi / 96);
    }

    public void UpdatePosition()
    {
        if (!_registered || _updatingPosition) return;

        _updatingPosition = true;
        try
        {
            Logger.Log($"UpdatePosition() called. _registered={_registered}");

            bool isWin = Win32Interop.IsWindow(_hwnd);
            bool isVisible = Win32Interop.IsWindowVisible(_hwnd);
            Win32Interop.RECT currentWindowRect = new Win32Interop.RECT();
            bool hasRect = Win32Interop.GetWindowRect(_hwnd, out currentWindowRect);
            Logger.Log($"UpdatePosition: HWND={_hwnd:X}, IsWindow={isWin}, IsWindowVisible={isVisible}, GetWindowRect Success={hasRect}, Rect=[L={currentWindowRect.Left}, T={currentWindowRect.Top}, R={currentWindowRect.Right}, B={currentWindowRect.Bottom}, W={currentWindowRect.Width}, H={currentWindowRect.Height}]");

            // Get the monitor the window is actually on
            var monitor = Win32Interop.MonitorFromWindow(
                _hwnd, Win32Interop.MONITOR_DEFAULTTONEAREST);
            Logger.Log($"UpdatePosition: MonitorFromWindow returned hMonitor={monitor:X}");

            var mi = new Win32Interop.MONITORINFO
            {
                cbSize = Marshal.SizeOf<Win32Interop.MONITORINFO>(),
            };
            bool getMonInfoRes = Win32Interop.GetMonitorInfo(monitor, ref mi);
            Logger.Log($"GetMonitorInfo Success={getMonInfoRes}, MonitorRect=[L={mi.rcMonitor.Left}, T={mi.rcMonitor.Top}, R={mi.rcMonitor.Right}, B={mi.rcMonitor.Bottom}], WorkRect=[L={mi.rcWork.Left}, T={mi.rcWork.Top}, R={mi.rcWork.Right}, B={mi.rcWork.Bottom}]");

            int heightPx = GetBarHeightPx();
            Logger.Log($"GetBarHeightPx() calculated height: {heightPx} px");

            var abd = NewAppBarData();
            abd.uEdge = Win32Interop.ABE_TOP;
            abd.rc = new Win32Interop.RECT
            {
                Left = mi.rcMonitor.Left,
                Top = mi.rcMonitor.Top,
                Right = mi.rcMonitor.Right,
                Bottom = mi.rcMonitor.Top + heightPx,
            };

            Logger.Log($"Calling ABM_QUERYPOS with rect: [L={abd.rc.Left}, T={abd.rc.Top}, R={abd.rc.Right}, B={abd.rc.Bottom}]");
            uint queryRes = Win32Interop.SHAppBarMessage(Win32Interop.ABM_QUERYPOS, ref abd);
            Logger.Log($"SHAppBarMessage(ABM_QUERYPOS) result: {queryRes}, rect returned: [L={abd.rc.Left}, T={abd.rc.Top}, R={abd.rc.Right}, B={abd.rc.Bottom}]");

            abd.rc.Bottom = abd.rc.Top + heightPx;

            Logger.Log($"Calling ABM_SETPOS with rect: [L={abd.rc.Left}, T={abd.rc.Top}, R={abd.rc.Right}, B={abd.rc.Bottom}]");
            uint setPosRes = Win32Interop.SHAppBarMessage(Win32Interop.ABM_SETPOS, ref abd);
            Logger.Log($"SHAppBarMessage(ABM_SETPOS) result: {setPosRes}, rect returned: [L={abd.rc.Left}, T={abd.rc.Top}, R={abd.rc.Right}, B={abd.rc.Bottom}]");

            ApplyRectToWindow(abd.rc);
        }
        finally
        {
            _updatingPosition = false;
        }
    }

    // ------------------------------------------------------------------
    private Win32Interop.APPBARDATA NewAppBarData()
    {
        return new Win32Interop.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Win32Interop.APPBARDATA>(),
            hWnd = _hwnd,
            uCallbackMessage = _callbackMsg,
            uEdge = Win32Interop.ABE_TOP,
        };
    }

    private void ApplyRectToWindow(Win32Interop.RECT rc)
    {
        Logger.Log($"ApplyRectToWindow: Setting position to [L={rc.Left}, T={rc.Top}, W={rc.Width}, H={rc.Height}]");
        bool setWindowPosRes = Win32Interop.SetWindowPos(
            _hwnd,
            new IntPtr(Win32Interop.HWND_TOPMOST),
            rc.Left, rc.Top, rc.Width, rc.Height,
            Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_SHOWWINDOW);
        int lastError = Marshal.GetLastWin32Error();
        Logger.Log($"SetWindowPos returned: {setWindowPosRes}, last error (if failed): {lastError}");
    }

    private void StripWindowChrome()
    {
        int style = Win32Interop.GetWindowStyle(_hwnd);
        style &= ~(Win32Interop.WS_CAPTION |
                   Win32Interop.WS_THICKFRAME |
                   Win32Interop.WS_SYSMENU |
                   Win32Interop.WS_MINIMIZEBOX |
                   Win32Interop.WS_MAXIMIZEBOX);
        Win32Interop.SetWindowStyle(_hwnd, style);

        int ex = Win32Interop.GetWindowExStyle(_hwnd);
        ex |= Win32Interop.WS_EX_TOPMOST | Win32Interop.WS_EX_TOOLWINDOW | Win32Interop.WS_EX_NOACTIVATE;
        ex &= ~Win32Interop.WS_EX_APPWINDOW;
        Win32Interop.SetWindowExStyle(_hwnd, ex);

        // Force frame recalculation so changes take effect
        Win32Interop.SetWindowPos(
            _hwnd,
            IntPtr.Zero,
            0, 0, 0, 0,
            Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE |
            Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE |
            Win32Interop.SWP_FRAMECHANGED);
    }

    private void InstallSubclass()
    {
        if (_subclass != null) return;
        _subclass = new WindowSubclass(_hwnd, WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _callbackMsg)
        {
            int notification = wParam.ToInt32();
            Logger.Log($"WindowMessageHook callback msg received: notification ABN_={notification} (0=STATECHANGE, 1=POSCHANGED, 2=FULLSCREENAPP, 3=WINDOWARRANGE)");
            if (notification == 1 /* ABN_POSCHANGED */ || notification == 0 /* ABN_STATECHANGE */)
            {
                UpdatePosition();
            }
        }
        else if (msg == Win32Interop.WM_SETTINGCHANGE)
        {
            Logger.Log("WindowMessageHook: WM_SETTINGCHANGE received.");
            UpdatePosition();
        }
        else if (msg == Win32Interop.WM_DISPLAYCHANGE)
        {
            Logger.Log("WindowMessageHook: WM_DISPLAYCHANGE received.");
            UpdatePosition();
        }
        else if (msg == Win32Interop.WM_DPICHANGED)
        {
            Logger.Log($"WindowMessageHook: WM_DPICHANGED received. wParam (new DPI)={wParam.ToInt32()}");
            UpdatePosition();
        }
        else if (_taskbarCreatedMsg != IntPtr.Zero &&
                 msg == (uint)_taskbarCreatedMsg.ToInt32())
        {
            Logger.Log("WindowMessageHook: TaskbarCreated message received (Explorer restarted)!");
            _registered = false;
            Register();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
    }
}
