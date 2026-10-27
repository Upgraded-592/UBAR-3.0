using System;
using System.Collections.Generic;
using System.Linq;
using UbarShared.Configuration;

namespace Ubar3;

/// <summary>
/// Persists the bar module layout (order, slot, visibility) to a JSON file
/// in the app's LocalState folder so it survives restarts.
/// </summary>
internal static class ModuleLayoutService
{
    /// <summary>
    /// Loads the saved layout. Returns null if no layout exists or loading fails.
    /// </summary>
    public static List<BarModule>? LoadLayout()
    {
        var saved = SettingsStore.LoadModuleLayout();
        if (saved == null) return null;

        var result = new List<BarModule>();
        foreach (var entry in saved)
        {
            if (Enum.TryParse<BarModuleId>(entry.Id, ignoreCase: true, out var enumId))
            {
                result.Add(new BarModule
                {
                    Id = enumId,
                    DisplayName = entry.DisplayName,
                    Glyph = entry.Glyph,
                    Slot = (BarSlot)(int)entry.Slot,
                    Order = entry.Order,
                    IsVisible = entry.IsVisible,
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Saves the given module list to disk.
    /// </summary>
    public static void SaveLayout(IEnumerable<BarModule> modules)
    {
        var entries = modules.Select(m => new ModuleLayoutEntry
        {
            Id = m.Id.ToString(),
            DisplayName = m.DisplayName,
            Glyph = m.Glyph,
            Slot = (UbarShared.Configuration.ModuleSlot)(int)m.Slot,
            Order = m.Order,
            IsVisible = m.IsVisible,
        });
        SettingsStore.SaveModuleLayout(entries);
    }

    /// <summary>
    /// Merges a saved layout onto a freshly-created default layout so that
    /// any new modules added in a future version still appear, while
    /// existing saved positions/visibility are preserved.
    /// </summary>
    public static List<BarModule> MergeWithDefaults(List<BarModule> saved)
    {
        var defaults = BarModuleDefaults.CreateDefaultLayout();
        var result = new List<BarModule>();

        foreach (var def in defaults)
        {
            var match = saved.FirstOrDefault(s => s.Id == def.Id);
            if (match != null)
            {
                result.Add(new BarModule
                {
                    Id = def.Id,
                    DisplayName = def.DisplayName,
                    Glyph = def.Glyph,
                    Slot = match.Slot,
                    Order = match.Order,
                    IsVisible = match.IsVisible,
                });
            }
            else
            {
                result.Add(def);
            }
        }

        return result;
    }

    /// <summary>Finds the saved BarModule entry for a given module id string, or null.</summary>
    public static BarModule? GetEntry(string moduleId)
    {
        var saved = LoadLayout();
        if (saved == null) return null;
        return saved.FirstOrDefault(m => m.Id.ToString().Equals(moduleId, StringComparison.OrdinalIgnoreCase));
    }
}
