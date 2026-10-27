using System.Runtime.InteropServices;

namespace Ubar3;

/// <summary>
/// Light-weight Win32 window subclass (SetWindowLongPtr on GWLP_WNDPROC).
/// The WinUI3 Desktop window is backed by a real HWND, so we can attach a
/// native wnd-proc to it to receive the messages the shell sends the
/// appbar (custom callback message, TaskbarCreated, WM_DISPLAYCHANGE, etc.).
/// </summary>
internal sealed class WindowSubclass : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _prevProc;
    private WndProc? _newProc;
    private readonly Func<IntPtr, uint, IntPtr, IntPtr, IntPtr> _handler;
    private bool _disposed;
    private GCHandle _selfRef;
    private GCHandle _delegateHandle;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public WindowSubclass(IntPtr hwnd, Func<IntPtr, uint, IntPtr, IntPtr, IntPtr> handler)
    {
        _hwnd = hwnd;
        _handler = handler;
        Logger.Log($"WindowSubclass constructor. HWND: {hwnd:X}, IsWindow: {Win32Interop.IsWindow(hwnd)}, IsWindowVisible: {Win32Interop.IsWindowVisible(hwnd)}");

        _newProc = new WndProc(SubclassProc);
        _delegateHandle = GCHandle.Alloc(_newProc);
        _selfRef = GCHandle.Alloc(this);

        var fnPtr = Marshal.GetFunctionPointerForDelegate(_newProc);
        _prevProc = Win32Interop.SetWindowLongPtr(
            _hwnd, Win32Interop.GWLP_WNDPROC, fnPtr);
        Logger.Log($"WindowSubclass: subclassed HWND. prevProc: {_prevProc:X}");
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        Logger.Log($"WndProc: hwnd={hWnd:X}, msg=0x{msg:X4}, wParam={wParam:X}, lParam={lParam:X}");
        IntPtr result = IntPtr.Zero;
        try
        {
            result = _handler(hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Logger.Log($"WndProc Exception in handler: {ex}");
        }

        if (result == IntPtr.Zero)
        {
            result = Win32Interop.CallWindowProc(
                _prevProc, hWnd, msg, wParam, lParam);
        }
        return result;
    }

    public void Dispose()
    {
        Logger.Log($"WindowSubclass.Dispose() called. disposed={_disposed}, hwnd={_hwnd:X}, prevProc={_prevProc:X}");
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero && _prevProc != IntPtr.Zero && Win32Interop.IsWindow(_hwnd))
        {
            var res = Win32Interop.SetWindowLongPtr(
                _hwnd, Win32Interop.GWLP_WNDPROC, _prevProc);
            Logger.Log($"WindowSubclass.Dispose: restored prevProc. Result: {res:X}");
        }

        if (_delegateHandle.IsAllocated) _delegateHandle.Free();
        if (_selfRef.IsAllocated) _selfRef.Free();
        _newProc = null;
        _hwnd = IntPtr.Zero;
        _prevProc = IntPtr.Zero;
    }
}
