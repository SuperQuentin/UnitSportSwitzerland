using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// The sound of a working building site (#617): hammering on the shell, the vibrator in a pour, an
/// angle grinder, a reversing beeper in the yard, a radio in the office, a crane's slewing motor.
/// Client only, created by <c>ClientWorld</c> with the other audio.
///
/// <para>
/// <b>Which sites.</b> The sites are planned from the tiles round the listener
/// (<see cref="SitePlans.For"/> on a worker, cached per tile), the same sites every peer works out.
/// Only the <see cref="SiteSoundPlan.MaxSites"/> nearest within <see cref="SiteSoundPlan.HearRange"/>
/// of their fence make sound. <b>When</b> is <see cref="SiteSoundPlan"/>: only in working hours
/// (<see cref="CraneMotion.Working"/>, the environment clock, as <see cref="SiteCranes"/> reads it),
/// so a site falls silent when its crane stops. The gaps between sounds run on the real clock: they
/// are audio presentation, not the world's.
/// </para>
///
/// <para>
/// <b>Cost.</b> A tick every <see cref="TickEvery"/> s looks at a handful of cached sites and asks
/// a site for its next sound once every several seconds; between ticks <c>_Process</c> only pops
/// due hammer blows off a fixed queue. Everything is preallocated; the clips are baked on a
/// worker the first time a site is near. <b>The origin</b>: sites and queued blows are held
/// tile-local, turned into world space only at the moment a sound plays, so nothing is cached in a
/// frame an origin shift could invalidate. The pool's players are <c>TopLevel</c>, which the shifter
/// moves itself.
/// </para>
/// </summary>
public partial class SiteSounds : Node
{
    private const float TickEvery = 0.25f;
    private const int PoolSize = 8, QueueSize = 24;
    /// <summary>A tile is planned when its edge is within this of the listener: a site reaches past its building.</summary>
    private const float TileReach = SiteSoundPlan.HearRange + 60f;
    private const float MaxDistance = SiteSoundPlan.HearRange + 25f;

    private sealed class Planned
    {
        public required ConstructionSite Site;
        public double NextAt = double.NaN;
        public long Step;
    }

    private struct Blow
    {
        public bool Used;
        public double Due;
        public TileId Tile;
        public Vector3 Local;
        public SiteSoundKind Kind;
        public float Db;
    }

    private readonly ChunkManager _chunks;
    private readonly Func<Node3D?> _listener;
    private readonly Random _rng = new(617);

    private readonly Dictionary<TileId, Planned[]> _tiles = new();
    private readonly Dictionary<TileId, Task<Planned[]>> _loading = new();
    private readonly List<TileId> _scratch = new();
    private readonly AudioStreamPlayer3D[] _pool = new AudioStreamPlayer3D[PoolSize];
    private readonly SfxBank?[] _banks = new SfxBank?[6];
    private readonly Blow[] _queue = new Blow[QueueSize];
    private readonly Planned?[] _near = new Planned?[SiteSoundPlan.MaxSites];
    private readonly float[] _nearDist = new float[SiteSoundPlan.MaxSites];
    private readonly TileId[] _nearTile = new TileId[SiteSoundPlan.MaxSites];

    private Task<float[][][]>? _bake;
    private TileId _here;
    private bool _haveHere, _baked;
    private double _now, _tickIn;

    public SiteSounds(ChunkManager chunks, Func<Node3D?> listener)
    {
        _chunks = chunks;
        _listener = listener;
    }

    public SiteSounds() : this(null!, () => null) { }

    public override void _Ready()
    {
        Name = "SiteSounds";
        SfxBus.Ensure();
        for (int i = 0; i < PoolSize; i++)
        {
            _pool[i] = new AudioStreamPlayer3D { Bus = SfxBus.Name, TopLevel = true, MaxDistance = MaxDistance, MaxPolyphony = 1 };
            AddChild(_pool[i]);
        }
    }

    public override void _Process(double delta)
    {
        if (_chunks == null) return;
        _now += delta;
        RunQueue();
        _tickIn -= delta;
        if (_tickIn > 0) return;
        _tickIn = TickEvery;
        Tick();
    }

