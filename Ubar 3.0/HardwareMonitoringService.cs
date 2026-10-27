using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.UI.Dispatching;
using Windows.Devices.Power;
using Windows.System.Power;

namespace Ubar3;

/// <summary>
/// Extended system metrics including battery, CPU/GPU temperatures, and GPU usage.
/// </summary>
public readonly record struct ExtendedSystemMetrics(
    double CpuUsage,
    double RamUsage,
    double? BatteryPercent,
    BatteryStatus? BatteryStatus,
    double? CpuTemperatureCelsius,
    double? GpuTemperatureCelsius,
    double? GpuUsagePercent,
    double? GpuMemoryUsagePercent,
    string? GpuName
);

/// <summary>
/// Battery status enum matching Windows.System.Power.BatteryStatus
/// </summary>
public enum BatteryStatus
{
    NotPresent = 0,
    Discharging = 1,
    Idle = 2,
    Charging = 3,
    Full = 4
}

/// <summary>
/// Collects extended system metrics: CPU, RAM, Battery, CPU/GPU temps, GPU usage.
/// Uses a single shared DispatcherQueueTimer. Subscribe/Unsubscribe from modules.
/// Implements IMetricsService for basic CPU/RAM metrics compatibility.
/// </summary>
public sealed class HardwareMonitoringService : IMetricsService
{
    // ---- Win32 imports for CPU usage ----
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
    private readonly DispatcherQueueTimer _timer;
    private int _basicSubscriberCount;
    private int _extendedSubscriberCount;
    private FILETIME _prevIdle, _prevKernel, _prevUser;
    private event Action<SystemMetrics>? _basicHandlers;
    private event Action<ExtendedSystemMetrics>? _extendedHandlers;

    // WMI queries cached
    private ManagementObjectSearcher? _cpuTempSearcher;
    private ManagementObjectSearcher? _gpuSearcher;
    private ManagementObjectSearcher? _gpuEngineSearcher;
    private ManagementObjectSearcher? _gpuAdapterMemorySearcher;
    private string? _cachedGpuName;
    // Primary GPU from DXGI (null if detection failed)
    private string? _gpuLuidTag;
    private ulong _gpuTotalMemoryBytes;
    private GpuVendor _gpuVendor = GpuVendor.Unknown;
    private bool _gpuUsageQueryBroken;
    private bool _gpuMemoryQueryBroken;

    private enum GpuVendor
    {
        Unknown,
        Intel,
        Nvidia,
        Amd,
        Microsoft // Basic Render / RemoteFX
    }

    public HardwareMonitoringService()
    {
        // Seed initial CPU times
        GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2); // 2s for hardware polling
        _timer.Tick += OnTick;

