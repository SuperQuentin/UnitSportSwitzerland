using Godot;
using UnitSport.Core;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>The sounds of a working building site (#617). Machine sounds come with the machines (#611-#614).</summary>
public enum SiteSoundKind : byte { Hammer = 0, Grinder = 1, Vibrator = 2, Beeper = 3, Radio = 4, CraneMotor = 5 }

/// <summary>
/// One thing to be heard on a site. <see cref="At"/> is tile-local, like the site.
/// <see cref="Strikes"/> and <see cref="StrikeGap"/>: a hammer comes as a burst of that many blows
/// that far apart (seconds); every other kind is one clip, <c>Strikes</c> 1. <see cref="Wait"/> is
/// how long after the start the site may be asked again, so a sound is never stacked on itself.
/// </summary>
public readonly record struct SiteSoundEvent(SiteSoundKind Kind, Vector3 At, int Strikes, float StrikeGap, float Wait, float Db);

/// <summary>
/// What a building site sounds like and where (#617), decided without Godot so a unit test can pin
/// it: a pure function of the site, the question's number and the environment clock, so it needs
/// nothing but the plan every peer already has.
///
/// <para>
/// <b>Two clocks.</b> Whether the site is noisy is environment time, <see cref="CraneMotion.Working"/>:
/// the crane's own hours (07:00-12:00, 13:00-17:00, not on Sundays), so the crane, the hammering and
/// the radio all start and stop together. When the next sound comes is audio presentation, the
/// real clock, which the caller keeps (<c>docs/notes/core/three-clocks.md</c>).
/// </para>
///
/// <para>
/// Each question (<c>step</c>, counted by the caller) is rolled from <see cref="Fnv.Unit"/> of the
/// site's key, so a replay gives the same sounds. What is picked depends on the phase: a
/// foundations site pours and reverses, a shell hammers formwork, a topped-out one cuts and grinds.
/// A kind the site has no place for (a radio without an office, a crane motor without a crane) is
/// not offered.
/// </para>
/// </summary>
public static class SiteSoundPlan
{
    /// <summary>A site is heard from this far, m (from its fence, not its middle).</summary>
    public const float HearRange = 150f;
    /// <summary>At most this many sites make sound at once: the nearest.</summary>
    public const int MaxSites = 2;

    /// <summary>Whether the site is working at this hour of this day: <see cref="CraneMotion.Working"/>.</summary>
    public static bool Audible(double hour, long day) => CraneMotion.Working(hour, day);

    // Weights per phase, in SiteSoundKind order: hammer, grinder, vibrator, beeper, radio, crane motor.
    private static readonly float[] Foundations = { 2f, 1f, 3f, 3f, 1f, 1f };
    private static readonly float[] Shell = { 4f, 2f, 2f, 2f, 1.5f, 2f };
    private static readonly float[] ToppedOut = { 2f, 3f, 0.3f, 1f, 2f, 1f };

    /// <summary>How far a plan point is from the site's fenced area, 0 inside.</summary>
    public static float Distance(ConstructionSite site, Vector2 p)
    {
        var a = site.Area;
        var d = p - a.Center;
        float u = Math.Max(0f, Math.Abs(d.Dot(a.AxisU)) - a.Width / 2);
        float v = Math.Max(0f, Math.Abs(d.Dot(a.AxisV)) - a.Depth / 2);
        return MathF.Sqrt(u * u + v * v);
    }

    /// <summary>
    /// The sound of question <paramref name="step"/> on a site, or null while it is not working.
    /// </summary>
    public static SiteSoundEvent? Next(ConstructionSite site, long step, double hour, long day)
    {
        if (!Audible(hour, day)) return null;
        double R(string q) => Fnv.Unit($"{site.Key}|snd|{step}|{q}");
        float Range(string q, float a, float b) => a + (float)R(q) * (b - a);

        SiteZone? office = null;
        foreach (var z in site.Zones)
            if (z.Kind == SiteZoneKind.Office) { office = z; break; }

        var weights = site.Phase switch { SitePhase.Foundations => Foundations, SitePhase.Shell => Shell, _ => ToppedOut };
        float total = 0f;
        for (int k = 0; k < weights.Length; k++) total += Weight(weights, k, office != null, site.Cranes.Count > 0);
        double roll = R("kind") * total;
        var kind = SiteSoundKind.Hammer;
        for (int k = 0; k < weights.Length; k++)
        {
            float w = Weight(weights, k, office != null, site.Cranes.Count > 0);
            if (w <= 0f) continue;
            kind = (SiteSoundKind)k;
            if (roll < w) break;
            roll -= w;
        }

        switch (kind)
        {
            case SiteSoundKind.Hammer:
            {
                // formwork and scaffold: on the upper levels, a burst of blows
                int strikes = 3 + (int)(R("n") * 7);
                float gap = Range("gap", 0.45f, 0.9f);
                return new SiteSoundEvent(kind, OnBuilding(site, R("u"), R("v"), R("level"), 0.6f), strikes, gap,
                    strikes * gap + Range("wait", 3f, 14f), -2f);
            }
            case SiteSoundKind.Grinder:
                return new SiteSoundEvent(kind, OnBuilding(site, R("u"), R("v"), R("level"), 0f), 1, 0f,
                    4f + Range("wait", 6f, 28f), -3f);
            case SiteSoundKind.Vibrator:
                return new SiteSoundEvent(kind, OnBuilding(site, R("u"), R("v"), R("level"), 0f), 1, 0f,
                    6f + Range("wait", 4f, 20f), -2f);
            case SiteSoundKind.Beeper:
                return new SiteSoundEvent(kind, InYard(site, R("t"), R("side")), 1, 0f, 4f + Range("wait", 8f, 25f), -6f);
            case SiteSoundKind.Radio:
            {
                var r = office!.Rect;
                var p = r.Center + r.AxisU * ((float)R("u") - 0.5f) * r.Width * 0.6f + r.AxisV * ((float)R("v") - 0.5f) * r.Depth * 0.6f;
                return new SiteSoundEvent(kind, new Vector3(p.X, site.Base + 2.5f, p.Y), 1, 0f, 8f + Range("wait", 12f, 36f), -9f);
            }
            default:
            {
                var crane = site.Cranes[(int)(R("crane") * site.Cranes.Count) % site.Cranes.Count];
                return new SiteSoundEvent(kind, new Vector3(crane.Base.X, site.Base + 4f, crane.Base.Y), 1, 0f,
                    4f + Range("wait", 10f, 32f), -6f);
            }
        }
    }

    private static float Weight(float[] weights, int kind, bool office, bool crane) =>
        kind == (int)SiteSoundKind.Radio && !office || kind == (int)SiteSoundKind.CraneMotor && !crane ? 0f : weights[kind];

    /// <summary>
    /// A point on the plan box, on a built level: any of them, or (<paramref name="upper"/> above 0)
    /// the top ones more often, where the work is.
    /// </summary>
    private static Vector3 OnBuilding(ConstructionSite site, double u, double v, double level, float upper)
    {
        var box = site.Box;
        var p = box.Center + box.AxisU * (float)(u - 0.5) * box.Width * 0.9f + box.AxisV * (float)(v - 0.5) * box.Depth * 0.9f;
        int levels = Math.Max(1, site.BuiltStoreys);
        float l = (float)level;
        if (upper > 0f) l = MathF.Pow(l, 1f - upper);   // pulled towards the top
        int at = Math.Clamp((int)(l * levels), 0, levels - 1);
        return new Vector3(p.X, site.Base + 1.2f + at * site.StoreyHeight, p.Y);
    }

    /// <summary>A point in the yard, on the way from the gate to the building: where a lorry reverses.</summary>
    private static Vector3 InYard(ConstructionSite site, double t, double side)
    {
        var lane = site.Gate + (site.Box.Center - site.Gate) * (0.15f + (float)t * 0.45f);
        var across = new Vector2(-(site.Box.Center - site.Gate).Y, (site.Box.Center - site.Gate).X).Normalized();
        var p = lane + across * ((float)side - 0.5f) * 5f;
        if (!site.Area.Contains(p, 0f)) p = lane;
        return new Vector3(p.X, site.Base + 1.5f, p.Y);
    }
}
