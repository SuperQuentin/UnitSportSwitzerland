using System.IO;
using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Birds;

/// <summary>
/// The birds around the player, and the hunt.
///
/// <para>
/// <b>Spawned from real data.</b> Every few tenths of a second a point 35–150 m away is sampled
/// for its land cover (<see cref="ChunkManager.TryGetCover"/>) and altitude, turned into a
/// <see cref="Habitat"/>, and a species is drawn from <see cref="BirdCatalog"/> weighted by
/// abundance, by the calendar month (a swift in January is not a thing) and by the hour (owls at
/// night). Flocking species arrive as a flock. Woodland birds perch on real trees from the
/// <c>.trees</c> tile, waterfowl swim at the water line, raptors circle on thermals, swifts hawk
/// for insects, kestrels hover.
/// </para>
///
/// <para>
/// <b>Shared</b> (#143): online, the dedicated server owns the birds (<see cref="Headless"/>: it spawns
/// around every player, steps them, flushes and kills them) and <see cref="BirdNet"/> sends each
/// peer the ones near it; a client's birds are puppets that follow those snapshots. A shot, a gun
/// round or a strike is only a <i>report</i> to the server, which validates it and tells everyone.
/// Offline the same class is its own authority. There are never more than <see cref="Budget"/> birds
/// around one player; a bird more than <see cref="DespawnDistance"/> from everyone is removed.
/// </para>
///
/// <para>
/// <b>Hunting</b>: the shotgun item (<see cref="ItemController.Fire"/>) casts a widening cone
/// from the camera; the nearest bird inside it, not behind a wall, falls. The field journal
/// scores it: a game species in season earns points, anything protected costs a heavy penalty.
/// Every shot flushes every bird within <see cref="ShotFlushRadius"/>.
/// </para>
/// </summary>
public partial class BirdLife : Node3D, Core.IOriginContainer, Core.IOriginShiftAware
{
    public const int Budget = 32;
    private const float SpawnMin = 35f, SpawnMax = 150f;
    public const float DespawnDistance = 240f;
    public const float Range = 55f;
    private const float ShotFlushRadius = 150f;
    private const float SeenRange = 70f;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly ItemController _items;
    private readonly List<Bird> _birds = new();
    private readonly Dictionary<int, Bird> _byId = new();
    private int _nextId;
    private readonly Random _rng = new();
    /// <summary>Tree tops per tile, relative to the tile's NW corner (so an origin shift leaves them be),
    /// bucketed in <see cref="TreeCell"/> m cells (a forest tile holds 100k+).</summary>
    private readonly Dictionary<TileId, Dictionary<(int, int), List<Vector3>>?> _trees = new();
    private const float TreeCell = 16f;
    /// <summary>Server only: the lean server's terrain keeps no cover raster, so the birds load their own per tile.</summary>
    private readonly Dictionary<TileId, byte[]?> _cover = new();
    private readonly HashSet<TileId> _coverLoading = new();
    private readonly HashSet<TileId> _loading = new();
    /// <summary>Building perches and density per tile (<see cref="TownPerches"/>), loaded like the trees.</summary>
    private readonly Dictionary<TileId, TownPerches?> _town = new();
    private readonly HashSet<TileId> _townLoading = new();
    /// <summary>Where each flock is settling: the first of a flock to land picks the spot, the rest land around it.</summary>
    private readonly Dictionary<int, (Vector3 At, double Until)> _flockSpots = new();
    private int _nextFlock;
    private double _clock;
    private readonly List<Perch> _perchScratch = new();
    private readonly List<Observer> _localWho = new(1);
    private Droppings? _droppings;
    private AudioStreamPlayer3D _call = null!;
    private double _spawnTimer;
    private double _callCooldown;

    /// <summary>This client's birds, for the aircraft guns (<c>CombatManager</c>).</summary>
    public static BirdLife? Instance { get; private set; }

    /// <summary>Airborne birds try to get out of an aircraft's way; probes switch it off.</summary>
    public bool EvadeAircraft { get; set; } = true;

    private double _aerialTimer;
    private readonly HashSet<Bird> _rolled = new();

    /// <summary>The dedicated server's copy: no meshes shown, sounds, journal or camera; it simulates for everyone.</summary>
    public bool Headless { get; init; }

    /// <summary>Server: fills the list with where the players are (position, velocity, in the air), for spawning, flushing and droppings.</summary>
    public Action<List<Observer>>? Observers { get; set; }
    private readonly List<Observer> _who = new();

    public readonly record struct Observer(Vector3 Position, Vector3 Velocity, bool Flying, long Peer = 1);

    /// <summary>The network side, set by <see cref="BirdNet"/>.</summary>
    public BirdNet? Net { get; set; }

    /// <summary>True when this instance decides what the birds do: the server, or a game with no server.</summary>
    public bool Authority => Headless || Net == null || !Net.Online;

    public BirdJournal Journal { get; private set; } = null!;
    public IReadOnlyList<Bird> Birds => _birds;

    /// <summary>Calendar month used for presence and hunting seasons; <c>--birdmonth N</c> overrides it.</summary>
    public int Month { get; set; } = DateTime.Now.Month;

    /// <summary>False stops the automatic spawning (the probe places its own birds).</summary>
    public bool AutoSpawn { get; set; } = true;

    /// <summary>Overrides who the birds react to; probes set it to their own body.</summary>
    public Func<FootPlayer?>? PlayerOverride { get; set; }

    /// <summary>This peer's origin: the birds fly in world space, <see cref="BirdNet"/> sends them in LV95 (#185).</summary>
    public WorldOrigin Origin => _origin;

    public BirdLife(ChunkManager chunks, WorldOrigin origin, ItemController? items)
    {
        _chunks = chunks;
        _origin = origin;
        _items = items!;
    }

