using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Ubar3;

/// <summary>
/// Central registry for all UBAR modules. Manages lifecycle, ordering,
/// enable/disable, persistence.
/// </summary>
public sealed class ModuleManager : IDisposable
{
    private readonly List<ModuleSlot> _slots = new();
    private readonly ModuleContext _ctx;

    public IReadOnlyList<ModuleSlot> Slots => _slots;

    public event Action? LayoutChanged;

    public ModuleManager(ModuleContext ctx) => _ctx = ctx;

    // ------------------------------------------------------------------ registration

    public void RegisterModule(IUbarModule module)
    {
        var saved = ModuleLayoutService.GetEntry(module.Id);
        var alignment = saved != null
            ? (ModuleAlignment)(int)saved.Slot
            : module.DefaultAlignment;
        var order = saved?.Order ?? module.DefaultOrder;
        var visible = saved?.IsVisible ?? module.DefaultVisible;

        var slot = new ModuleSlot(module, alignment, order)
        {
            IsVisible = visible
        };
        _slots.Add(slot);
    }

    public void InitializeAll()
    {
        Logger.Log($"[ModuleManager] InitializeAll START, {_slots.Count} slots");
        foreach (var slot in _slots)
        {
            Logger.Log($"[ModuleManager] Initializing module: {slot.Module.Id}");
            slot.Initialize(_ctx);
            Logger.Log($"[ModuleManager] Module {slot.Module.Id} initialized, State={slot.State}, IsFaulted={slot.IsFaulted}");
            if (slot.IsVisible && !slot.IsFaulted)
            {
                Logger.Log($"[ModuleManager] Enabling module: {slot.Module.Id}");
                slot.Enable();
            }
        }
        Logger.Log($"[ModuleManager] InitializeAll END");
    }

    // ------------------------------------------------------------------ enable/disable

    public void Enable(string id)
    {
        var slot = Find(id);
        if (slot == null || slot.IsFaulted) return;
        slot.Enable();
        slot.IsVisible = true;
        Save();
        LayoutChanged?.Invoke();
    }

    public void Disable(string id)
    {
        var slot = Find(id);
        if (slot == null || slot.IsFaulted) return;
        slot.Disable();
        slot.IsVisible = false;
        Save();
        LayoutChanged?.Invoke();
    }

    // ------------------------------------------------------------------ reorder

    public void Reorder(string id, ModuleAlignment newAlignment, int newOrder)
    {
        var slot = Find(id);
        if (slot == null) return;
        slot.Alignment = newAlignment;
        slot.Order = newOrder;
        RebalanceZone(newAlignment);
        Save();
        LayoutChanged?.Invoke();
    }

    public void ShiftLeft(string id)
    {
        Logger.Log($"[ModuleManager] ShiftLeft called for {id}");
        var slot = Find(id);
        if (slot == null) return;
        var peers = _slots.Where(s => s.Alignment == slot.Alignment).OrderBy(s => s.Order).ThenBy(s => s.Module.Id).ToList();
        int idx = peers.IndexOf(slot);
        if (idx <= 0) return;
        (peers[idx].Order, peers[idx - 1].Order) = (peers[idx - 1].Order, peers[idx].Order);
        Save(); LayoutChanged?.Invoke();
    }

    public void ShiftRight(string id)
    {
        Logger.Log($"[ModuleManager] ShiftRight called for {id}");
        var slot = Find(id);
        if (slot == null) return;
        var peers = _slots.Where(s => s.Alignment == slot.Alignment).OrderBy(s => s.Order).ThenBy(s => s.Module.Id).ToList();
        int idx = peers.IndexOf(slot);
        if (idx < 0 || idx >= peers.Count - 1) return;
        (peers[idx].Order, peers[idx + 1].Order) = (peers[idx + 1].Order, peers[idx].Order);
        Save(); LayoutChanged?.Invoke();
    }

    public void MoveToZone(string id, ModuleAlignment zone)
    {
        var slot = Find(id);
        if (slot == null) return;
        slot.Alignment = zone;
        slot.Order = _slots.Where(s => s.Alignment == zone).OrderBy(s => s.Order).ThenBy(s => s.Module.Id).Select(s => s.Order).DefaultIfEmpty(-1).Max() + 1;
        RebalanceZone(zone);
        Save(); LayoutChanged?.Invoke();
    }

