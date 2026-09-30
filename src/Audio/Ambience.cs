using System.Reflection;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Audio;

/// <summary>
/// The sound of the place you are standing in: cowbells on the alps, brooks, birdsong in woods,
/// church bells on the hour, and the odd distant rockfall high up. Everything is procedural — the
/// project ships no audio files — and everything is read from the same data the world is built
/// from, so the sound follows the map: a brook is heard where a <see cref="RoadClass.Watercourse"/>
/// really runs, a church where <c>places.json</c> has a town, cowbells over meadow.
///
/// <para>
/// <b>Cost.</b> The environment is sampled at most every <see cref="SampleInterval"/> seconds
/// (a few dozen dictionary lookups); between samples <c>_Process</c> only pops due events off a
/// small queue. The only per-sample work is the brook, and only while water is within earshot.
/// Heavy bells (church, rockfall, cow) are rendered on a worker thread at start-up; only the cheap
/// byte packing happens on the main thread. Bird species are baked lazily — a few ms each.
/// </para>
/// </summary>
public partial class Ambience : Node
{
    private const float SampleInterval = 0.5f;
    private const int PoolSize = 12;
    private const float WaterReach = 60f;
    private const float ChurchReach = 1500f;

    private readonly ChunkManager _chunks;
    private readonly Func<Node3D?> _listener;
    private readonly Random _rng = new(20260929);

    /// <summary>Overall level 0..1 (the sound-effects setting). 0 silences everything.</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>
    /// The world origin. <see cref="ChunkManager"/> keeps its own private, so if the integrator
    /// does not set this it is read from there once by reflection.
    /// </summary>
    public WorldOrigin? Origin { get; set; }

    private double _now;
    private double _envTimer;
    private readonly List<(double Due, Action Act)> _queue = new();

    // what the last environment sample found
    private float _pasture, _wooded, _altitude, _ground;
    private float _townDistance = float.MaxValue;
    private TileId _tile;
    private bool _haveTile;

    private readonly AudioStreamPlayer3D[] _pool = new AudioStreamPlayer3D[PoolSize];

    // --- baked banks (filled from the worker thread's result) ---
    private Task<Baked>? _bake;
    private SfxBank? _cowBank, _churchBank, _rockBank;
    private readonly Dictionary<int, SfxBank> _birdBanks = new();

    private sealed record Baked(float[][] Cow, float[][] Church, float[][] Rock);

    private sealed record Cow(int Variant, float Pitch, float Db);
    private Cow[] _herd = [];

    // --- cows ---
    private double _cowNext;
    private float _herdActivity = 0.5f;

    // --- birds ---
    private double _birdNext;
    private int[] _birdSet = [];

    // --- church ---
    private (double E, double N, int Buildings, string Name)[] _towns = [];
    private Task<PlaceIndex?>? _placesTask;
    private double _placesRetry;
    private int _lastStruck = -1;

    // --- rockfall ---
    private double _rockNext;

    // --- brook ---
    private readonly BrookSynth _brookSynth = new(new Random(7));
    private AudioStreamPlayer3D _brook = null!;
    private AudioStreamGeneratorPlayback? _brookPb;
    private Vector2[] _push = [];
    private readonly Dictionary<TileId, List<RoadSegment>?> _water = new();
    private readonly HashSet<TileId> _waterLoading = new();
    private Vector3 _brookTarget;
    private float _brookDist = float.MaxValue, _brookWidth;
    private bool _brookLake, _brookPlaced;
    private float _brookGoal, _brookLevel;

    public Ambience(ChunkManager chunks, Func<Node3D?> listener)
    {
        _chunks = chunks;
        _listener = listener;
    }

    public Ambience() : this(null!, () => null) { }

    public override void _Ready()
    {
        Name = "Ambience";
        SfxBus.Ensure();

        for (int i = 0; i < PoolSize; i++)
        {
            _pool[i] = new AudioStreamPlayer3D { Bus = SfxBus.Name, MaxPolyphony = 1 };
            AddChild(_pool[i]);
        }

        // the brook is a live generator, not a baked clip: a stream never repeats and its level
        // and bubble density follow the channel width without re-baking anything
        _brook = new AudioStreamPlayer3D
        {
            Stream = new AudioStreamGenerator { MixRate = Dsp.Rate, BufferLength = 0.15f },
            Bus = SfxBus.Name, UnitSize = 6f, MaxDistance = 140f,
        };
        AddChild(_brook);

        // bells are the expensive renders (a church bell is ~150k samples x 20 partials); doing
        // them off the main thread keeps the first frames smooth. Only plain float arrays cross.
        _bake = Task.Run(BakeAll);
        StartPlacesLoad();
    }

    // ------------------------------------------------------------------------------------
    // baking
    // ------------------------------------------------------------------------------------