    public BirdLife() : this(null!, null!, null) { }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        Name = "Birds";
        if (Headless) return;
        Instance = this;
        Journal = new BirdJournal();
        AddChild(Journal);
        _call = new AudioStreamPlayer3D { Name = "Call", Bus = SfxBus.Name, UnitSize = 12f, VolumeDb = -4f };
        AddChild(_call);
        _droppings = new Droppings();
        AddChild(_droppings);
        _items.Fire = Fire;

        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--birdmonth");
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int month) && month is >= 1 and <= 12)
            Month = month;

        // Hunting season opens: anyone without a gun gets one, including an existing save,
        // which never saw the starter kit that would otherwise have carried it.
        var inv = _items.Inventory;
        bool hasGun = false;
        for (int i = 0; i < Inventory.Size; i++) hasGun |= inv[i].Id == ItemId.Shotgun;
        if (!hasGun)
        {
            _items.Give(new ItemStack(ItemId.Shotgun, 1));
            _items.Give(new ItemStack(ItemId.Shells, 25));
        }
    }

    private FootPlayer? Player => PlayerOverride?.Invoke() ?? _items.UsablePlayer;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        if (Headless) { ProcessServer(dt); return; }
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        var player = Player;
        var focus = player?.GlobalPosition ?? cam.GlobalPosition;
        // online, only the server's birds exist: any of this client's own (spawned before it was
        // connected) go, or a server bird with the same number would be taken for one of them
        if (!Authority)
            for (int i = _birds.Count - 1; i >= 0; i--)
                if (!_birds[i].Remote) RemoveAt(i);

        _spawnTimer -= delta;
        _callCooldown -= delta;
        if (Authority && AutoSpawn && _spawnTimer <= 0 && _birds.Count < Budget)
        {
            _spawnTimer = 0.4;
            TrySpawn(focus);
        }
        // Flying fast, the birds that matter are the ones along the flight path, at the height they
        // really fly: without this every bird is near the ground behind a plane that left it.
        _aerialTimer -= delta;
        if (Authority && AutoSpawn && player is { IsFlying: true } flyer && flyer.GroundSpeed > 12f
            && _aerialTimer <= 0 && _birds.Count < Budget)
        {
            _aerialTimer = AerialInterval;
            SpawnAhead(flyer);
        }
        if (player is { IsFlying: true } pilot) CheckStrikes(pilot);

        // binoculars see much further than the naked eye
        float seen = cam.Fov < 20f ? 300f : SeenRange;
        _clock += delta;
        _localWho.Clear();
        if (player != null) _localWho.Add(new Observer(player.GlobalPosition, player.Velocity, player.IsFlying, Multiplayer.GetUniqueId()));
        for (int i = _birds.Count - 1; i >= 0; i--)
        {
            var b = _birds[i];
            b.Step(dt, this, player?.GlobalPosition);
            if (Authority) MaybeDrop(b, dt, _localWho);
            var p = b.Node.GlobalPosition;
            // a puppet lives as long as the server keeps sending it; its own birds go when far
            if (b.Gone || (!b.Remote && Flat(p - focus) > DespawnDistance))
            {
                RemoveAt(i);
                continue;
            }
            if (!b.Seen && b.State != Bird.Mode.Dead && cam.GlobalPosition.DistanceTo(p) < seen && cam.IsPositionInFrustum(p))
            {
                b.Seen = true;
                Journal.Seen(b.Species);
            }
        }
    }

    /// <summary>Takes a bird out without freeing its node (probes free their own).</summary>
    public void Remove(Bird b) { _birds.Remove(b); _byId.Remove(b.Id); }

    private void RemoveAt(int i)
    {
        var b = _birds[i];
        b.Node.QueueFree();
        _byId.Remove(b.Id);
        _birds.RemoveAt(i);
    }

    /// <summary>Most birds the server keeps for all players together.</summary>
    private const int MaxTotal = 256;
    private int _roundRobin;
    private double _forgetTimer;

    /// <summary>The server's tick: spawn around one player at a time, step everything, drop what nobody is near.</summary>
    private void ProcessServer(float dt)
    {
        var who = _who;
        who.Clear();
        Observers?.Invoke(who);
        _clock += dt;
        _forgetTimer -= dt;
        if (_forgetTimer <= 0) { _forgetTimer = 10; ForgetFarTiles(who); }
        _spawnTimer -= dt;
        if (AutoSpawn && who.Count > 0 && _spawnTimer <= 0 && _birds.Count < MaxTotal)
        {
            _spawnTimer = 0.4 / Math.Max(1, Math.Min(who.Count, 4));
            var o = who[_roundRobin++ % who.Count];
            int near = 0;
            foreach (var b in _birds) if (Flat(b.Node.GlobalPosition - o.Position) < DespawnDistance) near++;
            if (near < Budget) TrySpawn(o.Position, Budget - near);
            if (o.Flying && Flat(o.Velocity) > 12f && near < Budget) SpawnAhead(o.Position, o.Velocity, Budget - near);
        }
        for (int i = _birds.Count - 1; i >= 0; i--)
        {
            var b = _birds[i];
            var at = b.Node.GlobalPosition;
            Vector3? nearest = null;
            float best = float.MaxValue;
            foreach (var o in who)
            {
                float d = Flat(at - o.Position);
                if (d < best) { best = d; nearest = o.Position; }
            }
            b.Step(dt, this, nearest);
            if (b.Gone || best > DespawnDistance) { RemoveAt(i); continue; }
            MaybeDrop(b, dt, who);
        }
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>The ground (or water surface) under a point, or the point's own height when the tile is not loaded.</summary>
    public float Ground(Vector3 p) => _chunks.TryGetHeight(p, out float h) ? h : p.Y;

    // ------------------------------------------------------------------------------------
    // habitat and species
    // ------------------------------------------------------------------------------------

    public static Habitat HabitatAt(CoverClass c, float altitude) => c switch
    {
        CoverClass.Forest => Habitat.Forest,
        CoverClass.OpenForest => Habitat.Forest | Habitat.Edge,
        CoverClass.Woodland or CoverClass.Shrub or CoverClass.Clearcut => Habitat.Edge,
        CoverClass.Park or CoverClass.Cemetery or CoverClass.Allotment => Habitat.Edge | Habitat.Town,
        CoverClass.Rock or CoverClass.LooseRock or CoverClass.Scree or CoverClass.LooseScree
            or CoverClass.Boulders or CoverClass.Quarry => Habitat.Rock,
        CoverClass.Water => Habitat.Water,
        CoverClass.Wetland => Habitat.Wetland,
        CoverClass.Glacier or CoverClass.Snowfield => Habitat.Snow,
        CoverClass.Vineyard => Habitat.Vineyard,
        CoverClass.Orchard or CoverClass.Nursery => Habitat.Orchard,
        CoverClass.ParkingPublic or CoverClass.ParkingPrivate or CoverClass.RestArea or CoverClass.PavedArea or CoverClass.TownPaving or CoverClass.TunnelRoof
            or CoverClass.Institution or CoverClass.Industrial or CoverClass.Landfill or CoverClass.SportsField
            or CoverClass.Pool or CoverClass.Campsite or CoverClass.Leisure or CoverClass.Runway
            or CoverClass.Platform => Habitat.Town,
        // unmapped open ground: TLM has no farmland, so altitude decides what it is
        _ => altitude < 1000f ? Habitat.Farm : altitude < 1900f ? Habitat.Meadow : Habitat.Alpine,
    };

    /// <summary>A species for this habitat, altitude, month and hour, weighted by abundance; null if none fits.</summary>
    public BirdSpecies? Pick(Habitat habitat, float altitude)
    {
        double hour = World.DayNight.Instance?.Hour ?? 12.0;
        bool night = hour < 5.5 || hour > 21.5;
        double total = 0;
        Span<double> weights = stackalloc double[BirdCatalog.All.Length];
        for (int i = 0; i < BirdCatalog.All.Length; i++)
        {
            var s = BirdCatalog.All[i];
            double w = 0;
            if ((s.Habitat & habitat) != 0 && altitude >= s.MinAltitude - 100 && altitude <= s.MaxAltitude + 100)
                w = s.Abundance * s.PresenceIn(Month) * ((s.Body == BodyPlan.Owl) == night ? 1.0 : 0.1);
            weights[i] = w;
            total += w;
        }
        if (total <= 0) return null;
        double r = _rng.NextDouble() * total;
        for (int i = 0; i < weights.Length; i++)
            if ((r -= weights[i]) <= 0 && weights[i] > 0) return BirdCatalog.All[i];
        return null;
    }

    /// <summary>
    /// One spawn attempt at a random point around <paramref name="focus"/>: its habitat picks a
    /// species, the species' flock size picks how many. Returns the habitat tried (None if the
    /// ground there is not loaded yet) and how many birds appeared.
    /// </summary>
    public (Habitat Habitat, int Count) TrySpawn(Vector3 focus, int? room = null)
    {
        int free = room ?? Budget - _birds.Count;
        float a = (float)(_rng.NextDouble() * Mathf.Tau);
        float d = Mathf.Lerp(SpawnMin, SpawnMax, (float)_rng.NextDouble());
        var p = focus + new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
        if (!_chunks.TryGetHeight(p, out float ground) || !CoverAt(p, out var cover)) return (Habitat.None, 0);
        p.Y = ground;
        var habitat = HabitatAt(cover, ground);
        var tile = _origin.TileAt(p);
        EnsureTrees(tile);
        EnsureTown(tile);
        // wait for the buildings: spawning before they are read filled a town with farm birds that
        // then held the whole budget (2 town birds of 32 at Sion)
        if (_townLoading.Contains(tile)) return (Habitat.None, 0);
        // the cover reads a village as farmland: the buildings say it is a town. Most of a town's
        // birds are its own (pigeons, sparrows, swifts) on roofs, ledges and streets; the rest are
        // the garden birds of the Town habitat.
        if (TownAt(p) is { } town && town.BuildingsAround(p) >= TownBuildings && habitat is not (Habitat.Water or Habitat.Forest))
        {
            if (_rng.NextDouble() < 0.75) return (Habitat.Town, TownSpawn(p, town, free));
            habitat = Habitat.Town;
        }
        var s = Pick(habitat, ground);
        if (s == null) return (habitat, 0);

        int n = Math.Min(1 + _rng.Next(Math.Max(1, s.Flock)), free);
        var mode = ChooseMode(s, habitat);
        int flock = ++_nextFlock;
        for (int i = 0; i < n; i++)
        {
            float spread = 1.5f + n * 0.6f;
            var at = p + new Vector3((float)(_rng.NextDouble() * 2 - 1) * spread, 0, (float)(_rng.NextDouble() * 2 - 1) * spread);
            at.Y = Ground(at);
            Spawn(s, at, mode).Flock = flock;
        }
        return (habitat, n);
    }

    private Bird.Mode ChooseMode(BirdSpecies s, Habitat h)
    {
        if (s.Flight == FlightStyle.Soar) return Bird.Mode.Soaring;
        if (s.Flight == FlightStyle.Hover) return _rng.NextDouble() < 0.7 ? Bird.Mode.Hovering : Bird.Mode.Flying;
        if (s.Flight == FlightStyle.Dart) return Bird.Mode.Flying;
        bool swimmer = s.Body is BodyPlan.Waterfowl || (s.Body is BodyPlan.Gull && _rng.NextDouble() < 0.5);
        if (h == Habitat.Water && swimmer) return Bird.Mode.Swimming;
        if (_rng.NextDouble() < 0.25) return Bird.Mode.Flying;
        bool percher = s.Body is BodyPlan.Passerine or BodyPlan.Corvid or BodyPlan.Pigeon or BodyPlan.Woodpecker
            or BodyPlan.Raptor or BodyPlan.Owl;
        if (percher && (h & (Habitat.Forest | Habitat.Edge | Habitat.Orchard | Habitat.Town)) != 0) return Bird.Mode.Perched;
        return Bird.Mode.Ground;
    }

    /// <summary>
    /// Puts one bird in the world. Perched birds go to the top of the nearest real tree, if there
    /// is one; <paramref name="lift"/> fixes a hovering, soaring or flying bird's height above
    /// <paramref name="at"/> instead of drawing one.
    /// </summary>
    public Bird Spawn(BirdSpecies s, Vector3 at, Bird.Mode mode, float? lift = null, bool town = false, float? yaw = null)
    {
        if (mode == Bird.Mode.Perched && !town)
        {
            if (NearestTreeTop(at, 12f) is { } top) at = top;
            else mode = Bird.Mode.Ground;
        }
        var bird = new Bird(s, (float)_rng.NextDouble(), visual: !Headless) { Id = ++_nextId, Town = town };
        AddChild(bird.Node);
        bird.Begin(mode, at, this, _rng, lift, yaw);
        _birds.Add(bird);
        _byId[bird.Id] = bird;
        return bird;
    }

    // ------------------------------------------------------------------------------------
    // perches: the .trees tile, loaded once per tile through the cached source
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The land cover under a point. A client has it from its terrain; the dedicated server's
    /// terrain is coarse and keeps none (<c>lean-dedicated-server</c>), so the server loads the
    /// raster of a tile the first time a bird is spawned on it, and drops it when nobody is near.
    /// </summary>
    private bool CoverAt(Vector3 p, out CoverClass cover)
    {
        if (_chunks.TryGetCover(p, out cover)) return true;
        if (!Headless || _chunks.Source is not { } source) return false;
        var tile = _origin.TileAt(p);
        if (!_cover.TryGetValue(tile, out var raster))
        {
            if (_coverLoading.Add(tile)) LoadCover(source, tile);
            return false;
        }
        var (e, n) = _origin.ToLv95(p);
        int col = (int)Math.Round(e - tile.MinE), row = (int)Math.Round(tile.MaxN - n);
        int i = row * ChunkFormat.GridSize + col;
        if (raster == null || (uint)col >= ChunkFormat.GridSize || (uint)row >= ChunkFormat.GridSize || i >= raster.Length) return false;
        cover = (CoverClass)raster[i];
        return true;
    }

    private async void LoadCover(IChunkSource source, TileId tile)
    {
        try { _cover[tile] = await source.LoadCoverAsync(tile); }
        catch (Exception e)
        {
            GD.PushWarning($"[birds] cover {tile}: {e.Message}");
            _cover[tile] = null;
        }
        finally { _coverLoading.Remove(tile); }
    }

    /// <summary>Server: forgets the cover and trees of tiles no player is within 2 km of.</summary>
    private void ForgetFarTiles(IReadOnlyList<Observer> who)
    {
        bool Near(TileId t)
        {
            var centre = _origin.ToWorld(t.MinE + 500, t.MaxN - 500, 0);
            foreach (var o in who) if (Flat(o.Position - centre) < 2000f) return true;
            return false;
        }
        foreach (var t in _cover.Keys.Where(t => !Near(t)).ToList()) _cover.Remove(t);
        foreach (var t in _trees.Keys.Where(t => !Near(t)).ToList()) _trees.Remove(t);
        foreach (var t in _town.Keys.Where(t => !Near(t)).ToList()) _town.Remove(t);
        foreach (var f in _flockSpots.Where(f => f.Value.Until < _clock).Select(f => f.Key).ToList()) _flockSpots.Remove(f);
    }

    private void EnsureTrees(TileId tile)
    {
        if (_trees.ContainsKey(tile) || !_loading.Add(tile) || _chunks.Source is not { } source) return;
        LoadTrees(source, tile);
    }

    private async void LoadTrees(IChunkSource source, TileId tile)
    {
        try
        {
            var trees = await source.LoadTreesAsync(tile);
            _trees[tile] = trees == null ? null : await Task.Run(() =>
            {
                var cells = new Dictionary<(int, int), List<Vector3>>();
                foreach (var t in trees)
                {
                    var top = new Vector3(t.X, t.Y + t.Height * 0.92f, t.Z);
                    var key = Cell(top);
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<Vector3>();
                    list.Add(top);
                }
                return cells;
            });
        }
        catch (Exception e)
        {
            GD.PushWarning($"[birds] trees {tile}: {e.Message}");
            _trees[tile] = null;
        }
        finally { _loading.Remove(tile); }
    }

    private static (int, int) Cell(Vector3 p) => ((int)Mathf.Floor(p.X / TreeCell), (int)Mathf.Floor(p.Z / TreeCell));

    /// <summary>The nearest tree top within <paramref name="reach"/> (at most <see cref="TreeCell"/>) on this tile.</summary>
    private Vector3? NearestTreeTop(Vector3 at, float reach)
    {
        var tile = _origin.TileAt(at);
        if (!_trees.TryGetValue(tile, out var cells) || cells == null) return null;
        var corner = _origin.ToWorld(tile.MinE, tile.MaxN, 0);
        var local = at - corner;
        Vector3? best = null;
        float bestD = reach;
        var (cx, cz) = Cell(local);
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!cells.TryGetValue((cx + dx, cz + dz), out var tops)) continue;
                foreach (var t in tops)
                {
                    float d = Flat(t - local);
                    if (d < bestD) { bestD = d; best = t; }
                }
            }
        return best + corner;
    }

    /// <summary>The origin moved (#185): the birds' nodes have moved with it, their flight plans have not.</summary>
    public void OnOriginShifted(Core.OriginShift shift)
    {
        foreach (var b in _birds) b.Shift(shift);
        foreach (var f in _flockSpots.Keys.ToList()) _flockSpots[f] = (shift.Point(_flockSpots[f].At), _flockSpots[f].Until);
        // ponytail: town perches are built in world space; reload them rather than shift every perch (shifts are rare)
        _town.Clear();
    }

    // ------------------------------------------------------------------------------------
    // towns: perches on the buildings, the town birds, landing, droppings (#143)
    // ------------------------------------------------------------------------------------

    /// <summary>Real buildings (no barns or sheds) within the 192 m square around a point that make it a town.</summary>
    public const int TownBuildings = 14;

    /// <summary>
    /// A town's own birds and where they sit: the weight (before the calendar), then the share on
    /// roofs (ridges and eaves), on ledges, in the street and in the air. Feral pigeons first, then
    /// what a Swiss town really has (Vogelwarte atlas): sparrows, crows and jackdaws, black redstarts
    /// singing from the ridges, collared doves, blackbirds, wagtails and starlings; swifts and house
    /// martins over the roofs in summer (by <see cref="BirdSpecies.PresenceIn"/>); gulls only near water.
    /// </summary>
    private static readonly (BirdSpecies Species, float Weight, float Roof, float Ledge, float Street, float Air, bool Water)[] TownBirds =
    {
        (Named("Rock Dove"), 40, 0.40f, 0.25f, 0.30f, 0.05f, false),
        (Named("House Sparrow"), 22, 0.25f, 0.10f, 0.60f, 0.05f, false),
        (Named("Carrion Crow"), 7, 0.50f, 0f, 0.45f, 0.05f, false),
        (Named("Western Jackdaw"), 6, 0.55f, 0.15f, 0.25f, 0.05f, false),
        (Named("Black Redstart"), 7, 0.75f, 0.20f, 0.05f, 0f, false),
        (Named("Eurasian Collared Dove"), 5, 0.65f, 0.05f, 0.30f, 0f, false),
        (Named("Common Blackbird"), 6, 0.40f, 0f, 0.60f, 0f, false),
        (Named("White Wagtail"), 3, 0.20f, 0f, 0.80f, 0f, false),
        (Named("Common Starling"), 3, 0.70f, 0.10f, 0.20f, 0f, false),
        (Named("Eurasian Magpie"), 2, 0.50f, 0f, 0.50f, 0f, false),
        (Named("Common Swift"), 14, 0f, 0f, 0f, 1f, false),
        (Named("Alpine Swift"), 2, 0f, 0f, 0f, 1f, false),
        (Named("Western House Martin"), 4, 0f, 0f, 0f, 1f, false),
        (Named("Yellow-legged Gull"), 6, 0.55f, 0f, 0.15f, 0.30f, true),
        (Named("Black-headed Gull"), 6, 0.40f, 0f, 0.25f, 0.35f, true),
    };

    private static BirdSpecies Named(string name) => BirdCatalog.All.First(s => s.Name == name);

    private void EnsureTown(TileId tile)
    {
        if (_town.ContainsKey(tile) || !_townLoading.Add(tile) || _chunks.Source is not { } source) return;
        LoadTown(source, tile);
    }

    private async void LoadTown(IChunkSource source, TileId tile)
    {
        try
        {
            var buildings = await source.LoadBuildingsAsync(tile);
            var origin = _origin;
            _town[tile] = buildings == null ? null : await Task.Run(() => TownPerches.Build(buildings, origin));
        }
        catch (Exception e)
        {
            GD.PushWarning($"[birds] buildings {tile}: {e.Message}");
            _town[tile] = null;
        }
        finally { _townLoading.Remove(tile); }
    }

    private TownPerches? TownAt(Vector3 p) => _town.TryGetValue(_origin.TileAt(p), out var t) ? t : null;

    /// <summary>True where the buildings make a town (for probes); false until the tile's buildings are read.</summary>
    public bool IsTown(Vector3 p)
    {
        EnsureTown(_origin.TileAt(p));
        return TownAt(p) is { } t && t.BuildingsAround(p) >= TownBuildings;
    }

    /// <summary>A random perch of the wanted kinds within <paramref name="radius"/> of a point, on that point's tile.</summary>
    // ponytail: one tile only, a spot by a tile edge looks at half the town; look at the neighbour tile if that shows
    private Perch? RandomPerch(TownPerches town, Vector3 around, float radius, bool roof, bool ledge, bool street)
    {
        _perchScratch.Clear();
        int r = (int)Mathf.Ceil(radius / TownPerches.Cell);
        var (cx, cz) = TownPerches.CellOf(around, TownPerches.Cell);
        for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (!town.Cells.TryGetValue((cx + dx, cz + dz), out var cell)) continue;
                foreach (var p in cell)
                {
                    bool want = p.Kind switch { PerchKind.Ridge or PerchKind.Eave => roof, PerchKind.Ledge => ledge, _ => street };
                    if (want && Flat(p.At - around) < radius) _perchScratch.Add(p);
                }
            }
        return _perchScratch.Count == 0 ? null : _perchScratch[_rng.Next(_perchScratch.Count)];
    }

    /// <summary>Draws where a town bird of this kind goes: 0 roof, 1 ledge, 2 street, 3 air.</summary>
    private int Placement(BirdSpecies s, bool air)
    {
        int row = -1;
        for (int i = 0; i < TownBirds.Length && row < 0; i++) if (TownBirds[i].Species == s) row = i;
        var (roof, ledge, street, fly) = row >= 0 ? (TownBirds[row].Roof, TownBirds[row].Ledge, TownBirds[row].Street, TownBirds[row].Air) : (0.5f, 0f, 0.5f, 0f);
        if (!air) fly = 0f;
        double r = _rng.NextDouble() * (roof + ledge + street + fly);
        return (r -= roof) < 0 ? 0 : (r -= ledge) < 0 ? 1 : (r -= street) < 0 ? 2 : 3;
    }

    /// <summary>One town flock around <paramref name="p"/>: a species from <see cref="TownBirds"/>, sat on the buildings.</summary>
    private int TownSpawn(Vector3 p, TownPerches town, int room)
    {
        double hour = World.DayNight.Instance?.Hour ?? 12.0;
        bool night = hour < 5.5 || hour > 21.5;
        bool water = false;
        for (int k = 0; k < 6 && !water; k++)
        {
            float a = k * Mathf.Tau / 6f;
            water = CoverAt(p + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 250f, out var c) && c == CoverClass.Water;
        }
        double total = 0;
        Span<double> w = stackalloc double[TownBirds.Length];
        for (int i = 0; i < TownBirds.Length; i++)
        {
            var t = TownBirds[i];
            bool fits = p.Y >= t.Species.MinAltitude - 100 && p.Y <= t.Species.MaxAltitude + 100 && (water || !t.Water) && !(night && t.Air >= 1f);
            w[i] = fits ? t.Weight * t.Species.PresenceIn(Month) : 0;
            total += w[i];
        }
        if (total <= 0) return 0;
        double r = _rng.NextDouble() * total;
        int pick = 0;
        while (pick < w.Length - 1 && (r -= w[pick]) > 0) pick++;
        var s = TownBirds[pick].Species;

        int n = Math.Min(1 + _rng.Next(Math.Max(1, s.Flock)), room);
        int where = Placement(s, air: !night);   // at night they roost: nothing is out flying
        int flock = ++_nextFlock;
        if (where == 3)
        {
            for (int i = 0; i < n; i++)
                Spawn(s, p + new Vector3((float)(_rng.NextDouble() * 2 - 1) * 6f, 0, (float)(_rng.NextDouble() * 2 - 1) * 6f), Bird.Mode.Flying, town: true).Flock = flock;
            return n;
        }
        if (RandomPerch(town, p, 40f, where == 0, where == 1, where == 2) is not { } first) return 0;
        for (int i = 0; i < n; i++)
        {
            var perch = i == 0 ? first : RandomPerch(town, first.At, 12f, where == 0, where == 1, where == 2) ?? first;
            if (where == 2)
            {
                var at = perch.At + new Vector3((float)(_rng.NextDouble() * 2 - 1) * 1.5f, 0, (float)(_rng.NextDouble() * 2 - 1) * 1.5f);
                at.Y = Ground(at);
                Spawn(s, at, Bird.Mode.Ground, town: true).Flock = flock;
            }
            else Spawn(s, perch.At + new Vector3((float)(_rng.NextDouble() - 0.5) * 0.4f, 0, (float)(_rng.NextDouble() - 0.5) * 0.4f), Bird.Mode.Perched, town: true, yaw: perch.Yaw).Flock = flock;
        }
        return n;
    }

    /// <summary>
    /// Where a flying bird that has calmed down comes down (authority). A flock lands together: the
    /// first of it to ask picks a spot 30–80 m ahead and the others settle within a few metres of it.
    /// A town bird takes a roof, a ledge or the street; a perching bird a tree top; others the ground.
    /// Null keeps it flying a while longer.
    /// </summary>
    public (Vector3 At, Bird.Mode Mode, float? Yaw)? FindLanding(Bird b)
    {
        var p = b.Node.GlobalPosition;
        if (!_flockSpots.TryGetValue(b.Flock, out var spot) || spot.Until < _clock || b.Flock == 0)
        {
            var dir = b.Vel with { Y = 0 };
            dir = dir.LengthSquared() > 0.01f ? dir.Normalized() : Vector3.Forward.Rotated(Vector3.Up, (float)(_rng.NextDouble() * Mathf.Tau));
            spot = (p + dir * Mathf.Lerp(30f, 80f, (float)_rng.NextDouble()), _clock + 20.0);
            if (b.Flock != 0) _flockSpots[b.Flock] = spot;
        }
        var area = spot.At;
        if (b.Town && TownAt(area) is { } town)
        {
            int where = Placement(b.Species, air: false);
            if (RandomPerch(town, area, 25f, where == 0, where == 1, where == 2) is { } perch)
            {
                if (perch.Kind != PerchKind.Street) return (perch.At, Bird.Mode.Perched, perch.Yaw);
                var at = perch.At + new Vector3((float)(_rng.NextDouble() * 2 - 1) * 1.5f, 0, (float)(_rng.NextDouble() * 2 - 1) * 1.5f);
                return (at with { Y = Ground(at) }, Bird.Mode.Ground, null);
            }
        }
        var near = area + new Vector3((float)(_rng.NextDouble() * 2 - 1) * 4f, 0, (float)(_rng.NextDouble() * 2 - 1) * 4f);
        EnsureTrees(_origin.TileAt(near));
        bool percher = b.Species.Body is BodyPlan.Passerine or BodyPlan.Corvid or BodyPlan.Pigeon or BodyPlan.Woodpecker;
        if (percher && NearestTreeTop(near, 16f) is { } top) return (top, Bird.Mode.Perched, null);
        if (b.Species.Body is BodyPlan.Woodpecker || !_chunks.TryGetHeight(near, out float ground)
            || CoverAt(near, out var cover) && cover is CoverClass.Water or CoverClass.Forest) return null;
        return (near with { Y = ground }, Bird.Mode.Ground, null);
    }

    /// <summary>
    /// Pigeons now and then let go (authority): about once in 2½ minutes each while someone is within
    /// 80 m, and far more readily straight above a person, who is then the one hit. Cosmetic only:
    /// <see cref="Droppings"/> draws the fall and the splat on every client.
    /// </summary>
    private void MaybeDrop(Bird b, float dt, List<Observer> who)
    {
        if (b.Species.Body != BodyPlan.Pigeon || b.State is not (Bird.Mode.Perched or Bird.Mode.Flying)) return;
        var at = b.Node.GlobalPosition;
        float nearest = float.MaxValue;
        long victim = 0;
        foreach (var o in who)
        {
            float d = Flat(at - o.Position);
            nearest = Mathf.Min(nearest, d);
            float above = at.Y - o.Position.Y;
            if (d < 1.5f && above is > 2.5f and < 40f) victim = o.Peer;
        }
        if (nearest > 80f || _rng.NextDouble() > (victim != 0 ? DropAboveRate : DropRate) * dt) return;
        var vel = victim != 0 ? Vector3.Zero : b.Vel * 0.6f;
        var from = at + Vector3.Down * 0.05f;
        if (Headless) Net?.BroadcastDropping(from, vel, victim);
        else Dropping(from, vel, victim);
    }

    /// <summary>Per second, per pigeon: anywhere, and straight above someone.</summary>
    public const float DropRate = 1f / 150f, DropAboveRate = 0.25f;

    /// <summary>Client: a dropping falls from <paramref name="from"/>; <paramref name="victim"/> (a peer id, 0 for none) is who it lands on.</summary>
    public void Dropping(Vector3 from, Vector3 vel, long victim)
    {
        if (_droppings == null) return;
        bool mine = victim != 0 && victim == Multiplayer.GetUniqueId();
        Node3D? target = victim == 0 ? null
            : mine ? Player
            : GetNodeOrNull<Node3D>("../Players/" + victim);
        if (victim != 0 && !mine) { DropsOnOthers++; LastVictim = victim; }
        GD.Print($"[birds] dropping from {from.X:F0},{from.Y:F0},{from.Z:F0}{(victim != 0 ? $" on peer {victim}{(mine ? " (me)" : "")}" : "")}");
        _droppings.Drop(from, vel, target, mine ? () => Splattered(Player) : null);
    }

    /// <summary>Droppings this client saw fall on another player, and on whom last (probes read them).</summary>
    public int DropsOnOthers { get; private set; }
    public long LastVictim { get; private set; }

    /// <summary>Counts the droppings that landed on this player (probes read it).</summary>
    public int Splats { get; private set; }

    private void Splattered(FootPlayer? me)
    {
        Splats++;
        me?.Punch(0.05f);
        _droppings?.OnScreen();
        _items.Ui.Toast("A pigeon got you!");
    }

    // ------------------------------------------------------------------------------------
    // the hunt
    // ------------------------------------------------------------------------------------

    /// <summary>Called by the item controller after a shotgun shot (the blast already heard).</summary>
    private void Fire(FootPlayer player, Vector3 eye, Vector3 aim)
    {
        // online the server decides and tells everyone (and us, which is when the bird is bagged)
        var bird = Shoot(eye, aim, player);
        if (bird != null && Authority) Bag(bird.Species);
    }

    /// <summary>Scores a bird this player shot in the journal, and says so.</summary>
    public void Bag(BirdSpecies s)
    {
        int points = Journal.Bag(s, Month);
        _items.Ui.Toast(points > 0
            ? $"{s.Name} — +{points}   (score {Journal.Score})"
            : s.IsGame
                ? $"{s.Name}: out of season ({s.SeasonFrom}–{s.SeasonTo}). {points}"
                : $"PROTECTED: {s.Name} ({s.Latin}). {points}");
    }

    /// <summary>
    /// Fires a shot from <paramref name="from"/> along <paramref name="dir"/>. The nearest bird inside
    /// the shot cone that the world does not hide is the hit; returns it, or null for a miss. With
    /// authority every bird within the flush radius takes off and the hit one falls, here; online
    /// this client only picks the target (it has the walls) and reports to the server, which does
    /// both for everyone and answers with <see cref="RemoteKilled"/>.
    /// </summary>
    public Bird? Shoot(Vector3 from, Vector3 dir, CollisionObject3D? shooter = null)
    {
        dir = dir.Normalized();
        var best = Candidate(from, dir, shooter);
        if (Authority) return Resolve(from, dir, best, 1);
        Net!.Report(BirdNet.ReportShot, best?.Id ?? 0, from, dir);
        GD.Print(best == null ? "[birds] shot: nothing in the pattern" : $"[birds] shot: reported #{best.Id} {best.Species.Name}");
        if (best != null) best.MarkReported();
        return best;
    }

    private Bird? Candidate(Vector3 from, Vector3 dir, CollisionObject3D? shooter)
    {
        Bird? best = null;
        float bestAlong = float.MaxValue;
        bool online = !Authority;
        foreach (var b in _birds)
        {
            if (b.State is Bird.Mode.Falling or Bird.Mode.Dead || b.Reported || (online && !b.Remote)) continue;
            var to = b.Centre - from;
            float along = to.Dot(dir);
            if (along < 1f || along > Range) continue;
            float off = (to - dir * along).Length();
            // the pattern opens to about 1.4 m across at 35 m
            float pattern = 0.1f + along * 0.018f;
            if (off > pattern + b.HitRadius) continue;
            // past 30 m the pellets thin out and a hit becomes a chance
            float chance = Mathf.Clamp(1f - (along - FullChance) / (Range - FullChance), 0f, 1f);
            if (_rng.NextDouble() > chance) continue;
            if (along < bestAlong) { bestAlong = along; best = b; }
        }

        if (best != null)
        {
            // A wall is something between the gun and the bird, not what it stands or sits on (#143).
            // The ray goes to just above the bird and a hit within a metre of the end does not count
            // (a low-poly ground rises above a bird 30 m away at a grazing angle). A perched bird
            // sits INSIDE its tree's trunk cylinder (TreeColliders: the trunk is the whole tree
            // height, the perch 0.92 of it), so that trunk is skipped and the ray cast again.
            var exclude = new Godot.Collections.Array<Rid>();
            if (shooter != null) exclude.Add(shooter.GetRid());
            var target = best.Centre + Vector3.Up * 0.25f;
            var space = GetWorld3D().DirectSpaceState;
            for (int tries = 0; tries < 3 && best != null; tries++)
            {
                var wall = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, target, uint.MaxValue, exclude));
                if (wall.Count == 0 || from.DistanceTo(wall["position"].AsVector3()) >= from.DistanceTo(target) - 1.0f) break;
                if (wall["collider"].AsGodotObject() is StaticBody3D trunk && trunk.CollisionLayer == World.TreeColliders.Layer
                    && Flat(trunk.GlobalPosition - best.Centre) < 1.5f)
                {
                    exclude.Add(trunk.GetRid());
                    continue;
                }
                best = null;
            }
        }
        return best;
    }

    /// <summary>Authority: everything near the shot flushes, the hit bird (if any) falls and everyone is told.</summary>
    private Bird? Resolve(Vector3 from, Vector3 dir, Bird? best, long shooter)
    {
        foreach (var b in _birds)
            if (b != best && b.Node.GlobalPosition.DistanceTo(from) < ShotFlushRadius) b.Flush(from, _rng);
        if (best != null) KillBird(best, dir, shooter);
        return best;
    }

    private void KillBird(Bird b, Vector3 dir, long shooter)
    {
        b.Kill(dir);
        Feathers(b.Centre, b.Species.Back, b.Species.Belly);
        Net?.BroadcastKill(b, dir, shooter);
    }

    /// <summary>A pellet pattern this close to full strength, then fading to nothing at <see cref="Range"/>.</summary>
    private const float FullChance = 30f;

    // ------------------------------------------------------------------------------------
    // the server's side of a report, and a client's side of what the server says
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Server: a peer says it shot, hit a bird with a gun round, struck one, or scared one. The
    /// positions are the peer's own (checked against its body by <see cref="BirdNet"/>); the bird must
    /// exist, be alive and be where the report says, give or take what the network took.
    /// </summary>
    public void ServerReport(long peer, int kind, int id, Vector3 from, Vector3 dir)
    {
        dir = dir.LengthSquared() > 1e-6f ? dir.Normalized() : Vector3.Down;
        _byId.TryGetValue(id, out var b);
        if (b != null && b.State is Bird.Mode.Falling or Bird.Mode.Dead) b = null;
        switch (kind)
        {
            case BirdNet.ReportShot:
                if (b != null)
                {
                    var to = b.Centre - from;
                    float along = to.Dot(dir);
                    float off = (to - dir * along).Length();
                    // a moving bird has moved a metre or two since the shooter saw it
                    if (along < 0f || along > Range + 10f || off > 0.1f + along * 0.018f + b.HitRadius + 2.5f)
                    {
                        GD.Print($"[birds] peer {peer}: shot at #{id} refused (along {along:F1} m, off {off:F1} m)");
                        b = null;
                    }
                }
                Resolve(from, dir, b, peer);
                break;
            case BirdNet.ReportKill:
                if (b != null && b.Centre.DistanceTo(from) < 40f) KillBird(b, dir, peer);
                break;
            case BirdNet.ReportFlush:
                if (b != null && b.Centre.DistanceTo(from) < 80f) b.Flush(from, _rng);
                break;
        }
    }

    private readonly MemoryStream _snapshotBuffer = new();
    /// <summary>Reused: the caller sends the packets before asking for the next peer's.</summary>
    private readonly List<byte[]> _snapshotChunks = new();
    private BinaryWriter _snapshotWriter => _writer ??= new BinaryWriter(_snapshotBuffer);
    private BinaryWriter? _writer;

    /// <summary>
    /// Server: what <see cref="BirdNet"/> sends one peer: the birds within range of <paramref name="focus"/>.
    /// Each packet starts with an LV95 anchor near the peer (#185), and every bird is a float offset from it.
    /// </summary>
    public List<byte[]> Snapshot(GlobalPos focus, int tick)
    {
        var chunks = _snapshotChunks;
        chunks.Clear();
        var ms = _snapshotBuffer;
        var w = _snapshotWriter;
        double anchorE = System.Math.Round(focus.E / 1000) * 1000, anchorN = System.Math.Round(focus.N / 1000) * 1000;
        var toAnchor = _origin.SinceAnchor(anchorE, anchorN).Inverse;
        var near = _origin.ToWorld(focus);
        BeginSnapshot(anchorE, anchorN);
        int n = 0;
        foreach (var b in _birds)
        {
            var world = b.Node.GlobalPosition;
            if (Flat(world - near) > DespawnDistance + 30f) continue;
            var p = toAnchor.Point(world);
            var v = toAnchor.Direction(b.Vel);
            // flying birds every time; walking and swimming ones at a quarter of the rate (they amble);
            // perched and dead ones at an eighth: they only turn their heads (a town has dozens)
            int every = b.State switch { Bird.Mode.Perched or Bird.Mode.Dead => 8, Bird.Mode.Ground or Bird.Mode.Swimming => 4, _ => 1 };
            if ((tick + b.Id) % every != 0) continue;
            w.Write(b.Id); w.Write((ushort)b.Species.Index); w.Write((byte)((byte)b.State | (b.Town ? TownBit : 0)));
            w.Write(p.X); w.Write(p.Y); w.Write(p.Z); w.Write(b.Yaw);
            w.Write(v.X); w.Write(v.Y); w.Write(v.Z);
            if (++n == 32) { chunks.Add(ms.ToArray()); BeginSnapshot(anchorE, anchorN); n = 0; }
        }
        if (n > 0) chunks.Add(ms.ToArray());
        return chunks;
    }

    /// <summary>A new snapshot packet: it starts with its LV95 anchor.</summary>
    private void BeginSnapshot(double anchorE, double anchorN)
    {
        _snapshotBuffer.SetLength(0);
        _snapshotWriter.Write(anchorE);
        _snapshotWriter.Write(anchorN);
    }

    /// <summary>The state byte's top bit: a town bird (tame: it lets people come much closer).</summary>
    private const int TownBit = 0x80;

    /// <summary>Client: the server's birds near us. A bird not seen before becomes a puppet.</summary>
    public void ApplySnapshot(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var here = _origin.SinceAnchor(r.ReadDouble(), r.ReadDouble());
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            int id = r.ReadInt32();
            int species = r.ReadUInt16();
            byte bits = r.ReadByte();
            var state = (Bird.Mode)(bits & ~TownBit);
            var p = here.Point(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            float yaw = r.ReadSingle();
            var v = here.Direction(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
            if (!_byId.TryGetValue(id, out var b))
            {
                var sp = BirdNet.Species(species);
                if (sp == null) continue;
                b = new Bird(sp, id * 0.618034f % 1f) { Id = id, Town = (bits & TownBit) != 0 };
                AddChild(b.Node);
                b.BeginRemote(this, state, p, yaw);
                _birds.Add(b);
                _byId[id] = b;
            }
            b.ApplyNet(state, p, yaw, v);
        }
    }

    /// <summary>Client: the server says a bird fell. The shooter (this client, maybe) scores it.</summary>
    public void RemoteKilled(int id, int species, Vector3 at, Vector3 dir, long shooter)
    {
        if (_byId.TryGetValue(id, out var b)) { b.Kill(dir); Feathers(b.Centre, b.Species.Back, b.Species.Belly); }
        else if (BirdNet.Species(species) is { } sp) Feathers(at, sp.Back, sp.Belly);
        if (shooter == Multiplayer.GetUniqueId() && BirdNet.Species(species) is { } hit && !Headless) Bag(hit);
    }

    // ------------------------------------------------------------------------------------
    // aircraft: bird strikes and the guns
    // ------------------------------------------------------------------------------------

    /// <summary>Seconds between birds placed along a fast flight path.</summary>
    private const double AerialInterval = 2.5;

    /// <summary>
    /// Mass from length, kg: ~9·L³ lands a sparrow at 30 g, a pigeon at 0.5 kg, a goose at 4 kg and
    /// a golden eagle at 5.5 kg. The species table carries no mass.
    /// </summary>
    // ponytail: allometric guess, overestimates long-necked birds (a swan comes out at 30 kg, hence the cap); add a Mass column if strikes need to be exact
    public static float Mass(BirdSpecies s) => Mathf.Clamp(9f * s.Length * s.Length * s.Length, 0.01f, 12f);

    /// <summary>How high above the ground a species really flies, m.</summary>
    private static float Ceiling(BirdSpecies s) => s.Flight switch
    {
        FlightStyle.Soar => 400f,     // buzzards, eagles, vultures, storks on thermals
        FlightStyle.Dart => 150f,     // swifts hawk high
        FlightStyle.Glide => 200f,    // gulls, kites, harriers
        _ => s.Body is BodyPlan.Waterfowl or BodyPlan.Heron or BodyPlan.Raptor ? 150f : 50f,
    };

    /// <summary>
    /// One bird (or a flock) 120–220 m ahead of an aircraft, within 60 m of its track, at the pilot's
    /// height above the ground if that species goes that high, otherwise at its own ceiling — so a
    /// plane skimming a valley meets storks and swallows, and one at 1,000 m meets almost nothing.
    /// </summary>
    public int SpawnAhead(FootPlayer pilot) => SpawnAhead(pilot.GlobalPosition, pilot.Flight.Velocity, Budget - _birds.Count);

    /// <summary>The same for a craft at <paramref name="at"/> moving at <paramref name="velocity"/> (the server only knows that much of a remote pilot).</summary>
    public int SpawnAhead(Vector3 at, Vector3 velocity, int room)
    {
        var flat = velocity with { Y = 0 };
        if (flat.LengthSquared() < 1f) return 0;
        var dir = flat.Normalized();
        var p = at + dir * Mathf.Lerp(120f, 220f, (float)_rng.NextDouble())
            + dir.Cross(Vector3.Up) * ((float)_rng.NextDouble() * 2f - 1f) * 60f;
        if (!_chunks.TryGetHeight(p, out float ground) || !CoverAt(p, out var cover)) return 0;
        p.Y = ground;
        var s = Pick(HabitatAt(cover, ground), ground);
        if (s == null) return 0;
        float agl = at.Y - ground;
        float lift = Mathf.Clamp(agl + ((float)_rng.NextDouble() * 2f - 1f) * 15f, 3f, Ceiling(s));
        var mode = s.Flight == FlightStyle.Soar ? Bird.Mode.Soaring : Bird.Mode.Flying;
        int n = Math.Min(1 + _rng.Next(Math.Max(1, s.Flock)), room);
        int flock = ++_nextFlock;
        for (int i = 0; i < n; i++)
        {
            var spot = p + new Vector3((float)(_rng.NextDouble() * 2 - 1) * 4f, 0, (float)(_rng.NextDouble() * 2 - 1) * 4f);
            Spawn(s, spot, mode, lift + (float)(_rng.NextDouble() * 2 - 1) * 2f).Flock = flock;
        }
        return n;
    }

    /// <summary>
    /// Where the craft is solid enough to hit a bird: spheres, centre and radius. A plane, or a
    /// helicopter's rotor disc, is one big sphere; a canopy pilot is their body and their wing.
    /// </summary>
    private static (Vector3 Centre, float Radius, bool Intake)[] StrikeVolumes(FootPlayer pilot) => pilot.Ride switch
    {
        RideKind.Plane => new[] { (pilot.GlobalPosition + Vector3.Up * 1.4f, 4.5f, true) },
        RideKind.Helicopter => new[] { (pilot.GlobalPosition + Vector3.Up * 1.8f, 5f, true) },
        RideKind.Paraglider or RideKind.Parachute => new[]
        {
            (pilot.GlobalPosition + Vector3.Up * 1f, 0.7f, false),
            (pilot.GlobalTransform * new Vector3(0, pilot.Ride == RideKind.Paraglider ? 7.6f : 5.5f, 0), 4f, false),
        },
        RideKind.Wingsuit => new[] { (pilot.GlobalPosition + Vector3.Up * 1f, 1f, false) },
        _ => Array.Empty<(Vector3, float, bool)>(),
    };

    /// <summary>
    /// Birds against the local pilot's craft. Each airborne bird the craft will reach within 1.2 s
    /// gets ONE chance to dodge (small birds are quicker: 85% for a sparrow, 55% for an eagle);
    /// one that is inside a strike volume dies and hits the craft with ½·m·v², at the closing speed.
    /// Client-authoritative, like the guns: birds are this client's own, and so is the damage.
    /// </summary>
    private void CheckStrikes(FootPlayer pilot)
    {
        var volumes = StrikeVolumes(pilot);
        if (volumes.Length == 0) return;
        var craftVel = pilot.Flight.Velocity;
        float speed = craftVel.Length();
        var nose = -(pilot.Flight.Attitude == default ? pilot.GlobalBasis : pilot.Flight.Attitude.Orthonormalized()).Z;
        foreach (var b in _birds)
        {
            if (b.State is Bird.Mode.Falling or Bird.Mode.Dead || b.Reported || (!Authority && !b.Remote)) continue;
            var centre = b.Centre;
            var rel = centre - volumes[0].Centre;

            if (EvadeAircraft && speed > 5f && !_rolled.Contains(b) && b.State is not (Bird.Mode.Ground or Bird.Mode.Perched or Bird.Mode.Swimming))
            {
                var dir = craftVel / speed;
                float along = rel.Dot(dir);
                if (along > 0f && along < speed * 1.2f && (rel - dir * along).Length() < volumes[0].Radius + 6f)
                {
                    _rolled.Add(b);
                    float dodge = Mathf.Clamp(0.9f - b.Species.Length * 0.4f, 0.5f, 0.88f);
                    if (_rng.NextDouble() < dodge)
                    {
                        if (Authority) b.Flush(volumes[0].Centre, _rng);
                        else { Net!.Report(BirdNet.ReportFlush, b.Id, volumes[0].Centre, Vector3.Down); b.MarkReported(); }
                        continue;
                    }
                }
            }

            foreach (var (c, r, intake) in volumes)
            {
                if (centre.DistanceTo(c) > r + b.HitRadius) continue;
                float closing = (craftVel - b.Velocity).Length();
                float energy = 0.5f * Mass(b.Species) * closing * closing;
                bool vehicle = pilot.Vehicle is { IsVehicle: true };
                // a craft shrugs off what would floor a person: 100 J per point against 20
                float damage = vehicle ? Mathf.Min(energy / 100f, 70f) : Mathf.Min(energy / 20f, 60f);
                // through the propeller (nose side) or the rotor/intake: a big enough bird stops it
                bool ingested = intake && energy > EngineOutJoules
                    && (pilot.Ride == RideKind.Helicopter || (centre - c).Dot(nose) > 0f);
                var strikeDir = speed > 0.1f ? craftVel / speed : Vector3.Down;
                if (Authority) KillBird(b, strikeDir, 1);
                else { Net!.Report(BirdNet.ReportKill, b.Id, centre, strikeDir); b.MarkReported(); }
                Strikes++;
                LastStrike = (b.Species.Name, energy, damage, ingested);
                GD.Print($"[birds] strike: {b.Species.Name} {energy:F0} J -> {damage:F1} damage{(ingested ? ", engine out" : "")}");
                pilot.BirdStrike(damage, ingested);
                break;
            }
        }
        _rolled.RemoveWhere(b => b.State is Bird.Mode.Falling or Bird.Mode.Dead || !_birds.Contains(b));
    }

    /// <summary>Kinetic energy that stops an engine: a goose or an eagle at a light plane's speed, not a crow.</summary>
    public const float EngineOutJoules = 1500f;

    public int Strikes { get; private set; }
    public (string Species, float Joules, float Damage, bool EngineOut) LastStrike { get; private set; }

    /// <summary>
    /// A round from an aircraft gun between two points: the first bird it passes through falls.
    /// Scored in the journal only when the shot was this client's — a protected bird shot from the
    /// air costs what it costs on foot.
    /// </summary>
    public Bird? TracerHit(Vector3 from, Vector3 to, bool mine)
    {
        var seg = to - from;
        float len2 = seg.LengthSquared();
        if (len2 < 1e-6f) return null;
        // online the birds are the server's: only the shooter's own client reports what its rounds hit
        if (!Authority && !mine) return null;
        foreach (var b in _birds)
        {
            if (b.State is Bird.Mode.Falling or Bird.Mode.Dead || b.Reported || (!Authority && !b.Remote)) continue;
            var c = b.Centre;
            float t = Mathf.Clamp((c - from).Dot(seg) / len2, 0f, 1f);
            if (c.DistanceTo(from + seg * t) > b.HitRadius + 0.1f) continue;
            if (Authority) KillBird(b, seg.Normalized(), 1);
            else { Net!.Report(BirdNet.ReportKill, b.Id, from + seg * t, seg.Normalized()); b.MarkReported(); }
            // online the toast and the points come with the server's word (RemoteKilled)
            if (mine && Authority) Bag(b.Species);
            return b;
        }
        return null;
    }

    /// <summary>A bird took off near the player: let it call, if it is the calling kind.</summary>
    internal void Called(Bird b)
    {
        if (Headless || _callCooldown > 0 || b.Species.Body is not (BodyPlan.Passerine or BodyPlan.Corvid or BodyPlan.Wader or BodyPlan.Gull)) return;
        _callCooldown = 0.6;
        _call.Stream = CallFor(b.Species.Index % AmbienceDsp.SpeciesCount);
        _call.PitchScale = Mathf.Clamp(0.25f / Mathf.Max(b.Species.Length, 0.08f), 0.5f, 1.6f);
        _call.GlobalPosition = b.Node.GlobalPosition;
        _call.Play();
    }

    private static readonly Dictionary<int, AudioStreamWav> Calls = new();

    /// <summary>One of <see cref="Ambience"/>'s synthesised songs, reused as an alarm call.</summary>
    private static AudioStreamWav CallFor(int pattern) => Calls.TryGetValue(pattern, out var c) ? c
        : Calls[pattern] = Dsp.Encode(Dsp.Normalise(AmbienceDsp.BirdCall(new Random(pattern * 977), AmbienceDsp.BirdPattern(pattern)), 0.8f));

    private void Feathers(Vector3 at, Color a, Color b)
    {
        if (Headless) return;
        var puff = new CpuParticles3D
        {
            OneShot = true, Amount = 28, Lifetime = 1.8, Explosiveness = 0.95f,
            Mesh = new QuadMesh { Size = new Vector2(0.06f, 0.025f) },
            Direction = Vector3.Up, Spread = 180f, InitialVelocityMin = 0.6f, InitialVelocityMax = 2.8f,
            Gravity = new Vector3(0, -0.7f, 0), DampingMin = 1.5f, DampingMax = 3f,
            AngularVelocityMin = -360f, AngularVelocityMax = 360f,
            ColorRamp = new Gradient { Colors = new[] { a, b } },
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true, CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            },
        };
        AddChild(puff);
        puff.GlobalPosition = at;
        puff.Emitting = true;
        GetTree().CreateTimer(3.0).Timeout += puff.QueueFree;
    }
}

