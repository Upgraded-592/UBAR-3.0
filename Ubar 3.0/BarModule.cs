using System.Collections.Generic;

namespace Ubar3;

/// <summary>
/// Identifies a distinct, toggleable / movable piece of the bar.
/// </summary>
internal enum BarModuleId
{
    Launcher,   // Explorer / cmd / Task Manager quick-launch buttons
    Calendar,   // Date display (kept separate from the clock)
    Clock,      // Time display
    Cpu,        // CPU usage readout + progress bar
    Ram,        // RAM usage readout + progress bar
    Battery,    // Battery status
    CpuTemp,    // CPU temperature
    GpuTemp,    // GPU temperature
    Gpu,        // GPU usage
    Vram,       // VRAM usage
    Weather,    // Weather information
    Music,      // Media playback controls
    Bluetooth,  // Bluetooth device management
    Wifi,       // WiFi status
    Exit,       // Exit button
}

/// <summary>
/// The three horizontal slots a module can occupy.
/// </summary>
internal enum BarSlot
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>
/// Persisted state for a single module.
/// </summary>
internal sealed class BarModule
{
    public BarModuleId Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Glyph { get; init; } = string.Empty;
    public BarSlot Slot { get; set; }
    public int Order { get; set; }
    public bool IsVisible { get; set; } = true;
}

/// <summary>
/// Default layout + helpers.
/// </summary>
internal static class BarModuleDefaults
{
    public static IReadOnlyList<BarModule> CreateDefaultLayout() => new List<BarModule>
    {
        new BarModule { Id = BarModuleId.Launcher,  DisplayName = "App Launcher", Glyph = "\uE8B7", Slot = BarSlot.Left,   Order = 0, IsVisible = true },
        new BarModule { Id = BarModuleId.Calendar,  DisplayName = "Calendar",     Glyph = "\uEC92", Slot = BarSlot.Center, Order = 0, IsVisible = true },
        new BarModule { Id = BarModuleId.Clock,     DisplayName = "Clock",        Glyph = "\uEC92", Slot = BarSlot.Center, Order = 1, IsVisible = true },
        new BarModule { Id = BarModuleId.Cpu,       DisplayName = "CPU Usage",    Glyph = "\uE950", Slot = BarSlot.Right,  Order = 0, IsVisible = true },
        new BarModule { Id = BarModuleId.Ram,       DisplayName = "RAM Usage",    Glyph = "\uE9A1", Slot = BarSlot.Right,  Order = 1, IsVisible = true },
        new BarModule { Id = BarModuleId.Battery,   DisplayName = "Battery",      Glyph = "\uE83F", Slot = BarSlot.Right,  Order = 2, IsVisible = true },
        new BarModule { Id = BarModuleId.CpuTemp,   DisplayName = "CPU Temp",     Glyph = "\uE9C7", Slot = BarSlot.Right,  Order = 3, IsVisible = true },
        new BarModule { Id = BarModuleId.GpuTemp,   DisplayName = "GPU Temp",     Glyph = "\uE9C7", Slot = BarSlot.Right,  Order = 4, IsVisible = true },
        new BarModule { Id = BarModuleId.Gpu,       DisplayName = "GPU Usage",    Glyph = "\uEDA3", Slot = BarSlot.Right,  Order = 5, IsVisible = true },
        new BarModule { Id = BarModuleId.Vram,      DisplayName = "VRAM Usage",   Glyph = "\uE964", Slot = BarSlot.Right,  Order = 6, IsVisible = true },
        new BarModule { Id = BarModuleId.Weather,   DisplayName = "Weather",      Glyph = "\u2600", Slot = BarSlot.Right,  Order = 7, IsVisible = true },
        new BarModule { Id = BarModuleId.Music,     DisplayName = "Music",        Glyph = "\uE768", Slot = BarSlot.Right,  Order = 8, IsVisible = true },
        new BarModule { Id = BarModuleId.Bluetooth, DisplayName = "Bluetooth",    Glyph = "\uE702", Slot = BarSlot.Right,  Order = 9, IsVisible = true },
        new BarModule { Id = BarModuleId.Wifi,      DisplayName = "WiFi",         Glyph = "\uE701", Slot = BarSlot.Right,  Order = 10, IsVisible = true },
        new BarModule { Id = BarModuleId.Exit,      DisplayName = "Exit",         Glyph = "\uE7E8", Slot = BarSlot.Right,  Order = 11, IsVisible = true },
    };

    public static string GetIdString(BarModuleId id) => id.ToString();
}