    public void ReorderCommit()
    {
        Logger.Log($"[ModuleManager] ReorderCommit START");
        Logger.Log($"[ModuleManager] Before Rebalance - Left: {string.Join(",", _slots.Where(s=>s.Alignment==ModuleAlignment.Left).OrderBy(s=>s.Order).Select(s=>$"{s.Module.Id}:O={s.Order},V={s.IsVisible}"))}");
        Logger.Log($"[ModuleManager] Before Rebalance - Center: {string.Join(",", _slots.Where(s=>s.Alignment==ModuleAlignment.Center).OrderBy(s=>s.Order).Select(s=>$"{s.Module.Id}:O={s.Order},V={s.IsVisible}"))}");
        Logger.Log($"[ModuleManager] Before Rebalance - Right: {string.Join(",", _slots.Where(s=>s.Alignment==ModuleAlignment.Right).OrderBy(s=>s.Order).Select(s=>$"{s.Module.Id}:O={s.Order},V={s.IsVisible}"))}");

        RebalanceZone(ModuleAlignment.Left);
        RebalanceZone(ModuleAlignment.Center);
        RebalanceZone(ModuleAlignment.Right);
        
        Logger.Log($"[ModuleManager] After Rebalance - Left: {string.Join(",", _slots.Where(s=>s.Alignment==ModuleAlignment.Left).OrderBy(s=>s.Order).Select(s=>$"{s.Module.Id}:O={s.Order},V={s.IsVisible}"))}");
        Logger.Log($"[ModuleManager] After Rebalance - Center: {string.Join(",", _slots.Where(s=>s.Alignment==ModuleAlignment.Center).OrderBy(s=>s.Order).Select(s=>$"{s.Module.Id}:O={s.Order},V={s.IsVisible}"))}");
        Logger.Log($"[ModuleManager] After Rebalance - Right: {string.Join(",", _slots.Where(s=>s.Alignment==ModuleAlignment.Right).OrderBy(s=>s.Order).Select(s=>$"{s.Module.Id}:O={s.Order},V={s.IsVisible}"))}");

        Save();
        LayoutChanged?.Invoke();
        Logger.Log($"[ModuleManager] ReorderCommit END");
    }


    // ------------------------------------------------------------------ persistence

    private void Save()
    {
        // Convert ModuleSlots back to BarModules for existing persistence layer
        var barModules = _slots.Select(s => new BarModule
        {
            // BarModule uses BarModuleId enum; for native modules map Id string to enum,
            // for extensibility store as custom id string in a wrapper
            Id = MapToBarModuleId(s.Module.Id),
            DisplayName = s.Module.DisplayName,
            Glyph = s.Module.Glyph,
            Slot = (BarSlot)(int)s.Alignment,
            Order = s.Order,
            IsVisible = s.IsVisible,
        });
        ModuleLayoutService.SaveLayout(barModules);
    }

    private static BarModuleId MapToBarModuleId(string id) => id switch
    {
        "launcher"    => BarModuleId.Launcher,
        "calendar"    => BarModuleId.Calendar,
        "clock"       => BarModuleId.Clock,
        "cpu"         => BarModuleId.Cpu,
        "ram"         => BarModuleId.Ram,
        "battery"     => BarModuleId.Battery,
        "cputemp"     => BarModuleId.CpuTemp,
        "gputemp"     => BarModuleId.GpuTemp,
        "gpu"         => BarModuleId.Gpu,
        "vram"        => BarModuleId.Vram,
        "weather"     => BarModuleId.Weather,
        "music"       => BarModuleId.Music,
        "bluetooth"   => BarModuleId.Bluetooth,
        "wifi"        => BarModuleId.Wifi,
        "exit"        => BarModuleId.Exit,
        _             => BarModuleId.Exit,  // fallback
    };

    // ------------------------------------------------------------------ helpers

    private ModuleSlot? Find(string id) => _slots.FirstOrDefault(s => s.Module.Id == id);

    private void RebalanceZone(ModuleAlignment zone)
    {
        var inZone = _slots.Where(s => s.Alignment == zone && s.IsVisible).OrderBy(s => s.Order).ThenBy(s => s.Module.Id).ToList();
        Logger.Log($"[ModuleManager] RebalanceZone {zone}: {inZone.Count} visible slots, orders: {string.Join(",", inZone.Select(s => s.Order))}");
        for (int i = 0; i < inZone.Count; i++) inZone[i].Order = i;
    }

    public void Dispose()
    {
        foreach (var slot in _slots)
            try { slot.Dispose(); } catch { }
        _slots.Clear();
    }
}