/// <summary>One bird: its node, its wings, and a small state machine.</summary>
public sealed class Bird
{
    public enum Mode { Ground, Perched, Swimming, Flying, Soaring, Hovering, Falling, Dead }

    public readonly BirdSpecies Species;
    public readonly Node3D Node;
    private readonly Node3D? _wingA, _wingB;
    private readonly float _signA, _signB;
    private readonly BirdMesh.Parts _parts;

    public Mode State { get; private set; }

    /// <summary>The number the server knows this bird by (on a client, only a puppet's means anything).</summary>
    public int Id { get; init; }

    /// <summary>A puppet: it only follows what the server sends.</summary>
    public bool Remote { get; private set; }

    /// <summary>A town bird (#143): it lives on the buildings, lets people come close and lands back on a roof, a ledge or the street.</summary>
    public bool Town { get; init; }

    /// <summary>The flock it was spawned with (0: none); a flock lands together.</summary>
    public int Flock { get; set; }

    public float Yaw => _yaw;

    /// <summary>Measured velocity (what the server sends so a puppet can carry on between snapshots).</summary>
    public Vector3 Vel { get; private set; }

    private ulong _reportedUntil;

    /// <summary>This client already told the server about this bird; wait for its answer before acting on it again.</summary>
    public bool Reported => Time.GetTicksMsec() < _reportedUntil;
    public void MarkReported() => _reportedUntil = Time.GetTicksMsec() + 800;