        // Initialize WMI queries
        InitializeWmi();
        InitializeGpuCounters();
    }

    private void InitializeWmi()
    {
        try
        {
            // CPU Temperature: MSAcpi_ThermalZoneTemperature (returns tenths of Kelvin)
            var scope = new ManagementScope(@"root\wmi");
            _cpuTempSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"));
        }
        catch (Exception ex)
        {
            Logger.Log($"[HardwareMonitoringService] WMI CPU temp init failed: {ex.Message}");
        }

        try
        {
            // GPU info: Win32_VideoController
            _gpuSearcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
        }
        catch (Exception ex)
        {
            Logger.Log($"[HardwareMonitoringService] WMI GPU init failed: {ex.Message}");
        }
    }

    private void InitializeGpuCounters()
    {
        // 1) DXGI gives us the real primary adapter: name, LUID (to match perf
        //    counter instances) and the actual memory pools.
        var gpu = DxgiInterop.TryGetPrimaryGpu();
        if (gpu != null)
        {
            _cachedGpuName = gpu.Name;
            _gpuLuidTag = gpu.LuidTag;
            _gpuTotalMemoryBytes = gpu.DedicatedVideoBytes + gpu.SharedSystemBytes;
            _gpuVendor = gpu.VendorId switch
            {
                0x8086 => GpuVendor.Intel,
                0x10DE => GpuVendor.Nvidia,
                0x1002 or 0x1022 => GpuVendor.Amd,
                _ => GpuVendor.Unknown
            };
            Logger.Log($"[HardwareMonitoringService] DXGI primary GPU: {gpu.Name} ({gpu.LuidTag}), vendor={_gpuVendor}, dedicated={gpu.DedicatedVideoBytes / 1048576}MB, shared={gpu.SharedSystemBytes / 1048576}MB");
        }
        else
        {
            Logger.Log("[HardwareMonitoringService] DXGI GPU detection failed; falling back to WMI name lookup.");
        }

        // 2) The Win32_PerfFormattedData_GPUPerformanceCounters classes are what
        //    Task Manager reads. Unlike PerformanceCounterCategory names, WMI
        //    class names are not localized, so they work on every locale.
        //    ("GPU Engine" / "GPU Adapter Memory" are multi-instance categories,
        //    so PerformanceCounterCategory.GetCounters() without an instance
        //    name throws — that approach cannot work here.)
        try
        {
            _gpuEngineSearcher = new ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
        }
        catch (Exception ex)
        {
            Logger.Log($"[HardwareMonitoringService] GPUEngine searcher init failed: {ex.Message}");
        }

        try
        {
            _gpuAdapterMemorySearcher = new ManagementObjectSearcher(
                "SELECT Name, DedicatedUsage, SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
        }
        catch (Exception ex)
        {
            Logger.Log($"[HardwareMonitoringService] GPUAdapterMemory searcher init failed: {ex.Message}");
        }
    }

    // ---- IMetricsService implementation (basic CPU/RAM) ----
    public void Subscribe(Action<SystemMetrics> handler)
    {
        _basicHandlers += handler;
        _basicSubscriberCount++;
        if (_basicSubscriberCount == 1 && _extendedSubscriberCount == 0) _timer.Start();
    }

    public void Unsubscribe(Action<SystemMetrics> handler)
    {
        _basicHandlers -= handler;
        _basicSubscriberCount = Math.Max(0, _basicSubscriberCount - 1);
        if (_basicSubscriberCount == 0 && _extendedSubscriberCount == 0) _timer.Stop();
    }

    // ---- Extended metrics subscription ----
    /// <summary>Subscribe to extended metrics (battery, temps, GPU).</summary>
    public void SubscribeExtended(Action<ExtendedSystemMetrics> handler)
    {
        _extendedHandlers += handler;
        _extendedSubscriberCount++;
        if (_extendedSubscriberCount == 1 && _basicSubscriberCount == 0) _timer.Start();
    }

    /// <summary>Unsubscribe from extended metrics.</summary>
    public void UnsubscribeExtended(Action<ExtendedSystemMetrics> handler)
    {
        _extendedHandlers -= handler;
        _extendedSubscriberCount = Math.Max(0, _extendedSubscriberCount - 1);
        if (_extendedSubscriberCount == 0 && _basicSubscriberCount == 0) _timer.Stop();
    }

    public void Dispose()
    {
        _timer.Stop();
        _gpuEngineSearcher?.Dispose();
        _gpuAdapterMemorySearcher?.Dispose();
        _cpuTempSearcher?.Dispose();
        _gpuSearcher?.Dispose();
    }

    private async void OnTick(object? sender, object e)
    {
        try
        {
            double cpuUsage = GetCpuUsage();
            double ramUsage = GetRamUsage();

            var basicMetrics = new SystemMetrics(cpuUsage, ramUsage);
            _basicHandlers?.Invoke(basicMetrics);

            if (_extendedHandlers != null)
            {
                var extendedMetrics = new ExtendedSystemMetrics(
                    CpuUsage: cpuUsage,
                    RamUsage: ramUsage,
                    BatteryPercent: await GetBatteryPercentAsync(),
                    BatteryStatus: await GetBatteryStatusAsync(),
                    CpuTemperatureCelsius: GetCpuTemperature(),
                    GpuTemperatureCelsius: GetGpuTemperature(),
                    GpuUsagePercent: GetGpuUsage(),
                    GpuMemoryUsagePercent: GetGpuMemoryUsage(),
                    GpuName: GetGpuName()
                );
                _extendedHandlers.Invoke(extendedMetrics);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[HardwareMonitoringService] Tick error: {ex.Message}");
        }
    }

    private double GetCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        ulong i = ToUlong(idle), pi = ToUlong(_prevIdle);
        ulong k = ToUlong(kernel), pk = ToUlong(_prevKernel);
        ulong u = ToUlong(user), pu = ToUlong(_prevUser);

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

    private static async Task<double?> GetBatteryPercentAsync()
    {
        try
        {
            var battery = Battery.AggregateBattery;
            if (battery == null) return null;

            var report = battery.GetReport();
            if (report.FullChargeCapacityInMilliwattHours.HasValue &&
                report.RemainingCapacityInMilliwattHours.HasValue &&
                report.FullChargeCapacityInMilliwattHours.Value > 0)
            {
                return (double)report.RemainingCapacityInMilliwattHours.Value /
                       report.FullChargeCapacityInMilliwattHours.Value * 100.0;
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"[HardwareMonitoringService] Battery percent error: {ex.Message}");
            return null;
        }
    }

    private static async Task<BatteryStatus?> GetBatteryStatusAsync()
    {
        try
        {
            var battery = Battery.AggregateBattery;
            if (battery == null) return null;

            var status = battery.GetReport().Status;
            return status switch
            {
                Windows.System.Power.BatteryStatus.NotPresent => BatteryStatus.NotPresent,
                Windows.System.Power.BatteryStatus.Discharging => BatteryStatus.Discharging,
                Windows.System.Power.BatteryStatus.Idle => BatteryStatus.Idle,
                Windows.System.Power.BatteryStatus.Charging => BatteryStatus.Charging,
                _ => null
            };
        }
        catch (Exception ex)
        {
            Logger.Log($"[HardwareMonitoringService] Battery status error: {ex.Message}");
            return null;
        }
    }

    private double? GetCpuTemperature()
    {
        try
        {
            if (_cpuTempSearcher == null) return null;

            foreach (ManagementObject obj in _cpuTempSearcher.Get())
            {
                // CurrentTemperature is in tenths of Kelvin
                if (obj["CurrentTemperature"] is uint tempKelvinTenths)
                {
                    double tempKelvin = tempKelvinTenths / 10.0;
                    double tempCelsius = tempKelvin - 273.15;
                    if (tempCelsius > -50 && tempCelsius < 150) // Sanity check
                        return tempCelsius;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            Logger.Log($"[HardwareMonitoringService] CPU temp error: {ex.Message}");
            return null;
        }
    }

    private double? GetGpuTemperature()
    {
        // WMI doesn't expose GPU temp directly in Win32_VideoController
        // Would need NVAPI/ADL or OpenHardwareMonitor for this
        // For now, return null - can be extended with vendor-specific APIs
        try
        {
            // Try to read from WMI thermal zones for GPU
            var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature WHERE InstanceName LIKE '%GPU%' OR InstanceName LIKE '%VIDEO%'");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["CurrentTemperature"] is uint tempKelvinTenths)
                {
                    double tempKelvin = tempKelvinTenths / 10.0;
                    double tempCelsius = tempKelvin - 273.15;
                    if (tempCelsius > -50 && tempCelsius < 150)
                        return tempCelsius;
                }
            }
        }
        catch { }
        return null;
    }

    private double? GetGpuUsage()
    {
        if (_gpuEngineSearcher == null || _gpuUsageQueryBroken) return null;
        try
        {
            // One row per (process, engine). Sum per engine across processes,
            // then report the busiest engine — the same semantics Task Manager
            // uses for its headline GPU percentage.
            var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (ManagementObject obj in _gpuEngineSearcher.Get())
            {
                using (obj)
                {
                    var name = obj["Name"] as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (_gpuLuidTag != null && !name.Contains(_gpuLuidTag, StringComparison.OrdinalIgnoreCase)) continue;

                    int eng = name.IndexOf("_eng_", StringComparison.Ordinal);
                    string key = eng >= 0 ? name[(eng + 1)..] : name;

                    double util = Convert.ToDouble(obj["UtilizationPercentage"]);
                    perEngine.TryGetValue(key, out double cur);
                    perEngine[key] = cur + util;
                }
            }

            if (perEngine.Count == 0) return null;

            double max = 0;
            foreach (var v in perEngine.Values) if (v > max) max = v;
            return Math.Clamp(max, 0, 100);
        }
        catch (Exception ex)
        {
            _gpuUsageQueryBroken = true; // don't spam the log every tick
            Logger.Log($"[HardwareMonitoringService] GPU usage error (giving up until restart): {ex.Message}");
            return null;
        }
    }

    private double? GetGpuMemoryUsage()
    {
        if (_gpuAdapterMemorySearcher == null || _gpuMemoryQueryBroken) return null;
        try
        {
            double usedBytes = -1;

            foreach (ManagementObject obj in _gpuAdapterMemorySearcher.Get())
            {
                using (obj)
                {
                    var name = obj["Name"] as string;
                    if (_gpuLuidTag != null)
                    {
                        if (string.IsNullOrEmpty(name) ||
                            !name.Contains(_gpuLuidTag, StringComparison.OrdinalIgnoreCase)) continue;
                    }

                    double dedicated = Convert.ToDouble(obj["DedicatedUsage"]);
                    double shared = Convert.ToDouble(obj["SharedUsage"]);
                    double used = dedicated + shared;
                    if (used > usedBytes) usedBytes = used;
                }
            }

            if (usedBytes < 0 || _gpuTotalMemoryBytes == 0) return null;
            return Math.Clamp(usedBytes / _gpuTotalMemoryBytes * 100.0, 0, 100);
        }
        catch (Exception ex)
        {
            _gpuMemoryQueryBroken = true; // don't spam the log every tick
            Logger.Log($"[HardwareMonitoringService] GPU memory error (giving up until restart): {ex.Message}");
            return null;
        }
    }

    private string? GetGpuName()
    {
        if (!string.IsNullOrEmpty(_cachedGpuName)) return _cachedGpuName;

        try
        {
            if (_gpuSearcher != null)
            {
                foreach (ManagementObject obj in _gpuSearcher.Get())
                {
                    var name = obj["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(name) && !name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                    {
                        _cachedGpuName = name;
                        return name;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static ulong ToUlong(FILETIME ft) =>
        ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
}