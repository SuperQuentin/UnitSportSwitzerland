using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Loot;

/// <summary>
/// Collecting from the land itself: <b>stone</b> on mapped rock, scree and quarries, <b>water</b>
/// at a lake, river or stream, <b>firewood</b> from a tree (or the floor of a wood). Hold Gather
/// (G / pad X) on foot outdoors; a bar fills, and moving away cancels it.
///
/// <para>
/// Everything is read from data the game already has, so what you can collect is what is really
/// there: the cover raster says where the rock and the lakes are, the <c>.road</c> tile holds the
/// streams the raster is too coarse to see, and the <c>.trees</c> file holds every tree.
/// </para>
///
/// <para>
/// <b>Local, like the inventory.</b> Natural resources are not contested the way a house's one
/// fridge is, so there is no server round-trip: each spot (an 8 m cell, or one tree) gives a few
/// harvests and then grows back after <see cref="RegrowSeconds"/>, remembered for the session only.
/// </para>
/// </summary>
public partial class Gathering : Node, Core.IOriginShiftAware
{
    public enum Resource { None, Stone, Water, TreeWood, Deadwood, Pumpkin, Treat, Crop }

    /// <summary>
    /// A picked spot comes back half a day of environment time later (#579): berries and firewood
    /// are a world process, so they regrow with the world rather than on the wall clock. At the
    /// default 24 min a day that is about 12 real minutes at 1x, where the old value was 20 real
    /// minutes flat.
    /// </summary>
    private const double RegrowSeconds = 12 * 3600;
    private const float TreeReach = 2.3f;
    private const float StreamReach = 1.6f;
    private const float CancelDistance = 0.9f;
    private const float SpotCell = 8f;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly ItemController _items;

    // what is where, per tile, loaded once through the (cached) chunk source
    private readonly Dictionary<TileId, Dictionary<long, List<Vector3>>?> _trees = new();
    private readonly Dictionary<TileId, List<RoadSegment>?> _streams = new();
    private readonly HashSet<TileId> _loading = new();

    // how much each spot has given: key → (harvests, first harvest time)
    private readonly Dictionary<string, (int Count, double Since)> _taken = new();

    // current target and the hold in progress
    private (Resource Kind, string Spot, CoverClass Cover) _target;
    private double _pollTimer;
    private double _progress;
    private Vector3 _startedAt;
    private double _tickTimer;

    private CanvasLayer _ui = null!;
    private Label _prompt = null!;
    private ProgressBar _bar = null!;
    private AudioStreamPlayer _sfx = null!;
    private readonly Random _rng = new();

    // a ripe field cell ahead (#494): where it is and what grows there
    private string _cropLabel = "";
    private double _cropE, _cropN;

    public Gathering(ChunkManager chunks, WorldOrigin origin, ItemController items)
    {
        _chunks = chunks;
        _origin = origin;
        _items = items;
    }

    public Gathering() : this(null!, null!, null!) { }

    /// <summary>Overrides who is gathering; probes set it to their own body.</summary>
    public Func<FootPlayer?>? PlayerOverride { get; set; }

    /// <summary>What the player can collect right now, for probes.</summary>
    public Resource Target => _target.Kind;
    /// <summary>The prompt line as shown, "[G] Hold to harvest wheat", for probes.</summary>
    public string Prompt => _prompt?.Text ?? "";

    /// <summary>The one in the client world, for the fishing rod's stream test (#493).</summary>
    public static Gathering? Instance { get; private set; }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// Whether <paramref name="ground"/> (a point on the ground) lies in a mapped stream: a watercourse line
    /// with no water surface to it (#493: a float cast into a brook). Loads the tile's streams on first use,
    /// so the first cast at a new tile may miss.
    /// </summary>
    public bool StreamAt(Vector3 ground)
    {
        var tile = _origin.TileAt(ground);
        EnsureLoaded(tile);
        return NearStream(tile, ground, ground);
    }