    private Vector3 _netPos, _netVel;
    private float _netYaw, _netAge;

    /// <summary>World velocity while flying free; zero otherwise (a soaring bird's circle is slow next to an aircraft).</summary>
    public Vector3 Velocity => State != Mode.Flying ? Vector3.Zero : Remote ? _netVel : _velocity;
    public bool Seen { get; set; }
    public bool Gone { get; private set; }

    private Vector3 _velocity, _anchor, _walkTo;
    private float _phase, _timer, _flee, _yaw, _angle, _radius, _spin, _seed;
    private BirdLife _life = null!;

    /// <param name="visual">False on the dedicated server: a bare node, no meshes to keep in step.</param>
    public Bird(BirdSpecies species, float seed, bool visual = true)
    {
        Species = species;
        _seed = seed;
        _parts = BirdMesh.Get(species);
        Node = new Node3D { Name = "Bird" };
        if (!visual) return;
        Node.AddChild(new MeshInstance3D { Mesh = _parts.Body, MaterialOverride = BirdMesh.Material });
        _wingA = Wing(_parts.WingA, out _signA);
        _wingB = Wing(_parts.WingB, out _signB);
    }

    private Node3D Wing(ArrayMesh mesh, out float sign)
    {
        // the half turn in MeshScratch.Build decides which side a wing ends up on; ask the mesh
        sign = mesh.GetAabb().GetCenter().X >= 0 ? 1f : -1f;
        var n = new MeshInstance3D { Mesh = mesh, MaterialOverride = BirdMesh.Material, Position = _parts.Shoulder };
        Node.AddChild(n);
        return n;
    }

