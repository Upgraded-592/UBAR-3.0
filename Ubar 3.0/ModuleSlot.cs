using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ubar3;

public enum ModuleSlotState { Enabled, Disabled, Faulted }

/// <summary>
/// Wraps a single <see cref="IUbarModule"/> with error isolation.
/// All calls to the module are wrapped in try/catch; a faulted module
/// is quarantined and replaced with an error indicator.
/// </summary>
public sealed class ModuleSlot
{
    private FrameworkElement? _view;

    public IUbarModule Module { get; }
    public ModuleAlignment Alignment { get; set; }
    public int Order { get; set; }
    public bool IsVisible { get; set; } = true;
    public ModuleSlotState State { get; private set; } = ModuleSlotState.Disabled;

    public bool IsEnabled => State == ModuleSlotState.Enabled;
    public bool IsFaulted => State == ModuleSlotState.Faulted;

    public ModuleSlot(IUbarModule module, ModuleAlignment alignment, int order)
    {
        Module = module;
        Alignment = alignment;
        Order = order;
    }

    /// <summary>
    /// Initializes the module and creates its view. Safe to call once.
    /// </summary>
    public void Initialize(ModuleContext ctx)
    {
        if (IsFaulted) return;
        try
        {
            var view = Module.Initialize(ctx);
            _view = view ?? throw new InvalidOperationException("Initialize() returned null.");
        }
        catch (Exception ex)
        {
            Logger.Log($"[ModuleSlot] Module '{Module.Id}' Initialize failed: {ex}");
            _view = MakeFaultView($"'{Module.Id}' failed to initialize");
            State = ModuleSlotState.Faulted;
        }
    }

    /// <summary>Returns the module's view element (or a fault indicator).</summary>
    public FrameworkElement GetView() =>
        _view ?? MakeFaultView($"'{Module.Id}' not initialized");

    public void Enable()
    {
        if (IsFaulted) return;
        if (State == ModuleSlotState.Enabled) return;
        try
        {
            Module.OnEnabled();
            State = ModuleSlotState.Enabled;
        }
        catch (Exception ex)
        {
            Logger.Log($"[ModuleSlot] Module '{Module.Id}' OnEnabled failed: {ex}");
            State = ModuleSlotState.Faulted;
        }
    }

    public void Disable()
    {
        if (IsFaulted || State == ModuleSlotState.Disabled) return;
        try
        {
            Module.OnDisabled();
            State = ModuleSlotState.Disabled;
        }
        catch (Exception ex)
        {
            Logger.Log($"[ModuleSlot] Module '{Module.Id}' OnDisabled failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { Module.Dispose(); }
        catch (Exception ex) { Logger.Log($"[ModuleSlot] Module '{Module.Id}' Dispose failed: {ex.Message}"); }
        _view = null;
    }

    private static Border MakeFaultView(string tooltip)
    {
        var b = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new Microsoft.UI.Xaml.CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(60, 255, 60, 60)),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "⚠",
                FontSize = 11,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            }
        };
        ToolTipService.SetToolTip(b, tooltip);
        return b;
    }
}
