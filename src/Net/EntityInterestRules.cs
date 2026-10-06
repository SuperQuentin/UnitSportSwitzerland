using System;
using System.Collections.Generic;

namespace UnitSport.Net;

/// <summary>
/// Who is sent which vehicle, dropped item or radio (#689), pure: the rules behind
/// <see cref="EntityInterest"/>. A peer only has the entities that could be seen from where its
/// player stands, so the traffic and the nodes on a client grow with how crowded its spot is, not
/// with how much the server holds across the country.
///
/// <para>
/// The range is the size the thing presents on screen, as <see cref="Interest"/> judges players: a
/// thing comes when it would be <see cref="MinPixels"/> across, a speck nobody sees appear. Its
/// model is drawn only to <see cref="FadeEnd"/> of that range, fading in over <see cref="FadeMargin"/>,
/// so it arrives invisible and fades in, and it leaves (at <see cref="LeaveFactor"/>) already gone.
/// </para>
/// </summary>
public static class EntityInterestRules
{
    /// <summary>A reference render width, as <see cref="Interest.RenderWidth"/>: relevance never depends on a viewer's screen.</summary>
    public const float RenderWidth = 1152f;

    /// <summary>Below this many pixels across, a thing is noise.</summary>
    public const float MinPixels = 1.5f;

    /// <summary>Always sent this close, whatever its size.</summary>
    public const float AlwaysWithin = 150f;

    /// <summary>Leaves at this multiple of the range it came at, so an edge does not blink.</summary>
    public const float LeaveFactor = 1.25f;

    /// <summary>Its model is drawn to this fraction of the range...</summary>
    public const float FadeEnd = 0.8f;

    /// <summary>...fading out over this fraction beyond it: gone by 0.9 of the range, long before it is despawned.</summary>
    public const float FadeMargin = 0.1f;

    /// <summary>A viewer is judged from where it will be this many seconds on: a plane meets things already there.</summary>
    public const double LookAheadSeconds = 2;

    /// <summary>Faster than this (m/s) is a teleport, not travel: no look-ahead from it.</summary>
    public const double MaxSpeed = 400;

    /// <summary>Most new entities spawned on one peer per round, nearest first: a join or a teleport does not burst.</summary>
    public const int SpawnBudget = 32;

    /// <summary>A dropped item: drawn floating to 90 m (<c>DropFloat.DrawRange</c>), so sent a margin beyond it.</summary>
    public const float ItemRange = 120f;

    /// <summary>A radio: heard well before it is seen.</summary>
    public const float RadioRange = 300f;

    /// <summary>How far a thing <paramref name="sizeM"/> across stays <see cref="MinPixels"/> wide on screen, capped by what the viewer draws.</summary>
    public static float RangeOfSize(float sizeM, float far, float fovDeg)
    {
        float halfFov = MathF.PI / 180f * Math.Clamp(fovDeg, 20f, 150f) * 0.5f;
        float pixelsPerRadian = RenderWidth / (2f * MathF.Tan(halfFov));
        return Math.Min(sizeM * pixelsPerRadian / MinPixels, far);
    }

    /// <summary>A vehicle's range: its size on screen, never under <see cref="AlwaysWithin"/>.</summary>
    public static float VehicleRange(float sizeM, float far, float fovDeg) =>
        Math.Max(AlwaysWithin, RangeOfSize(sizeM, far, fovDeg));

    /// <summary>Whether a thing at <paramref name="distance"/> is sent: within its range, or within the leave range if it already was.</summary>
    public static bool Keep(double distance, float range, bool was) =>
        distance <= AlwaysWithin || distance <= range * (was ? LeaveFactor : 1f);

    /// <summary>
    /// Where a viewer is judged from: <see cref="LookAheadSeconds"/> along the way it went since the
    /// last look, unless it jumped (a teleport, the first look).
    /// </summary>
    public static (double E, double N) Ahead(double e, double n, double prevE, double prevN, double dt)
    {
        if (dt <= 0) return (e, n);
        double ve = (e - prevE) / dt, vn = (n - prevN) / dt;
        if (ve * ve + vn * vn > MaxSpeed * MaxSpeed) return (e, n);
        return (e + ve * LookAheadSeconds, n + vn * LookAheadSeconds);
    }

    /// <summary>One entity as one viewer sees it this round.</summary>
    public readonly record struct Candidate(int Index, double Distance, float Range, bool Was);

    /// <summary>
    /// The entities a viewer has after this round, into <paramref name="keep"/> (indices): those it
    /// had and still may keep, and at most <paramref name="budget"/> newcomers, nearest first.
    /// <paramref name="newcomers"/> is scratch, cleared here.
    /// </summary>
    public static void Select(IReadOnlyList<Candidate> candidates, List<int> keep, List<Candidate> newcomers, int budget = SpawnBudget)
    {
        keep.Clear();
        newcomers.Clear();
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (!Keep(c.Distance, c.Range, c.Was)) continue;
            if (c.Was) keep.Add(c.Index);
            else newcomers.Add(c);
        }
        if (newcomers.Count > budget) newcomers.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        for (int i = 0; i < newcomers.Count && i < budget; i++) keep.Add(newcomers[i].Index);
    }
}