    /// <summary>Where a shot aims: the middle of the body, not the feet.</summary>
    public Vector3 Centre => Node.GlobalPosition + Node.GlobalTransform.Basis * _parts.Shoulder;

    /// <summary>The world positions this bird steers by, moved with the origin (#185).</summary>
    public void Shift(Core.OriginShift shift)
    {
        _anchor = shift.Point(_anchor);
        _walkTo = shift.Point(_walkTo);
        _velocity = shift.Direction(_velocity);
        // a remote bird's last word from the server: followed until the next one (#185)
        _netPos = shift.Point(_netPos);
        _netVel = shift.Direction(_netVel);
    }

    /// <summary>What the shot pattern has to touch: the body, plus the wings when they are spread.</summary>
    public float HitRadius => Species.Length * 0.4f + (Airborne ? Species.Wingspan * 0.25f : 0f);

    private bool Airborne => State is Mode.Flying or Mode.Soaring or Mode.Hovering;

    private float Cruise => Species.Flight == FlightStyle.Dart ? 12f + 6f * _seed : 5f + 9f * Mathf.Sqrt(Species.Length);
    private float FlapHz => 2.5f * Mathf.Pow(Mathf.Max(Species.Length, 0.08f), -0.7f);
    /// <summary>How close a person may come before it flies: town birds are used to people (a feral pigeon lets you within 4 m).</summary>
    public float FlushDistance => Town ? 2f + 4f * Mathf.Sqrt(Species.Length) : 5f + 18f * Mathf.Sqrt(Species.Length);