    private static Baked BakeAll()
    {
        var cows = new float[12][];
        for (int i = 0; i < cows.Length; i++) cows[i] = AmbienceDsp.CowBell(new Random(1000 + i * 131));
        var church = new float[3][];
        float[] primes = [187f, 233f, 279f];
        for (int i = 0; i < church.Length; i++) church[i] = AmbienceDsp.ChurchBell(new Random(2000 + i * 17), primes[i]);
        var rock = new float[3][];
        for (int i = 0; i < rock.Length; i++) rock[i] = AmbienceDsp.Rockfall(new Random(3000 + i * 29));
        return new Baked(cows, church, rock);
    }

    private void CheckBake()
    {
        if (_bake is not { IsCompleted: true } t) return;
        _bake = null;
        if (t.IsFaulted) { GD.PushWarning($"[ambience] bake failed: {t.Exception?.GetBaseException().Message}"); return; }
        var b = t.Result;
        static AudioStreamWav[] Enc(float[][] a) => a.Select(x => Dsp.Encode(Dsp.Normalise(x, 0.9f))).ToArray();
        _cowBank = new SfxBank("cowbell", Enc(b.Cow)) { PitchJitter = 0.02f, VolumeJitterDb = 2f };
        _churchBank = new SfxBank("church", Enc(b.Church)) { PitchJitter = 0.004f, VolumeJitterDb = 0.5f };
        _rockBank = new SfxBank("rockfall", Enc(b.Rock)) { PitchJitter = 0.1f, VolumeJitterDb = 2f };
        // a herd: each animal keeps its own bell, tuning and loudness for the whole session
        var r = new Random(99);
        _herd = Enumerable.Range(0, 8)
            .Select(i => new Cow(i % b.Cow.Length, 0.85f + (float)r.NextDouble() * 0.35f, -2f - (float)r.NextDouble() * 5f))
            .ToArray();
    }

    private SfxBank BirdBank(int species)
    {
        if (_birdBanks.TryGetValue(species, out var bank)) return bank;
        var pattern = AmbienceDsp.BirdPattern(species);
        // variants differ in pitch and timing by a few percent: recognisably the same bird, never
        // the same recording
        var variants = Enumerable.Range(0, 3)
            .Select(v => Dsp.Encode(Dsp.Normalise(AmbienceDsp.BirdCall(new Random(species * 977 + v * 31), pattern), 0.8f)))
            .ToArray();
        return _birdBanks[species] = new SfxBank($"bird{species}", variants) { PitchJitter = 0.02f, VolumeJitterDb = 2f };
    }

    private void StartPlacesLoad()
    {
        _placesTask = Task.Run(() =>
        {
            foreach (string dir in new[] { TerrainPaths.FindChunkDir(), TerrainPaths.FindCacheDir() })
            {
                string path = System.IO.Path.Combine(dir, PlaceIndex.FileName);
                if (!System.IO.File.Exists(path)) continue;
                try
                {
                    var idx = PlaceIndex.FromJson(System.IO.File.ReadAllText(path));
                    if (idx.Places.Count > 0) return idx;
                }
                catch { /* an unreadable index is just no church bells */ }
            }
            return null;
        });
    }

    /// <summary>Re-reads places.json (a streaming client gets it after connecting).</summary>
    public void ReloadPlaces() => StartPlacesLoad();

    // ------------------------------------------------------------------------------------
    // per frame
    // ------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (_chunks == null) return;
        _now += delta;
        CheckBake();
        PollPlaces();

        var ears = _listener();
        if (ears == null || Volume < 0.001f || ResolveOrigin() == null)
        {
            _queue.Clear();
            _brookGoal = 0;
            PumpBrook(delta, default);
            return;
        }

        var pos = ears.GlobalPosition;
        _envTimer -= delta;
        if (_envTimer <= 0)
        {
            _envTimer = SampleInterval;
            SampleEnvironment(pos);
            UpdateWater(pos);
        }

        RunCows(pos);
        RunBirds(pos);
        RunChurch(pos);
        RunRockfall(pos);

        for (int i = _queue.Count - 1; i >= 0; i--)
            if (_queue[i].Due <= _now)
            {
                var act = _queue[i].Act;
                _queue.RemoveAt(i);
                act();
            }

