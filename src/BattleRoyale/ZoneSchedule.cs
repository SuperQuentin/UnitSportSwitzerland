using Godot;

namespace UnitSport.BattleRoyale;

/// <summary>The zone at one moment. Positions are metres east/north of the region centre.</summary>
/// <param name="Phase">0 while looting, then 1..<see cref="ZoneSchedule.Phases"/>; past the last, the last.</param>
/// <param name="Shrinking">The circle is closing in on <see cref="NextCentre"/>/<see cref="NextRadius"/> right now.</param>
/// <param name="Left">Seconds until the next change: the shrink starting, or ending.</param>
/// <param name="Dps">Health per second lost outside the circle.</param>
public readonly record struct ZoneState(int Phase, bool Shrinking, double Left, Vector2 Centre, float Radius,
    Vector2 NextCentre, float NextRadius, float Dps, bool Over)
{
    public bool Outside(Vector2 at) => at.DistanceTo(Centre) > Radius;
    public float DistanceToEdge(Vector2 at) => Radius - at.DistanceTo(Centre);
}

/// <summary>
/// The whole zone of one match, from its seed: every client builds the same one from the one
/// state message and reads it off the shared clock (<c>Net/ClockSync</c>), so nothing about the
/// zone is ever sent while it moves. Times are seconds since the match started; the 6 km "normal"
/// timetable scales with the first circle's radius and the pace, and that circle is sized for the
/// field (docs/notes/br/zone.md).
/// </summary>
public sealed class ZoneSchedule
{
    /// <summary>Looting before the first circle shows (6 km, normal).</summary>
    public const double LootSeconds = 240;
    public const int Phases = 8;
    /// <summary>Ground per player in the first circle, m² (20 players: about the 6 km circle).</summary>
    public const float AreaPerPlayer = 1.5e6f;
    /// <summary>The smallest first circle, m, whatever the field.</summary>
    public const float MinRadius = 900f;
    /// <summary>The first circle of the 6 km region the timetable is written for, m.</summary>
    private const float BaseRadius = 6000f * 0.57f;
    /// <summary>A small field's match is shorter, but no shorter than half the timetable.</summary>
    private const double MinScale = 0.5;

    // radius as a fraction of the first circle, the wait before each shrink, the shrink, the damage
    private static readonly float[] RadiusFraction = { 1f, 0.618f, 0.397f, 0.25f, 0.147f, 0.082f, 0.041f, 0.0176f, 0f };
    private static readonly double[] Wait = { 0, 120, 180, 150, 120, 105, 90, 75, 60 };
    private static readonly double[] Shrink = { 0, 180, 150, 120, 105, 90, 75, 60, 60 };
    private static readonly float[] Damage = { 0f, 2f, 3f, 5f, 7f, 10f, 14f, 20f, 30f };   // #455: the first circle bites

    private readonly Vector2[] _centre = new Vector2[Phases + 1];
    private readonly float[] _radius = new float[Phases + 1];
    /// <summary>When phase i's wait starts (i ≥ 1); [Phases + 1] is when the last shrink ends.</summary>
    private readonly double[] _start = new double[Phases + 2];
    private readonly double _scale;

    public float Side { get; }
    public double Scale => _scale;
    /// <summary>Seconds from the start to the zone closing for good.</summary>
    public double Duration => _start[Phases + 1];

    /// <summary>
    /// The first circle's radius for <paramref name="field"/> players: <see cref="AreaPerPlayer"/> each,
    /// between <see cref="MinRadius"/> and the full circle (0.57 × the side: the corners are out from the start).
    /// A field of 0 (not known yet: the lobby) gets the full circle.
    /// </summary>
    public static float FirstRadius(float side, int field)
    {
        float full = side * 0.57f;
        if (field <= 0) return full;
        return Math.Clamp(Mathf.Sqrt(field * AreaPerPlayer / Mathf.Pi), Math.Min(MinRadius, full), full);
    }

    /// <summary>Candidates tried for each centre when the ground is judged (<see cref="Badness"/>).</summary>
    public const int Tries = 12;

    /// <summary>A circle at most this bad (a share of water and cliff, 0..1) is taken at once.</summary>
    public const float GoodEnough = 0.3f;

