using Godot;

namespace UnitSport.Terrain.Stadiums;

/// <summary>
/// A stadium's occluder (#885): the facade as a ring of walls just inside it, and the roof's solid
/// part, in the stadium frame (u, y, v). From the stands or the stage the bowl hides the city round
/// it, and occlusion culling (<see cref="ChunkManager.ApplyOcclusion"/>) then skips drawing it: from
/// the stage of Geneva, half of the primitives in view were buildings behind the stands. The roof's
/// translucent ring and its opening stay open, so the sky and what is seen through it still draw.
/// </summary>
public static class StadiumOccluder
{
    private const int CornerSegments = 6;
    /// <summary>The walls stand this far inside the facade: an occluder must never hide what really shows.</summary>
    private const float Inset = 0.8f;

    public static OccluderInstance3D? Make(StadiumSpec s)
    {
        if (s.Kind == VenueKind.OpenAir) return null;   // a fence hides nothing
        float top = s.RoofOuter - s.RoofThickness - 0.5f;
        float maxDepth = Math.Max(Math.Max(s.DepthNorth, s.DepthSouth), Math.Max(s.DepthEast, s.DepthWest));
        // a corner radius at least the deepest side's: rounder than the real facade, so inside it
        var outer = Ring(-(s.FrontHalfU + s.DepthWest) + Inset, s.FrontHalfU + s.DepthEast - Inset,
            -(s.FrontHalfV + s.DepthNorth) + Inset, s.FrontHalfV + s.DepthSouth - Inset, s.FrontCorner + maxDepth);
        // an arena's roof is closed: the "opening" ring shrinks to nothing round the centre
        float hole = s.Kind == VenueKind.Arena ? -Math.Min(s.FrontHalfU, s.FrontHalfV) + 0.5f : s.RoofInset + s.RoofRing + 0.5f;
        var inner = Ring(-(s.FrontHalfU + hole), s.FrontHalfU + hole, -(s.FrontHalfV + hole), s.FrontHalfV + hole, s.FrontCorner + hole);

        var v = new List<Vector3>();
        var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int o = v.Count;
            v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            // both windings: the bowl hides from inside and out
            idx.AddRange(new[] { o, o + 1, o + 2, o, o + 2, o + 3, o, o + 2, o + 1, o, o + 3, o + 2 });
        }
        int n = outer.Count;
        for (int i = 0; i < n; i++)
        {
            var a = outer[i];
            var b = outer[(i + 1) % n];
            Quad(new Vector3(a.X, -2f, a.Y), new Vector3(b.X, -2f, b.Y), new Vector3(b.X, top, b.Y), new Vector3(a.X, top, a.Y));
            // the roof, from the facade in to where its translucent ring begins
            if (s.Kind == VenueKind.Arena || s.RoofInset + s.RoofRing < maxDepth)
            {
                var ia = inner[i];
                var ib = inner[(i + 1) % n];
                Quad(new Vector3(a.X, top, a.Y), new Vector3(b.X, top, b.Y), new Vector3(ib.X, top, ib.Y), new Vector3(ia.X, top, ia.Y));
            }
        }
        var occluder = new ArrayOccluder3D();
        occluder.SetArrays(v.ToArray(), idx.ToArray());
        return new OccluderInstance3D { Name = "StadiumOccluder", Occluder = occluder };
    }

    /// <summary>A rounded rectangle, clockwise from above, the same number of points for any size (so two rings pair up).</summary>
    private static List<Vector2> Ring(float u0, float u1, float v0, float v1, float r)
    {
        r = Math.Clamp(r, 0.05f, Math.Max(0.05f, Math.Min((u1 - u0) / 2, (v1 - v0) / 2) - 0.01f));
        var pts = new List<Vector2>();
        (Vector2 C, float A)[] corners =
        {
            (new Vector2(u1 - r, v0 + r), -MathF.PI / 2), (new Vector2(u1 - r, v1 - r), 0f),
            (new Vector2(u0 + r, v1 - r), MathF.PI / 2), (new Vector2(u0 + r, v0 + r), MathF.PI),
        };
        foreach (var (c, a0) in corners)
            for (int k = 0; k <= CornerSegments; k++)
            {
                float a = a0 + MathF.PI / 2 * k / CornerSegments;
                pts.Add(c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r);
            }
        return pts;
    }
}
