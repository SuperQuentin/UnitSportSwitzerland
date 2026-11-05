namespace UnitSport.Tools.RoadGen.Rewrite;

using System.Reflection;
using UnitSport.Terrain.Format;
using UnitSport.Tools.RoadGen.Geometry;

/// <summary>
/// A road tile's records between the stage's tile-local frame on a work frame (<see cref="FrameUnits"/>: a
/// plane-aligned square round the leaf, y the height above sea level) and the leaf's own anchor frame (x east,
/// y up, z south at the leaf's centre, as every leaf section holds its features) (#1507, G2 B3 4b). Every
/// position goes through the leaf's <see cref="LeafTransform"/> (<see cref="LeafTransform.PlaneToLocal"/> /
/// <see cref="LeafTransform.LocalToPlane"/>), every heading through its Jacobian at that point; widths,
/// offsets and stations along a line are metres in both (the plane is true to 3 ppm).
///
/// <para>
/// The tile is changed in place: point and vertex arrays are rewritten where they are (paint along a
/// segment, transitions, junction paths and conflict pairs hold references to them, which stay valid; an
/// array two records share is turned once), and the few init-only scalar positions (signals, overheads,
/// gantries, approaches, catenary supports, junction path ends) are set through their property setters.
/// </para>
/// </summary>
public static class LeafRecords
{
    /// <summary>The stage's finished tile into its leaf's anchor frame.</summary>
    public static void ToLeaf(RoadTile tile, FrameUnits units) =>
        Map(tile, new ToAnchor(units.TransformOf(tile.Id), units.West(tile.Id), units.North(tile.Id)));

    /// <summary>A leaf's records (its anchor frame) into the stage's tile-local frame of <paramref name="id"/>.</summary>
    public static void FromLeaf(RoadTile tile, FrameUnits units, TileId id) =>
        Map(tile, new FromAnchor(units.TransformOf(id), units.West(id), units.North(id)));

    /// <summary>A plane point and height above sea level from the stage's tile-local frame of <paramref name="id"/>.</summary>
    public static (double X, double Y) PlaneOf(FrameUnits units, TileId id, double x, double z) =>
        (units.West(id) + x, units.North(id) - z);

    private interface IMap
    {
        (float X, float Y, float Z) Point(float x, float y, float z);
        /// <summary>A heading about +Y, 0 facing −Z (north), at a point (before the move).</summary>
        float Godot(float heading, float x, float y, float z);
        /// <summary>An angle counter-clockwise from east (x), at a point (before the move).</summary>
        float Math(float angle, float x, float y, float z);
    }

    private sealed class ToAnchor(LeafTransform t, double west, double north) : IMap
    {
        public (float X, float Y, float Z) Point(float x, float y, float z)
        {
            var (lx, ly, lz) = t.PlaneToLocal(west + x, north - z, y);
            return ((float)lx, (float)ly, (float)lz);
        }

        public float Godot(float heading, float x, float y, float z)
        {
            var (dx, dz) = t.DirectionToLocal(west + x, north - z, -System.Math.Sin(heading), System.Math.Cos(heading), y);
            return (float)System.Math.Atan2(-dx, -dz);
        }

        public float Math(float angle, float x, float y, float z)
        {
            var (dx, dz) = t.DirectionToLocal(west + x, north - z, System.Math.Cos(angle), System.Math.Sin(angle), y);
            return (float)System.Math.Atan2(-dz, dx);
        }
    }

    private sealed class FromAnchor(LeafTransform t, double west, double north) : IMap
    {
        public (float X, float Y, float Z) Point(float x, float y, float z)
        {
            var (px, py, h) = t.LocalToPlane(x, y, z);
            return ((float)(px - west), (float)h, (float)(north - py));
        }

        public float Godot(float heading, float x, float y, float z)
        {
            var (px, py, h) = t.LocalToPlane(x, y, z);
            var (dx, dy) = t.DirectionToPlane(px, py, -System.Math.Sin(heading), -System.Math.Cos(heading), h);
            return (float)System.Math.Atan2(-dx, dy);
        }

        public float Math(float angle, float x, float y, float z)
        {
            var (px, py, h) = t.LocalToPlane(x, y, z);
            var (dx, dy) = t.DirectionToPlane(px, py, System.Math.Cos(angle), -System.Math.Sin(angle), h);
            return (float)System.Math.Atan2(dy, dx);
        }
    }