    /// <summary>Lands at all, once calm again: not the swifts, soarers or hoverers, nor water birds (they fly on and are gone).</summary>
    private bool Lands => Species.Flight is not (FlightStyle.Dart or FlightStyle.Soar or FlightStyle.Hover)
        && (Species.Body is BodyPlan.Passerine or BodyPlan.Corvid or BodyPlan.Pigeon or BodyPlan.Woodpecker || (Town && Species.Body == BodyPlan.Gull));

    private Vector3? _land;
    private Mode _landMode;
    private float? _landYaw;
    private float _airTime, _settle;

    public void Begin(Mode mode, Vector3 at, BirdLife life, Random rng, float? lift = null, float? yaw = null)
    {
        _life = life;
        State = mode;
        _yaw = yaw ?? (float)(rng.NextDouble() * Mathf.Tau);
        _timer = (float)rng.NextDouble() * 3f;
        _settle = 5f + 15f * _seed;
        switch (mode)
        {
            case Mode.Soaring:
                _radius = 25f + 40f * (float)rng.NextDouble();
                _anchor = at + Vector3.Up * (lift ?? 50f + 110f * (float)rng.NextDouble());
                _angle = (float)(rng.NextDouble() * Mathf.Tau);
                at = _anchor + new Vector3(Mathf.Cos(_angle), 0, Mathf.Sin(_angle)) * _radius;
                break;
            case Mode.Hovering:
                at += Vector3.Up * (lift ?? 10f + 20f * (float)rng.NextDouble());
                _anchor = at;
                break;
            case Mode.Flying:
                at += Vector3.Up * (lift ?? (Species.Flight == FlightStyle.Dart ? 6f + 30f * (float)rng.NextDouble() : 12f + 30f * (float)rng.NextDouble()));
                _velocity = new Vector3(Mathf.Sin(_yaw), 0, Mathf.Cos(_yaw)) * Cruise;
                break;
            case Mode.Swimming:
                at.Y += 0.12f - _parts.SwimDepth;
                break;
        }
        _walkTo = at;
        Node.GlobalPosition = at;
        Pose(0f);
    }

