using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Occasions;

/// <summary>
/// What an occasion's <see cref="Occasion.Ambience"/> is handed each frame: where the ears are,
/// how dark it is, and a way to play a baked sound somewhere in the world.
/// </summary>
public sealed class OccasionAudio
{
    private readonly OccasionAmbience _owner;

    internal OccasionAudio(OccasionAmbience owner) => _owner = owner;

    public Vector3 Ears { get; internal set; }
    /// <summary>0 by day .. 1 at night, from <see cref="World.DayNight"/>.</summary>
    public float Night { get; internal set; }
    /// <summary>Seconds since the node started — for an occasion's own timers.</summary>
    public double Now { get; internal set; }
    /// <summary>Real local time: the church clock, like <see cref="Ambience"/>'s bells.</summary>
    public DateTime Clock => DateTime.Now;
    public Random Rng { get; } = new();

    public float Rand(float a, float b) => a + (float)Rng.NextDouble() * (b - a);

    /// <summary>One variant of a baked sound, or null while it is still baking off the main thread.</summary>
    public AudioStreamWav? Bank(string name, Func<Random, float[]> make, int variants = 3) =>
        _owner.Bank(name, make, variants, Rng);

    public void Speak(AudioStream stream, Vector3 at, float pitch, float db, float unit, float maxDistance) =>
        _owner.Speak(stream, at, pitch, db, unit, maxDistance);

    public void At(double delay, Action act) => _owner.At(delay, act);

    /// <summary>A ground point <paramref name="min"/>..<paramref name="max"/> m from the ears, optionally on a kind of cover.</summary>
    public Vector3? Spot(float min, float max, Func<CoverClass, bool>? accept = null) => _owner.Spot(Ears, min, max, accept, Rng);

    /// <summary>The nearest town's centre on the ground within <paramref name="within"/> m — where its church is.</summary>
    public Vector3? NearestTown(float within) => _owner.NearestTown(Ears, within);
}

/// <summary>
/// Plays the running occasions' sounds (the Audio facet): each occasion schedules its own in
/// <see cref="Occasion.Ambience"/>; this owns the voices, the baking and the placement. Kept apart
/// from <see cref="Ambience"/> so the everyday soundscape is untouched when nothing is running.
/// Client only.
/// </summary>
public partial class OccasionAmbience : Node
{
    private const int PoolSize = 6;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly Func<Node3D?> _listener;
    private readonly AudioStreamPlayer3D[] _pool = new AudioStreamPlayer3D[PoolSize];
    private readonly Dictionary<string, Task<AudioStreamWav[]>> _banks = new();
    private readonly List<(double Due, Action Act)> _queue = new();
    private readonly OccasionAudio _audio;
    private double _now;

    public OccasionAmbience(ChunkManager chunks, WorldOrigin origin, Func<Node3D?> listener)
    {
        Name = "OccasionAmbience";
        _chunks = chunks;
        _origin = origin;
        _listener = listener;
        _audio = new OccasionAudio(this);
    }

    public OccasionAmbience() : this(null!, null!, () => null) { }

    public override void _Ready()
    {
        SfxBus.Ensure();
        for (int i = 0; i < PoolSize; i++)
        {
            _pool[i] = new AudioStreamPlayer3D { Bus = SfxBus.Name, MaxPolyphony = 1 };
            AddChild(_pool[i]);
        }
    }

    public override void _Process(double delta)
    {
        _now += delta;
        if (OccasionManager.Instance is not { } m || _listener() is not { } ears || GameSettings.Current.AmbienceVolume < 0.001f)
        {
            _queue.Clear();
            return;
        }

        _audio.Ears = ears.GlobalPosition;
        _audio.Night = World.DayNight.Instance?.Night ?? 0f;
        _audio.Now = _now;
        foreach (var a in m.Active)
            if (a.Has(OccasionFacets.Audio)) a.Content.Ambience(_audio);

        for (int i = _queue.Count - 1; i >= 0; i--)
            if (_queue[i].Due <= _now)
            {
                var act = _queue[i].Act;
                _queue.RemoveAt(i);
                act();
            }
    }

    internal AudioStreamWav? Bank(string name, Func<Random, float[]> make, int variants, Random pick)
    {
        if (!_banks.TryGetValue(name, out var task))
        {
            // a bell is ~150k samples x 9 partials: bake off the main thread, like Ambience does
            _banks[name] = task = Task.Run(() => Enumerable.Range(0, variants)
                .Select(v => Dsp.Encode(Dsp.Normalise(make(new Random(name.GetHashCode() + v * 7919)), 0.9f)))
                .ToArray());
        }
        return task.IsCompletedSuccessfully ? task.Result[pick.Next(task.Result.Length)] : null;
    }

    internal void Speak(AudioStream stream, Vector3 at, float pitch, float db, float unit, float maxDistance)
    {
        foreach (var p in _pool)
        {
            if (p.Playing) continue;
            p.Stream = stream;
            p.GlobalPosition = at;
            p.PitchScale = pitch;
            p.VolumeDb = db + Mathf.LinearToDb(GameSettings.Current.AmbienceVolume);
            p.UnitSize = unit;
            p.MaxDistance = maxDistance;
            p.Play();
            return;
        }
    }

    internal void At(double delay, Action act) => _queue.Add((_now + delay, act));

    internal Vector3? Spot(Vector3 ears, float min, float max, Func<CoverClass, bool>? accept, Random rng)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            float a = (float)rng.NextDouble() * Mathf.Tau, d = min + (float)rng.NextDouble() * (max - min);
            var p = ears + new Vector3(Mathf.Cos(a) * d, 0, Mathf.Sin(a) * d);
            if (accept != null && (!_chunks.TryGetCover(p, out var c) || !accept(c))) continue;
            if (_chunks.TryGetHeight(p, out float y)) return p with { Y = y };
        }
        return null;
    }

    internal Vector3? NearestTown(Vector3 ears, float within)
    {
        var (e, n) = _origin.ToLv95(ears);
        foreach (var t in OccasionTowns.Near(e, n, within))
        {
            var p = _origin.ToWorld(t.E, t.N, 0);
            return p with { Y = _chunks.TryGetHeight(p, out float y) ? y : ears.Y };
        }
        return null;
    }
}