    public override void _Ready()
    {
        Name = "Gathering";
        _ui = new CanvasLayer { Layer = 9 };
        AddChild(_ui);

        _prompt = UiTheme.Prompt(-180);
        _ui.AddChild(_prompt);

        _bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, ShowPercentage = false, Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.6f) });
        _bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = new Color(0.95f, 0.78f, 0.25f) });
        _bar.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        _bar.Position = new Vector2(-90, -146);
        _bar.Size = new Vector2(180, 8);
        _ui.AddChild(_bar);

        _sfx = new AudioStreamPlayer { VolumeDb = -8, Bus = SfxBus.Name };
        AddChild(_sfx);
    }

    // ------------------------------------------------------------------------------------
    // per frame
    // ------------------------------------------------------------------------------------

    // what the prompt says now: rebuilt only when one of these changes
    private Resource _shownKind;
    private bool _shownDepleted;
    private string? _shownWhat, _shownKey;

    public override void _Process(double delta)
    {
        var p = Eligible();
        if (p == null)
        {
            _target = default;
            Cancel();
            _prompt.Visible = false;
            return;
        }

        _pollTimer -= delta;
        if (_pollTimer <= 0 && _progress <= 0)
        {
            _pollTimer = 0.15;
            _target = Find(p);
        }

        bool holding = PlayerInput.Held(PlayerInput.Gather);
        if (_target.Kind == Resource.None)
        {
            Cancel();
            _prompt.Visible = false;
            return;
        }

        string what = _target.Kind == Resource.Crop ? _cropLabel : _target.Kind == Resource.Treat
            ? Occasions.OccasionHunt.Instance?.LabelFor(_target.Spot) ?? "it"
            : Label(_target.Kind);
        bool depleted = Remaining(_target) <= 0;
        _prompt.Visible = _progress <= 0;
        // the line is built only when what it says changes (no string a frame, #221)
        string key = InputHints.Label(PlayerInput.Gather);
        if (_target.Kind != _shownKind || depleted != _shownDepleted || what != _shownWhat || !ReferenceEquals(key, _shownKey))
        {
            (_shownKind, _shownDepleted, _shownWhat, _shownKey) = (_target.Kind, depleted, what, key);
            _prompt.Text = depleted ? $"Nothing left to {Verb(_target.Kind)} here" : $"[{key}] Hold to {Verb(_target.Kind)} {what}";
        }

        if (!holding || depleted)
        {
            Cancel();
            return;
        }

        if (_progress <= 0) _startedAt = p.GlobalPosition;
        if (p.GlobalPosition.DistanceTo(_startedAt) > CancelDistance) { Cancel(); return; }

        _progress += delta / Duration(_target.Kind);
        _bar.Visible = true;
        _bar.Value = _progress;
        _tickTimer -= delta;
        if (_tickTimer <= 0)
        {
            _tickTimer = _target.Kind == Resource.Water ? 0.45 : 0.32;
            Play(_target.Kind == Resource.Stone ? SfxSynth.Impact : SfxSynth.Tick,
                _target.Kind switch { Resource.Stone => 1.6f, Resource.Water => 0.5f, _ => 0.8f } + (float)_rng.NextDouble() * 0.2f);
        }
        if (_progress >= 1) Complete();
    }

    /// <summary>The feet this deep in the water (m, <see cref="FootPlayer.WadeDepth"/>) to fill a bottle from it.</summary>
    public const float WadeToGather = 0.05f;

    private FootPlayer? Eligible()
    {
        var p = PlayerOverride?.Invoke() ?? _items.UsablePlayer;
        if (p == null || p.Indoors || !p.IsOnFloor() || UiFocus.TextEntryActive) return null;
        return p;
    }

    private void Cancel()
    {
        _progress = 0;
        _tickTimer = 0;
        _bar.Visible = false;
    }

    private void Complete()
    {
        var target = _target;
        Cancel();
        if (target.Kind == Resource.Crop)
        {
            // a ripe field cell (#494): the farm decides what it gives and turns it to stubble
            if (Farming.HandFarming.Instance?.HarvestAt(_cropE, _cropN) is { Count: > 0 }) Play(SfxSynth.Chime, 1.3f);
            return;
        }
        if (target.Kind == Resource.Treat)
        {
            // an occasion hunt spot: claimed once per player per occasion, and the reward is its own
            Occasions.OccasionHunt.Instance?.Claim(target.Spot, _items);
            Play(SfxSynth.Chime, 1.5f);
            return;
        }
        var (id, count) = Yield(target);
        int fit = Math.Min(count, _items.Inventory.Room(id));
        if (fit <= 0)
        {
            _items.Ui.Toast("No room in your pack.");
            return;
        }
        _items.Inventory.Add(id, fit);
        bool fresh = !_taken.TryGetValue(target.Spot, out var t) || Now - t.Since >= RegrowSeconds;
        _taken[target.Spot] = fresh ? (1, Now) : (t.Count + 1, t.Since);
        _items.Ui.Toast($"+{fit} {ItemDefs.Get(id)!.Name}");
        Play(SfxSynth.Chime, 1.3f);
    }

    private void Play(AudioStreamWav stream, float pitch)
    {
        _sfx.Stream = stream;
        _sfx.PitchScale = pitch;
        _sfx.Play();
    }

    /// <summary>Environment seconds (#579): regrowth keeps the world's pace.</summary>
    private static double Now => World.WorldClock.EnvNow;

    // ------------------------------------------------------------------------------------
    // rules
    // ------------------------------------------------------------------------------------

    private static string Label(Resource r) => r switch
    {
        Resource.Stone => "stones",
        Resource.Water => "water",
        Resource.TreeWood => "firewood",
        Resource.Pumpkin => "a pumpkin",
        Resource.Crop => "the crop",
        _ => "dead wood",
    };

    private static string Verb(Resource r) => r switch
    {
        Resource.Water => "fill up with",
        Resource.TreeWood => "chop",
        Resource.Pumpkin => "pick",
        Resource.Crop => "harvest",
        Resource.Treat => "take",
        _ => "gather",
    };

    private static double Duration(Resource r) => r switch
    {
        Resource.Water => 1.2,
        Resource.Stone => 1.6,
        Resource.TreeWood => 2.2,
        Resource.Pumpkin => 1.0,
        Resource.Crop => 1.4,
        Resource.Treat => 0.6,
        _ => 1.4,
    };

    /// <summary>Harvests a spot gives before it needs to grow back. Water never runs out.</summary>
    private static int Capacity(Resource r) => r switch
    {
        Resource.Water => int.MaxValue,
        Resource.Stone => 4,
        Resource.TreeWood => 2,
        Resource.Pumpkin => 3,
        Resource.Crop => int.MaxValue,   // the cell itself turns to stubble
        Resource.Treat => 1,
        _ => 2,
    };

    private int Remaining((Resource Kind, string Spot, CoverClass Cover) t)
    {
        if (!_taken.TryGetValue(t.Spot, out var taken) || Now - taken.Since >= RegrowSeconds) return Capacity(t.Kind);
        return Capacity(t.Kind) - taken.Count;
    }

    private (ItemId Id, int Count) Yield((Resource Kind, string Spot, CoverClass Cover) t)
    {
        // in a Battle Royale match (#276) the land gives building material: planks off a tree, more stone
        if (BattleRoyale.BrManager.Instance?.InMatch == true)
            switch (t.Kind)
            {
                case Resource.TreeWood: return (ItemId.WoodPlanks, 3);
                case Resource.Deadwood: return (ItemId.WoodPlanks, 1);
                case Resource.Stone: return (ItemId.Stone, 4);
            }
        switch (t.Kind)
        {
            case Resource.Water:
                return (ItemId.WaterBottle, 1);
            case Resource.TreeWood:
                // a Swiss army knife in the pack (#273): its saw gets one more log out of every tree
                return (ItemId.Firewood, _rng.Next(2, 5) + (_items.Inventory.Contains(ItemId.SwissArmyKnife) ? 1 : 0));
            case Resource.Deadwood:
                return (ItemId.Firewood, _rng.Next(1, 3));
            case Resource.Pumpkin:
                return (ItemId.Pumpkin, 1);
            default:
                // loose ground gives gravel as well as stones; a quarry gives the most
                bool loose = t.Cover is CoverClass.Scree or CoverClass.LooseScree or CoverClass.LooseRock or CoverClass.Quarry;
                if (loose && _rng.NextDouble() < (t.Cover == CoverClass.Quarry ? 0.35 : 0.2))
                    return (ItemId.SandBag, 1);
                return (ItemId.Stone, _rng.Next(t.Cover == CoverClass.Quarry ? 2 : 1, 4));
        }
    }

    private static bool IsStony(CoverClass c) => c is CoverClass.Rock or CoverClass.LooseRock or CoverClass.Scree
        or CoverClass.LooseScree or CoverClass.Boulders or CoverClass.Quarry;

    // ------------------------------------------------------------------------------------
    // what is in front of the player
    // ------------------------------------------------------------------------------------

    private (Resource, string, CoverClass) Find(FootPlayer p)
    {
        var feet = p.GlobalPosition;
        var fwd = -p.Camera.GlobalTransform.Basis.Z;
        fwd.Y = 0;
        fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
        var ahead = feet + fwd * 1.2f;
        var tile = _origin.TileAt(feet);
        EnsureLoaded(tile);

        // water: only standing in it, wading (#380): not from a boat's deck, a pier, a bridge or the
        // shore (the prompt showed over any water in reach, aboard the steamer included). A mapped
        // stream (a line, no surface to stand in) from its bank, on the ground.
        if (p.WadeDepth > WadeToGather) return (Resource.Water, "water", CoverClass.Water);
        if (p.DeckOn == "" && !World.WaterField.TryLevelAt(feet, out _) && NearStream(tile, feet, ahead))
            return (Resource.Water, "water", CoverClass.Water);

        // a running occasion: its hunt spot by a door, or a pumpkin patch underfoot
        if (Occasions.OccasionHunt.Instance?.SpotNear(feet, ahead) is { } hunt)
            return (Resource.Treat, hunt, CoverClass.Open);
        if (Occasions.OccasionDecor.Instance is { } decor && (decor.InPatch(ahead) || decor.InPatch(feet)))
            return (Resource.Pumpkin, Spot("pumpkin", ahead), CoverClass.Open);

        // a ripe field cell ahead (#494): harvested by hand
        if (Farming.HandFarming.Instance?.RipeAhead(p, out _cropE, out _cropN) is { } ripe)
        {
            _cropLabel = Farming.FarmRules.CropName(ripe.Crop).ToLowerInvariant();
            return (Resource.Crop, Spot("crop", ahead), CoverClass.Open);
        }

        // a tree in reach, the nearest one
        if (NearestTree(tile, feet, ahead) is { } tree)
        {
            var (te, tn) = _origin.ToLv95(tree);
            return (Resource.TreeWood, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tree:{te:F1}:{tn:F1}"), CoverClass.Forest);
        }

        // stone underfoot or just ahead
        foreach (var at in new[] { ahead, feet })
            if (_chunks.TryGetCover(at, out var c) && IsStony(c))
                return (Resource.Stone, Spot("stone", at), c);

        // the floor of a wood: fallen branches, less than a tree gives
        if (_chunks.TryGetCover(feet, out var here) && CoverFormat.IsWooded(here))
            return (Resource.Deadwood, Spot("dead", feet), here);

        return (Resource.None, "", CoverClass.Open);
    }

    /// <summary>
    /// A spot's name, from its cell of the LV95 grid: the same place has the same name whatever the
    /// origin is (#185), on every client.
    /// </summary>
    private string Spot(string kind, Vector3 at)
    {
        var (e, n) = _origin.ToLv95(at);
        return $"{kind}:{(long)Math.Floor(e / SpotCell)}:{(long)Math.Floor(n / SpotCell)}";
    }

    private void EnsureLoaded(TileId tile)
    {
        if (_trees.ContainsKey(tile) || !_loading.Add(tile) || _chunks.Source is not { } source) return;
        LoadTile(source, tile);
    }

    private int _epoch;

    /// <summary>
    /// Drops what was read from the tiles, for when the world under them is replaced (the
    /// generated fallback retiring). Loads already in flight are discarded when they land.
    /// </summary>
    public void Forget()
    {
        _epoch++;
        _trees.Clear();
        _streams.Clear();
        _loading.Clear();
        _taken.Clear();   // keyed by world cell, which a rebase gives a new meaning
        _target = default;
        _progress = 0;
    }

    private async void LoadTile(IChunkSource source, TileId tile)
    {
        int epoch = _epoch;
        try
        {
            var trees = await source.LoadTreesAsync(tile);
            var roads = await source.LoadRoadsAsync(tile);
            // bucket the trees into 10 m cells of the tile, relative to its NW corner (as the file
            // has them), so an origin shift (#185) leaves the index be: a tile holds tens of thousands
            var index = trees == null ? null : await Task.Run(() =>
            {
                var cells = new Dictionary<long, List<Vector3>>();
                foreach (var t in trees)
                {
                    var local = new Vector3(t.X, t.Y, t.Z);
                    long key = CellKey(local);
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new();
                    list.Add(local);
                }
                return cells;
            });
            if (epoch != _epoch) return;
            _trees[tile] = index;
            _streams[tile] = roads?.Segments
                .Where(s => s.Class is RoadClass.Watercourse or RoadClass.Bisse && (s.Flags & RoadFlags.Tunnel) == 0)
                .ToList();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[gather] tile {tile}: {e.Message}");
            if (epoch == _epoch) _trees[tile] = null;
        }
        finally { if (epoch == _epoch) _loading.Remove(tile); }
    }

    /// <summary>A 10 m cell of a tile, from a point relative to the tile's NW corner.</summary>
    private static long CellKey(Vector3 local) =>
        ((long)Mathf.FloorToInt(local.X / 10f) << 32) ^ (uint)Mathf.FloorToInt(local.Z / 10f);

    private Vector3? NearestTree(TileId tile, Vector3 feet, Vector3 ahead)
    {
        if (!_trees.TryGetValue(tile, out var cells) || cells == null) return null;
        // the index is relative to the tile: so is the search, and the answer goes back to world
        var corner = _origin.ToWorld(tile.MinE, tile.MaxN, 0);
        feet -= corner;
        ahead -= corner;
        Vector3? best = null;
        float bestD = TreeReach;
        int cx = Mathf.FloorToInt(ahead.X / 10f), cz = Mathf.FloorToInt(ahead.Z / 10f);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                long key = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                if (!cells.TryGetValue(key, out var list)) continue;
                foreach (var t in list)
                {
                    if (Mathf.Abs(t.Y - feet.Y) > 3f) continue;
                    // measured from wherever is closer, the feet or a step ahead, so a tree
                    // beside the player counts as well as one in front
                    float d = Mathf.Min(MathX.FlatLength(t - feet), MathX.FlatLength(t - ahead));
                    if (d < bestD) { bestD = d; best = t; }
                }
            }
        return best + corner;
    }

    private bool NearStream(TileId tile, Vector3 feet, Vector3 ahead)
    {
        if (!_streams.TryGetValue(tile, out var segs) || segs == null) return false;
        var (e, n) = _origin.ToLv95(ahead);
        double lx = e - tile.MinE, lz = tile.MaxN - n;
        foreach (var s in segs)
        {
            float reach = s.Width / 2 + StreamReach;
            var pts = s.Points;
            for (int i = 0; i + 5 < pts.Length; i += 3)
            {
                double ax = pts[i], az = pts[i + 2], bx = pts[i + 3], bz = pts[i + 5];
                double vx = bx - ax, vz = bz - az, len2 = vx * vx + vz * vz;
                double u = len2 > 1e-9 ? Math.Clamp(((lx - ax) * vx + (lz - az) * vz) / len2, 0, 1) : 0;
                double px = ax + vx * u - lx, pz = az + vz * u - lz;
                if (px * px + pz * pz > reach * reach) continue;
                float y = pts[i + 1] + (float)u * (pts[i + 4] - pts[i + 1]);
                if (feet.Y - y < 2.5f && y - feet.Y < 1f) return true;
            }
        }
        return false;
    }


    /// <summary>The origin moved (#185): where the current harvest started moved with it.</summary>
    public void OnOriginShifted(Core.OriginShift shift) => _startedAt = shift.Point(_startedAt);

    /// <summary>For probes: runs one full harvest of whatever is in front of the player, instantly.</summary>
    public bool DebugHarvest(FootPlayer p)
    {
        _target = Find(p);
        if (_target.Kind == Resource.None || Remaining(_target) <= 0) return false;
        Complete();
        return true;
    }
}