        PumpBrook(delta, pos);
    }

    private WorldOrigin? ResolveOrigin()
    {
        if (Origin != null) return Origin;
        Origin = typeof(ChunkManager).GetField("_origin", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_chunks) as WorldOrigin;
        return Origin;
    }

    private void PollPlaces()
    {
        if (_placesTask is { IsCompleted: true } t)
        {
            _placesTask = null;
            if (t.IsCompletedSuccessfully && t.Result is { } idx)
                _towns = idx.Places.Where(p => p.Kind == PlaceKind.Town && p.Buildings >= 20)
                    .Select(p => (p.E, p.N, p.Buildings, p.Name)).ToArray();
            else _placesRetry = _now + 30;   // not there yet: a client may still be downloading it
        }
        if (_placesTask == null && _towns.Length == 0 && _placesRetry > 0 && _now >= _placesRetry)
        {
            _placesRetry = 0;
            StartPlacesLoad();
        }
    }

    // ------------------------------------------------------------------------------------
    // sampling the surroundings
    // ------------------------------------------------------------------------------------

    private static bool IsPasture(CoverClass c) =>
        // Open is also the fallback for unmapped ground, and TLM maps no arable parcels, so
        // Open IS the meadow. Sparse larch pasture and wet meadows carry cows too.
        c is CoverClass.Open or CoverClass.OpenForest or CoverClass.Wetland or CoverClass.Clearcut;

    private void SampleEnvironment(Vector3 pos)
    {
        var origin = Origin!;
        var tile = origin.TileAt(pos);
        if (!_haveTile || tile != _tile)
        {
            _tile = tile;
            _haveTile = true;
            // the same forest keeps the same birds: species are drawn from the tile, not from time
            var r = new Random(unchecked(tile.E * 73856093 ^ tile.N * 19349663));
            _birdSet = Enumerable.Range(0, AmbienceDsp.SpeciesCount).OrderBy(_ => r.Next()).Take(2 + r.Next(2)).ToArray();
        }

        if (_chunks.TryGetHeight(pos, out float h)) { _ground = h; _altitude = h; }
        else { _ground = pos.Y - 1.5f; _altitude = pos.Y; }

        int known = 0, pasture = 0, wooded = 0;
        void Look(Vector3 p)
        {
            if (!_chunks.TryGetCover(p, out var c)) return;
            known++;
            if (IsPasture(c)) pasture++;
            if (CoverFormat.IsWooded(c)) wooded++;
        }
        Look(pos);
        for (int k = 0; k < 8; k++)
        {
            float a = k * Mathf.Tau / 8f;
            var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            Look(pos + dir * 40f);
            Look(pos + dir * 110f);
        }
        _pasture = known > 0 ? (float)pasture / known : 0f;
        _wooded = known > 0 ? (float)wooded / known : 0f;

        // a random walk, so the herd is busy for a while and then idle for a while
        _herdActivity = Mathf.Clamp(_herdActivity + ((float)_rng.NextDouble() - 0.5f) * 0.35f, 0.05f, 1f);

        _townDistance = float.MaxValue;
        foreach (var t in _towns)
        {
            float dx = (float)(t.E - origin.E) - pos.X, dz = (float)-(t.N - origin.N) - pos.Z;
            _townDistance = Mathf.Min(_townDistance, MathF.Sqrt(dx * dx + dz * dz));
        }
    }

    private static float Trapezoid(float x, float a, float b, float c, float d) =>
        x <= a || x >= d ? 0f : x < b ? (x - a) / (b - a) : x <= c ? 1f : (d - x) / (d - c);

    // ------------------------------------------------------------------------------------
    // voices
    // ------------------------------------------------------------------------------------

    private void Speak(AudioStream stream, Vector3 pos, float pitch, float db, float unit, float maxDist)
    {
        foreach (var p in _pool)
        {
            if (p.Playing) continue;
            p.Stream = stream;
            p.GlobalPosition = pos;
            p.PitchScale = pitch;
            p.VolumeDb = db + Mathf.LinearToDb(Volume);
            p.UnitSize = unit;
            p.MaxDistance = maxDist;
            p.Play();
            return;
        }
        // pool exhausted: dropping an ambient sound is inaudible, stealing a voice would click
    }

    private void At(double delay, Action act) => _queue.Add((_now + delay, act));

    private float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    /// <summary>A random ground spot 'min'..'max' m from the ears, or null when no height is known.</summary>
    private Vector3? Spot(Vector3 pos, float min, float max, Func<CoverClass, bool>? accept = null)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            float a = Rand(0, Mathf.Tau), d = Rand(min, max);
            var p = pos + new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
            if (accept != null && _chunks.TryGetCover(p, out var c) && !accept(c)) continue;
            if (!_chunks.TryGetHeight(p, out float y)) continue;
            p.Y = y;
            return p;
        }
        return null;
    }

    // ------------------------------------------------------------------------------------
    // 1. cowbells
    // ------------------------------------------------------------------------------------

    private void RunCows(Vector3 pos)
    {
        if (_cowBank == null || _herd.Length == 0 || _now < _cowNext) return;
        float alt = Trapezoid(_altitude, 700f, 900f, 2200f, 2450f);
        // near a village there is a road, a garden and a house, not a herd
        float village = _townDistance < 350f ? 0.15f : 1f;
        float chance = _pasture * alt * village;
        if (chance < 0.2f) { _cowNext = _now + 2; return; }

        var cow = _herd[_rng.Next(_herd.Length)];
        var spot = Spot(pos, 30f, 200f, IsPasture);
        if (spot is not { } start) { _cowNext = _now + 3; return; }

        // a cow shakes its head a few times as it grazes and walks, then goes quiet
        int clanks = 2 + _rng.Next(5);
        double t = 0;
        var bell = _cowBank.Variants[cow.Variant % _cowBank.Variants.Length];
        for (int i = 0; i < clanks; i++)
        {
            t += i == 0 ? 0 : Rand(0.3f, 1.7f);
            var at = start + new Vector3(Rand(-2, 2), 0, Rand(-2, 2)) * (1 + i * 0.4f);
            float pitch = cow.Pitch * (1f + Rand(-0.012f, 0.012f));
            float db = cow.Db + Rand(-3f, 1f);
            At(t, () => Speak(bell, at + Vector3.Up * 0.9f, pitch, db, 10f, 420f));
        }
        float silence = Rand(6f, 32f) / (0.25f + _herdActivity) / (0.4f + chance);
        _cowNext = _now + t + silence;
    }

    // ------------------------------------------------------------------------------------
    // 3. birds
    // ------------------------------------------------------------------------------------

    private static float BirdActivity()
    {
        var n = DateTime.Now;
        float h = n.Hour + n.Minute / 60f;
        // a dawn chorus, a quieter midday, a second wind toward dusk, and night silence
        if (h < 5.5f || h > 20.5f) return 0f;
        if (h < 9f) return 1f;
        if (h < 17f) return 0.45f;
        return 0.7f;
    }

    private void RunBirds(Vector3 pos)
    {
        if (_now < _birdNext || _birdSet.Length == 0) return;
        float act = BirdActivity();
        if (_wooded < 0.35f || act <= 0f) { _birdNext = _now + 3; return; }

        int species = _birdSet[_rng.Next(_birdSet.Length)];
        // birds sit in the canopy: a spot in wooded cover, well above the ground
        if (Spot(pos, 15f, 90f, CoverFormat.IsWooded) is { } s)
        {
            var bank = BirdBank(species);
            var (stream, pitch, db) = bank.Pick(_rng);
            Speak(stream, s + Vector3.Up * Rand(4f, 15f), pitch, db - 8f, 5f, 200f);

            if (_rng.NextDouble() < 0.3)   // another bird answers from somewhere else
                At(Rand(0.7f, 2f), () =>
                {
                    int other = _birdSet[_rng.Next(_birdSet.Length)];
                    if (Spot(pos, 15f, 90f, CoverFormat.IsWooded) is { } s2)
                    {
                        var (st, pi, d) = BirdBank(other).Pick(_rng);
                        Speak(st, s2 + Vector3.Up * Rand(4f, 15f), pi, d - 9f, 5f, 200f);
                    }
                });
        }
        _birdNext = _now + Rand(1.5f, 8f) / (_wooded * act + 0.05f);
    }

    // ------------------------------------------------------------------------------------
    // 4. church bells
    // ------------------------------------------------------------------------------------

    private void RunChurch(Vector3 pos)
    {
        if (_churchBank == null || _towns.Length == 0) return;
        var now = DateTime.Now;
        if (now.Minute != 0 || now.Second > 30) return;
        int key = now.DayOfYear * 24 + now.Hour;
        if (key == _lastStruck) return;
        _lastStruck = key;

        var origin = Origin!;
        int strikes = now.Hour % 12;
        if (strikes == 0) strikes = 12;

        var near = _towns
            .Select(t => (Town: t, X: (float)(t.E - origin.E), Z: (float)-(t.N - origin.N)))
            .Select(t => (t.Town, t.X, t.Z, D: MathF.Sqrt((t.X - pos.X) * (t.X - pos.X) + (t.Z - pos.Z) * (t.Z - pos.Z))))
            .Where(t => t.D < ChurchReach)
            .OrderBy(t => t.D).Take(2).ToList();

        foreach (var t in near)
        {
            // neighbouring villages never strike in step, and each keeps its own bell
            double offset = Rand(0f, 5f);
            var bell = _churchBank.Variants[Math.Abs(t.Town.Name.GetHashCode()) % _churchBank.Variants.Length];
            float wx = t.X, wz = t.Z;
            float ground = _chunks.TryGetHeight(new Vector3(wx, 0, wz), out float gy) ? gy : _ground;
            for (int i = 0; i < strikes; i++)
            {
                double when = offset + i * Rand(2.4f, 2.6f);
                At(when, () => Speak(bell, new Vector3(wx, ground + 22f, wz), 1f + Rand(-0.002f, 0.002f), 4f, 90f, 2400f));
            }
        }
    }

    // ------------------------------------------------------------------------------------
    // 5. rockfall
    // ------------------------------------------------------------------------------------

    private void RunRockfall(Vector3 pos)
    {
        if (_altitude < 2500f) { _rockNext = 0; return; }
        if (_rockBank == null) return;
        if (_rockNext == 0) { _rockNext = _now + Rand(20f, 120f); return; }   // not the moment you arrive
        if (_now < _rockNext) return;

        float a = Rand(0, Mathf.Tau), d = Rand(350f, 900f);
        var at = pos + new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
        // far tiles have no height in hand; a slope somewhere above/below the listener will do
        at.Y = _chunks.TryGetHeight(at, out float y) ? y + 30f : pos.Y + Rand(-100f, 200f);
        var (stream, pitch, db) = _rockBank.Pick(_rng);
        Speak(stream, at, pitch, db - 2f, 140f, 3000f);
        _rockNext = _now + Rand(60f, 180f);
    }

    // ------------------------------------------------------------------------------------
    // 2. streams
    // ------------------------------------------------------------------------------------

    private void UpdateWater(Vector3 pos)
    {
        var origin = Origin!;
        var tile = origin.TileAt(pos);
        var (e, n) = origin.ToLv95(pos);
        double lx = e - tile.MinE, lz = tile.MaxN - n;

        // the tile under the listener, plus any neighbour whose edge is within earshot
        var needed = new List<TileId> { tile };
        int dx = lx < 70 ? -1 : lx > 930 ? 1 : 0;
        int dz = lz < 70 ? 1 : lz > 930 ? -1 : 0;   // tile N grows northward, z southward
        if (dx != 0) needed.Add(new TileId(tile.E + dx, tile.N));
        if (dz != 0) needed.Add(new TileId(tile.E, tile.N + dz));
        if (dx != 0 && dz != 0) needed.Add(new TileId(tile.E + dx, tile.N + dz));

        float best = float.MaxValue;
        Vector3 bestPos = default;
        float bestWidth = 0;
        bool lake = false;

        foreach (var t in needed)
        {
            if (!_water.TryGetValue(t, out var segs)) { EnsureWater(t); continue; }
            if (segs == null) continue;
            double offX = t.MinE - origin.E, offZ = t.MaxN - origin.N;
            foreach (var s in segs)
            {
                var pts = s.Points;
                for (int i = 0; i + 5 < pts.Length; i += 3)
                {
                    float ax = (float)(offX + pts[i]), az = (float)(pts[i + 2] - offZ);
                    float bx = (float)(offX + pts[i + 3]), bz = (float)(pts[i + 5] - offZ);
                    float vx = bx - ax, vz = bz - az, len2 = vx * vx + vz * vz;
                    float u = len2 > 1e-6f ? Mathf.Clamp(((pos.X - ax) * vx + (pos.Z - az) * vz) / len2, 0f, 1f) : 0f;
                    float px = ax + vx * u, pz = az + vz * u;
                    float dd = MathF.Sqrt((px - pos.X) * (px - pos.X) + (pz - pos.Z) * (pz - pos.Z));
                    if (dd < best)
                    {
                        best = dd; bestWidth = s.Width;
                        bestPos = new Vector3(px, pts[i + 1] + u * (pts[i + 4] - pts[i + 1]), pz);
                        lake = false;
                    }
                }
            }
        }

        // mapped water cover (lakes and wide rivers the raster does see): a soft lapping instead
        for (int r = 1; r <= 4; r++)
            for (int k = 0; k < 8; k++)
            {
                float d = r * 15f, a = k * Mathf.Tau / 8f + r * 0.4f;
                var p = pos + new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
                if (d >= best) continue;
                if (_chunks.TryGetCover(p, out var c) && c == CoverClass.Water && _chunks.TryGetHeight(p, out float y))
                {
                    best = d; bestPos = new Vector3(p.X, y, p.Z); bestWidth = 12f; lake = true;
                }
            }

        _brookDist = best;
        if (best <= WaterReach)
        {
            _brookTarget = bestPos + Vector3.Up * 0.3f;
            _brookWidth = bestWidth;
            _brookLake = lake;
        }
        // proximity curve: the sound is on the point of audibility at the edge, loud beside it
        float prox = Mathf.Clamp(1f - best / WaterReach, 0f, 1f);
        float width = Mathf.Clamp(bestWidth / 6f, 0.3f, 1f);
        _brookGoal = best <= WaterReach ? prox * prox * (0.45f + 0.55f * width) * (lake ? 0.5f : 1f) : 0f;

        if (_water.Count > 16)
            foreach (var k in _water.Keys.Where(k => !needed.Contains(k)).ToList()) _water.Remove(k);
    }

    private void EnsureWater(TileId tile)
    {
        if (!_waterLoading.Add(tile) || _chunks.Source is not { } source) return;
        LoadWater(source, tile);
    }

    private int _waterEpoch;

    /// <summary>Drops the cached stream lines, for when the world under them is replaced.</summary>
    public void ForgetTiles()
    {
        _waterEpoch++;
        _water.Clear();
        _waterLoading.Clear();
    }

    private async void LoadWater(IChunkSource source, TileId tile)
    {
        int epoch = _waterEpoch;
        try
        {
            var roads = await source.LoadRoadsAsync(tile);
            // same filter as Gathering: a tunnelled channel is under a mountain, not beside you
            if (epoch == _waterEpoch)
                _water[tile] = roads?.Segments
                    .Where(s => s.Class is RoadClass.Watercourse or RoadClass.Bisse && (s.Flags & RoadFlags.Tunnel) == 0)
                    .ToList();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[ambience] water tile {tile}: {ex.Message}");
            if (epoch == _waterEpoch) _water[tile] = null;
        }
        finally { if (epoch == _waterEpoch) _waterLoading.Remove(tile); }
    }

    private void PumpBrook(double delta, Vector3 ears)
    {
        bool want = _brookGoal > 0.01f;
        if (want && !_brook.Playing)
        {
            _brook.GlobalPosition = _brookTarget;
            _brook.Play();
            _brookPb = (AudioStreamGeneratorPlayback)_brook.GetStreamPlayback();
            _brookLevel = 0;
        }
        if (!_brook.Playing || _brookPb == null) return;

        // glide toward the nearest water point rather than jumping when it changes
        if (want) _brook.GlobalPosition = _brook.GlobalPosition.Lerp(_brookTarget, 1f - MathF.Exp(-4f * (float)delta));
        _brook.VolumeDb = Mathf.LinearToDb(Mathf.Max(Volume, 1e-4f)) - 3f;

        int frames = _brookPb.GetFramesAvailable();
        if (frames > 0)
        {
            if (_push.Length != frames) _push = new Vector2[frames];
            float wide = _brookLake ? 0.2f : Mathf.Clamp(_brookWidth / 8f, 0.1f, 1f);
            float density = _brookLake ? 45f : 60f + 200f * wide;
            float lowMul = 1f / (1f + wide * 1.2f);   // wider water, bigger bubbles, lower pitch
            const float k = 1f / (0.2f * Dsp.Rate);
            for (int i = 0; i < frames; i++)
            {
                _brookLevel += (_brookGoal - _brookLevel) * k;
                float s = _brookLevel < 1e-4f ? 0f : _brookSynth.Next(density, lowMul, 0.35f + 0.4f * wide) * _brookLevel;
                _push[i] = new Vector2(s, s);
            }
            _brookPb.PushBuffer(_push);
        }
        if (!want && _brookLevel < 0.002f)
        {
            _brook.Stop();
            _brookPb = null;
        }
    }
}

