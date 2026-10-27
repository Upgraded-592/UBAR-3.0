using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Ubar3;

/// <summary>Snapshot of system metrics collected each tick.</summary>
public readonly record struct SystemMetrics(double CpuUsage, double RamUsage);

/// <summary>
/// Collects CPU and RAM usage once per second via a single shared DispatcherTimer.
/// Modules call Subscribe/Unsubscribe from OnEnabled/OnDisabled.
/// The timer is stopped automatically when there are no active subscribers,
/// so disabled modules cost zero background work.
/// </summary>
public sealed class SystemMetricsService : IMetricsService
{
    // ---- Win32 imports ----
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME lpIdleTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    // ---- State ----
    private readonly DispatcherTimer _timer;
    private int _subscriberCount;
    private FILETIME _prevIdle, _prevKernel, _prevUser;
    private event Action<SystemMetrics>? _handlers;

    public SystemMetricsService()
    {
        // Seed initial CPU times so the first diff is meaningful
        GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
    }

    /// <summary>Register a handler and start the timer if this is the first subscriber.</summary>
    public void Subscribe(Action<SystemMetrics> handler)
    {
        _handlers += handler;
        _subscriberCount++;
        if (_subscriberCount == 1) _timer.Start();
    }

    /// <summary>Unregister a handler and stop the timer when no subscribers remain.</summary>
    public void Unsubscribe(Action<SystemMetrics> handler)
    {
        _handlers -= handler;
        _subscriberCount = Math.Max(0, _subscriberCount - 1);
        if (_subscriberCount == 0) _timer.Stop();
    }

    public void Dispose() => _timer.Stop();

    private void OnTick(object? sender, object e)
    {
        try
        {
            var metrics = new SystemMetrics(GetCpuUsage(), GetRamUsage());
            _handlers?.Invoke(metrics);
        }
        catch (Exception ex)
        {
            Logger.Log($"[SystemMetricsService] Tick error: {ex.Message}");
        }
    }

    private double GetCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        ulong i = ToUlong(idle),   pi = ToUlong(_prevIdle);
        ulong k = ToUlong(kernel), pk = ToUlong(_prevKernel);
        ulong u = ToUlong(user),   pu = ToUlong(_prevUser);

        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;

        ulong total = (k - pk) + (u - pu);
        if (total == 0) return 0;
        ulong active = total - (i - pi);
        return Math.Clamp(active * 100.0 / total, 0, 100);
    }

    private static double GetRamUsage()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? m.dwMemoryLoad : 0;
    }

    private static ulong ToUlong(FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
}
