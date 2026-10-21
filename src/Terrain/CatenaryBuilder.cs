using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Overhead lines over electrified rail (#1052, <c>docs/plans/catenary.md</c>) from a tile's <c>CTNY</c> section, into the road
/// mesh: masts (a main line's square steel, a tram's round), a cantilever's arms out over its track, a span wire across a street
/// with its ends on facades (a wall plate) or masts, the hangers down to the contact wires, the wires themselves and, on a main
/// line, the messenger above the contact wire with droppers between. Collision is the masts only: everything else is above any
/// vehicle (the wire 5.5 m up and more).
/// </summary>
public static class CatenaryBuilder
{
    private static readonly Color MainSteel = new(0.42f, 0.44f, 0.45f);
    private static readonly Color TramSteel = new(0.52f, 0.54f, 0.55f);
    private static readonly Color Copper = new(0.20f, 0.18f, 0.16f);   // weathered contact wire, dark against the sky

    /// <summary>Masts: a main line's square side, a tram's round radius; the wire's radius (thicker than its 12 mm, or it vanishes), a mast's sink (m).</summary>
    private const float MainMastHalf = 0.12f, TramMastRadius = 0.11f, WireRadius = 0.02f, ArmRadius = 0.04f, Sink = 0.5f;
    /// <summary>A span wire runs this far over the contact wire at its ends and sags this much; a wall plate's half side (m).</summary>
    private const float SpanAbove = 0.9f, SpanSag = 0.25f, WallPlate = 0.12f;

    public static void Append(RoadTile tile, List<Vector3> vertices, List<Color> colors, List<Vector2> uvs, List<Vector2> uv2s, List<int> indices)
    {
        var mesh = new Mesh(vertices, colors, uvs, uv2s, indices);
        foreach (var s in tile.CatenarySupports) Support(mesh, s);
        foreach (var w in tile.CatenaryWires) Wire(mesh, w);
    }

    /// <summary>Collision for the masts: square columns from below the foot to 4 m (all else is overhead).</summary>
    public static Vector3[] BuildCollisionFaces(RoadTile tile)
    {
        var faces = new List<Vector3>();
        foreach (var s in tile.CatenarySupports)
        {
            if (s.Kind == CatenarySupportKind.Cantilever || !s.AWall) Column(faces, new Vector3(s.AX, s.AY, s.AZ));
            if (s.Kind == CatenarySupportKind.Span && !s.BWall) Column(faces, new Vector3(s.BX, s.BY, s.BZ));
        }
        return faces.ToArray();

        static void Column(List<Vector3> faces, Vector3 foot)
        {
            const float H = 4f, R = MainMastHalf;
            var c = new[] { new Vector3(R, 0, R), new Vector3(-R, 0, R), new Vector3(-R, 0, -R), new Vector3(R, 0, -R) };
            for (int k = 0; k < 4; k++)
            {
                var a0 = foot + c[k] + Vector3.Down * Sink;
                var b0 = foot + c[(k + 1) % 4] + Vector3.Down * Sink;
                var a1 = foot + c[k] + Vector3.Up * H;
                var b1 = foot + c[(k + 1) % 4] + Vector3.Up * H;
                faces.Add(a0); faces.Add(b0); faces.Add(b1);
                faces.Add(a0); faces.Add(b1); faces.Add(a1);
            }
        }
    }

    private static void Support(Mesh m, CatenarySupport s)
    {
        bool main = s.Line == CatenaryLine.Main;
        var steel = (main ? MainSteel : TramSteel).SrgbToLinear();
        var wire = Copper.SrgbToLinear();
        var a = new Vector3(s.AX, s.AY, s.AZ);
        var b = new Vector3(s.BX, s.BY, s.BZ);
        if (s.Kind == CatenarySupportKind.Cantilever)
        {
            // the mast up past the messenger (main) or the arm (tram); the arm out over the track, a brace under it
            float arm = s.WireY + (main ? RoadCatenary.SystemHeight : RoadCatenary.TramHang);
            float top = arm + (main ? 0.8f : 0.6f);
            Mast(m, a, top, main, steel);
            var dir = new Vector3(b.X - a.X, 0f, b.Z - a.Z);
            float reach = dir.Length();
            if (reach < 0.1f) return;
            dir /= reach;
            var tip = b + dir * 0.4f;
            m.Tube(steel, new Vector3(a.X, arm, a.Z), new Vector3(tip.X, arm, tip.Z), ArmRadius);
            m.Tube(steel, new Vector3(a.X, arm - 1.0f, a.Z), new Vector3(a.X + dir.X * reach * 0.6f, arm, a.Z + dir.Z * reach * 0.6f), ArmRadius * 0.7f);
            if (main)
            {
                // the registration arm from the mast to the contact wire under the messenger
                m.Tube(steel, new Vector3(a.X, s.WireY + 0.25f, a.Z), new Vector3(b.X, s.WireY, b.Z), ArmRadius * 0.6f);
                m.Tube(wire, new Vector3(b.X, arm, b.Z), new Vector3(b.X, s.WireY, b.Z), WireRadius);
            }
            else m.Tube(wire, new Vector3(b.X, arm, b.Z), new Vector3(b.X, s.WireY, b.Z), WireRadius);
            return;
        }
        // a span: its ends on wall plates or masts, the wire sagging between, a hanger down to each contact wire
        float ends = s.WireY + SpanAbove;
        if (s.AWall) m.Box(wire, new Vector3(a.X, ends, a.Z), WallPlate); else Mast(m, a, ends + 0.5f, main, steel);
        if (s.BWall) m.Box(wire, new Vector3(b.X, ends, b.Z), WallPlate); else Mast(m, b, ends + 0.5f, main, steel);
        float length = new Vector2(b.X - a.X, b.Z - a.Z).Length();
        if (length < 0.5f) return;
        float SpanY(Vector3 at)
        {
            float t = Mathf.Clamp(new Vector2(at.X - a.X, at.Z - a.Z).Length() / length, 0f, 1f);
            return ends - 4f * SpanSag * t * (1f - t);
        }
        const int Pieces = 12;
        for (int k = 0; k < Pieces; k++)
        {
            var p0 = a.Lerp(b, (float)k / Pieces);
            var p1 = a.Lerp(b, (float)(k + 1) / Pieces);
            m.Tube(wire, new Vector3(p0.X, SpanY(p0), p0.Z), new Vector3(p1.X, SpanY(p1), p1.Z), WireRadius);
        }
        for (int i = 0; i + 1 < s.Over.Length; i += 2)
        {
            var over = new Vector3(s.Over[i], s.WireY, s.Over[i + 1]);
            float hang = main ? s.WireY + RoadCatenary.SystemHeight : SpanY(over);
            m.Tube(wire, new Vector3(over.X, SpanY(over), over.Z), over, WireRadius);
            if (main && hang > SpanY(over)) m.Tube(wire, new Vector3(over.X, hang, over.Z), over, WireRadius);
        }
    }

    /// <summary>A contact wire along its points; on a main line the messenger above it and droppers between.</summary>
    private static void Wire(Mesh m, CatenaryWire w)
    {
        var wire = Copper.SrgbToLinear();
        bool main = w.Line == CatenaryLine.Main;
        var up = Vector3.Up * RoadCatenary.SystemHeight;
        float along = 0f, nextDropper = 0f;
        for (int i = 3; i + 2 < w.Points.Length; i += 3)
        {
            var p0 = new Vector3(w.Points[i - 3], w.Points[i - 2], w.Points[i - 1]);
            var p1 = new Vector3(w.Points[i], w.Points[i + 1], w.Points[i + 2]);
            m.Tube(wire, p0, p1, WireRadius);
            if (!main) continue;
            m.Tube(wire, p0 + up, p1 + up, WireRadius);
            float len = p0.DistanceTo(p1);
            while (nextDropper <= along + len)
            {
                var d = p0.Lerp(p1, len < 1e-4f ? 0f : (nextDropper - along) / len);
                m.Tube(wire, d, d + up, WireRadius * 0.5f);
                nextDropper += RoadCatenary.DropperStep;
            }
            along += len;
        }
    }

    private static void Mast(Mesh m, Vector3 foot, float top, bool main, Color steel)
    {
        if (main) m.Post(steel, foot, top, MainMastHalf);
        else m.Round(steel, foot, top, TramMastRadius);
    }

    /// <summary>The road mesh's lists, with the few shapes the lines need (faces wound both ways: the road material draws both sides).</summary>
    private readonly record struct Mesh(List<Vector3> V, List<Color> C, List<Vector2> Uv, List<Vector2> Uv2, List<int> I)
    {
        private void Quad(Color colour, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int start = V.Count;
            foreach (var p in (ReadOnlySpan<Vector3>)[a, b, c, d])
            {
                V.Add(p);
                C.Add(colour);
                Uv.Add(Vector2.Zero);
                Uv2.Add(Vector2.Zero);
            }
            I.Add(start); I.Add(start + 1); I.Add(start + 2);
            I.Add(start); I.Add(start + 2); I.Add(start + 3);
        }

        /// <summary>A four-sided tube from <paramref name="a"/> to <paramref name="b"/>.</summary>
        public void Tube(Color colour, Vector3 a, Vector3 b, float radius)
        {
            var axis = b - a;
            if (axis.LengthSquared() < 1e-8f) return;
            axis = axis.Normalized();
            var u = Mathf.Abs(axis.Y) > 0.9f ? axis.Cross(Vector3.Right).Normalized() : axis.Cross(Vector3.Up).Normalized();
            var v = axis.Cross(u);
            var d = new[] { u * radius, v * radius, -u * radius, -v * radius };
            for (int k = 0; k < 4; k++) Quad(colour, a + d[k], a + d[(k + 1) % 4], b + d[(k + 1) % 4], b + d[k]);
        }

        public void Post(Color colour, Vector3 foot, float top, float half)
        {
            var c = new[] { new Vector3(half, 0, half), new Vector3(-half, 0, half), new Vector3(-half, 0, -half), new Vector3(half, 0, -half) };
            for (int k = 0; k < 4; k++)
            {
                var p = c[k];
                var q = c[(k + 1) % 4];
                Quad(colour, foot + p + Vector3.Down * Sink, foot + q + Vector3.Down * Sink,
                    new Vector3(foot.X + q.X, top, foot.Z + q.Z), new Vector3(foot.X + p.X, top, foot.Z + p.Z));
            }
            Quad(colour, new Vector3(foot.X + c[3].X, top, foot.Z + c[3].Z), new Vector3(foot.X + c[2].X, top, foot.Z + c[2].Z),
                new Vector3(foot.X + c[1].X, top, foot.Z + c[1].Z), new Vector3(foot.X + c[0].X, top, foot.Z + c[0].Z));
        }

        public void Round(Color colour, Vector3 foot, float top, float radius)
        {
            const int Sides = 8;
            for (int k = 0; k < Sides; k++)
            {
                float a0 = Mathf.Tau * k / Sides, a1 = Mathf.Tau * (k + 1) / Sides;
                var p = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * radius;
                var q = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * radius;
                Quad(colour, foot + p + Vector3.Down * Sink, foot + q + Vector3.Down * Sink,
                    new Vector3(foot.X + q.X, top, foot.Z + q.Z), new Vector3(foot.X + p.X, top, foot.Z + p.Z));
            }
        }

        public void Box(Color colour, Vector3 centre, float half)
        {
            Post(colour, centre + Vector3.Down * (half - Sink), centre.Y + half, half);
        }
    }
}