/// <summary>
/// A live babbling brook after Andy Farnell's <i>Designing Sound</i>: running water is thousands
/// of tiny air bubbles, each a damped sine whose pitch rises as it decays. Bubble radius sets the
/// pitch (small bubbles are high), the decay rate follows from the pitch (a small bubble rings
/// out faster), and the number born per second sets how fast the water seems to run. A soft
/// low-passed noise bed under it stands for the water body itself.
/// </summary>
internal sealed class BrookSynth
{
    private struct Bubble { public float Phase, Dphase, Growth, Amp, Fall; public int Age; }

    private const int MaxBubbles = 32;
    private readonly Bubble[] _b = new Bubble[MaxBubbles];
    private int _count;
    private readonly Random _rng;
    private float _lp1, _lp2, _wander = 1f;

    public BrookSynth(Random rng) => _rng = rng;

    public float Next(float density, float lowMul, float bed)
    {
        if (_count < MaxBubbles && _rng.NextDouble() < density / Dsp.Rate) Spawn(lowMul);

        float sum = 0;
        for (int i = 0; i < _count;)
        {
            ref var b = ref _b[i];
            float attack = MathF.Min(1f, b.Age / 22f);   // ~1 ms, or each bubble begins with a click
            sum += MathF.Sin(b.Phase) * b.Amp * attack;
            b.Phase += b.Dphase;
            if (b.Phase > MathF.Tau) b.Phase -= MathF.Tau;
            b.Dphase *= b.Growth;
            b.Amp *= b.Fall;
            b.Age++;
            if (b.Amp < 0.004f) _b[i] = _b[--_count];
            else i++;
        }

        float n = (float)(_rng.NextDouble() * 2 - 1);
        _lp1 += Dsp.Coef(420f) * (n - _lp1);
        _lp2 += Dsp.Coef(2400f) * (n - _lp2);
        _wander = Math.Clamp(_wander + ((float)_rng.NextDouble() - 0.5f) * 0.0006f, 0.6f, 1.4f);
        float noiseBed = (_lp1 * 1.1f + (_lp2 - _lp1) * 0.22f) * bed * 0.6f * _wander;

        float y = MathF.Tanh(sum * 0.32f + noiseBed);
        return float.IsFinite(y) ? y : 0f;
    }

