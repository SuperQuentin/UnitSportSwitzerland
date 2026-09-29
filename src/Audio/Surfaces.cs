using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using static UnitSport.Audio.Dsp;

namespace UnitSport.Audio;

/// <summary>What the feet are on. Drives footstep, landing and ski-hiss character.</summary>
public enum Surface { Grass, Asphalt, Gravel, Rock, Snow, Ice, Forest, Water, Wood, Indoor }

/// <summary>
/// Ground-material lookup and the synthesised sound banks that go with it.
///
/// <para>
/// The material comes from data the game already streams: the cover raster (what the ground is)
/// and the <c>.road</c> tile (what a track or road is made of). A road wins over the cover under
/// it, because a road is drawn over the field it crosses and TLM cover polygons ignore roads
/// entirely: walking a gravel lane through a meadow must sound like gravel.
/// </para>
///
/// <para>
/// Lookups are cached: the material only changes over metres and the answer is asked for every
/// footfall, sometimes from several places. Banks are baked lazily, one surface at a time, so a
/// player who never sees ice never pays to synthesise it.
/// </para>
/// </summary>
public static class Surfaces
{
    /// <summary>
    /// The world origin, needed to turn a world position into a tile-local one for the road
    /// lookup (<see cref="ChunkManager"/> does not expose its own). Set once at boot; without it
    /// only the cover raster is consulted.
    /// </summary>
    public static WorldOrigin? Origin { get; set; }

    private const float RoadYTolerance = 2.5f;   // a road under a bridge is not the one being walked
    private const float CacheSeconds = 0.25f;
    private const float CacheMetres = 0.5f;

    private static readonly Dictionary<TileId, List<RoadSegment>?> Roads = new();
    private static readonly HashSet<TileId> Loading = new();

    private static Surface _cached;
    private static Vector3 _cachedAt = new(float.NaN, 0, 0);
    private static bool _cachedIndoors;
    private static double _cachedTime = -100;

    /// <summary>The surface under <paramref name="feet"/>. Cheap enough to call every frame.</summary>
    public static Surface At(ChunkManager chunks, Vector3 feet, bool indoors)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (indoors == _cachedIndoors && now - _cachedTime < CacheSeconds
            && !float.IsNaN(_cachedAt.X) && _cachedAt.DistanceTo(feet) < CacheMetres)
            return _cached;