    private static void Map(RoadTile tile, IMap m)
    {
        var done = new HashSet<float[]>(ReferenceEqualityComparer.Instance);
        void Triples(float[] a, int stride = 3)
        {
            if (!done.Add(a)) return;
            for (int k = 0; k + 2 < a.Length; k += stride)
            {
                if (float.IsNaN(a[k]) || float.IsNaN(a[k + 2])) continue;
                (a[k], a[k + 1], a[k + 2]) = m.Point(a[k], a[k + 1], a[k + 2]);
            }
        }
        /// a point's height after the move, for a height kept apart from its point (a structure's lower edge, a wire)
        float HeightAt(float x, float y, float z) => m.Point(x, y, z).Y;

        foreach (var s in tile.Segments) Triples(s.Points);
        foreach (var j in tile.Junctions) Triples(j.Vertices);
        foreach (var p in tile.Paint) Triples(p.Vertices);
        foreach (var l in tile.LinearProps) Triples(l.Points, 4);
        foreach (var a in tile.AreaProps) Triples(a.Vertices);
        for (int i = 0; i < tile.PointProps.Count; i++)
        {
            var p = tile.PointProps[i];
            var (x, y, z) = m.Point(p.X, p.Y, p.Z);
            tile.PointProps[i] = p with { X = x, Y = y, Z = z, Heading = m.Godot(p.Heading, p.X, p.Y, p.Z) };
        }
        foreach (var s in tile.Signals)
        {
            for (int i = 0; i < s.Poles.Count; i++)
            {
                var p = s.Poles[i];
                var (x, y, z) = m.Point(p.X, p.Y, p.Z);
                s.Poles[i] = p with
                {
                    X = x, Y = y, Z = z,
                    CarHeading = m.Godot(p.CarHeading, p.X, p.Y, p.Z), PedHeading = m.Godot(p.PedHeading, p.X, p.Y, p.Z),
                };
            }
            foreach (var o in s.Overheads)
            {
                var (ax, ay, az) = m.Point(o.X, o.Y, o.Z);
                var (bx, by, bz) = m.Point(o.EndX, o.EndY, o.EndZ);
                Set(o, (ax, ay, az, bx, by, bz), HeightAt(o.X, o.Bottom, o.Z), m.Godot(o.Heading, o.X, o.Y, o.Z));
            }
            foreach (var g in s.Gantries)
            {
                var (ax, ay, az) = m.Point(g.X, g.Y, g.Z);
                var (bx, by, bz) = m.Point(g.EndX, g.EndY, g.EndZ);
                Set(g, (ax, ay, az, bx, by, bz), HeightAt(g.X, g.Bottom, g.Z), m.Godot(g.Heading, g.X, g.Y, g.Z));
            }
            foreach (var c in s.ConflictLines) Triples(c.Points);
            Triples(s.Stops);
            var (sx, sy, sz) = m.Point(s.X, s.Y, s.Z);
            SetXyz(s, sx, sy, sz);
        }
        foreach (var a in tile.Approaches)
        {
            float heading = m.Math(a.Heading, a.X, a.Y, a.Z);
            var (x, y, z) = m.Point(a.X, a.Y, a.Z);
            SetXyz(a, x, y, z);
            Prop(a, nameof(RoadApproach.Heading), heading);
        }
        for (int i = 0; i < tile.Parking.Count; i++)
        {
            var b = tile.Parking[i];
            var (x, y, z) = m.Point(b.X, b.Y, b.Z);
            tile.Parking[i] = b with { X = x, Y = y, Z = z, Heading = m.Godot(b.Heading, b.X, b.Y, b.Z) };
        }
        foreach (var p in tile.JunctionPaths)
        {
            Triples(p.Points);
            Prop(p, nameof(RoadJunctionPath.From), End(p.From));
            Prop(p, nameof(RoadJunctionPath.To), End(p.To));
        }
        foreach (var c in tile.CatenarySupports)
        {
            var over = c.Over;
            var moved = new float[over.Length];
            for (int k = 0; k + 1 < over.Length; k += 2)
            {
                var (x, _, z) = m.Point(over[k], c.WireY, over[k + 1]);
                (moved[k], moved[k + 1]) = (x, z);
            }
            float wire = HeightAt(c.AX, c.WireY, c.AZ);
            var (ax, ay, az) = m.Point(c.AX, c.AY, c.AZ);
            var (bx, by, bz) = m.Point(c.BX, c.BY, c.BZ);
            foreach (var (name, v) in (ReadOnlySpan<(string, float)>)[(nameof(CatenarySupport.AX), ax), (nameof(CatenarySupport.AY), ay), (nameof(CatenarySupport.AZ), az),
                         (nameof(CatenarySupport.BX), bx), (nameof(CatenarySupport.BY), by), (nameof(CatenarySupport.BZ), bz), (nameof(CatenarySupport.WireY), wire)])
                Prop(c, name, v);
            c.Over = moved;
        }
        foreach (var w in tile.CatenaryWires) Triples(w.Points);

        JunctionPathEnd End(JunctionPathEnd e)
        {
            var (x, y, z) = m.Point(e.X, e.Y, e.Z);
            return e with { X = x, Y = y, Z = z };
        }
    }

    private static void Set(object o, (float X, float Y, float Z, float EndX, float EndY, float EndZ) p, float bottom, float heading)
    {
        SetXyz(o, p.X, p.Y, p.Z);
        Prop(o, "EndX", p.EndX);
        Prop(o, "EndY", p.EndY);
        Prop(o, "EndZ", p.EndZ);
        Prop(o, "Bottom", bottom);
        Prop(o, "Heading", heading);
    }

    private static void SetXyz(object o, float x, float y, float z)
    {
        Prop(o, "X", x);
        Prop(o, "Y", y);
        Prop(o, "Z", z);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, string), PropertyInfo> Props = new();

    /// <summary>Sets an init-only property of a record the stage built (the converter is its last writer).</summary>
    private static void Prop(object o, string name, object value) =>
        Props.GetOrAdd((o.GetType(), name), k => k.Item1.GetProperty(k.Item2) ?? throw new MissingMemberException(k.Item1.Name, k.Item2))
            .SetValue(o, value);
}