    /// <summary>The planned sites round the listener, the nearest asked for their next sound.</summary>
    private void Tick()
    {
        var origin = _chunks.Origin;
        var source = _chunks.Source;
        if (origin == null || source == null || _listener() is not { } ears) return;
        var pos = ears.GlobalPosition;
        var (e, n) = origin.ToLv95(pos);
        var here = TileId.FromLv95(e, n);
        if (!_haveHere || here != _here)
        {
            _here = here;
            _haveHere = true;
            Evict(here);
        }
        Request(source, here, e, n);
        Collect();
        Pick(origin, pos);
        if (_near[0] == null) return;

        if (_bake == null && !_baked) _bake = Task.Run(BakeAll);
        if (_bake is { IsCompleted: true } done)
        {
            _bake = null;
            if (done.IsCompletedSuccessfully) MakeBanks(done.Result);
            else GD.PushWarning($"[sitesounds] bake failed: {done.Exception?.GetBaseException().Message}");
        }
        if (!_baked) return;

        double hour = World.WorldClock.CurrentHour;
        long day = (long)Math.Floor((World.WorldClock.EnvNow + World.WorldClock.HourShift) / 86400.0);
        for (int i = 0; i < _near.Length; i++)
            if (_near[i] is { } p)
            {
                // the first sound comes a moment after arriving, and not in step with the next site's
                if (double.IsNaN(p.NextAt)) p.NextAt = _now + Fnv.Unit(p.Site.Key + "|snd|first") * 4.0;
                if (p.NextAt <= _now) Schedule(p, _nearTile[i], hour, day);
            }
    }

    /// <summary>Plans, on a worker, every tile within reach that has not been.</summary>
    private void Request(IChunkSource source, TileId here, double e, double n)
    {
        for (int dE = -1; dE <= 1; dE++)
            for (int dN = -1; dN <= 1; dN++)
            {
                var id = new TileId(here.E + dE, here.N + dN);
                if (_tiles.ContainsKey(id) || _loading.ContainsKey(id)) continue;
                double dx = Math.Max(0, Math.Max(id.MinE - e, e - (id.MinE + 1000)));
                double dz = Math.Max(0, Math.Max(id.MinN - n, n - (id.MinN + 1000)));
                if (dx * dx + dz * dz > TileReach * TileReach) continue;
                _loading[id] = Task.Run(() => PlanTile(source, id));
            }
    }

    private static async Task<Planned[]> PlanTile(IChunkSource source, TileId id)
    {
        try
        {
            var buildings = await source.LoadBuildingsAsync(id).ConfigureAwait(false);
            if (buildings == null || !SitePlans.HasSite(buildings)) return [];
            var roads = await source.LoadRoadsAsync(id).ConfigureAwait(false);
            return SitePlans.For(buildings, roads).Select(s => new Planned { Site = s }).ToArray();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[sitesounds] tile {id}: {ex.Message}");
            return [];
        }
    }

    private void Collect()
    {
        if (_loading.Count == 0) return;
        _scratch.Clear();
        foreach (var (id, task) in _loading)
            if (task.IsCompleted) _scratch.Add(id);
        foreach (var id in _scratch)
        {
            var task = _loading[id];
            _loading.Remove(id);
            _tiles[id] = task.IsCompletedSuccessfully ? task.Result : [];
        }
    }

    /// <summary>Tiles two or more away are forgotten: a site is re-planned if the listener comes back.</summary>
    private void Evict(TileId here)
    {
        _scratch.Clear();
        foreach (var id in _tiles.Keys)
            if (Math.Abs(id.E - here.E) > 2 || Math.Abs(id.N - here.N) > 2) _scratch.Add(id);
        foreach (var id in _scratch) _tiles.Remove(id);
    }

