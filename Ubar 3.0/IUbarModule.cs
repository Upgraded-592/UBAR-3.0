using Microsoft.UI.Xaml;

namespace Ubar3;

/// <summary>Alignment zone within the bar.</summary>
public enum ModuleAlignment { Left, Center, Right }

/// <summary>
/// Common interface for metrics services.
/// </summary>
public interface IMetricsService : IDisposable
{
    void Subscribe(Action<SystemMetrics> handler);
    void Unsubscribe(Action<SystemMetrics> handler);
}

/// <summary>
/// Core contract for every UBAR module. Modules must be self-contained:
/// they create their own UI, manage their own background work,
/// and clean up after themselves.
/// </summary>
public interface IUbarModule
{
    /// <summary>Stable unique key used for persistence (e.g. "clock", "launcher.explorer").</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in context menus and customization UI.</summary>
    string DisplayName { get; }

    /// <summary>Segoe Fluent Icons glyph for this module (e.g. "\uE8B7").</summary>
    string Glyph { get; }

    /// <summary>Default zone when no saved layout exists.</summary>
    ModuleAlignment DefaultAlignment { get; }

    /// <summary>Default sort order within the alignment zone.</summary>
    int DefaultOrder { get; }

    /// <summary>
    /// Whether the module is visible before the user has changed anything.
    /// Modules opt out by overriding; the user can still enable them from the
    /// bar's context menu or Settings.
    /// </summary>
    bool DefaultVisible => true;

    /// <summary>
    /// Called once on the UI thread during startup.
    /// Must create and return the module's root FrameworkElement.
    /// The returned element is owned by the module — do NOT call this twice.
    /// </summary>
    FrameworkElement Initialize(ModuleContext context);

    /// <summary>Called when the module should start its background work (timer subscriptions, etc.).</summary>
    void OnEnabled();

    /// <summary>Called when the module should suspend background work. Must be zero-cost.</summary>
    void OnDisabled();

    /// <summary>Full cleanup. After this the module is never used again.</summary>
    void Dispose();
}

/// <summary>Shared services passed to every module during initialization.</summary>
public sealed class ModuleContext
{
    public required IMetricsService Metrics { get; init; }
    public ModuleManager? Modules { get; set; }  // set after ModuleManager is created
}
