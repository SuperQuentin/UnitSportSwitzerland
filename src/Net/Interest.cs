using System;
using Godot;
using UnitSport.Player;

namespace UnitSport.Net;

/// <summary>
/// Whether one player could actually see another: the rule behind who is sent whom.
///
/// <para>
/// Pure functions, no nodes: the server's <see cref="InterestService"/> runs them for every pair a
/// couple of times a second. A player nobody can see costs nothing on those peers — they are not
/// sent its position, and the spawner despawns its node there — so the traffic grows with how
/// crowded a spot is, not with how many people are online across the country.
/// </para>
/// </summary>
public static class Interest
{
    /// <summary>A reference render width (the 1152-wide UI canvas), fixed so relevance never depends on a viewer's screen.</summary>
    public const float RenderWidth = 1152f;

    /// <summary>Below this many pixels across, a target is noise, not a person.</summary>
    public const float MinPixels = 1.5f;

    /// <summary>Always relevant this close, whatever the terrain says: you can hear them.</summary>
    public const float AlwaysWithin = 150f;

    /// <summary>Leaves at this multiple of the range it entered at, so an edge does not blink.</summary>
    public const float LeaveFactor = 1.15f;

    /// <summary>Above this height over the ground a target is seen against the sky, not the hillside.</summary>
    public const float SkyAgl = 25f;

    /// <summary>A silhouette against the sky reads from much further than the same size against grass.</summary>
    public const float SkyContrast = 1.6f;

    /// <summary>The lens a viewer uses, as it reports it.</summary>
    public readonly record struct View(float Far, float FovDeg)
    {
        public static readonly View Default = new(60000f, 75f);
    }

    /// <summary>The size a target presents, in metres: what decides how far it can be seen.</summary>
    public static float SizeOf(RideKind kind) => kind switch
    {
        RideKind.OnFoot or RideKind.Skis => 1.8f,
        RideKind.RoadBike => 1.9f,
        RideKind.Wingsuit => 2.0f,
        RideKind.Parachute => 8f,
        RideKind.Paraglider => 11f,
        RideKind.Helicopter => 11f,
        RideKind.Plane => 10f,
        RideKind.A320 => 37f,
        RideKind.Freighter => 40f,
        _ when CarCatalog.IsCar(kind) => 4.4f,
        RideKind.Jetski => 3.4f,
        RideKind.Speedboat => 7f,
        _ => 2.1f,   // 64+: motorbikes and whatever is appended after them
    };

    /// <summary>How far a target of this kind stays at least <see cref="MinPixels"/> wide on screen.</summary>
    public static float Range(RideKind kind, float targetAgl, View view)
    {
        float size = SizeOf(kind) * (targetAgl > SkyAgl ? SkyContrast : 1f);
        float halfFov = Mathf.DegToRad(Mathf.Clamp(view.FovDeg, 20f, 150f)) * 0.5f;
        float pixelsPerRadian = RenderWidth / (2f * Mathf.Tan(halfFov));
        return Mathf.Min(size * pixelsPerRadian / MinPixels, view.Far);
    }

    /// <summary>
    /// Whether <paramref name="viewer"/> should be told about the target this round.
    /// </summary>
    /// <param name="wasRelevant">Last round's answer: a target already shown leaves further out.</param>
    /// <param name="together">In the same race: always shown, however far back.</param>
    /// <param name="lineOfSight">Terrain test, only asked when distance alone says yes.</param>
    public static bool Relevant(Vector3 viewer, Vector3 target, RideKind kind, float targetAgl, View view,
        bool wasRelevant, bool together, Func<Vector3, Vector3, bool>? lineOfSight)
    {
        if (together) return true;
        float d = viewer.DistanceTo(target);
        if (d <= AlwaysWithin) return true;
        float range = Range(kind, targetAgl, view) * (wasRelevant ? LeaveFactor : 1f);
        if (d > range) return false;
        return lineOfSight?.Invoke(viewer, target) ?? true;
    }

    /// <summary>
    /// Line of sight over a coarse height function: false when the ground stands above the sight
    /// line anywhere between the two, by more than <paramref name="margin"/>.
    ///
    /// <para>
    /// The 100 m horizon lattice rounds ridges off, so the margin errs toward "visible": a player
    /// on a ridge line is kept rather than lost, and the cost of that mistake is one packet stream.
    /// </para>
    /// </summary>
    public static bool Clear(Vector3 eye, Vector3 target, Func<Vector3, float?> ground, float step = 100f, float margin = 12f)
    {
        var flat = new Vector3(target.X - eye.X, 0, target.Z - eye.Z);
        float length = flat.Length();
        int n = (int)(length / step);
        for (int i = 1; i < n; i++)
        {
            float t = i / (float)n;
            var p = eye.Lerp(target, t);
            if (ground(p) is { } g && g > p.Y + margin) return false;
        }
        return true;
    }

    /// <summary>Runnable self-check of the rules above: <c>--interestcheck</c>.</summary>
    public static bool SelfCheck()
    {
        var view = View.Default;
        bool ok = true;
        void Expect(bool got, bool want, string what)
        {
            GD.Print($"[interest] {(got == want ? "ok  " : "FAIL")} {what}");
            ok &= got == want;
        }
        var here = Vector3.Zero;
        Expect(Relevant(here, new Vector3(300, 0, 0), RideKind.OnFoot, 0, view, false, false, null), true, "walker 300 m");
        Expect(Relevant(here, new Vector3(1000, 0, 0), RideKind.OnFoot, 0, view, false, false, null), false, "walker 1 km");
        Expect(Relevant(here, new Vector3(1600, 0, 0), (RideKind)CarCatalog.First, 0, view, false, false, null), true, "car 1.6 km");
        Expect(Relevant(here, new Vector3(4000, 300, 0), RideKind.Plane, 300, view, false, false, null), true, "plane 4 km in the sky");
        Expect(Relevant(here, new Vector3(3000, 0, 0), RideKind.OnFoot, 0, view, false, true, null), true, "same race 3 km back");
        Expect(Relevant(here, new Vector3(100, 0, 0), RideKind.OnFoot, 0, view, false, false, (_, _) => false), true, "within 150 m behind a wall");
        // hysteresis: just past the entry range, kept only if it was already shown
        float r = Range(RideKind.OnFoot, 0, view);
        Expect(Relevant(here, new Vector3(r * 1.1f, 0, 0), RideKind.OnFoot, 0, view, true, false, null), true, "edge, already shown");
        Expect(Relevant(here, new Vector3(r * 1.1f, 0, 0), RideKind.OnFoot, 0, view, false, false, null), false, "edge, not yet shown");
        // a 300 m ridge halfway between two walkers 1 km apart hides them from each other
        Func<Vector3, float?> ridge = p => Mathf.Abs(p.X - 400f) < 60f ? 300f : 0f;
        Expect(Clear(new Vector3(0, 2, 0), new Vector3(800, 1, 0), ridge), false, "ridge blocks");
        Expect(Clear(new Vector3(0, 2, 0), new Vector3(800, 600, 0), ridge), true, "plane over the ridge");
        Expect(Clear(new Vector3(0, 2, 0), new Vector3(800, 1, 0), _ => 0f), true, "flat ground");
        return ok;
    }
}