    private void Spawn(float lowMul)
    {
        // log-uniform between 300 and 3000 Hz: equal numbers per octave-ish, as in real water
        float f = MathF.Max(120f, 300f * lowMul * MathF.Pow(10f, (float)_rng.NextDouble()));
        float decay = 0.043f * f + 0.0014f * MathF.Pow(f, 1.5f);   // Farnell's damping for a bubble of that size
        float rise = 0.35f * decay;                                 // pitch climbs while it rings
        float u = (float)_rng.NextDouble();
        _b[_count++] = new Bubble
        {
            Phase = 0,
            Dphase = MathF.Tau * f / Dsp.Rate,
            Growth = 1f + rise / Dsp.Rate,
            Amp = 0.2f + 0.6f * u * u,
            Fall = MathF.Exp(-decay / Dsp.Rate),
            Age = 0,
        };
    }
}

/// <summary>The offline renderers behind <see cref="Ambience"/> (all pure functions of a seed).</summary>
internal static class AmbienceDsp
{
    public const int SpeciesCount = 10;

    private readonly record struct Partial(float Hz, float Amp, float Decay);

    /// <summary>
    /// Modal synthesis: a struck bell is a sum of decaying sinusoids at its vibration modes, plus
    /// a short noisy click for the strike. <paramref name="beat"/> adds a slightly detuned twin of
    /// every mode — a real bell is never perfectly round, so each mode splits in two and beats.
    /// </summary>
    private static float[] Modal(Random rng, float seconds, IReadOnlyList<Partial> ps, float click, float beat)
    {
        int n = (int)(seconds * Dsp.Rate);
        var s = new float[n];
        foreach (var p in ps)
        {
            int limit = Math.Min(n, (int)(7f / p.Decay * Dsp.Rate));   // stop at e^-7, inaudible
            for (int twin = 0; twin < (beat > 0 ? 2 : 1); twin++)
            {
                float hz = p.Hz * (twin == 0 ? 1f : 1f + beat);
                float a = p.Amp * (twin == 0 ? 1f : 0.6f);
                float ph = (float)rng.NextDouble() * MathF.Tau;
                float w = MathF.Tau * hz / Dsp.Rate, fall = MathF.Exp(-p.Decay / Dsp.Rate);
                for (int i = 0; i < limit; i++)
                {
                    s[i] += a * MathF.Sin(ph);
                    ph += w;
                    a *= fall;
                }
            }
        }
        if (click > 0)
        {
            int m = Math.Min(n, (int)(0.004f * Dsp.Rate));
            float prev = 0;
            for (int i = 0; i < m; i++)
            {
                float x = (float)(rng.NextDouble() * 2 - 1);
                s[i] += (x - prev) * click * MathF.Exp(-i / (0.0012f * Dsp.Rate));   // differenced = high-passed
                prev = x;
            }
        }
        return s;
    }

