using System;
using System.IO;
using System.Text;
using Windows.Storage;

namespace Ubar3;

internal static class Logger
{
    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static bool _initialized;
    private static string? _logPath;

    private static string GetLogPath()
    {
        if (_logPath != null) return _logPath;
        try
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            if (localFolder != null)
            {
                var dir = localFolder.Path;
                _logPath = Path.Combine(dir, "UBAR", "ubar.log");
            }
        }
        catch { }
        
        if (_logPath == null)
        {
            _logPath = Path.Combine(Path.GetTempPath(), "UBAR", "ubar.log");
        }
        return _logPath;
    }

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    var logDir = Path.GetDirectoryName(GetLogPath())!;
                    Directory.CreateDirectory(logDir);
                    _writer = new StreamWriter(GetLogPath(), append: true, Encoding.UTF8, bufferSize: 4096)
                    {
                        AutoFlush = true
                    };
                    _initialized = true;
                }
            }

            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n";
            lock (_lock)
            {
                _writer?.Write(entry);
            }
            System.Diagnostics.Debug.WriteLine($"[UBAR] {message}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UBAR] Logger failed: {ex.Message}");
        }
    }
}
