using System.Runtime.CompilerServices;
using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

// Godot's managed maths only (Vector3, Mathf): linked into the unit tests (docs/notes/general/testing.md).

/// <summary>
/// What a glider can read off the ground at a point: its height, its cover and the world clock.
/// <see cref="AirLiftField"/> answers it from the streamed tiles; the tests answer it with a function.
/// Positions are LV95 metres (east, north).
/// </summary>
public interface IAirLiftTerrain
{
    /// <summary>The ground's altitude; false where the tile is not loaded.</summary>
    bool TryHeight(double e, double n, out float height);

    /// <summary>The land cover; false where it is not known (a far tile): counts as 0.8.</summary>
    bool TryCover(double e, double n, out CoverClass cover);

    /// <summary>The hour of day, 0..24, at a moment of the server's clock.</summary>
    double HourAt(double serverNow);

    /// <summary>The day's number at a moment of the server's clock: it picks the wind of the day.</summary>
    long DayAt(double serverNow);
}

/// <summary>A thermal now: where it rises, how hard (0.25..~0.9), how wide and how high.</summary>
/// <param name="E">LV95 east of the source.</param>
/// <param name="N">LV95 north of the source.</param>
/// <param name="Ground">Altitude of the ground under the source, m.</param>
/// <param name="Strength">0.25..1: sun x aspect x cover x life.</param>
/// <param name="Radius">Core radius, m: 35 + 45 x strength.</param>
/// <param name="Top">Height of the column above <paramref name="Ground"/>, m: 300 + 900 x strength.</param>
public readonly record struct Thermal(double E, double N, float Ground, float Strength, float Radius, float Top)
{
    /// <summary>The altitude where the column ends.</summary>
    public float TopAltitude => Ground + Top;
}

/// <summary>The wind of one day: where it blows from and how fast, m/s.</summary>
/// <param name="FromBearingDeg">Compass bearing it comes from, clockwise from north (270 = west).</param>
public readonly record struct DayWind(float FromBearingDeg, float Speed)
{
    /// <summary>Unit vector of the direction it comes from, as (east, north).</summary>
    public (float E, float N) FromDir => (Mathf.Sin(Mathf.DegToRad(FromBearingDeg)), Mathf.Cos(Mathf.DegToRad(FromBearingDeg)));
}

/// <summary>
/// The air's own motion over Switzerland (#1225, <c>docs/notes/world/air-lift.md</c>): thermals under
/// the sun, ridge lift on the wind's side of a hill, and updraft volumes (a lit campfire, a bottled
/// gust). One query, <see cref="At"/>, a pure function of LV95 position, altitude, the server's clock
/// and the ground, so the server and every peer compute the same air and nothing is summed per frame.
///
/// <para>
/// Axes of the result are the game world's: x east, y up, z south (-Z is north). Almost always only
/// y is set. Main thread only.
/// </para>
/// </summary>
public static class AirLift
{
    /// <summary>Cell size of the thermal candidates, m.</summary>
    public const double CellSize = 400.0;

    /// <summary>Under this a thermal does not exist.</summary>
    public const float MinStrength = 0.25f;

    /// <summary>The strongest core lift, m/s (a strength of 0.8 and over).</summary>
    public const float MaxCoreLift = 3.5f;

    /// <summary>Cached cells, least recently used out first.</summary>
    public const int CacheCells = 256;

    /// <summary>The ground's climb is read over this distance upwind of the point, m.</summary>
    public const float RidgeProbe = 40f;

    /// <summary>Ridge lift fades out over this height above the ground, m.</summary>
    public const float RidgeHeight = 200f;

    // ---- the sum ---------------------------------------------------------------------------------

    /// <summary>
    /// The air's velocity at a point and a moment, m/s, world axes (x east, y up, z south).
    /// <paramref name="alt"/> is the altitude above sea level.
    /// </summary>
    public static Vector3 At(double e, double n, float alt, double serverNow, IAirLiftTerrain t)
    {
        float up = Thermals(e, n, alt, serverNow, t) + Ridge(e, n, alt, serverNow, t) + Volumes.LiftAt(e, n, alt, serverNow);
        return up == 0f ? Vector3.Zero : new Vector3(0f, up, 0f);
    }

    // ---- thermals --------------------------------------------------------------------------------

    /// <summary>One cell's fixed part: where its candidate sits and what the ground there is like.</summary>
    private sealed class Source
    {
        public double E, N;
        public float Ground, NormE, NormU, NormN, CoverFactor, Period, Phase;
        public long Stamp;
    }

    private sealed class Cache
    {
        public readonly Dictionary<long, Source> Cells = new(CacheCells + 8);
        public long Clock;
    }