    /// <param name="side">The region's side, m.</param>
    /// <param name="pace">0.8 short, 1 normal, 1.3 long.</param>
    /// <param name="field">Players at GO (<see cref="BrState.Field"/>); 0 for the full circle.</param>
    /// <param name="centres">The centres the server chose (<see cref="Centres"/>, sent in <see cref="BrState.ZoneCentres"/>):
    /// used as they are, so every client draws the server's zone. Null: drawn here from the seed.</param>
    /// <param name="badness">Server only (#477): how unplayable a circle (centre, radius) is, 0..1 (water,
    /// cliffs). Each centre is then the first of <see cref="Tries"/> seeded candidates at most
    /// <see cref="GoodEnough"/>, else the least bad. Null: the first candidate, as before.</param>
    public ZoneSchedule(int seed, float side, float pace, int field = 0, float[]? centres = null, Func<Vector2, float, float>? badness = null)
    {
        Side = side;
        var rng = new Random(seed);
        float half = side * 0.5f;
        _radius[0] = FirstRadius(side, field);
        _scale = Math.Max(MinScale, _radius[0] / BaseRadius) * pace;
        for (int i = 1; i <= Phases; i++) _radius[i] = _radius[0] * RadiusFraction[i];
        bool given = centres is { Length: (Phases + 1) * 2 } && centres.All(float.IsFinite);
        for (int i = 0; i <= Phases; i++)
        {
            if (given)
            {
                _centre[i] = new Vector2(centres![i * 2], centres[i * 2 + 1]);
                continue;
            }
            // the first circle anywhere inside the full one; each next anywhere it still fits inside
            // the one before; all inside the square
            var from = i == 0 ? Vector2.Zero : _centre[i - 1];
            float room = i == 0 ? side * 0.57f - _radius[0] : _radius[i - 1] - _radius[i];
            float lim = half - _radius[i];
            _centre[i] = Inside(from, room, lim, rng);
            if (badness == null) continue;
            float best = badness(_centre[i], _radius[i]);
            for (int t = 1; t < Tries && best > GoodEnough; t++)
            {
                var c = Inside(from, room, lim, rng);
                float b = badness(c, _radius[i]);
                if (b < best) (best, _centre[i]) = (b, c);
            }
        }
        _start[1] = LootSeconds * _scale;
        for (int i = 1; i <= Phases; i++) _start[i + 1] = _start[i] + (Wait[i] + Shrink[i]) * _scale;
    }

    /// <summary>Uniform in the disc of radius <paramref name="room"/> around <paramref name="from"/>, clamped to ±<paramref name="lim"/>.</summary>
    private static Vector2 Inside(Vector2 from, float room, float lim, Random rng)
    {
        float r = room * Mathf.Sqrt((float)rng.NextDouble()), a = (float)(rng.NextDouble() * Math.Tau);
        var c = from + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
        lim = Mathf.Max(0f, lim);
        c = new Vector2(Mathf.Clamp(c.X, -lim, lim), Mathf.Clamp(c.Y, -lim, lim));
        // the clamp may push it out of the old circle: pull it back in
        var off = c - from;
        return off.Length() > room ? from + off.Normalized() * room : c;
    }

    public Vector2 CentreOf(int phase) => _centre[Math.Clamp(phase, 0, Phases)];

    /// <summary>Every centre, x then y, phase 0 first: what the server sends (<see cref="BrState.ZoneCentres"/>).</summary>
    public float[] Centres => _centre.SelectMany(c => new[] { c.X, c.Y }).ToArray();

    /// <summary>
    /// How unplayable a circle is, 0..1, from samples of its ground (#477): the share of points on water
    /// plus the share on slopes over 35°, at most 1. <paramref name="wet"/> and <paramref name="height"/>
    /// read the ground at a zone point (metres east/north of the region centre). Pure.
    /// </summary>
    public static float Badness(Vector2 centre, float radius, Func<Vector2, bool> wet, Func<Vector2, float> height)
    {
        const float Step = 50f, Steep = 0.7f;   // tan 35°
        int n = 0, bad = 0;
        float r = Mathf.Max(radius, 25f);
        // a sunflower of points over the disc, the same every time
        for (int k = 0; k < 41; k++)
        {
            float d = r * Mathf.Sqrt((k + 0.5f) / 41f), a = k * 2.39996f;
            var p = centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d;
            n++;
            if (wet(p)) { bad++; continue; }
            float dx = height(p + new Vector2(Step, 0)) - height(p - new Vector2(Step, 0));
            float dy = height(p + new Vector2(0, Step)) - height(p - new Vector2(0, Step));
            if (Mathf.Sqrt(dx * dx + dy * dy) / (2 * Step) > Steep) bad++;
        }
        return Mathf.Min(1f, bad / (float)n);
    }
    public float RadiusOf(int phase) => _radius[Math.Clamp(phase, 0, Phases)];

    /// <summary>
    /// When the next shrink starts, if the zone is waiting at <paramref name="t"/>: from the loot time,
    /// phase 1's (after its wait). Null while it shrinks or once it is over (<c>/br zone</c>, #425).
    /// </summary>
    public double? NextShrinkAt(double t)
    {
        var z = At(t);
        if (z.Shrinking || z.Over) return null;
        int i = Math.Max(z.Phase, 1);
        return _start[i] + Wait[i] * _scale;
    }

    public ZoneState At(double t)
    {
        // while looting, the next circle is already the first shrink's (#477): plan the way in
        if (t < _start[1])
            return new ZoneState(0, false, _start[1] - t, _centre[0], _radius[0], _centre[1], _radius[1], 0f, false);
        for (int i = 1; i <= Phases; i++)
        {
            if (t >= _start[i + 1]) continue;
            double shrinkAt = _start[i] + Wait[i] * _scale;
            if (t < shrinkAt)
                return new ZoneState(i, false, shrinkAt - t, _centre[i - 1], _radius[i - 1], _centre[i], _radius[i], Damage[i], false);
            float k = (float)((t - shrinkAt) / (_start[i + 1] - shrinkAt));
            return new ZoneState(i, true, _start[i + 1] - t, _centre[i - 1].Lerp(_centre[i], k),
                Mathf.Lerp(_radius[i - 1], _radius[i], k), _centre[i], _radius[i], Damage[i], false);
        }
        return new ZoneState(Phases, false, 0, _centre[Phases], _radius[Phases], _centre[Phases], _radius[Phases], Damage[Phases], true);
    }
}
