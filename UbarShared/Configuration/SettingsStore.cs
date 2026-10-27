using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Windows.Storage;

namespace UbarShared.Configuration;

/// <summary>
/// Centralized configuration store for UBAR and UBAR Settings.
/// Both applications read/write the same JSON files in the app's LocalState folder.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
    };

    private const string ModuleLayoutFileName = "module_layout.json";
    private const string LauncherAppsFileName = "launcher_apps.json";
    private const string GeneralSettingsFileName = "general_settings.json";

    /// <summary>
    /// Gets the LocalState folder path for the packaged app.
    /// Falls back to temp path if running unpackaged.
    /// </summary>
    public static string GetLocalStatePath()
    {
        try
        {
            return ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "UBAR");
        }
    }

    private static string GetFilePath(string fileName)
    {
        string dir = GetLocalStatePath();
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return Path.Combine(dir, fileName);
    }

    // ============================================================
    // General Settings
    // ============================================================

    public static GeneralSettings LoadGeneralSettings()
    {
        try
        {
            string path = GetFilePath(GeneralSettingsFileName);
            if (!File.Exists(path)) return new GeneralSettings();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GeneralSettings>(json, s_jsonOpts) ?? new GeneralSettings();
        }
        catch
        {
            return new GeneralSettings();
        }
    }

    public static void SaveGeneralSettings(GeneralSettings settings)
    {
        try
        {
            string path = GetFilePath(GeneralSettingsFileName);
            string json = JsonSerializer.Serialize(settings, s_jsonOpts);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best effort
        }
    }

    // ============================================================
    // Module Layout
    // ============================================================

    public static List<ModuleLayoutEntry>? LoadModuleLayout()
    {
        try
        {
            string path = GetFilePath(ModuleLayoutFileName);
            if (!File.Exists(path)) return null;

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ModuleLayoutEntry>>(json, s_jsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveModuleLayout(IEnumerable<ModuleLayoutEntry> entries)
    {
        try
        {
            string path = GetFilePath(ModuleLayoutFileName);
            var ordered = entries
                .OrderBy(e => (int)e.Slot)
                .ThenBy(e => e.Order)
                .ToList();

            string json = JsonSerializer.Serialize(ordered, s_jsonOpts);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best effort
        }
    }

    // ============================================================
    // Launcher Apps
    // ============================================================

    public static List<LauncherAppEntry>? LoadLauncherApps()
    {
        try
        {
            string path = GetFilePath(LauncherAppsFileName);
            if (!File.Exists(path)) return null;

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<LauncherAppEntry>>(json, s_jsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveLauncherApps(IEnumerable<LauncherAppEntry> apps)
    {
        try
        {
            string path = GetFilePath(LauncherAppsFileName);
            string json = JsonSerializer.Serialize(apps.ToList(), s_jsonOpts);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best effort
        }
    }
}

/// <summary>
/// General UBAR settings that apply to the bar itself.
/// </summary>
public sealed class GeneralSettings
{
    public bool AutoHideOnFullscreen { get; set; } = true;
    public int BarHeightDip { get; set; } = 60;
    public bool ShowClockSeconds { get; set; } = false;
    public string Theme { get; set; } = "System"; // System, Light, Dark

    // Weather module settings
    public string WeatherApiKey { get; set; } = string.Empty; // Tomorrow.io API key (or set TOMORROW_API_KEY env var)
    public string WeatherLocationMode { get; set; } = "auto"; // "auto" or "manual"
    public string WeatherCityName { get; set; } = string.Empty;
    public double WeatherLatitude { get; set; } = 0;
    public double WeatherLongitude { get; set; } = 0;
    public string WeatherUnits { get; set; } = "metric"; // "metric" or "imperial"
    public int WeatherRefreshInterval { get; set; } = 15; // minutes
    public bool WeatherShowForecast { get; set; } = true;
}

/// <summary>
/// Persisted module layout entry (shared between UBAR and Settings).
/// </summary>
public sealed class ModuleLayoutEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Glyph { get; set; } = string.Empty;
    public ModuleSlot Slot { get; set; }
    public int Order { get; set; }
    public bool IsVisible { get; set; } = true;
}

/// <summary>
/// Module slot alignment (matches UBAR's ModuleAlignment).
/// </summary>
public enum ModuleSlot
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>
/// Launcher app entry (shared between UBAR and Settings).
/// </summary>
public sealed class LauncherAppEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Glyph { get; set; } = "\uE8B7";
    public string ImagePath { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public bool IsVisible { get; set; } = true;
}