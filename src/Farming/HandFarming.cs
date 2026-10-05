using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Farming;

/// <summary>
/// Farming by hand (#494, <c>docs/notes/farming/hand-farming.md</c>): with an
/// <see cref="ItemUse.Farm"/> item in hand, Use (LMB / pad RB / VR trigger: the existing
/// <c>use_item</c>) works the field cell ahead after a short bar: the hoe ploughs it, a seed sows it
/// (one seed item covers <see cref="FarmTables.CellsPerSeed"/> cells, the rest is remembered),
/// fertiliser feeds the 3x3 cells round it. A ripe cell is harvested with Gather (G / pad X / VR:
/// the gather binding) through <c>Loot/Gathering</c>, which calls <see cref="HarvestAhead"/>.
/// Also the field readout: looking at a field cell shows "Wheat — ripe 100%".
/// </summary>
public partial class HandFarming : Node
{
    public static HandFarming? Instance { get; private set; }

    /// <summary>The cell worked is this far ahead of the feet.</summary>
    public const float Reach = 1.8f;
    private const float CancelDistance = 0.9f;

    private readonly ItemController _items;
    private readonly WorldOrigin _origin;

    private CanvasLayer _ui = null!;
    private Label _prompt = null!, _readout = null!;
    private ProgressBar _bar = null!;
    private string _promptText = "", _readoutText = "";
    private double _poll;

    // the stroke in progress: its tool, the cell, where it started, how far along
    private FarmTool _tool;
    private CropKind _seed;
    private int _slot = -1;
    private ItemId _item;
    private double _e, _n, _progress, _duration;
    private Vector3 _startedAt;

    /// <summary>Cells still sown by the seed item already opened, per crop.</summary>
    private readonly Dictionary<CropKind, int> _seedCells = new();

    /// <summary>The last stroke (probes).</summary>
    public FarmStroke LastStroke { get; private set; }
    /// <summary>What the last hand harvest gave (probes).</summary>
    public (ItemId Id, int Count) LastHarvest { get; private set; }
    /// <summary>The readout line shown now ("" when none).</summary>
    public string Readout => _readoutText;
    /// <summary>A stroke is under way.</summary>
    public bool Busy => _tool != FarmTool.None;

    public HandFarming(ItemController items, WorldOrigin origin)
    {
        _items = items;
        _origin = origin;
        Name = "HandFarming";
    }

    public HandFarming() : this(null!, null!) { }

    public override void _EnterTree() => Instance = this;
    public override void _ExitTree() { if (Instance == this) Instance = null; }