    /// <summary>
    /// A cowbell: a tapered sheet-metal bell. Its modes are strongly inharmonic (ratios near
    /// 1, 2.32, 4.25, 6.63, 9.38) and the upper ones die fast, which is the "clonk" then the
    /// ringing tin. Each seed gets its own size, taper and damping — every cow's bell is different.
    /// </summary>
    public static float[] CowBell(Random rng)
    {
        float f0 = 420f + (float)rng.NextDouble() * 520f;
        float[] ratios = [1f, 2.32f, 4.25f, 6.63f, 9.38f];
        float[] amps = [1f, 0.75f, 0.5f, 0.3f, 0.18f];
        float d0 = 5f + (float)rng.NextDouble() * 5f;
        var ps = new List<Partial>();
        for (int i = 0; i < ratios.Length; i++)
        {
            float hz = f0 * ratios[i] * (1f + ((float)rng.NextDouble() - 0.5f) * 0.03f);
            if (hz > 9500) continue;
            ps.Add(new Partial(hz, amps[i] * (0.7f + 0.6f * (float)rng.NextDouble()), d0 * (1f + i * (0.7f + 0.5f * (float)rng.NextDouble()))));
        }
        return Modal(rng, 1.2f, ps, 0.6f, 0f);
    }

    /// <summary>
    /// A large church bell (prime near 190-280 Hz): hum, prime, minor-third tierce, quint,
    /// nominal and the upper partials, low ones ringing for seconds. The minor third over the
    /// prime is what makes a bell sound like a bell rather than a chord.
    /// </summary>
    public static float[] ChurchBell(Random rng, float prime)
    {
        (float ratio, float amp, float decay)[] modes =
        [
            (0.5f, 0.55f, 0.32f),   // hum
            (1.0f, 1.00f, 0.45f),   // prime
            (1.2f, 0.85f, 0.55f),   // tierce: the minor third
            (1.5f, 0.50f, 0.80f),   // quint
            (2.0f, 0.70f, 1.00f),   // nominal
            (2.5f, 0.30f, 1.40f),
            (3.0f, 0.30f, 1.80f),
            (4.0f, 0.22f, 2.40f),
            (5.4f, 0.12f, 3.40f),
        ];
        var ps = modes.Select(m => new Partial(
            prime * m.ratio * (1f + ((float)rng.NextDouble() - 0.5f) * 0.008f),
            m.amp * (0.85f + 0.3f * (float)rng.NextDouble()),
            m.decay * (0.9f + 0.2f * (float)rng.NextDouble()))).ToList();
        return Modal(rng, 8f, ps, 0.35f, 0.0018f);
    }

