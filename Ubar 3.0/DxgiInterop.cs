using System;
using System.Runtime.InteropServices;

namespace Ubar3;

/// <summary>
/// Minimal DXGI interop for enumerating GPU adapters without a D3D device.
/// Vtable layouts follow the Windows SDK dxgi.h (IDXGIObject defines
/// SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent — in
/// that order — before the derived interface's own methods).
/// </summary>
internal static class DxgiInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ADAPTER_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public LUID AdapterLuid;
    }

    [ComImport]
    [Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369")] // IDXGIFactory
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory
    {
        // IDXGIObject
        void SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        void SetPrivateDataInterface(ref Guid name, IntPtr data);
        void GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        void GetParent(ref Guid riid, out IntPtr parent);
        // IDXGIFactory
        [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter adapter);
        void MakeWindowAssociation(IntPtr hwnd, uint flags);
        void GetWindowAssociation(out IntPtr hwnd);
        void CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        void CreateSoftwareAdapter(IntPtr module, out IDXGIAdapter adapter);
    }

    [ComImport]
    [Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0")] // IDXGIAdapter
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter
    {
        // IDXGIObject
        void SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        void SetPrivateDataInterface(ref Guid name, IntPtr data);
        void GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        void GetParent(ref Guid riid, out IntPtr parent);
        // IDXGIAdapter
        void EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(out ADAPTER_DESC desc);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory(ref Guid riid, out IDXGIFactory factory);

    private static readonly Guid IID_IDXGIFactory = new("7b7166ec-21c7-44ae-b21a-c9ae321ae369");

    /// <summary>Primary hardware GPU info obtained via DXGI.</summary>
    public sealed record GpuAdapterInfo(
        string Name,
        string LuidTag,      // matches perf-counter instance names, e.g. "luid_0x00000000_0x0001204C"
        uint VendorId,       // 0x8086 Intel, 0x10DE NVIDIA, 0x1002/0x1022 AMD, 0x1414 Microsoft
        ulong DedicatedVideoBytes,
        ulong SharedSystemBytes);

    /// <summary>
    /// Enumerates adapters and returns the first real hardware GPU,
    /// skipping software adapters (e.g. "Microsoft Basic Render Driver").
    /// </summary>
    public static GpuAdapterInfo? TryGetPrimaryGpu()
    {
        try
        {
            var iid = IID_IDXGIFactory;
            int hr = CreateDXGIFactory(ref iid, out var factory);
            if (hr != 0 || factory is null) return null;

            try
            {
                for (uint i = 0; ; i++)
                {
                    int ehr = factory.EnumAdapters(i, out var adapter);
                    if (ehr != 0) break; // DXGI_ERROR_NOT_FOUND — end of list
                    if (adapter is null) continue;

                    try
                    {
                        if (adapter.GetDesc(out var desc) == 0)
                        {
                            var name = desc.Description.Trim();
                            if (name.Length > 0 && !IsSoftwareAdapter(name, desc.VendorId))
                            {
                                return new GpuAdapterInfo(
                                    name,
                                    $"luid_0x{(uint)desc.AdapterLuid.HighPart:X8}_0x{desc.AdapterLuid.LowPart:X8}",
                                    desc.VendorId,
                                    desc.DedicatedVideoMemory,
                                    desc.SharedSystemMemory);
                            }
                        }
                    }
                    finally
                    {
                        _ = Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                _ = Marshal.ReleaseComObject(factory);
            }
        }
        catch
        {
            // DXGI unavailable — callers fall back to WMI.
        }
        return null;
    }

    private static bool IsSoftwareAdapter(string name, uint vendorId) =>
        vendorId == 0x1414 || // Microsoft Corporation
        name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase);
}