    public override void _Ready()
    {
        _ui = new CanvasLayer { Layer = 9 };
        AddChild(_ui);
        _readout = UiTheme.Prompt(-240);
        _ui.AddChild(_readout);
        _prompt = UiTheme.Prompt(-210);
        _ui.AddChild(_prompt);
        _bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, ShowPercentage = false, Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.6f) });
        _bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.55f, 0.80f, 0.30f) });
        _bar.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _bar.Position = new Vector2(-90, -176);
        _bar.Size = new Vector2(180, 8);
        _ui.AddChild(_bar);
    }

    /// <summary>What a farm item does: the tool and, for a seed, the crop.</summary>
    public static (FarmTool Tool, CropKind Seed) ToolOf(ItemId id) => id switch
    {
        ItemId.Hoe => (FarmTool.Plough, CropKind.None),
        ItemId.Fertiliser => (FarmTool.Fertilise, CropKind.None),
        _ => FarmTables.CropOf(id) is var c and not CropKind.None ? (FarmTool.Sow, c) : (FarmTool.None, CropKind.None),
    };

    /// <summary>The LV95 point the player works: <see cref="Reach"/> ahead of the feet, along the view.</summary>
    public (double E, double N) Ahead(FootPlayer p)
    {
        var fwd = -p.Camera.GlobalTransform.Basis.Z;
        fwd.Y = 0;
        fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
        return _origin.ToLv95(p.GlobalPosition + fwd * Reach);
    }

    private static string Verb(FarmTool tool, CropKind seed) => tool switch
    {
        FarmTool.Plough => "Till the soil",
        FarmTool.Sow => $"Sow {FarmRules.CropName(seed).ToLowerInvariant()}",
        FarmTool.Fertilise => "Spread fertiliser",
        _ => "",
    };

    /// <summary>
    /// Use with a farm item in hand (<c>ItemController</c>, <see cref="ItemUse.Farm"/>): starts the
    /// stroke on the cell ahead. <paramref name="slot"/> -1: nothing is taken from the pack (probes).
    /// </summary>
    public void Use(FootPlayer player, int slot, ItemId id)
    {
        if (Busy || FarmField.Instance is not { } farm) return;
        var (tool, seed) = ToolOf(id);
        if (tool == FarmTool.None) return;
        var (e, n) = Ahead(player);
        if (farm.CellAtLv95(e, n) is not { } view)
        {
            _items?.Ui.Toast("No field here.");
            return;
        }
        if (!FarmTables.CanWork(tool, view.Stage))
        {
            _items?.Ui.Toast(tool switch
            {
                FarmTool.Plough => $"Nothing to till: {FarmRules.StageName(view.Stage)}.",
                FarmTool.Sow => "Till the soil first (a hoe).",
                _ => "Fertiliser feeds a sown crop.",
            });
            return;
        }
        _tool = tool;
        _seed = seed;
        _slot = slot;
        _item = id;
        _e = e;
        _n = n;
        _progress = 0;
        _duration = tool switch { FarmTool.Plough => 1.2, FarmTool.Sow => 0.6, _ => 0.9 };
        _startedAt = player.GlobalPosition;
    }

    public override void _Process(double delta)
    {
        var p = _items?.UsablePlayer;
        if (Busy)
        {
            if (p == null || p.GlobalPosition.DistanceTo(_startedAt) > CancelDistance) Cancel();
            else
            {
                _progress += delta / _duration;
                _bar.Visible = true;
                _bar.Value = _progress;
                if (_progress >= 1) Complete();
            }
        }
        _poll -= delta;
        if (_poll > 0) return;
        _poll = 0.15;
        string prompt = "", readout = "";
        if (p != null && !p.Indoors && FarmField.Instance is { } farm)
        {
            var (e, n) = Ahead(p);
            if (farm.CellAtLv95(e, n) is { } view)
            {
                readout = Describe(view);
                var held = _items.Inventory.Held;
                if (!held.IsEmpty && !Busy && ToolOf(held.Id) is { Tool: not FarmTool.None } t && FarmTables.CanWork(t.Tool, view.Stage))
                    prompt = $"{InputHints.Tag(PlayerInput.UseItem)} {Verb(t.Tool, t.Seed)}";
            }
        }
        if (prompt != _promptText) { _promptText = prompt; _prompt.Text = prompt; _prompt.Visible = prompt != ""; }
        if (readout != _readoutText) { _readoutText = readout; _readout.Text = readout; _readout.Visible = readout != ""; }
    }

    /// <summary>"Wheat — ripe 100%", "Meadow — mown 40%", "Potatoes — stubble".</summary>
    public static string Describe(FieldCellView v)
    {
        string head = FarmRules.CropName(v.Crop);
        string stage = FarmRules.StageName(v.Stage);
        bool grows = v.Stage is FieldStage.Sown or FieldStage.Growing or FieldStage.Ripe or FieldStage.Mown;
        return grows ? $"{head} — {stage} {MathF.Round(v.Growth * 100):F0}%" : $"{head} — {stage}";
    }

    private void Cancel()
    {
        _tool = FarmTool.None;
        _progress = 0;
        _bar.Visible = false;
    }

    private void Complete()
    {
        var (tool, seed, slot, item) = (_tool, _seed, _slot, _item);
        Cancel();
        if (FarmField.Instance is not { } farm) return;
        bool useInventory = slot >= 0 && _items != null;
        if (tool == FarmTool.Sow && useInventory && _seedCells.GetValueOrDefault(seed) <= 0)
        {
            // a fresh seed item: it sows CellsPerSeed cells
            if (_items!.Inventory[slot].Id != item || !_items.Inventory.TakeOne(slot)) { _items.Ui.Toast("No seed left."); return; }
            _seedCells[seed] = FarmTables.CellsPerSeed;
        }
        LastStroke = WorkAt(farm, tool, _e, _n, seed, tool == FarmTool.Fertilise ? 1 : 0);
        if (LastStroke.Cells <= 0) return;
        if (tool == FarmTool.Sow && useInventory) _seedCells[seed] = _seedCells.GetValueOrDefault(seed) - LastStroke.Cells;
        if (tool == FarmTool.Fertilise && useInventory) _items!.Inventory.TakeOne(slot);
    }

    /// <summary>Works the cell holding (e, n) and, with <paramref name="ring"/> 1, the eight round it.</summary>
    public static FarmStroke WorkAt(FarmField farm, FarmTool tool, double e, double n, CropKind seed, int ring)
    {
        const float cs = FieldFormat.CellSize;
        double ce = (Math.Floor(e / cs) + 0.5) * cs, cn = (Math.Floor(n / cs) + 0.5) * cs;
        double half = ring * cs + cs * 0.45;
        return farm.SweepLv95(tool, ce, cn - half, ce, cn + half, (float)(half * 2), seed);
    }

    /// <summary>
    /// The ripe cell ahead, harvested by hand (Gathering's hold): what it gave, at least
    /// <see cref="FarmTables.HandYieldMin"/>, put in the pack. (None, 0) when nothing was ripe.
    /// </summary>
    public (ItemId Id, int Count) HarvestAt(double e, double n)
    {
        if (FarmField.Instance is not { } farm || farm.CellAtLv95(e, n) is not { Stage: FieldStage.Ripe } view) return (ItemId.None, 0);
        var stroke = WorkAt(farm, FarmTool.Harvest, e, n, CropKind.None, 0);
        if (stroke.Cells <= 0) return (ItemId.None, 0);
        var id = FarmTables.YieldOf(view.Crop);
        int count = FarmRules.HandYield(view.Crop);
        LastHarvest = (id, count);
        if (_items != null && id != ItemId.None)
        {
            _items.Give(new ItemStack(id, count));
            _items.Ui.Toast($"+{count} {ItemDefs.Get(id)?.Name ?? id.ToString()}");
        }
        return LastHarvest;
    }

    /// <summary>The ripe crop ahead of a player (Gathering's offer), or null.</summary>
    public FieldCellView? RipeAhead(FootPlayer p, out double e, out double n)
    {
        (e, n) = Ahead(p);
        return FarmField.Instance?.CellAtLv95(e, n) is { Stage: FieldStage.Ripe } v && !FarmTables.IsGrass(v.Crop) ? v : null;
    }
}