    public void BeginRemote(BirdLife life, Mode state, Vector3 at, float yaw)
    {
        _life = life;
        Remote = true;
        State = state;
        _yaw = _netYaw = yaw;
        _netPos = at;
        Node.GlobalPosition = at;
        Pose(0f);
    }

    /// <summary>A snapshot of this bird from the server.</summary>
    public void ApplyNet(Mode state, Vector3 pos, float yaw, Vector3 vel)
    {
        // a snapshot taken before the kill must not lift a falling bird back into the air
        if (State is Mode.Falling or Mode.Dead && state is not (Mode.Falling or Mode.Dead)) return;
        bool grounded = State is Mode.Ground or Mode.Perched or Mode.Swimming;
        State = state;
        // scared off, here too: let it call
        if (grounded && state == Mode.Flying) _life.Called(this);
        _netPos = pos; _netVel = vel; _netYaw = yaw; _netAge = 0f;
    }

    private void StepRemote(float dt)
    {
        _netAge += dt;
        // perched birds are sent once a second (unreliable): three lost in a row are not a departure
        if (_netAge > 4.5f) { Gone = true; return; }
        float age = Mathf.Min(_netAge, 0.5f);
        var target = _netPos + _netVel * age;
        if (State == Mode.Falling) target += Vector3.Down * (0.5f * Rideable.Gravity * age * age);
        // the server's ground is its coarse 10 m grid (lean-dedicated-server): a bird on the ground,
        // on the water or dead stands on this client's ground, and a falling one stops there
        float ground = _life.Ground(target);
        target.Y = State switch
        {
            Mode.Ground or Mode.Dead => ground,
            Mode.Swimming => ground + 0.12f - _parts.SwimDepth,
            Mode.Falling => Mathf.Max(target.Y, ground),
            _ => target.Y,
        };
        var p = Node.GlobalPosition;
        float k = 1f - Mathf.Exp(-10f * dt);
        p = (p - target).LengthSquared() > 400f ? target : p.Lerp(target, k);
        _yaw = Mathf.LerpAngle(_yaw, _netYaw, k);
        float flap = State switch
        {
            Mode.Flying => Mathf.Sin(_phase) * 0.9f,
            Mode.Soaring => 0.08f,
            Mode.Hovering => Mathf.Sin(_phase) * 0.7f,
            Mode.Falling => Mathf.Sin(_phase * 0.5f) * 0.5f,
            _ => 0f,
        };
        _phase += Mathf.Tau * FlapHz * dt;
        Node.GlobalPosition = p;
        Pose(flap);
    }

