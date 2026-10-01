using System.Linq;
using Godot;

namespace UnitSport.Gpx.Cinema;

/// <summary>
/// Chooses what to film and when to cut.
///
/// <para>
/// It reads the event timeline from <see cref="TrackAnalyser"/>, so it knows what is about to
/// happen and can be in position before it does — cutting <i>ahead</i> of a moment rather than
/// reacting to one. That is the whole difference between an edit and a security camera.
/// </para>
///
/// <para>
/// Every candidate placement is tested before it is committed: a camera under the terrain or
/// behind a ridge is rejected and another is tried. Without that the mode spends a third of its
/// running time filming the inside of a hillside, which is the failure everyone who builds one of
/// these hits first.
/// </para>
/// </summary>
public sealed class Director
{
    /// <summary>
    /// How far ahead of a moment to cut, in SCREEN seconds, so the camera has settled when it
    /// lands. Converted to track time at the current clock speed — at 8x, 1.6 seconds of screen
    /// time is nearly 13 seconds of the run, and using the raw figure gave a fifth of a second of
    /// warning, which is no warning at all.
    /// </summary>
    private const double LeadSeconds = 1.6;

    /// <summary>
    /// Ceiling on that widened lead, in TRACK seconds.
    ///
    /// <para>
    /// Scaling the lead by the clock is right, but unbounded it eats the timeline. At 32x it
    /// opens a 51-second-wide window while the clock also advances 32 track seconds per screen
    /// second, so on any ordinary run the window is never empty - every frame past a shot's
    /// MinSeconds sees an "imminent" moment and cuts, then cuts again on the next frame because
    /// the same event is still inside it. That is the whole of the "cuts far too fast at speed"
    /// complaint: the shot LENGTHS were always in screen seconds and always correct.
    /// </para>
    /// </summary>
    private const double MaxLeadSeconds = 12.0;

    /// <summary>Placements tried before falling back to the chase.</summary>
    private const int Attempts = 6;

    private Shot[] _shots =
    {
        new StabilisedHead(), new HelmetPov(), new OverTheShoulder(), new AnkleCam(),
        new Handheld(), new DroneOrbit(), new DroneReveal(), new LockedOff(),
        new LowHeroPass(), new TopDown(), new ChaseShot(),
    };

    private readonly RandomNumberGenerator _rng = new();
    private IReadOnlyList<CinemaEvent> _events = Array.Empty<CinemaEvent>();

    private Shot? _current;
    private Shot? _previous;
    private double _held;
    private double _target;
    private int _cursor;

    /// <summary>
    /// Index of the event the running shot was cut for, or -1 for filler. The second half of the
    /// fix above: an event may pull exactly one cut. Without this the same moment re-triggers on
    /// every frame it remains imminent, which at speed is most of them.
    /// </summary>
    private int _covered = -1;

    /// <summary>
    /// Multiplies every shot's min and max duration. 1 is the authored pacing; higher holds each
    /// shot longer. Exposed because "how fast should a film cut" is taste, not a fact.
    /// </summary>
    public float Pacing { get; set; } = 1f;

    /// <summary>Name of the running shot, for the HUD.</summary>
    public string CurrentShot => _current?.Name ?? "—";

    /// <summary>
    /// Every shot the director can choose between, in a fixed order — for a HUD list letting the
    /// player pick one by hand. A throwaway array: these instances are read for <c>Name</c> and
    /// then discarded, never stepped, so there is no shared mutable state with the live shots in
    /// <see cref="_shots"/> to worry about.
    /// </summary>
    public static IReadOnlyList<string> ShotNames { get; } = new Shot[]
    {
        new StabilisedHead(), new HelmetPov(), new OverTheShoulder(), new AnkleCam(),
        new Handheld(), new DroneOrbit(), new DroneReveal(), new LockedOff(),
        new LowHeroPass(), new TopDown(), new ChaseShot(),
    }.Select(s => s.Name).ToArray();

    private Shot? _forced;

    /// <summary>The origin moved (#185): the plan's places and every shot's own positions follow.</summary>
    public void Shift(Core.OriginShift shift)
    {
        _events = _events.Select(e => e with { Where = shift.Point(e.Where) }).ToList();
        foreach (var shot in _shots) shot.Shift(shift);
    }

    /// <summary>
    /// Pins the director to one named shot, or clears the pin. Placement (<c>Begin</c>) is still
    /// tested every time the forced shot is (re)started, so it never opens on a bad vantage — a
    /// camera under the terrain is still rejected and retried next frame — but once running it is
    /// held regardless of <see cref="Shot.StillGood"/>, the event timeline, or <see cref="Pacing"/>,
    /// none of which mean anything once a human has taken over the choice.
    /// </summary>
    /// <summary>Absolute Racing: the same director, cutting between the car shots.</summary>
    public static Director ForRacing() { var d = new Director(); d._shots = RacingShots.All(); return d; }

    public void SetForced(string? name)
    {
        _forced = name == null ? null : _shots.FirstOrDefault(s => s.Name == name);
    }

    /// <summary>Cuts made so far, and how many placements were rejected before they stuck.</summary>
    public int Cuts { get; private set; }
    public int Rejected { get; private set; }

    /// <summary>The event this shot is covering, if any.</summary>
    public CinemaEventKind Covering { get; private set; } = CinemaEventKind.Start;