        Surface s = indoors ? Surface.Indoor : Compute(chunks, feet);
        _cached = s; _cachedAt = feet; _cachedIndoors = indoors; _cachedTime = now;
        return s;
    }

    private static Surface Compute(ChunkManager chunks, Vector3 feet)
    {
        bool known = chunks.TryGetCover(feet, out var cover);

        // water is water even where a road is mapped across it: the raster says the lake is here
        if (known && cover == CoverClass.Water) return Surface.Water;

        if (RoadUnder(chunks, feet) is { } road) return road;
        return known ? FromCover(cover) : Surface.Grass;
    }

    /// <summary>Cover class to surface. Public so probes can check the table.</summary>
    public static Surface FromCover(CoverClass c) => c switch
    {
        CoverClass.Glacier or CoverClass.Snowfield => Surface.Snow,
        CoverClass.Rock or CoverClass.LooseRock or CoverClass.Boulders => Surface.Rock,
        CoverClass.Scree or CoverClass.LooseScree or CoverClass.Quarry or CoverClass.Landfill
            or CoverClass.Military or CoverClass.Campsite => Surface.Gravel,
        CoverClass.Forest or CoverClass.OpenForest or CoverClass.Woodland or CoverClass.Shrub => Surface.Forest,
        CoverClass.Water or CoverClass.Wetland => Surface.Water,
        CoverClass.ParkingPublic or CoverClass.ParkingPrivate or CoverClass.RestArea or CoverClass.PavedArea
            or CoverClass.Industrial or CoverClass.Runway or CoverClass.Platform => Surface.Asphalt,
        _ => Surface.Grass,
    };

    // ------------------------------------------------------------------------------------
    // roads
    // ------------------------------------------------------------------------------------

    private static Surface? RoadUnder(ChunkManager chunks, Vector3 feet)
    {
        if (Origin is not { } origin || chunks.Source is not { } source) return null;
        var (e, n) = origin.ToLv95(feet);
        var tile = TileId.FromLv95(e, n);
        if (!Roads.TryGetValue(tile, out var segs))
        {
            // fetched once through the (cached) chunk source; until it lands there is simply no road
            if (Loading.Add(tile)) Load(source, tile);
            return null;
        }
        if (segs == null) return null;

        double lx = e - tile.MinE, lz = tile.MaxN - n;
        Surface? best = null;
        float bestD = float.MaxValue;
        foreach (var s in segs)
        {
            float reach = s.Width * 0.5f + 0.3f;
            var pts = s.Points;
            for (int i = 0; i + 5 < pts.Length; i += 3)
            {
                double ax = pts[i], az = pts[i + 2], vx = pts[i + 3] - ax, vz = pts[i + 5] - az;
                double len2 = vx * vx + vz * vz;
                double u = len2 > 1e-9 ? Math.Clamp(((lx - ax) * vx + (lz - az) * vz) / len2, 0, 1) : 0;
                double px = ax + vx * u - lx, pz = az + vz * u - lz;
                float d2 = (float)(px * px + pz * pz);
                if (d2 > reach * reach || d2 >= bestD) continue;
                float y = pts[i + 1] + (float)u * (pts[i + 4] - pts[i + 1]);
                if (Mathf.Abs(feet.Y - y) > RoadYTolerance) continue;
                bestD = d2;
                best = s.Class == RoadClass.Railway ? Surface.Gravel   // ballast
                     : s.Surface == RoadSurface.Paved ? Surface.Asphalt
                     : s.Surface == RoadSurface.Natural ? Surface.Gravel
                     // unsurveyed: wide classes are tarmac, tracks and paths are dirt
                     : s.Class <= RoadClass.Minor || s.Class == RoadClass.Square ? Surface.Asphalt : Surface.Gravel;
            }
        }
        return best;
    }

    /// <summary>
    /// A <c>.road</c> tile also holds cableways, streams, walls and barriers; none of those is
    /// something a foot lands on. Tunnels stay: a tunnel floor is a road.
    /// </summary>
    private static bool Walkable(RoadSegment s) => s.Class <= RoadClass.Railway && s.Class != RoadClass.Unknown;

    private static async void Load(IChunkSource source, TileId tile)
    {
        try
        {
            var roads = await source.LoadRoadsAsync(tile);
            Roads[tile] = roads?.Segments.Where(Walkable).ToList();
        }
        catch { Roads[tile] = null; }
        finally { Loading.Remove(tile); }
    }

    // ------------------------------------------------------------------------------------
    // banks
    // ------------------------------------------------------------------------------------

    private static readonly Dictionary<Surface, SfxBank> StepBanks = new();
    private static readonly Dictionary<Surface, SfxBank> LandBanks = new();

    /// <summary>Footstep variants for a surface (8, lazily baked).</summary>
    public static SfxBank Steps(Surface s)
    {
        if (!StepBanks.TryGetValue(s, out var b))
            StepBanks[s] = b = SfxBank.Build($"step_{s}", 8, 0.22f, 500 + (int)s * 101, (rng, n) => Make(s, rng, n, false));
        return b;
    }

    /// <summary>Landing variants for a surface (6, lazily baked): heavier, longer, lower.</summary>
    public static SfxBank Landing(Surface s)
    {
        if (!LandBanks.TryGetValue(s, out var b))
            LandBanks[s] = b = SfxBank.Build($"land_{s}", 6, 0.5f, 900 + (int)s * 101, (rng, n) => Make(s, rng, n, true));
        return b;
    }

    /// <summary>
    /// Ski hiss hints per surface: low-pass Hz, high-pass Hz and a gain multiplier. Powder is a
    /// dark, quiet shhh; ice is thin, bright and harsh; bare ground under skis just scrapes.
    /// </summary>
    public static (float lowPassHz, float highPassHz, float gain) SkiHiss(Surface s) => s switch
    {
        Surface.Snow => (3200f, 350f, 0.85f),
        Surface.Ice => (9500f, 1800f, 1.2f),
        Surface.Asphalt => (6000f, 900f, 0.8f),
        Surface.Gravel => (5200f, 600f, 0.9f),
        Surface.Rock => (5800f, 800f, 0.8f),
        Surface.Grass => (2400f, 250f, 0.5f),
        Surface.Forest => (2800f, 250f, 0.5f),
        Surface.Water => (2200f, 150f, 0.45f),
        _ => (4000f, 500f, 0.5f),
    };

    // --- synthesis ---------------------------------------------------------------------

    private static float J(Random r, float amount) => 1f + ((float)r.NextDouble() * 2 - 1) * amount;

    /// <summary>
    /// Sparse random impulses, thicker where the decay envelope is high, then filtered into
    /// grains. Granular materials (snow, gravel) are made of many tiny collisions, and a smooth
    /// noise burst never sounds like that however it is filtered.
    /// </summary>
    private static float[] Grains(Random rng, int n, float perSecond, float decay, float hpA, float lpA)
    {
        var g = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / Rate;
            if (rng.NextDouble() < perSecond * Mathf.Exp(-decay * t) / Rate)
                g[i] = ((float)rng.NextDouble() * 2 - 1) * (0.4f + 0.6f * (float)rng.NextDouble());
        }
        if (hpA > 0) g = HighPass(g, hpA);
        if (lpA > 0) g = LowPass(g, lpA);
        return g;
    }

    /// <summary>A decaying sine on an exponential glide from f0 to f1, added into <paramref name="s"/>.</summary>
    private static void Tone(float[] s, float f0, float f1, float slide, float decay, float gain, float delay = 0f)
    {
        float phase = 0;
        int start = (int)(delay * Rate);
        for (int i = start; i < s.Length; i++)
        {
            float t = (float)(i - start) / Rate;
            float f = f0 * Mathf.Pow(f1 / f0, slide > 0 ? Mathf.Min(1f, t / slide) : 1f);
            phase += Mathf.Tau * f / Rate;
            s[i] += Mathf.Sin(phase) * Mathf.Min(1f, t * 800f) * Mathf.Exp(-decay * t) * gain;
        }
    }

    /// <summary>A short high-passed noise click starting at <paramref name="delay"/>.</summary>
    private static void Click(Random rng, float[] s, float delay, float decay, float gain, float hpA = 0.3f)
    {
        int start = (int)(delay * Rate);
        int len = Math.Min(s.Length - start, (int)(Rate * 0.03f));
        if (len <= 8) return;
        var c = HighPass(Noise(rng, len), hpA);
        for (int i = 0; i < len; i++) s[start + i] += c[i] * Mathf.Exp(-decay * i / Rate) * gain;
    }

    private static void Mix(float[] s, float[] x, float gain, Func<float, float> env)
    {
        for (int i = 0; i < s.Length; i++) s[i] += x[i] * gain * env((float)i / Rate);
    }

    /// <summary>
    /// One footstep or landing. A landing is the step with everything slowed (longer decays),
    /// more material thrown up and a low body thump under it: a landing is a heavier step, so it
    /// shares the surface's character instead of being one generic thud on every ground.
    /// </summary>
    private static float[] Make(Surface surf, Random rng, int n, bool land)
    {
        var s = new float[n];
        float slow = land ? 0.45f : 1f;      // decay multiplier
        float more = land ? 2.2f : 1f;       // grain density multiplier

        switch (surf)
        {
            case Surface.Snow:
            {
                // low-passed body of compressing snow, dense grain clicks, and sometimes a squeak
                var lo = LowPass(Noise(rng, n), Coef(900f * J(rng, 0.2f)));
                float d = 16f * slow * J(rng, 0.2f);
                Mix(s, lo, 5f, t => Mathf.Min(1f, t * 300f) * Mathf.Exp(-d * t));
                var grains = Grains(rng, n, 1400f * more * J(rng, 0.2f), 9f * slow, 0.15f, 0.55f);
                Mix(s, grains, 3.5f, t => 1f);
                if (rng.NextDouble() < 0.55)
                    Tone(s, 2300f * J(rng, 0.25f), 3100f * J(rng, 0.25f), 0.05f, 45f,
                        0.07f * (land ? 0.5f : 1f), 0.01f + 0.03f * (float)rng.NextDouble());
                break;
            }
            case Surface.Rock:
            {
                float d = 260f * slow * J(rng, 0.2f);
                Click(rng, s, 0f, d, 1.4f, 0.18f);
                Tone(s, 1900f * J(rng, 0.2f), 1600f, 0.03f, 200f * slow, 0.25f);
                int pebbles = rng.Next(3, 6) + (land ? 3 : 0);
                for (int k = 0; k < pebbles; k++)
                    Click(rng, s, 0.012f + (float)rng.NextDouble() * 0.09f, 500f, 0.08f + 0.28f * (float)rng.NextDouble(), 0.25f);
                break;
            }
            case Surface.Gravel:
            {
                var bp = BandPass(Noise(rng, n), Coef(500f), Coef(3800f * J(rng, 0.15f)));
                float d = 14f * slow * J(rng, 0.2f);
                Mix(s, bp, 2.5f, t => Mathf.Exp(-d * t));
                var grains = Grains(rng, n, 2600f * more, 12f * slow, 0.1f, 0.6f);
                Mix(s, grains, 3f, t => 1f);
                break;
            }
            case Surface.Asphalt:
            {
                // a dry tick of the hard sole, and the scuff of it settling
                Click(rng, s, 0f, 380f * slow * J(rng, 0.2f), 1.5f, 0.3f);
                var scuff = BandPass(Noise(rng, n), Coef(1200f), Coef(5000f));
                float sd = 28f * slow;
                Mix(s, scuff, 0.55f, t => Mathf.Min(1f, t * 60f) * Mathf.Exp(-sd * t));
                break;
            }
            case Surface.Grass:
            {
                var sw = BandPass(Noise(rng, n), Coef(180f), Coef(2200f * J(rng, 0.2f)));
                float peak = (0.045f + 0.025f * (float)rng.NextDouble()) * (land ? 1.4f : 1f);
                float width = 0.05f * J(rng, 0.1f);
                Mix(s, sw, 5f, t => Mathf.Exp(-Mathf.Pow((t - peak) / width, 2f)));
                break;
            }
            case Surface.Forest:
            {
                // soft thud on needles and humus, with the odd twig giving way
                Tone(s, 120f * J(rng, 0.2f), 60f, 0.08f, 34f * slow, 0.8f);
                var lo = LowPass(Noise(rng, n), Coef(700f));
                float ld = 26f * slow;
                Mix(s, lo, 3f, t => Mathf.Exp(-ld * t));
                if (rng.NextDouble() < 0.3)
                {
                    float at = 0.015f + 0.05f * (float)rng.NextDouble();
                    Click(rng, s, at, 700f, 0.7f, 0.25f);
                    Click(rng, s, at + 0.004f, 900f, 0.4f, 0.2f);
                }
                break;
            }
            case Surface.Water:
            {
                var burst = LowPass(Noise(rng, n), Coef(3500f * J(rng, 0.2f)));
                float d = 20f * slow * J(rng, 0.2f);
                Mix(s, burst, 3f, t => Mathf.Min(1f, t * 400f) * Mathf.Exp(-d * t));
                // the resonant bubble: a cavity closing sounds like a falling sweep
                Tone(s, 950f * J(rng, 0.2f), 300f, 0.09f, 14f * slow, 0.5f, 0.004f);
                int drops = rng.Next(2, 5) + (land ? 2 : 0);
                for (int k = 0; k < drops; k++)
                {
                    float f = 1200f + 1500f * (float)rng.NextDouble();
                    Tone(s, f, f * 1.3f, 0.02f, 90f, 0.12f, 0.04f + (float)rng.NextDouble() * 0.16f);
                }
                break;
            }
            case Surface.Ice:
            {
                Click(rng, s, 0f, 420f * slow, 1.5f, 0.3f);
                float f = 3300f * J(rng, 0.12f);
                Tone(s, f, f, 0f, 22f * slow, 0.3f);                  // the glassy ring...
                Tone(s, f * 1.53f, f * 1.53f, 0f, 30f * slow, 0.18f); // ...with an inharmonic partial
                break;
            }
            default: // Wood, Indoor
            {
                // a hollow knock: body resonance plus a click of the sole. Indoor is the same
                // floor with a carpet-and-furniture-damped ring.
                bool dead = surf == Surface.Indoor;
                float f = (dead ? 150f : 230f) * J(rng, 0.25f);
                float d = (dead ? 55f : 26f) * slow * J(rng, 0.2f);
                Tone(s, f * 1.15f, f, 0.03f, d, 1f);
                Tone(s, f * 2.4f, f * 2.4f, 0f, d * 2f, dead ? 0.15f : 0.35f);
                Click(rng, s, 0f, 500f, dead ? 0.3f : 0.6f, 0.2f);
                if (dead) { var lp = LowPass(s, Coef(1800f)); Array.Copy(lp, s, n); }
                break;
            }
        }

        if (land)
        {
            // the weight of the body coming down, whatever it came down on
            float f0 = (surf is Surface.Rock or Surface.Asphalt or Surface.Ice ? 95f : 70f) * J(rng, 0.15f);
            Tone(s, f0, f0 * 0.45f, 0.2f, 12f, 0.9f * (surf is Surface.Snow or Surface.Grass ? 0.6f : 1f));
        }
        return s;
    }
}
