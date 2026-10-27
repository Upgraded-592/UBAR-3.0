using System;
using System.Collections.Generic;
using System.Linq;
using UbarShared.Configuration;

namespace Ubar3;

/// <summary>
/// Persists the user-configurable launcher app list to a JSON file in the
/// app's LocalState folder so it survives restarts.
/// </summary>
internal static class LauncherAppService
{
    /// <summary>
    /// Loads the saved launcher apps. Returns null if none exist or loading fails.
    /// </summary>
    public static List<LauncherApp>? LoadApps()
    {
        var saved = SettingsStore.LoadLauncherApps();
        if (saved == null) return null;

        return saved.Select(e => new LauncherApp
        {
            Id = e.Id,
            DisplayName = e.DisplayName,
            Glyph = e.Glyph,
            ImagePath = e.ImagePath,
            Command = e.Command,
            Arguments = e.Arguments,
            IsVisible = e.IsVisible,
        }).ToList();
    }

    /// <summary>
    /// Saves the given launcher app list to disk.
    /// </summary>
    public static void SaveApps(IEnumerable<LauncherApp> apps)
    {
        var entries = apps.Select(a => new LauncherAppEntry
        {
            Id = a.Id,
            DisplayName = a.DisplayName,
            Glyph = a.Glyph,
            ImagePath = a.ImagePath,
            Command = a.Command,
            Arguments = a.Arguments,
            IsVisible = a.IsVisible,
        });
        SettingsStore.SaveLauncherApps(entries);
    }

    /// <summary>
    /// Merges a saved list onto the defaults so newly-added default entries
    /// still appear while preserving user edits/removals.
    /// </summary>
    public static List<LauncherApp> MergeWithDefaults(List<LauncherApp> saved)
    {
        var defaults = LauncherAppDefaults.CreateDefaultApps();
        var result = new List<LauncherApp>();

        // Preserve saved entries (user may have edited them)
        foreach (var s in saved)
        {
            result.Add(s);
        }

        // Add any default entries whose Id is not already present
        foreach (var def in defaults)
        {
            if (!result.Any(r => r.Id == def.Id))
            {
                result.Add(def);
            }
        }

        return result;
    }
}
