using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace Ubar3;

/// <summary>
/// Shared cache + extraction of application icons (exe icon → XAML
/// ImageSource, including composite half-and-half icons for snap groups).
/// Used by the App Selector and Active App modules.
/// </summary>
internal static class AppIconHelper
{
    private static readonly Dictionary<string, Drawing.Bitmap?> _bitmaps = new();
    private static readonly Dictionary<string, ImageSource?> _images = new();
    private static readonly Dictionary<uint, (string Exe, string Name, DateTime Time)> _pidInfo = new();
    private static readonly TimeSpan PidInfoTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Resolves a pid to (exe path, display name). Cached with a TTL so
    /// recycled pids don't stick with a dead process's identity.
    /// </summary>
    public static (string Exe, string Name) GetProcessInfo(uint pid)
    {
        if (_pidInfo.TryGetValue(pid, out var cached) &&
            DateTime.UtcNow - cached.Time < PidInfoTtl)
        {
            return (cached.Exe, cached.Name);
        }

        try
        {
            using var p = Process.GetProcessById((int)pid);
            var exe = p.MainModule?.FileName ?? "";
            var name = string.IsNullOrEmpty(exe)
                ? p.ProcessName
                : Path.GetFileNameWithoutExtension(exe);
            _pidInfo[pid] = (exe, name, DateTime.UtcNow);
            return (exe, name);
        }
        catch
        {
            _pidInfo[pid] = ("", "App", DateTime.UtcNow);
            return ("", "App");
        }
    }

    /// <summary>Exe icon as a XAML image, cached per path.</summary>
    public static ImageSource? GetIconImageSource(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (_images.TryGetValue(exePath, out var cached)) return cached;
        var src = TryCreateIconSource(exePath);
        _images[exePath] = src;
        return src;
    }

    /// <summary>
    /// Builds the merged half-and-half icon: left app on the left half,
    /// right app on the right half.
    /// </summary>
    public static ImageSource? GetMergedIconSource(string exeLeft, string exeRight)
    {
        try
        {
            Drawing.Bitmap? bL = GetOrLoadIconBitmap(exeLeft);
            Drawing.Bitmap? bR = GetOrLoadIconBitmap(exeRight);
            if (bL == null || bR == null) return null;

            using var merged = new Drawing.Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var g = Drawing.Graphics.FromImage(merged))
            {
                g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bL, new Drawing.Rectangle(0, 0, 16, 32));
                g.DrawImage(bR, new Drawing.Rectangle(16, 0, 16, 32));
            }
            return BitmapToSource(merged);
        }
        catch (Exception ex)
        {
            Logger.Log($"[AppIconHelper] Merged icon failed: {ex.Message}");
            return null;
        }
    }

    private static ImageSource? TryCreateIconSource(string exePath)
    {
        var bmp = LoadIconBitmap(exePath);
        if (bmp == null) return null;
        using (bmp) return BitmapToSource(bmp);
    }

    /// <summary>Extracts the exe's icon as a GDI+ bitmap (cached, caller must not dispose).</summary>
    private static Drawing.Bitmap? GetOrLoadIconBitmap(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (_bitmaps.TryGetValue(exePath, out var cached)) return cached;
        var bmp = LoadIconBitmap(exePath);
        _bitmaps[exePath] = bmp;
        return bmp;
    }

    private static Drawing.Bitmap? LoadIconBitmap(string exePath)
    {
        try
        {
            var shfi = new Win32Interop.SHFILEINFO();
            IntPtr res = Win32Interop.SHGetFileInfo(exePath, 0, out shfi,
                (uint)Marshal.SizeOf<Win32Interop.SHFILEINFO>(),
                Win32Interop.SHGFI_ICON | Win32Interop.SHGFI_LARGEICON);
            if (res == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
            using var icon = Drawing.Icon.FromHandle(shfi.hIcon);
            var bmp = new Drawing.Bitmap(icon.ToBitmap());
            _ = Win32Interop.DestroyIcon(shfi.hIcon);
            return bmp;
        }
        catch (Exception ex)
        {
            Logger.Log($"[AppIconHelper] Icon for '{exePath}' failed: {ex.Message}");
            return null;
        }
    }

    private static ImageSource BitmapToSource(Drawing.Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.SetSource(ms.AsRandomAccessStream());
        return bi;
    }
}