    /// <summary>The <see cref="SiteSoundPlan.MaxSites"/> nearest sites within earshot, nearest first.</summary>
    private void Pick(WorldOrigin origin, Vector3 pos)
    {
        Array.Clear(_near);
        foreach (var (id, sites) in _tiles)
        {
            if (sites.Length == 0) continue;
            var at = origin.ToWorld(id.MinE, id.MaxN, 0);
            var local = new Vector2(pos.X - at.X, pos.Z - at.Z);
            foreach (var p in sites)
            {
                float d = SiteSoundPlan.Distance(p.Site, local);
                if (d > SiteSoundPlan.HearRange) continue;
                // insert in order, keeping the nearest few
                for (int k = 0; k < _near.Length; k++)
                {
                    if (_near[k] != null && _nearDist[k] <= d) continue;
                    for (int m = _near.Length - 1; m > k; m--)
                    {
                        _near[m] = _near[m - 1];
                        _nearDist[m] = _nearDist[m - 1];
                        _nearTile[m] = _nearTile[m - 1];
                    }
                    _near[k] = p;
                    _nearDist[k] = d;
                    _nearTile[k] = id;
                    break;
                }
            }
        }
    }

    private void Schedule(Planned p, TileId tile, double hour, long day)
    {
        if (SiteSoundPlan.Next(p.Site, p.Step, hour, day) is not { } ev)
        {
            p.NextAt = _now + 5;   // not working: look again in a few seconds
            return;
        }
        p.Step++;
        p.NextAt = _now + ev.Wait;
        for (int i = 0; i < ev.Strikes; i++)
        {
            // a hand's rhythm, not a machine's
            double due = _now + i * ev.StrikeGap * (0.85 + _rng.NextDouble() * 0.3);
            Enqueue(due, tile, ev.At, ev.Kind, ev.Db - (i == 0 ? 0f : (float)_rng.NextDouble() * 2f));
        }
    }

    private void Enqueue(double due, TileId tile, Vector3 local, SiteSoundKind kind, float db)
    {
        for (int i = 0; i < _queue.Length; i++)
        {
            if (_queue[i].Used) continue;
            _queue[i] = new Blow { Used = true, Due = due, Tile = tile, Local = local, Kind = kind, Db = db };
            return;
        }
        // a full queue drops a blow: inaudible
    }

    private void RunQueue()
    {
        for (int i = 0; i < _queue.Length; i++)
        {
            if (!_queue[i].Used || _queue[i].Due > _now) continue;
            var b = _queue[i];
            _queue[i].Used = false;
            if (_chunks.Origin is not { } origin) continue;
            Play(b.Kind, origin.ToWorld(b.Tile.MinE, b.Tile.MaxN, 0) + b.Local, b.Db);
        }
    }

    private void Play(SiteSoundKind kind, Vector3 at, float db)
    {
        if (_banks[(int)kind] is not { } bank) return;
        foreach (var p in _pool)
        {
            if (p.Playing) continue;
            var (stream, pitch, volume) = bank.Pick(_rng);
            p.Stream = stream;
            p.PitchScale = pitch;
            p.VolumeDb = db + volume + Mathf.LinearToDb(SfxBus.SliderGain(GameSettings.Current.AmbienceVolume));
            p.UnitSize = kind is SiteSoundKind.Beeper or SiteSoundKind.Radio ? 6f : 10f;
            p.GlobalPosition = at;
            p.Play();
            return;
        }
        // pool exhausted: dropping a site sound is inaudible, stealing a voice would click
    }

    private static float[][][] BakeAll()
    {
        var all = new float[6][][];
        for (int k = 0; k < all.Length; k++) all[k] = SiteSfx.Clips((SiteSoundKind)k);
        return all;
    }

    private void MakeBanks(float[][][] clips)
    {
        string[] names = ["site_hammer", "site_grinder", "site_vibrator", "site_beeper", "site_radio", "site_crane"];
        float[] pitchJitter = [0.05f, 0.03f, 0.02f, 0.015f, 0.01f, 0.03f];
        for (int k = 0; k < clips.Length; k++)
            _banks[k] = new SfxBank(names[k], clips[k].Select(Dsp.Encode).ToArray())
                { PitchJitter = pitchJitter[k], VolumeJitterDb = k == 0 ? 2.5f : 1.5f };
        _baked = true;
    }
}