    private static readonly ConditionalWeakTable<IAirLiftTerrain, Cache> Caches = new();
    private static IAirLiftTerrain? _lastTerrain;
    private static Cache? _lastCache;

    private static Cache CacheOf(IAirLiftTerrain t)
    {
        if (ReferenceEquals(t, _lastTerrain) && _lastCache != null) return _lastCache;
        var cache = Caches.GetValue(t, _ => new Cache());
        _lastTerrain = t;
        _lastCache = cache;
        return cache;
    }

    /// <summary>Forgets the cached cells of a terrain (its tiles changed under them).</summary>
    public static void Forget(IAirLiftTerrain t) => CacheOf(t).Cells.Clear();

    private static long CellKey(long ci, long cj) => (ci << 32) ^ (cj & 0xFFFFFFFFL);

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A slice of a hash as 0..1 (20 bits).</summary>
    private static double Unit(ulong h, int slice) => ((h >> (slice * 20)) & 0xFFFFF) / 1048576.0;

    private static ulong CellHash(long ci, long cj) => Mix(Mix((ulong)ci * 0xD6E8FEB86659FD93UL) ^ (ulong)cj);

    /// <summary>The cell's candidate with its ground read, or null while the tile is not loaded.</summary>
    private static Source? SourceOf(Cache cache, long ci, long cj, IAirLiftTerrain t)
    {
        long key = CellKey(ci, cj);
        if (cache.Cells.TryGetValue(key, out var hit))
        {
            hit.Stamp = ++cache.Clock;
            return hit;
        }
        ulong h = CellHash(ci, cj);
        double e = (ci + 0.15 + 0.7 * Unit(h, 0)) * CellSize, n = (cj + 0.15 + 0.7 * Unit(h, 1)) * CellSize;
        if (!t.TryHeight(e, n, out float ground)) return null;
        // the slope from heights 20 m around the source; a missing neighbour reads as flat
        const double d = 20.0;
        float hE = t.TryHeight(e + d, n, out float a) ? a : ground, hW = t.TryHeight(e - d, n, out float b) ? b : ground;
        float hN = t.TryHeight(e, n + d, out float c) ? c : ground, hS = t.TryHeight(e, n - d, out float f) ? f : ground;
        var normal = new Vector3((hW - hE) / (float)(2 * d), 1f, (hS - hN) / (float)(2 * d)).Normalized();
        ulong h2 = Mix(h);
        var s = new Source
        {
            E = e, N = n, Ground = ground, NormE = normal.X, NormU = normal.Y, NormN = normal.Z,
            CoverFactor = t.TryCover(e, n, out var cover) ? CoverFactorOf(cover) : 0.8f,
            Period = 360f + 240f * (float)Unit(h2, 0),
            Phase = (float)Unit(h2, 1),
            Stamp = ++cache.Clock,
        };
        if (cache.Cells.Count >= CacheCells) Evict(cache);
        cache.Cells[key] = s;
        return s;
    }

    private static void Evict(Cache cache)
    {
        long oldest = long.MaxValue, victim = 0;
        foreach (var (k, v) in cache.Cells)
            if (v.Stamp < oldest) { oldest = v.Stamp; victim = k; }
        cache.Cells.Remove(victim);
    }

    /// <summary>How much of the sun's heat the ground gives back: bare and sunny ground best, trees less, water and ice none.</summary>
    public static float CoverFactorOf(CoverClass c) => c switch
    {
        CoverClass.Rock or CoverClass.LooseRock or CoverClass.Scree or CoverClass.LooseScree or CoverClass.Boulders
            or CoverClass.Open or CoverClass.Vineyard or CoverClass.PavedArea or CoverClass.ParkingPublic
            or CoverClass.ParkingPrivate or CoverClass.ParkingPaved or CoverClass.RestArea => 1f,
        CoverClass.Forest or CoverClass.OpenForest or CoverClass.Woodland or CoverClass.Shrub => 0.6f,
        CoverClass.Water or CoverClass.Glacier or CoverClass.Snowfield => 0f,
        _ => 0.8f,
    };

    /// <summary>
    /// A thermal breathes: it builds, holds near full for a few minutes and dies away on a cycle of its
    /// own (6-10 min). 0..1.
    /// </summary>
    public static float Life(float period, float phase, double now)
    {
        double u = now / period + phase;
        float sin = (float)Math.Sin((u - Math.Floor(u)) * Math.PI * 2.0);
        return Math.Min(1f, 2.5f * Math.Max(0f, sin));
    }

    private static float StrengthOf(Source s, Vector3 sun, double now)
    {
        float sunUp = sun.Y;
        if (sunUp <= 0f) return 0f;
        // sun.X is east, sun.Z is south; the normal is (east, up, north)
        float aspect = 0.4f + 0.6f * Math.Max(0f, s.NormE * sun.X + s.NormU * sun.Y - s.NormN * sun.Z);
        return sunUp * aspect * s.CoverFactor * Life(s.Period, s.Phase, now);
    }