    /// <summary>Takes off away from <paramref name="from"/> and keeps going.</summary>
    public void Flush(Vector3 from, Random rng)
    {
        if (State is Mode.Falling or Mode.Dead) return;
        var away = (Node.GlobalPosition - from) with { Y = 0 };
        if (away.LengthSquared() < 0.01f) away = Vector3.Forward;
        away = away.Normalized().Rotated(Vector3.Up, (float)(rng.NextDouble() - 0.5) * 1.4f);
        _velocity = away * Cruise * 1.2f + Vector3.Up * Cruise * 0.5f;
        _flee = 3f;
        _land = null;
        _settle = _airTime + 6f + 12f * _seed;   // a while in the air before it comes down again
        bool wasGrounded = !Airborne;
        State = Mode.Flying;
        if (wasGrounded) _life.Called(this);
    }

    public void Kill(Vector3 shot)
    {
        if (Remote) { _netVel = shot * 2f + Vector3.Up * 1.5f; _netPos = Node.GlobalPosition; _netAge = 0f; }
        State = Mode.Falling;
        _velocity = shot * 2f + Vector3.Up * 1.5f + (Airborne ? _velocity * 0.4f : Vector3.Zero);
        _spin = 6f;
    }

    public void Step(float dt, BirdLife life, Vector3? player)
    {
        if (Remote) { StepRemote(dt); return; }
        var p = Node.GlobalPosition;
        var before = p;
        if (player is { } me && State is Mode.Ground or Mode.Perched or Mode.Swimming
            && p.DistanceTo(me) < FlushDistance)
            Flush(me, Random.Shared);

        float flap = 0f;
        switch (State)
        {
            case Mode.Ground:
            {
                // forage: short walks between pecks
                _timer -= dt;
                if (_timer < 0)
                {
                    _timer = 1f + 3f * Hash(p.X);
                    _walkTo = p + new Vector3(Hash(p.Z) - 0.5f, 0, Hash(p.X + p.Z) - 0.5f) * (1f + Species.Length * 6f);
                }
                var step = (_walkTo - p) with { Y = 0 };
                float speed = 0.25f + Species.Length * 0.8f;
                if (step.Length() > 0.05f)
                {
                    p += step.LimitLength(speed * dt);
                    _yaw = Mathf.Atan2(step.X, step.Z);
                }
                p.Y = life.Ground(p);
                break;
            }
            case Mode.Perched:
                _timer -= dt;
                if (_timer < 0) { _timer = 2f + 4f * Hash(_yaw); _yaw += (Hash(_timer) - 0.5f) * 2f; }
                break;
            case Mode.Swimming:
                _timer -= dt;
                if (_timer < 0) { _timer = 4f + 6f * Hash(p.X); _yaw += (Hash(p.Z) - 0.5f) * 1.5f; }
                p += new Vector3(Mathf.Sin(_yaw), 0, Mathf.Cos(_yaw)) * 0.3f * dt;
                p.Y = life.Ground(p) + 0.12f - _parts.SwimDepth;
                break;
            case Mode.Flying:
            {
                _flee -= dt;
                _airTime += dt;
                float ground = life.Ground(p);
                if (_land is { } dest)
                {
                    // gliding in to the perch, braking over the last metres
                    var to = dest - p;
                    float dist = to.Length();
                    if (dist < 0.3f)
                    {
                        p = dest;
                        State = _landMode;
                        _land = null;
                        _velocity = Vector3.Zero;
                        _walkTo = p;
                        _timer = 1f + 3f * _seed;
                        if (_landYaw is { } face) _yaw = face;
                        break;
                    }
                    float speed = Mathf.Min(Cruise, 1.5f + dist * 0.7f);
                    var want = to / dist * speed;
                    _velocity = dist < 8f ? want : _velocity.Lerp(want, 1f - Mathf.Exp(-3f * dt));
                    p += _velocity * dt;
                    if (dist > 8f && p.Y < ground + 1f) p.Y = ground + 1f;
                    _yaw = Mathf.Atan2(_velocity.X, _velocity.Z);
                    flap = dist < 3f ? Mathf.Sin(_phase * 1.5f) * 0.9f : Mathf.Sin(_phase) * 0.9f;
                    break;
                }
                if (_flee <= 0 && _airTime > _settle && Lands)
                {
                    _settle = _airTime + 3f;   // nowhere to land: look again in a moment
                    if (life.FindLanding(this) is { } landing) (_land, _landMode, _landYaw) = landing;
                }
                if (_flee <= 0)
                {
                    // level out after the escape, and keep clear of the ground
                    float wantY = p.Y < ground + 8f ? Cruise * 0.4f : 0f;
                    _velocity.Y = Mathf.MoveToward(_velocity.Y, wantY, 4f * dt);
                    var flat = _velocity with { Y = 0 };
                    if (flat.LengthSquared() < 0.01f) flat = Vector3.Forward;
                    _velocity = flat.Normalized() * Cruise + Vector3.Up * _velocity.Y;
                }
                p += _velocity * dt;
                if (p.Y < ground + 1f) p.Y = ground + 1f;
                if (Species.Flight == FlightStyle.Undulate) p.Y += Mathf.Sin(_phase * 0.25f) * 0.6f * dt * Cruise * 0.2f;
                _yaw = Mathf.Atan2(_velocity.X, _velocity.Z);
                // undulating flight is bursts of beats between closed-wing bounds
                flap = Species.Flight == FlightStyle.Undulate && Mathf.Sin(_phase * 0.2f) < 0 ? -0.9f
                     : Species.Flight == FlightStyle.Glide && Mathf.Sin(_phase * 0.15f) < 0.3f ? 0.05f
                     : Mathf.Sin(_phase) * 0.9f;
                break;
            }
            case Mode.Soaring:
            {
                _angle += Cruise / _radius * dt;
                _anchor += new Vector3(0.6f, 0, 0.3f) * dt;
                var next = _anchor + new Vector3(Mathf.Cos(_angle), 0, Mathf.Sin(_angle)) * _radius;
                var v = next - p;
                p = next;
                if (v.LengthSquared() > 1e-6f) _yaw = Mathf.Atan2(v.X, v.Z);
                flap = 0.08f + (Mathf.Sin(_phase * 0.05f) > 0.95f ? Mathf.Sin(_phase) * 0.6f : 0f);
                break;
            }
            case Mode.Hovering:
                p = _anchor + new Vector3(Mathf.Sin(_phase * 0.03f) * 0.4f, Mathf.Sin(_phase * 0.05f) * 0.3f, 0);
                flap = Mathf.Sin(_phase) * 0.7f;
                break;
            case Mode.Falling:
            {
                _velocity.Y -= Rideable.Gravity * dt;
                _velocity *= 1f - 0.6f * dt;
                p += _velocity * dt;
                _spin += 3f * dt;
                _yaw += _spin * dt;
                float floor = life.Ground(p);
                if (p.Y <= floor)
                {
                    p.Y = floor;
                    State = Mode.Dead;
                    _timer = 40f;
                }
                flap = Mathf.Sin(_phase * 0.5f) * 0.5f;
                break;
            }
            case Mode.Dead:
                _timer -= dt;
                if (_timer < 0) Gone = true;
                break;
        }

        _phase += Mathf.Tau * FlapHz * dt;
        if (dt > 0f) Vel = (p - before) / dt;
        Node.GlobalPosition = p;
        Pose(flap);
    }

    private void Pose(float flap)
    {
        bool wings = State is Mode.Flying or Mode.Soaring or Mode.Hovering or Mode.Falling;
        if (_wingA != null && _wingB != null)
        {
            _wingA.Visible = _wingB.Visible = wings;
            _wingA.Rotation = new Vector3(0, 0, _signA * flap);
            _wingB.Rotation = new Vector3(0, 0, _signB * flap);
        }

        // a node faces −Z, so a bird heading along (sin yaw, cos yaw) turns by yaw + π
        var basis = new Basis(Vector3.Up, _yaw + Mathf.Pi);
        if (State == Mode.Soaring) basis *= new Basis(Vector3.Back, -0.35f);
        if (State == Mode.Dead) basis *= new Basis(Vector3.Back, Mathf.Pi * 0.5f);
        Node.Basis = basis;
    }

    private float Hash(float x) => Mathf.PosMod(Mathf.Sin(x * 12.9898f + _seed * 78.233f) * 43758.547f, 1f);
}