    /// <summary>
    /// Distant rockfall: a rumble that swells, a shower of small rock impacts (each a short
    /// decaying resonance, 90-700 Hz, with a noisy edge) thinning out, all low-passed as
    /// distance would — high frequencies do not survive a kilometre of mountain air.
    /// </summary>
    public static float[] Rockfall(Random rng)
    {
        float seconds = 6f + (float)rng.NextDouble() * 3f;
        int n = (int)(seconds * Dsp.Rate);
        var s = new float[n];
        var hits = new List<(float Phase, float W, float Amp, float Fall)>();
        float rumble = 0;
        float peakAt = 0.15f + (float)rng.NextDouble() * 0.1f;   // fraction of the way through
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / n;
            float env = t < peakAt ? t / peakAt : MathF.Exp(-(t - peakAt) * 4.2f);
            env *= env * (3f - 2f * MathF.Min(1f, env));   // eased, so it swells rather than starts
            if (hits.Count < 60 && rng.NextDouble() < env * 280.0 / Dsp.Rate)
            {
                float hz = 90f + 610f * MathF.Pow((float)rng.NextDouble(), 1.6f);
                hits.Add((0, MathF.Tau * hz / Dsp.Rate, (0.2f + (float)rng.NextDouble()) * (0.4f + env), MathF.Exp(-(25f + hz * 0.05f) / Dsp.Rate)));
            }
            float sum = 0;
            for (int h = 0; h < hits.Count;)
            {
                var b = hits[h];
                sum += MathF.Sin(b.Phase) * b.Amp;
                b.Phase += b.W; b.Amp *= b.Fall;
                if (b.Amp < 0.004f) { hits[h] = hits[^1]; hits.RemoveAt(hits.Count - 1); }
                else { hits[h] = b; h++; }
            }
            float nz = (float)(rng.NextDouble() * 2 - 1);
            rumble += 0.012f * (nz - rumble);
            s[i] = sum * 0.35f + rumble * 9f * env;
        }
        return Dsp.LowPass(s, Dsp.Coef(1100f));
    }

    /// <summary>
    /// One species' fixed song: 2-8 FM chirps in a pattern (repeated, rising, falling or varied)
    /// derived only from the species number, so a bird always sings its own tune.
    /// </summary>
    public readonly record struct Note(float F0, float F1, float Dur, float Gap, float Vib, float VibHz, float Fm);

    public static Note[] BirdPattern(int species)
    {
        var r = new Random(species * 7919 + 13);
        float R() => (float)r.NextDouble();
        int count = 2 + r.Next(7);
        float baseF = 2300f + R() * 2000f;
        int kind = r.Next(4);
        float dur = 0.04f + R() * 0.09f, gap = 0.03f + R() * 0.1f;
        bool up = r.Next(2) == 0;
        float sweep = 0.15f + R() * 0.5f;
        float vib = R() < 0.4f ? 0.01f + R() * 0.03f : 0f, vibHz = 20f + R() * 40f;
        float fm = 0.2f + R() * 0.7f;
        var notes = new Note[count];
        for (int i = 0; i < count; i++)
        {
            float f0 = kind switch
            {
                0 => baseF,
                1 => baseF * (1f + 0.07f * i),
                2 => baseF * (1f - 0.05f * i),
                _ => baseF * (0.8f + 0.5f * R()),
            };
            f0 = Math.Clamp(f0, 1900f, 5000f);
            float f1 = Math.Clamp(f0 * (up ? 1f + sweep : 1f - sweep * 0.6f), 1800f, 5200f);
            bool last = i == count - 1;
            notes[i] = new Note(f0, f1, last ? dur * 1.6f : dur, i % 3 == 2 ? gap * 2.2f : gap, vib, vibHz, fm);
        }
        return notes;
    }

    public static float[] BirdCall(Random rng, Note[] pattern)
    {
        float pitchMul = 1f + ((float)rng.NextDouble() - 0.5f) * 0.08f;
        float timeMul = 1f + ((float)rng.NextDouble() - 0.5f) * 0.16f;
        float total = 0.05f;
        foreach (var nt in pattern) total += (nt.Dur + nt.Gap) * timeMul;
        var s = new float[(int)(total * Dsp.Rate)];

        int pos = 0;
        foreach (var nt in pattern)
        {
            int len = (int)(nt.Dur * timeMul * Dsp.Rate);
            float carrier = 0, mod = 0;
            for (int i = 0; i < len && pos + i < s.Length; i++)
            {
                float u = (float)i / len, t = (float)i / Dsp.Rate;
                float e = u * u * (3f - 2f * u);                             // smoothstep glissando
                float f = (nt.F0 + (nt.F1 - nt.F0) * e) * pitchMul;
                f *= 1f + nt.Vib * MathF.Sin(MathF.Tau * nt.VibHz * t);
                f = MathF.Min(f, 5400f);   // the FM sidebands must stay under Nyquist (11 kHz)
                carrier += MathF.Tau * f / Dsp.Rate;
                mod += MathF.Tau * f / Dsp.Rate;
                float hann = MathF.Sin(MathF.PI * u);
                float env = MathF.Sqrt(hann);
                s[pos + i] += MathF.Sin(carrier + nt.Fm * (1f - u * 0.6f) * MathF.Sin(mod)) * env;
            }
            pos += (int)((nt.Dur + nt.Gap) * timeMul * Dsp.Rate);
        }
        return s;
    }
}