    /// <summary>
    /// Seeded, so the same run renders the same film twice. The old Cinematic mode used
    /// <c>GD.Randf()</c>, which meant no export was ever reproducible.
    /// </summary>
    public void Prepare(IReadOnlyList<CinemaEvent> events, ulong seed)
    {
        _events = events;
        _rng.Seed = seed;
        _cursor = 0;
        _current = null;
        _previous = null;
        _held = 0;
        _covered = -1;
        Cuts = 0;
        Rejected = 0;
    }

    public void Step(ShotContext ctx)
    {
        ctx.Rng.Seed = _rng.Seed;   // shots draw from the same seeded stream
        _held += ctx.Dt;

        if (_forced != null)
        {
            StepForced(ctx);
            _current?.Step(ctx);
            return;
        }

        bool expired = _current == null || _held >= _target;
        bool broken = _current != null && !_current.StillGood(ctx);

        // Imminent() is called unconditionally, never behind a short-circuit: it is what walks
        // _cursor past events the clock has left behind, so skipping it would park the cursor on
        // the covered event for ever and no later moment would ever cut.
        var upcoming = Imminent(ctx.Time, ctx.ClockSpeed);

        // An event pulls exactly one cut. Re-cutting for the one already on screen is what
        // collapsed every shot to its MinSeconds at high speed.
        bool moment = _current != null && upcoming != null
            && _held >= _current.MinSeconds * Pacing && _cursor != _covered;

        if (expired || broken || moment) Cut(ctx);

        _current?.Step(ctx);
    }

    /// <summary>The next event close enough that a shot should already be covering it.</summary>
    private CinemaEvent? Imminent(double time, float clockSpeed)
    {
        double lead = Math.Min(LeadSeconds * Math.Max(1f, clockSpeed), MaxLeadSeconds);

        while (_cursor < _events.Count && _events[_cursor].Time < time - lead) _cursor++;

        if (_cursor >= _events.Count) return null;
        var next = _events[_cursor];

        return next.Time - time <= lead ? next : null;
    }

    /// <summary>
    /// The forced-shot equivalent of <see cref="Cut"/>: switches to it once, on a successful
    /// <c>Begin</c>, and otherwise leaves whatever is already running alone rather than cutting to
    /// black while a placement keeps failing (terrain still streaming in, most often).
    /// </summary>
    private void StepForced(ShotContext ctx)
    {
        if (_current == _forced) return;
        if (!_forced!.Begin(ctx)) return;

        _previous = _current;
        _current = _forced;
        _held = 0;
        Cuts++;
    }

    private void Cut(ShotContext ctx)
    {
        var moment = Imminent(ctx.Time, ctx.ClockSpeed);
        var kind = moment?.Kind ?? CinemaEventKind.Start;
        int index = moment != null ? _cursor : -1;

        // Weight each candidate by how well it suits the moment, then reject anything that is a
        // repeat, the same scale as the outgoing shot, or two rig shots in a row. Same scale plus
        // a similar angle across a cut is an accidental jump cut, and reads as a glitch.
        var pool = new List<(Shot Shot, float Weight)>();
        foreach (var shot in _shots)
        {
            if (shot == _current) continue;

            float fit = moment != null ? shot.Fit(kind) : 1f;
            if (fit <= 0) continue;

            float weight = shot.Weight * fit;
            if (_current != null && shot.Scale == _current.Scale) weight *= 0.25f;
            if (_current != null && shot.Family == ShotFamily.Rig && _current.Family == ShotFamily.Rig)
                weight *= 0.2f;
            if (shot == _previous) weight *= 0.3f;

            pool.Add((shot, weight));
        }

        for (int attempt = 0; attempt < Attempts && pool.Count > 0; attempt++)
        {
            var pick = Draw(pool);
            if (pick == null) break;

            if (pick.Begin(ctx))
            {
                Commit(ctx, pick, moment, index);
                return;
            }

            Rejected++;
            pool.RemoveAll(p => p.Shot == pick);
        }

        // Nothing placed cleanly — the chase always can, because it sits behind the runner.
        var fallback = _shots[^1];
        fallback.Begin(ctx);
        Commit(ctx, fallback, moment, index);
    }

    private void Commit(ShotContext ctx, Shot shot, CinemaEvent? moment, int index)
    {
        _previous = _current;
        _current = shot;
        _held = 0;
        _covered = index;
        Covering = moment?.Kind ?? Covering;
        Cuts++;

        // A shot covering a real moment gets held longer; filler is cut sooner. Screen seconds
        // throughout, so a scene lasts as long at 32x as at 1x.
        float span = shot.MaxSeconds - shot.MinSeconds;
        float bias = moment != null ? 0.65f : 0.3f;
        _target = (shot.MinSeconds + span * (bias + _rng.Randf() * (1f - bias))) * Pacing;
    }

    private Shot? Draw(List<(Shot Shot, float Weight)> pool)
    {
        float total = 0;
        foreach (var (_, w) in pool) total += w;
        if (total <= 0) return pool.Count > 0 ? pool[0].Shot : null;

        float roll = _rng.Randf() * total;
        foreach (var (shot, w) in pool)
        {
            roll -= w;
            if (roll <= 0) return shot;
        }

        return pool[^1].Shot;
    }
}