    private static Thermal Make(Source s, float strength) =>
        new(s.E, s.N, s.Ground, strength, 35f + 45f * strength, 300f + 900f * strength);

    /// <summary>Lift (m/s) of a thermal at a horizontal distance <paramref name="d"/> and a height above its source.</summary>
    public static float Profile(float strength, float radius, float top, float d, float height)
    {
        if (height >= top) return 0f;
        float x = d / radius;
        if (x > 2.4f) return 0f;
        float peak = MaxCoreLift * Math.Min(1f, strength / 0.8f);
        float ring = x - 1.5f;
        float lift = peak * MathF.Exp(-2f * x * x) - 0.8f * (peak / MaxCoreLift) * MathF.Exp(-(ring * ring) / 0.1225f);
        float fadeFrom = top * 0.85f;
        if (height > fadeFrom) lift *= (top - height) / (top - fadeFrom);
        return lift;
    }

    private static float Thermals(double e, double n, float alt, double now, IAirLiftTerrain t)
    {
        var sun = SunPath.Direction((float)t.HourAt(now), out float elevation);
        if (elevation <= 0f) return 0f;
        var cache = CacheOf(t);
        long ci = (long)Math.Floor(e / CellSize), cj = (long)Math.Floor(n / CellSize);
        float sum = 0f;
        for (long i = ci - 1; i <= ci + 1; i++)
            for (long j = cj - 1; j <= cj + 1; j++)
            {
                if (SourceOf(cache, i, j, t) is not { } s) continue;
                double dx = e - s.E, dy = n - s.N;
                if (dx * dx + dy * dy > 200.0 * 200.0) continue;   // the widest thermal's ring ends well inside
                float strength = StrengthOf(s, sun, now);
                if (strength < MinStrength) continue;
                sum += Profile(strength, 35f + 45f * strength, 300f + 900f * strength,
                    (float)Math.Sqrt(dx * dx + dy * dy), alt - s.Ground);
            }
        return sum;
    }

    /// <summary>
    /// The thermals alive now within <paramref name="radius"/> metres of a point, strongest first, up to
    /// the span's length; returns how many. For the cues (birds, specks), not for flight.
    /// </summary>
    public static int ThermalsNear(double e, double n, double radius, double now, IAirLiftTerrain t, Span<Thermal> into)
    {
        var sun = SunPath.Direction((float)t.HourAt(now), out float elevation);
        if (elevation <= 0f || into.IsEmpty) return 0;
        var cache = CacheOf(t);
        long i0 = (long)Math.Floor((e - radius) / CellSize), i1 = (long)Math.Floor((e + radius) / CellSize);
        long j0 = (long)Math.Floor((n - radius) / CellSize), j1 = (long)Math.Floor((n + radius) / CellSize);
        int count = 0;
        for (long i = i0; i <= i1; i++)
            for (long j = j0; j <= j1; j++)
            {
                if (SourceOf(cache, i, j, t) is not { } s) continue;
                double dx = e - s.E, dy = n - s.N;
                if (dx * dx + dy * dy > radius * radius) continue;
                float strength = StrengthOf(s, sun, now);
                if (strength < MinStrength) continue;
                var thermal = Make(s, strength);
                // kept sorted, strongest first; a full span drops its weakest
                int at;
                if (count < into.Length) at = count++;
                else if (strength > into[^1].Strength) at = into.Length - 1;
                else continue;
                into[at] = thermal;
                while (at > 0 && into[at - 1].Strength < into[at].Strength) { (into[at - 1], into[at]) = (into[at], into[at - 1]); at--; }
            }
        return count;
    }

    /// <summary><see cref="ThermalsNear(double, double, double, double, IAirLiftTerrain, Span{Thermal})"/> into a new array (tests, probes; allocates).</summary>
    public static Thermal[] ThermalsNear(double e, double n, double radius, double now, IAirLiftTerrain t, int max = 16)
    {
        var buffer = new Thermal[max];
        int count = ThermalsNear(e, n, radius, now, t, buffer.AsSpan());
        return buffer[..count];
    }

    // ---- ridge lift ------------------------------------------------------------------------------

    /// <summary>
    /// The wind of a day: from a hash of its number. West or south-west 60 %, the Bise (north-east) 25 %,
    /// the Foehn (south) 15 %, 3-8 m/s.
    /// </summary>
    public static DayWind WindOf(long day)
    {
        ulong h = Mix((ulong)day ^ 0x51AB1E0F00DUL);
        double kind = Unit(h, 0);
        float from = kind < 0.6 ? (Unit(h, 1) < 0.5 ? 270f : 225f) : kind < 0.85 ? 45f : 180f;
        return new DayWind(from, 3f + 5f * (float)Unit(h, 2));
    }

