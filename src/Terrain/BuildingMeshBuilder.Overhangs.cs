using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

public static partial class BuildingMeshBuilder
{
    /// <summary>
    /// Draws <see cref="RoofOverhangs"/> strips as slabs <see cref="RoofOverhangs.Thickness"/>
    /// deep (#1067): the top in the roof's plane with its tile courses (the roof frame, CUSTOM0.w
    /// 1), the soffit straight under it, the fascia board along the outer edge and a closing end
    /// where a strip stops short of a neighbour. Each plane's eave in <paramref name="roofSpans"/>
    /// moves out to the strips' lowest point first, so the gutter the shader paints at the eave
    /// runs along the overhang's edge and the courses carry on across the joint.
    /// Every face is wound to face out (Godot's front is clockwise): the slab is closed where it
    /// shows, and the soffit inside the wall is never seen.
    /// </summary>
    private static void AppendOverhangs(List<RoofOverhangs.Strip> strips,
        Dictionary<(int, int, int, int), (float Min, float Max)> roofSpans, Color roof, Color wall, BuildingKind kind, FacadeSink sink)
    {
        if (strips.Count == 0) return;
        foreach (var s in strips)
        {
            if (!roofSpans.TryGetValue(s.Plane, out var span)) continue;
            float low = Mathf.Min(s.OuterP.Dot(s.UpSlope), s.OuterQ.Dot(s.UpSlope));
            if (low < span.Min) roofSpans[s.Plane] = (low, span.Max);
        }
        // a house's or a barn's eaves are boarded timber; masonry blocks and churches are rendered under theirs
        var soffit = kind is BuildingKind.House or BuildingKind.Agricultural or BuildingKind.Annex or BuildingKind.Garage
            ? new Color(0.55f, 0.43f, 0.31f).SrgbToLinear()
            : wall * 0.86f;
        soffit.A = 1f;
        var fascia = roof * 0.72f;
        fascia.A = 1f;
        var down = new Vector3(0f, RoofOverhangs.Thickness, 0f);
        foreach (var s in strips)
        {
            if (!roofSpans.TryGetValue(s.Plane, out var span)) continue;
            var along = new Vector3(-s.UpSlope.Z, 0f, s.UpSlope.X).Normalized();
            // the top: the roof's own frame, as the roof triangles get it
            Face(sink, s.P, s.Q, s.OuterQ, s.OuterP, s.Normal, roof, p => (
                new Vector2(p.Dot(along), -1f),
                new Vector2(p.Dot(s.UpSlope) - span.Min, span.Max - span.Min),
                new Vector4(s.UpSlope.X, s.UpSlope.Y, s.UpSlope.Z, 1f)));
            // the soffit, the fascia and the open ends: plain, no windows
            Face(sink, s.P - down, s.Q - down, s.OuterQ - down, s.OuterP - down, -s.Normal, soffit, Plain);
            Face(sink, s.OuterP, s.OuterQ, s.OuterQ - down, s.OuterP - down, s.Out, fascia, Plain);
            var dir = (s.Q - s.P).Normalized();
            if (s.CapP) Face(sink, s.P, s.OuterP, s.OuterP - down, s.P - down, -dir, fascia, Plain);
            if (s.CapQ) Face(sink, s.Q, s.OuterQ, s.OuterQ - down, s.Q - down, dir, fascia, Plain);
        }
    }

    private static (Vector2 Uv, Vector2 Uv2, Vector4 Frame) Plain(Vector3 p) => (new Vector2(0f, -1f), Vector2.Zero, Vector4.Zero);

    // a quad as two triangles facing `out` (Godot's front face is clockwise seen from the front)
    private static void Face(FacadeSink sink, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 out_, Color color,
        Func<Vector3, (Vector2 Uv, Vector2 Uv2, Vector4 Frame)> attributes)
    {
        if ((b - a).Cross(c - a).Dot(out_) > 0f) (b, d) = (d, b);
        Span<Vector3> corners = stackalloc Vector3[] { a, b, c, a, c, d };
        foreach (var p in corners)
        {
            var (uv, uv2, frame) = attributes(p);
            sink.V.Add(p);
            sink.C.Add(color);
            sink.Uv.Add(uv);
            sink.Uv2.Add(uv2);
            sink.Frames.Add(frame.X);
            sink.Frames.Add(frame.Y);
            sink.Frames.Add(frame.Z);
            sink.Frames.Add(frame.W);
        }
    }
}
