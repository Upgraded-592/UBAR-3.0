using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;

namespace Ubar3;

/// <summary>
/// Settings window. Currently hosts the module layout editor: modules can be
/// dragged between the Left / Center / Right zones and reordered within a zone.
/// Every change is applied to <see cref="ModuleManager"/> immediately and
/// persisted through <see cref="ModuleLayoutService"/>.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly ModuleAlignment[] Zones =
    {
        ModuleAlignment.Left, ModuleAlignment.Center, ModuleAlignment.Right,
    };

    private static readonly FontFamily IconFont = new("Segoe Fluent Icons");

    private readonly Dictionary<ModuleAlignment, ListView> _zoneLists = new();
    private ModuleSlot? _draggedSlot;
    private bool _isApplying;

    public SettingsWindow()
    {
        Title = "UBAR Settings";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(980, 620));

        Content = BuildContent();
        RefreshLists();

        App.Modules.LayoutChanged += OnLayoutChanged;
        Closed += (_, _) => App.Modules.LayoutChanged -= OnLayoutChanged;
    }

    // ------------------------------------------------------------------ layout

    private FrameworkElement BuildContent()
    {
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 16 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock
        {
            Text = "Bar Layout",
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = "Drag modules between zones or reorder them within a zone. "
                 + "Use the arrows for keyboard-friendly moves, and the eye button to hide a module.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var zoneGrid = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < Zones.Length; i++)
            zoneGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < Zones.Length; i++)
        {
            var column = BuildZoneColumn(Zones[i]);
            Grid.SetColumn(column, i);
            zoneGrid.Children.Add(column);
        }
        Grid.SetRow(zoneGrid, 1);
        root.Children.Add(zoneGrid);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private FrameworkElement BuildZoneColumn(ModuleAlignment zone)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            CanDragItems = true,
            AllowDrop = true,
            Padding = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Stretch,
            // An explicit brush (even fully transparent) keeps the empty area
            // hit-testable so a zone with no modules still accepts drops.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        list.DragItemsStarting += OnDragItemsStarting;
        list.DragOver += OnDragOver;
        list.DragLeave += (_, e) => e.Handled = true;
        list.Drop += (s, e) => OnDrop(zone, s as ListView, e);
        _zoneLists[zone] = list;

        var panel = new Grid { RowSpacing = 8 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = zone.ToString(),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 0, 0),
        };
        Grid.SetRow(title, 0);
        panel.Children.Add(title);
        Grid.SetRow(list, 1);
        panel.Children.Add(list);

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Padding = new Thickness(8),
            Child = panel,
        };
    }

    private FrameworkElement BuildFooter()
    {
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var showAll = new Button { Content = "Show All" };
        showAll.Click += (_, _) =>
        {
            foreach (var slot in App.Modules.Slots.Where(s => !s.IsVisible && !s.IsFaulted))
                App.Modules.Enable(slot.Module.Id);
            Apply();
        };
        footer.Children.Add(showAll);

        var reset = new Button { Content = "Reset to Default" };
        reset.Click += (_, _) => ResetLayout();
        footer.Children.Add(reset);

        var close = new Button
        {
            Content = "Close",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        close.Click += (_, _) => Close();
        footer.Children.Add(close);

        return footer;
    }

    // ------------------------------------------------------------------ rendering

    private void RefreshLists()
    {
        foreach (var zone in Zones)
        {
            if (!_zoneLists.TryGetValue(zone, out var list)) continue;
            list.Items.Clear();
            foreach (var slot in SlotsIn(zone))
                list.Items.Add(BuildModuleCard(slot));
        }
    }

    private static List<ModuleSlot> SlotsIn(ModuleAlignment zone) =>
        App.Modules.Slots.Where(s => s.Alignment == zone).OrderBy(s => s.Order).ToList();

    private FrameworkElement BuildModuleCard(ModuleSlot slot)
    {
        var grid = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var gripper = new FontIcon
        {
            Glyph = "\uE76F",
            FontFamily = IconFont,
            FontSize = 14,
            Opacity = 0.5,
        };
        Grid.SetColumn(gripper, 0);
        grid.Children.Add(gripper);

        var moduleGlyph = string.IsNullOrEmpty(slot.Module.Glyph) ? "\uE700" : slot.Module.Glyph;
        var glyph = new FontIcon
        {
            Glyph = moduleGlyph,
            FontFamily = ModuleGlyphFont.For(moduleGlyph),
            FontSize = 16,
        };
        Grid.SetColumn(glyph, 1);
        grid.Children.Add(glyph);

        var name = new TextBlock
        {
            Text = slot.Module.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = slot.IsVisible ? 1.0 : 0.45,
        };
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);

        var actions = BuildCardActions(slot);
        Grid.SetColumn(actions, 3);
        grid.Children.Add(actions);

        var card = new Border
        {
            Tag = slot,
            Padding = new Thickness(8, 6, 4, 6),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = grid,
        };
        ToolTipService.SetToolTip(card, slot.IsFaulted
            ? $"{slot.Module.Id} — module failed to load"
            : slot.Module.Id);
        return card;
    }

    private FrameworkElement BuildCardActions(ModuleSlot slot)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        actions.Children.Add(IconButton("\uE76B", "Move to previous zone",
            slot.Alignment != ModuleAlignment.Left, () => ShiftZone(slot, -1)));
        actions.Children.Add(IconButton("\uE70E", "Move up",
            SlotsIn(slot.Alignment).IndexOf(slot) > 0, () => ShiftOrder(slot, -1)));
        actions.Children.Add(IconButton("\uE70D", "Move down",
            SlotsIn(slot.Alignment).IndexOf(slot) < SlotsIn(slot.Alignment).Count - 1, () => ShiftOrder(slot, 1)));
        actions.Children.Add(IconButton("\uE76C", "Move to next zone",
            slot.Alignment != ModuleAlignment.Right, () => ShiftZone(slot, 1)));
        actions.Children.Add(IconButton(slot.IsVisible ? "\uE7B3" : "\uED1A",
            slot.IsVisible ? "Hide module" : "Show module",
            !slot.IsFaulted, () => ToggleVisibility(slot)));

        return actions;
    }

    private static Button IconButton(string glyph, string tooltip, bool enabled, Action onClick)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontFamily = IconFont, FontSize = 12 },
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 0,
            Background = null,
            BorderThickness = new Thickness(0),
            IsEnabled = enabled,
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    // ------------------------------------------------------------------ drag & drop

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedSlot = e.Items.Count > 0 && e.Items[0] is FrameworkElement fe && fe.Tag is ModuleSlot s
            ? s
            : null;

        if (_draggedSlot == null)
        {
            e.Cancel = true;
            return;
        }

        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(_draggedSlot.Module.Id);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_draggedSlot == null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        if (e.DragUIOverride != null)
        {
            e.DragUIOverride.Caption = "Move";
            e.DragUIOverride.IsGlyphVisible = false;
        }
        e.Handled = true;
    }

    private void OnDrop(ModuleAlignment zone, ListView? list, DragEventArgs e)
    {
        e.Handled = true;
        var slot = _draggedSlot;
        _draggedSlot = null;
        if (list == null || slot == null) return;

        MoveSlot(slot, zone, DropIndex(list, slot, e.GetPosition(list).Y));
    }

    /// <summary>
    /// Insertion index for a drop at <paramref name="y"/>, counted over the
    /// list's items with the dragged card excluded.
    /// </summary>
    private static int DropIndex(ListView list, ModuleSlot dragged, double y)
    {
        int index = 0;
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.Items[i] is FrameworkElement item && ReferenceEquals(item.Tag, dragged)) continue;
            if (list.ContainerFromIndex(i) is FrameworkElement container)
            {
                double top = container.TransformToVisual(list).TransformPoint(new Point(0, 0)).Y;
                if (y < top + container.ActualHeight / 2) break;
            }
            index++;
        }
        return index;
    }

    // ------------------------------------------------------------------ mutations

    /// <summary>Moves a module into <paramref name="zone"/> at <paramref name="index"/>.</summary>
    private void MoveSlot(ModuleSlot slot, ModuleAlignment zone, int index)
    {
        var target = SlotsIn(zone).Where(s => !ReferenceEquals(s, slot)).ToList();
        index = Math.Clamp(index, 0, target.Count);
        target.Insert(index, slot);

        slot.Alignment = zone;
        for (int i = 0; i < target.Count; i++) target[i].Order = i;

        ReindexAllZones();
        Apply();
    }

    private void ShiftOrder(ModuleSlot slot, int delta)
    {
        var peers = SlotsIn(slot.Alignment);
        int idx = peers.IndexOf(slot);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= peers.Count) return;

        peers.RemoveAt(idx);
        peers.Insert(target, slot);
        for (int i = 0; i < peers.Count; i++) peers[i].Order = i;
        Apply();
    }

    private void ShiftZone(ModuleSlot slot, int delta)
    {
        int zone = (int)slot.Alignment + delta;
        if (zone < 0 || zone >= Zones.Length) return;
        MoveSlot(slot, (ModuleAlignment)zone, int.MaxValue);
    }

    private void ToggleVisibility(ModuleSlot slot)
    {
        _isApplying = true;
        try
        {
            if (slot.IsVisible) App.Modules.Disable(slot.Module.Id);
            else App.Modules.Enable(slot.Module.Id);
        }
        finally
        {
            _isApplying = false;
        }
        Apply();
    }

    private void ResetLayout()
    {
        _isApplying = true;
        try
        {
            foreach (var slot in App.Modules.Slots)
            {
                slot.Alignment = slot.Module.DefaultAlignment;
                slot.Order = slot.Module.DefaultOrder;
                slot.IsVisible = true;
                if (!slot.IsEnabled && !slot.IsFaulted) slot.Enable();
            }
        }
        finally
        {
            _isApplying = false;
        }
        Apply();
    }

    /// <summary>Compacts every zone's order values so they stay 0..n-1 with no gaps.</summary>
    private static void ReindexAllZones()
    {
        foreach (var zone in Zones)
        {
            var inZone = SlotsIn(zone);
            for (int i = 0; i < inZone.Count; i++) inZone[i].Order = i;
        }
    }

    /// <summary>Persists the current slot state, rebuilds the bar, then redraws this window.</summary>
    private void Apply()
    {
        _isApplying = true;
        try
        {
            App.Modules.ReorderCommit();
        }
        catch (Exception ex)
        {
            Logger.Log($"[SettingsWindow] Apply failed: {ex}");
        }
        finally
        {
            _isApplying = false;
        }
        RefreshLists();
    }

    private void OnLayoutChanged()
    {
        // Ignore the echo of our own changes; only react to edits made elsewhere
        // (e.g. the bar's right-click menu) while this window is open.
        if (_isApplying) return;
        RefreshLists();
    }
}