    /// <summary>Fade of the ridge lift with the height above the ground: 1 on it, 0 at <see cref="RidgeHeight"/>.</summary>
    public static float RidgeFade(float agl) => Math.Clamp(1f - agl / RidgeHeight, 0f, 1f);

    private static float Ridge(double e, double n, float alt, double now, IAirLiftTerrain t)
    {
        if (!t.TryHeight(e, n, out float ground)) return 0f;
        float fade = RidgeFade(alt - ground);
        if (fade <= 0f) return 0f;
        var wind = WindOf(t.DayAt(now));
        var (fe, fn) = wind.FromDir;
        if (!t.TryHeight(e + fe * RidgeProbe, n + fn * RidgeProbe, out float upwind)) return 0f;
        // the ground's climb over the stretch the wind has just crossed: a windward face
        float rise = (ground - upwind) / RidgeProbe;
        return rise >= 0f
            ? wind.Speed * Math.Min(rise, 1f) * fade
            : -0.5f * wind.Speed * Math.Min(-rise, 1f) * fade;
    }

    // ---- updraft volumes -------------------------------------------------------------------------

    /// <summary>
    /// Vertical updraft cones: a lit campfire, a bottled gust, a trial's fixed updraft. A volume is
    /// flat out to its radius, fades to nothing at 1.5 x radius, rises <c>Top</c> metres above its base
    /// altitude (fading over the last 30 %) and may expire on the server's clock. Keys: pass your own
    /// (a campfire's object id) to <see cref="Set"/>; <see cref="Add"/> hands out negative ones.
    /// </summary>
    public static class Volumes
    {
        private struct Volume
        {
            public long Key;
            public double E, N, Alt, Expires;
            public float Radius, Top, Speed;
        }

        private static readonly List<Volume> All = new();
        private static long _nextKey = -1;

        public static int Count => All.Count;

        /// <summary>
        /// A temporary updraft (the rewards plan's bottled gust): <paramref name="seconds"/> from
        /// <paramref name="serverNow"/>. Returns its key.
        /// </summary>
        public static long Add(double e, double n, double alt, float radius, double seconds, double serverNow,
            float top = 40f, float speed = 8f)
        {
            long key = _nextKey--;
            Put(new Volume { Key = key, E = e, N = n, Alt = alt, Radius = radius, Top = top, Speed = speed, Expires = serverNow + seconds });
            return key;
        }

        /// <summary>A persistent updraft under your own key (a burning campfire): replaces the one with that key.</summary>
        public static void Set(long key, double e, double n, double alt, float radius, float top, float speed) =>
            Put(new Volume { Key = key, E = e, N = n, Alt = alt, Radius = radius, Top = top, Speed = speed, Expires = double.PositiveInfinity });

        public static bool Remove(long key)
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i].Key == key) { All.RemoveAt(i); return true; }
            return false;
        }

        public static bool Contains(long key)
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i].Key == key) return true;
            return false;
        }

        public static void Clear() => All.Clear();

        /// <summary>Drops the ones that have run out by this moment of the server's clock.</summary>
        public static void Prune(double serverNow)
        {
            for (int i = All.Count - 1; i >= 0; i--)
                if (All[i].Expires <= serverNow) All.RemoveAt(i);
        }

        private static void Put(in Volume v)
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i].Key == v.Key) { All[i] = v; return; }
            All.Add(v);
        }

        /// <summary>The summed vertical speed of every volume reaching this point, m/s.</summary>
        public static float LiftAt(double e, double n, float alt, double serverNow)
        {
            float sum = 0f;
            for (int i = 0; i < All.Count; i++)
            {
                var v = All[i];
                if (serverNow >= v.Expires) continue;
                double dx = e - v.E, dy = n - v.N;
                double reach = v.Radius * 1.5;
                if (dx * dx + dy * dy >= reach * reach) continue;
                float height = alt - (float)v.Alt;
                if (height < -2f || height >= v.Top) continue;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                float across = d <= v.Radius ? 1f : 1f - Smooth((d - v.Radius) / (0.5f * v.Radius));
                float fadeFrom = v.Top * 0.7f;
                float along = height > fadeFrom ? (v.Top - height) / (v.Top - fadeFrom) : height < 0f ? (height + 2f) / 2f : 1f;
                sum += v.Speed * across * along;
            }
            return sum;
        }

        private static float Smooth(float x)
        {
            x = Math.Clamp(x, 0f, 1f);
            return x * x * (3f - 2f * x);
        }
    }
}
