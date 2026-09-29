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
/// <b>Local and cosmetic</b>, like the traffic: each client has its own birds, nothing is
/// replicated, and there are never more than <see cref="Budget"/> of them. A bird that ends up
/// more than <see cref="DespawnDistance"/> away is simply removed.
/// </para>
///
/// <para>
/// <b>Hunting</b>: the shotgun item (<see cref="ItemController.Fire"/>) casts a widening cone
/// from the camera; the nearest bird inside it, not behind a wall, falls. The field journal
/// scores it: a game species in season earns points, anything protected costs a heavy penalty.
/// Every shot flushes every bird within <see cref="ShotFlushRadius"/>.
/// </para>
/// </summary>
public partial class BirdLife : Node3D
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
    private readonly Random _rng = new();
    private readonly Dictionary<TileId, List<Vector3>?> _trees = new();
    private readonly HashSet<TileId> _loading = new();
    private AudioStreamPlayer _gun = null!;
    private AudioStreamPlayer3D _call = null!;
    private double _spawnTimer;
    private double _callCooldown;

    public BirdJournal Journal { get; private set; } = null!;
    public IReadOnlyList<Bird> Birds => _birds;

    /// <summary>Calendar month used for presence and hunting seasons; <c>--birdmonth N</c> overrides it.</summary>
    public int Month { get; set; } = DateTime.Now.Month;

    /// <summary>False stops the automatic spawning (the probe places its own birds).</summary>
    public bool AutoSpawn { get; set; } = true;

    /// <summary>Overrides who the birds react to; probes set it to their own body.</summary>
    public Func<FootPlayer?>? PlayerOverride { get; set; }

    public BirdLife(ChunkManager chunks, WorldOrigin origin, ItemController items)
    {
        _chunks = chunks;
        _origin = origin;
        _items = items;
    }

    public BirdLife() : this(null!, null!, null!) { }

    public override void _Ready()
    {
        Name = "Birds";
        Journal = new BirdJournal();
        AddChild(Journal);
        _gun = new AudioStreamPlayer { Name = "Gun", Bus = SfxBus.Name, VolumeDb = -3f };
        AddChild(_gun);
        _call = new AudioStreamPlayer3D { Name = "Call", Bus = SfxBus.Name, UnitSize = 12f, VolumeDb = -4f };
        AddChild(_call);
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
            inv.Add(ItemId.Shotgun, 1);
            inv.Add(ItemId.Shells, 25);
        }
    }

    private FootPlayer? Player => PlayerOverride?.Invoke() ?? _items.UsablePlayer;

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        var player = Player;
        var focus = player?.GlobalPosition ?? cam.GlobalPosition;

        _spawnTimer -= delta;
        _callCooldown -= delta;
        if (AutoSpawn && _spawnTimer <= 0 && _birds.Count < Budget)
        {
            _spawnTimer = 0.4;
            TrySpawn(focus);
        }

        // binoculars see much further than the naked eye
        float seen = cam.Fov < 20f ? 300f : SeenRange;
        for (int i = _birds.Count - 1; i >= 0; i--)
        {
            var b = _birds[i];
            b.Step(dt, this, player?.GlobalPosition);
            var p = b.Node.GlobalPosition;
            if (b.Gone || Flat(p - focus) > DespawnDistance)
            {
                b.Node.QueueFree();
                _birds.RemoveAt(i);
                continue;
            }
            if (!b.Seen && b.State != Bird.Mode.Dead && cam.GlobalPosition.DistanceTo(p) < seen && cam.IsPositionInFrustum(p))
            {
                b.Seen = true;
                Journal.Seen(b.Species);
            }
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
        CoverClass.ParkingPublic or CoverClass.ParkingPrivate or CoverClass.RestArea or CoverClass.PavedArea
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
    public (Habitat Habitat, int Count) TrySpawn(Vector3 focus)
    {
        float a = (float)(_rng.NextDouble() * Mathf.Tau);
        float d = Mathf.Lerp(SpawnMin, SpawnMax, (float)_rng.NextDouble());
        var p = focus + new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
        if (!_chunks.TryGetHeight(p, out float ground) || !_chunks.TryGetCover(p, out var cover)) return (Habitat.None, 0);
        p.Y = ground;
        var habitat = HabitatAt(cover, ground);
        EnsureTrees(_origin.TileAt(p));
        var s = Pick(habitat, ground);
        if (s == null) return (habitat, 0);

        int n = Math.Min(1 + _rng.Next(Math.Max(1, s.Flock)), Budget - _birds.Count);
        var mode = ChooseMode(s, habitat);
        for (int i = 0; i < n; i++)
        {
            float spread = 1.5f + n * 0.6f;
            var at = p + new Vector3((float)(_rng.NextDouble() * 2 - 1) * spread, 0, (float)(_rng.NextDouble() * 2 - 1) * spread);
            at.Y = Ground(at);
            Spawn(s, at, mode);
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
    /// is one; <paramref name="lift"/> fixes a hovering bird's height instead of drawing one.
    /// </summary>
    public Bird Spawn(BirdSpecies s, Vector3 at, Bird.Mode mode, float? lift = null)
    {
        if (mode == Bird.Mode.Perched)
        {
            if (NearestTreeTop(at, 12f) is { } top) at = top;
            else mode = Bird.Mode.Ground;
        }
        var bird = new Bird(s, (float)_rng.NextDouble());
        AddChild(bird.Node);
        bird.Begin(mode, at, this, _rng, lift);
        _birds.Add(bird);
        return bird;
    }

    // ------------------------------------------------------------------------------------
    // perches: the .trees tile, loaded once per tile through the cached source
    // ------------------------------------------------------------------------------------

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
            var origin = _origin;
            _trees[tile] = trees == null ? null : await Task.Run(() => trees
                .Select(t => origin.ToWorld(tile.MinE + t.X, tile.MaxN - t.Z, t.Y + t.Height * 0.92f))
                .ToList());
        }
        catch (Exception e)
        {
            GD.PushWarning($"[birds] trees {tile}: {e.Message}");
            _trees[tile] = null;
        }
        finally { _loading.Remove(tile); }
    }

    // ponytail: linear scan of the tile's trees (tens of thousands) at most every 0.4 s; bucket like Gathering if it shows up in a profile
    private Vector3? NearestTreeTop(Vector3 at, float reach)
    {
        if (!_trees.TryGetValue(_origin.TileAt(at), out var tops) || tops == null) return null;
        Vector3? best = null;
        float bestD = reach;
        foreach (var t in tops)
        {
            float d = Flat(t - at);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    // ------------------------------------------------------------------------------------
    // the hunt
    // ------------------------------------------------------------------------------------

    private static SfxBank? _blast;

    /// <summary>A shotgun report: a sharp crack, a noise body and a low thump, then a short tail.</summary>
    public static SfxBank Blast => _blast ??= SfxBank.Build("shotgun", 6, 1.2f, 71, (rng, n) =>
    {
        float J() => 1f + ((float)rng.NextDouble() * 2 - 1) * 0.1f;
        var crack = Dsp.HighPass(Dsp.Noise(rng, n), 0.3f);
        var body = Dsp.LowPass(Dsp.Noise(rng, n), 0.08f * J());
        float d1 = 70f * J(), d2 = 11f * J(), d3 = 2.5f * J(), f = 55f * J();
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Dsp.Rate;
            s[i] = crack[i] * 2.5f * Mathf.Exp(-d1 * t) + body[i] * 7f * Mathf.Exp(-d2 * t)
                 + Mathf.Sin(Mathf.Tau * f * t) * 0.9f * Mathf.Exp(-14f * t)
                 + body[i] * 2.5f * Mathf.Exp(-d3 * t) * Mathf.Min(1f, t * 20f);
        }
        return s;
    });

    /// <summary>Called by the item controller with a shell already spent.</summary>
    private void Fire(FootPlayer player)
    {
        var (stream, pitch, db) = Blast.Pick(_rng);
        _gun.Stream = stream;
        _gun.PitchScale = pitch;
        _gun.VolumeDb = -3f + db;
        _gun.Play();

        var cam = player.Camera;
        var hit = Shoot(cam.GlobalPosition, -cam.GlobalTransform.Basis.Z, player);
        if (hit == null) return;

        var s = hit.Species;
        int points = Journal.Bag(s, Month);
        _items.Ui.Toast(points > 0
            ? $"{s.Name} — +{points}   (score {Journal.Score})"
            : s.IsGame
                ? $"{s.Name}: out of season ({s.SeasonFrom}–{s.SeasonTo}). {points}"
                : $"PROTECTED: {s.Name} ({s.Latin}). {points}");
    }

    /// <summary>
    /// Fires a shot from <paramref name="from"/> along <paramref name="dir"/>: every bird within
    /// the flush radius takes off, and the nearest bird inside the shot cone that the world does
    /// not hide falls. Returns that bird, or null for a miss.
    /// </summary>
    public Bird? Shoot(Vector3 from, Vector3 dir, CollisionObject3D? shooter = null)
    {
        dir = dir.Normalized();
        Bird? best = null;
        float bestAlong = float.MaxValue;
        foreach (var b in _birds)
        {
            if (b.State is Bird.Mode.Falling or Bird.Mode.Dead) continue;
            var to = b.Centre - from;
            float along = to.Dot(dir);
            if (along < 1f || along > Range) continue;
            float off = (to - dir * along).Length();
            // the pattern opens to about 1.4 m across at 35 m
            float pattern = 0.1f + along * 0.018f;
            if (off > pattern + b.HitRadius) continue;
            // past 30 m the pellets thin out and a hit becomes a chance
            float chance = Mathf.Clamp(1f - (along - 30f) / 25f, 0f, 1f);
            if (_rng.NextDouble() > chance) continue;
            if (along < bestAlong) { bestAlong = along; best = b; }
        }

        if (best != null)
        {
            var exclude = new Godot.Collections.Array<Rid>();
            if (shooter != null) exclude.Add(shooter.GetRid());
            var query = PhysicsRayQueryParameters3D.Create(from, best.Centre, uint.MaxValue, exclude);
            var wall = GetWorld3D().DirectSpaceState.IntersectRay(query);
            if (wall.Count > 0 && from.DistanceTo(wall["position"].AsVector3()) < bestAlong - 0.5f) best = null;
        }

        foreach (var b in _birds)
            if (b != best && b.Node.GlobalPosition.DistanceTo(from) < ShotFlushRadius) b.Flush(from, _rng);

        if (best != null)
        {
            best.Kill(dir);
            Feathers(best.Centre, best.Species.Back, best.Species.Belly);
        }
        return best;
    }

    /// <summary>A bird took off near the player: let it call, if it is the calling kind.</summary>
    internal void Called(Bird b)
    {
        if (_callCooldown > 0 || b.Species.Body is not (BodyPlan.Passerine or BodyPlan.Corvid or BodyPlan.Wader or BodyPlan.Gull)) return;
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
    private readonly Node3D _wingA, _wingB;
    private readonly float _signA, _signB;
    private readonly BirdMesh.Parts _parts;

    public Mode State { get; private set; }
    public bool Seen { get; set; }
    public bool Gone { get; private set; }

    private Vector3 _velocity, _anchor, _walkTo;
    private float _phase, _timer, _flee, _yaw, _angle, _radius, _spin, _seed;
    private BirdLife _life = null!;

    public Bird(BirdSpecies species, float seed)
    {
        Species = species;
        _seed = seed;
        _parts = BirdMesh.Get(species);
        Node = new Node3D { Name = "Bird" };
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

    /// <summary>What the shot pattern has to touch: the body, plus the wings when they are spread.</summary>
    public float HitRadius => Species.Length * 0.4f + (Airborne ? Species.Wingspan * 0.25f : 0f);

    private bool Airborne => State is Mode.Flying or Mode.Soaring or Mode.Hovering;

    private float Cruise => Species.Flight == FlightStyle.Dart ? 12f + 6f * _seed : 5f + 9f * Mathf.Sqrt(Species.Length);
    private float FlapHz => 2.5f * Mathf.Pow(Mathf.Max(Species.Length, 0.08f), -0.7f);
    private float FlushDistance => 5f + 18f * Mathf.Sqrt(Species.Length);

    public void Begin(Mode mode, Vector3 at, BirdLife life, Random rng, float? lift = null)
    {
        _life = life;
        State = mode;
        _yaw = (float)(rng.NextDouble() * Mathf.Tau);
        _timer = (float)rng.NextDouble() * 3f;
        switch (mode)
        {
            case Mode.Soaring:
                _radius = 25f + 40f * (float)rng.NextDouble();
                _anchor = at + Vector3.Up * (50f + 110f * (float)rng.NextDouble());
                _angle = (float)(rng.NextDouble() * Mathf.Tau);
                at = _anchor + new Vector3(Mathf.Cos(_angle), 0, Mathf.Sin(_angle)) * _radius;
                break;
            case Mode.Hovering:
                at += Vector3.Up * (lift ?? 10f + 20f * (float)rng.NextDouble());
                _anchor = at;
                break;
            case Mode.Flying:
                at += Vector3.Up * (Species.Flight == FlightStyle.Dart ? 6f + 30f * (float)rng.NextDouble() : 12f + 30f * (float)rng.NextDouble());
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

    /// <summary>Takes off away from <paramref name="from"/> and keeps going.</summary>
    public void Flush(Vector3 from, Random rng)
    {
        if (State is Mode.Falling or Mode.Dead) return;
        var away = (Node.GlobalPosition - from) with { Y = 0 };
        if (away.LengthSquared() < 0.01f) away = Vector3.Forward;
        away = away.Normalized().Rotated(Vector3.Up, (float)(rng.NextDouble() - 0.5) * 1.4f);
        _velocity = away * Cruise * 1.2f + Vector3.Up * Cruise * 0.5f;
        _flee = 3f;
        bool wasGrounded = !Airborne;
        State = Mode.Flying;
        if (wasGrounded) _life.Called(this);
    }

    public void Kill(Vector3 shot)
    {
        State = Mode.Falling;
        _velocity = shot * 2f + Vector3.Up * 1.5f + (Airborne ? _velocity * 0.4f : Vector3.Zero);
        _spin = 6f;
    }

    public void Step(float dt, BirdLife life, Vector3? player)
    {
        var p = Node.GlobalPosition;
        if (player is { } me && State is Mode.Ground or Mode.Perched or Mode.Swimming
            && p.DistanceTo(me) < FlushDistance)
            Flush(me, new Random());

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
                float ground = life.Ground(p);
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
        Node.GlobalPosition = p;
        Pose(flap);
    }

    private void Pose(float flap)
    {
        bool wings = State is Mode.Flying or Mode.Soaring or Mode.Hovering or Mode.Falling;
        _wingA.Visible = _wingB.Visible = wings;
        _wingA.Rotation = new Vector3(0, 0, _signA * flap);
        _wingB.Rotation = new Vector3(0, 0, _signB * flap);

        // a node faces −Z, so a bird heading along (sin yaw, cos yaw) turns by yaw + π
        var basis = new Basis(Vector3.Up, _yaw + Mathf.Pi);
        if (State == Mode.Soaring) basis *= new Basis(Vector3.Back, -0.35f);
        if (State == Mode.Dead) basis *= new Basis(Vector3.Back, Mathf.Pi * 0.5f);
        Node.Basis = basis;
    }

    private float Hash(float x) => Mathf.PosMod(Mathf.Sin(x * 12.9898f + _seed * 78.233f) * 43758.547f, 1f);
}
