namespace UnitSport.Terrain.Format;

/// <summary>
/// Who drives a junction path (#818, #709): cars (and trucks), riders on a bike lane, or riders from a separated path to another
/// (#971: round the core, over the next arm's bike crossing or round the corner; its ends on the paths, not on a lane).
/// </summary>
public enum JunctionPathKind : byte { Car = 0, Bike = 1, Path = 2 }

/// <summary>
/// One end of a junction path (#818, #709): where its road meets the core's mouth, a point on the road's centreline (tile-local
/// x, y, z, in the path's tile; it may lie a little past the tile's edge when the road crosses it there), and the lane centre's
/// offset there, metres right of a driver arriving on that arm (driving toward the junction). The game ties it to the road end
/// at that point, as it ties road ends to each other, so a path reaches roads in the next tile.
/// </summary>
public readonly record struct JunctionPathEnd(float X, float Y, float Z, float Offset);

/// <summary>
/// A lane-to-lane path through a junction core (#818 phase 5, #709, section <c>JPTH</c>), as the core planner laid its
/// connector: from an arriving lane at its arm's mouth to a departing lane at the exit's mouth, the line traffic follows
/// through the junction, the paint drawn along its edges. Tile-local points (x east, z south) with heights on the cap.
/// </summary>
public sealed class RoadJunctionPath
{
    /// <summary>The core's cap (one of the tile's <see cref="RoadTile.Junctions"/>).</summary>
    public required RoadJunction Junction { get; init; }
    public required JunctionPathEnd From { get; init; }
    public required JunctionPathEnd To { get; init; }
    /// <summary>The move it makes from its arm (one of left, through, right; U-turns are not laid).</summary>
    public required SignalMoves Move { get; init; }
    public JunctionPathKind Kind { get; init; }
    /// <summary>Index of the junction's record in the tile's <see cref="RoadTile.Signals"/>, -1 without lights; and the group whose green it goes on.</summary>
    public short Signal { get; init; } = -1;
    public byte Group { get; init; }
    /// <summary>Its width leaving its lane and arriving in the exit's (m).</summary>
    public float Width { get; init; }
    public float ToWidth { get; init; }
    /// <summary>The centre line, tile-local x, y, z per point (at least two points).</summary>
    public required float[] Points { get; init; }

    public int PointCount => Points.Length / 3;

    /// <summary>1 is the first; a reader skips a section of a version it does not know.</summary>
    public const byte SectionVersion = 1;

    /// <summary>
    /// Writes the paths whose cap the tile holds: <c>version u8, count varint</c>; per path <c>junction varint, from (x, y, z, offset zigzag cm), to (the same),
    /// move u8, kind u8, signal zigzag, group u8, width varint cm, toWidth varint cm, pointCount varint</c>, then its points as
    /// zigzag cm: the first absolute, each next as the delta from the one before (x, y, z).
    /// </summary>
    public static void Write(BinaryWriter w, IReadOnlyList<RoadJunctionPath> paths, IReadOnlyList<RoadJunction> junctions)
    {
        var junction = new Dictionary<RoadJunction, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < junctions.Count; i++) junction[junctions[i]] = i;
        // a path whose cap the tile no longer holds is left out
        var kept = paths.Where(p => junction.ContainsKey(p.Junction)).ToList();
        w.Write(SectionVersion);
        w.Write7BitEncodedInt(kept.Count);
        foreach (var p in kept)
        {
            w.Write7BitEncodedInt(junction[p.Junction]);
            WriteEnd(w, p.From);
            WriteEnd(w, p.To);
            w.Write((byte)p.Move);
            w.Write((byte)p.Kind);
            Zigzag(w, p.Signal);
            w.Write(p.Group);
            w.Write7BitEncodedInt(Cm(p.Width));
            w.Write7BitEncodedInt(Cm(p.ToWidth));
            int n = p.PointCount;
            w.Write7BitEncodedInt(n);
            int px = 0, py = 0, pz = 0;
            for (int i = 0; i < n; i++)
            {
                int x = Cm(p.Points[i * 3]), y = Cm(p.Points[i * 3 + 1]), z = Cm(p.Points[i * 3 + 2]);
                Zigzag(w, x - px); Zigzag(w, y - py); Zigzag(w, z - pz);
                (px, py, pz) = (x, y, z);
            }
        }
    }

    /// <summary>A path as read: its cap as an index, resolved once the caps are read.</summary>
    public sealed record Raw(int Junction, JunctionPathEnd From, JunctionPathEnd To,
        SignalMoves Move, JunctionPathKind Kind, short Signal, byte Group, float Width, float ToWidth, float[] Points);

    /// <summary>Reads JPTH; null for a section version this build does not know (skipped whole).</summary>
    public static List<Raw>? Read(BinaryReader r)
    {
        if (r.ReadByte() != SectionVersion) return null;
        int count = r.Read7BitEncodedInt();
        var result = new List<Raw>(count);
        for (int i = 0; i < count; i++)
        {
            int junction = r.Read7BitEncodedInt();
            var from = ReadEnd(r);
            var to = ReadEnd(r);
            var move = (SignalMoves)r.ReadByte();
            var kind = (JunctionPathKind)r.ReadByte();
            short signal = (short)ReadZigzag(r);
            byte group = r.ReadByte();
            float width = r.Read7BitEncodedInt() / 100f, toWidth = r.Read7BitEncodedInt() / 100f;
            int n = r.Read7BitEncodedInt();
            var points = new float[n * 3];
            int px = 0, py = 0, pz = 0;
            for (int k = 0; k < n; k++)
            {
                px += ReadZigzag(r); py += ReadZigzag(r); pz += ReadZigzag(r);
                points[k * 3] = px / 100f; points[k * 3 + 1] = py / 100f; points[k * 3 + 2] = pz / 100f;
            }
            result.Add(new Raw(junction, from, to, move, kind, signal, group, width, toWidth, points));
        }
        return result;
    }

    /// <summary>The paths read, on the tile's caps (a path naming one the tile lacks is dropped).</summary>
    public static IEnumerable<RoadJunctionPath> Resolve(IEnumerable<Raw> raw, IReadOnlyList<RoadJunction> junctions)
    {
        foreach (var p in raw)
        {
            if (p.Junction >= junctions.Count) continue;
            yield return new RoadJunctionPath
            {
                Junction = junctions[p.Junction], From = p.From, To = p.To,
                Move = p.Move, Kind = p.Kind, Signal = p.Signal, Group = p.Group, Width = p.Width, ToWidth = p.ToWidth, Points = p.Points,
            };
        }
    }

    private static void WriteEnd(BinaryWriter w, JunctionPathEnd e)
    {
        Zigzag(w, Cm(e.X)); Zigzag(w, Cm(e.Y)); Zigzag(w, Cm(e.Z));
        Zigzag(w, Cm(e.Offset));
    }

    private static JunctionPathEnd ReadEnd(BinaryReader r) =>
        new(ReadZigzag(r) / 100f, ReadZigzag(r) / 100f, ReadZigzag(r) / 100f, ReadZigzag(r) / 100f);

    private static int Cm(double v) => (int)Math.Round(v * 100);

    private static void Zigzag(BinaryWriter w, int v) => w.Write7BitEncodedInt((v << 1) ^ (v >> 31));

    private static int ReadZigzag(BinaryReader r)
    {
        int v = r.Read7BitEncodedInt();
        return (int)((uint)v >> 1) ^ -(v & 1);
    }
}
