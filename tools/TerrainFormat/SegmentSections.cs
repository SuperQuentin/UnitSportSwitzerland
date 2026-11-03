namespace UnitSport.Terrain.Format;

/// <summary>
/// A segment's cross-section at each of its points (#818, the user's generalization: every street is built from a section per
/// point): where its ribbon's two edges lie, where each side's bands start, and each side's profile out from there. An
/// ordinary segment's is the same at every point (its width, its sides as <c>ATR2</c> holds them, a turn lane's widening
/// pushing a side out by <see cref="RoadSide.ShiftAt"/>); a transition's (<see cref="RoadTransition"/>, <c>TRNS</c>) varies:
/// the carriageway's edges and every band's width from its strips. Offsets are metres right of the drawing direction (the
/// left ones negative), across the per-vertex bisector as the ribbon. Shared by the game (ribbon, sides, blend, collision) and
/// the network stage's checks, so both read one section.
/// </summary>
public sealed class SegmentSections
{
    public required float[] RibbonLeft { get; init; }
    public required float[] RibbonRight { get; init; }
    /// <summary>Where each side's bands start (its kerb): the ribbon's edge, or past a turn lane's widening.</summary>
    public required float[] SideLeft { get; init; }
    public required float[] SideRight { get; init; }
    /// <summary>Each side's profile at each point, null where nothing lies beside (one object at every point of an ordinary segment).</summary>
    public required RoadStreetSection.Profile?[] LeftProfile { get; init; }
    public required RoadStreetSection.Profile?[] RightProfile { get; init; }
    /// <summary>A transition's: widths change along it.</summary>
    public bool Varies { get; init; }

    public int Count => RibbonLeft.Length;

    /// <summary>How far each side's section reaches out from the centreline at point <paramref name="i"/> (bands included, at least 0).</summary>
    public float OuterLeft(int i) => Math.Max(0, -SideLeft[i] + (LeftProfile[i]?.Width ?? 0));
    public float OuterRight(int i) => Math.Max(0, SideRight[i] + (RightProfile[i]?.Width ?? 0));

    /// <summary>Plan metres along the segment from its first point, at each point.</summary>
    public static float[] Stations(RoadSegment seg)
    {
        var p = seg.Points;
        int n = seg.PointCount;
        var at = new float[n];
        for (int i = 1; i < n; i++)
        {
            float dx = p[i * 3] - p[i * 3 - 3], dz = p[i * 3 + 2] - p[i * 3 - 1];
            at[i] = at[i - 1] + MathF.Sqrt(dx * dx + dz * dz);
        }
        return at;
    }

    /// <summary>The sections of <paramref name="seg"/>, from <paramref name="transition"/> where one covers it.</summary>
    public static SegmentSections Of(RoadSegment seg, RoadTransition? transition = null)
    {
        int n = seg.PointCount;
        var a = seg.Attributes;
        bool lowered = a.Has(RoadAttrFlags.LoweredKerbs);
        var rl = new float[n];
        var rr = new float[n];
        var sl = new float[n];
        var sr = new float[n];
        var lp = new RoadStreetSection.Profile?[n];
        var rp = new RoadStreetSection.Profile?[n];
        if (transition is { } t)
        {
            var at = Stations(seg);
            for (int i = 0; i < n; i++)
            {
                var (left, right) = t.CarriagewayAt(at[i]);
                (rl[i], rr[i], sl[i], sr[i]) = (left, right, left, right);
                lp[i] = RoadStreetSection.For(a.Left, t.BandsAt(false, at[i]), lowered);
                rp[i] = RoadStreetSection.For(a.Right, t.BandsAt(true, at[i]), lowered);
            }
            return new SegmentSections { RibbonLeft = rl, RibbonRight = rr, SideLeft = sl, SideRight = sr, LeftProfile = lp, RightProfile = rp, Varies = true };
        }
        float half = seg.Width * 0.5f;
        var left0 = RoadStreetSection.For(a.Left, lowered);
        var right0 = RoadStreetSection.For(a.Right, lowered);
        bool shifted = a.Left.ShiftStartCm != a.Left.ShiftEndCm || a.Right.ShiftStartCm != a.Right.ShiftEndCm
            || a.Left.ShiftStartCm != 0 || a.Right.ShiftStartCm != 0;
        var along = shifted ? RoadStreetSection.Fractions(seg) : null;
        for (int i = 0; i < n; i++)
        {
            rl[i] = -half;
            rr[i] = half;
            sl[i] = -(half + (along is null ? 0 : a.Left.ShiftAt(along[i])));
            sr[i] = half + (along is null ? 0 : a.Right.ShiftAt(along[i]));
            lp[i] = left0;
            rp[i] = right0;
        }
        return new SegmentSections { RibbonLeft = rl, RibbonRight = rr, SideLeft = sl, SideRight = sr, LeftProfile = lp, RightProfile = rp };
    }

    /// <summary>The transition covering each segment of <paramref name="tile"/> (null for none), by segment index.</summary>
    public static RoadTransition?[] TransitionsOf(RoadTile tile)
    {
        var result = new RoadTransition?[tile.Segments.Count];
        if (tile.Transitions.Count == 0) return result;
        var index = new Dictionary<RoadSegment, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < tile.Segments.Count; i++) index[tile.Segments[i]] = i;
        foreach (var t in tile.Transitions)
            if (index.TryGetValue(t.Segment, out int k) && t.Segment.Attributes.Has(RoadAttrFlags.InTransition)) result[k] = t;
        return result;
    }
}
