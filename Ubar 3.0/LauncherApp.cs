using System.Collections.Generic;

namespace Ubar3;

/// <summary>
/// A user-configurable quick-launch entry shown in the App Launcher module.
/// </summary>
public sealed class LauncherApp
{
    /// <summary>Stable id used for persistence. Use a short unique string.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name shown in tooltips / config UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Segoe Fluent Icons glyph (e.g. "\uE8B7"). Used when ImagePath is empty.</summary>
    public string Glyph { get; set; } = "\uE8B7";

    /// <summary>Optional path to an image file (.ico, .png, .jpg, .jpeg, .bmp, .gif, .tiff). Takes precedence over Glyph.</summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>Executable path or shell command to launch.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Optional arguments passed to the command.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Whether this entry is shown in the bar.</summary>
    public bool IsVisible { get; set; } = true;
}

/// <summary>
/// Default launcher entries + helpers.
/// </summary>
public static class LauncherAppDefaults
{
    public static IReadOnlyList<LauncherApp> CreateDefaultApps() => new List<LauncherApp>
    {
        new LauncherApp { Id = "explorer",  DisplayName = "File Explorer",   Glyph = "\uE8B7", Command = "explorer.exe" },
        new LauncherApp { Id = "cmd",       DisplayName = "Command Prompt",  Glyph = "\uE756", Command = "cmd.exe", Arguments = "" },
        new LauncherApp { Id = "taskmgr",   DisplayName = "Task Manager",    Glyph = "\uF120", Command = "taskmgr.exe" },
    };
}
