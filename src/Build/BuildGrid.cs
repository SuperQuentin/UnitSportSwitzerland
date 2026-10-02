using UnitSport.Items;

namespace UnitSport.Build;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md). The rules of
// building (#274): the grid, what a piece costs and holds, and what stands up.

/// <summary>What a piece is. Saved and sent as a number: append only.</summary>
public enum PieceKind : byte { Floor, Wall, WindowWall, DoorWall, HalfWall, Stairs, Roof, Pillar }

/// <summary>What a piece is made of. Saved and sent as a number: append only.</summary>
public enum BuildMaterial : byte { Wood, Stone, Metal, Sandbag }

/// <summary>Which of a cell's places a piece takes: its floor, one of its edges, or its volume.</summary>
public enum SlotClass : byte { Floor, Edge, Volume }

/// <summary>
/// A place on a structure's grid. Cell (X, Y, Z): X and Z in <see cref="BuildGrid.Cell"/> steps, Y in
/// storeys. An edge is stored on its cell's −Z side (0) or −X side (3) only, so the wall between two
/// cells has one key (<see cref="Edge"/> normalises).
/// </summary>
public readonly record struct Slot(int X, int Y, int Z, SlotClass Class, int Side = 0)
{
    public static Slot Floor(int x, int y, int z) => new(x, y, z, SlotClass.Floor);
    public static Slot Volume(int x, int y, int z) => new(x, y, z, SlotClass.Volume);

    /// <summary>The edge on <paramref name="side"/> (0 −Z, 1 +X, 2 +Z, 3 −X) of a cell, normalised.</summary>
    public static Slot Edge(int x, int y, int z, int side) => (side & 3) switch
    {
        1 => new(x + 1, y, z, SlotClass.Edge, 3),
        2 => new(x, y, z + 1, SlotClass.Edge, 0),
        int s => new(x, y, z, SlotClass.Edge, s),
    };
}

/// <summary>
/// One piece. <see cref="Dir"/> is the way stairs climb and a roof's ridge runs (0 −Z, 1 +X, 2 +Z,
/// 3 −X); edges and floors ignore it. <see cref="Grounded"/>: it stands on the terrain (on stilts if
/// need be), so it needs nothing else to hold it up.
/// </summary>
public readonly record struct Piece(Slot Slot, PieceKind Kind, int Dir, BuildMaterial Material, bool Grounded);

/// <summary>What a material costs per piece, how much it takes, how long it grows, how far it reaches out unsupported.</summary>
public sealed record MaterialSpec(BuildMaterial Material, string Name, (ItemId Id, int Count)[] Cost, float Hp, float Seconds, int Span);

public static class BuildGrid
{
    /// <summary>A cell's width and depth, and a storey's height, in metres: close to a real storey.</summary>
    public const float Cell = 2.4f, Storey = 2.4f;

    /// <summary>How far below a grounded piece the terrain may be: legs grow down that far.</summary>
    public const float StiltMax = 6f;

    public const int MaxPiecesFree = 400, MaxPiecesMatch = 300;

    public static readonly MaterialSpec[] Materials =
    {
        new(BuildMaterial.Wood, "Wood", new[] { (ItemId.WoodPlanks, 5) }, 150, 2f, 6),
        new(BuildMaterial.Stone, "Stone", new[] { (ItemId.Stone, 8) }, 300, 4f, 4),
        new(BuildMaterial.Metal, "Metal", new[] { (ItemId.ScrapMetal, 4), (ItemId.Screws, 2) }, 500, 6f, 10),
        new(BuildMaterial.Sandbag, "Sandbags", new[] { (ItemId.SandBag, 3) }, 400, 3f, 2),
    };

    public static MaterialSpec Spec(BuildMaterial m) => Materials[(int)m];

    public static readonly PieceKind[] Kinds = Enum.GetValues<PieceKind>();

    public static string Name(PieceKind k) => k switch
    {
        PieceKind.WindowWall => "Window wall",
        PieceKind.DoorWall => "Door wall",
        PieceKind.HalfWall => "Half wall",
        _ => k.ToString(),
    };

    public static SlotClass ClassOf(PieceKind k) => k switch
    {
        PieceKind.Floor => SlotClass.Floor,
        PieceKind.Stairs or PieceKind.Roof or PieceKind.Pillar => SlotClass.Volume,
        _ => SlotClass.Edge,
    };

    /// <summary>Sandbags only stack into walls.</summary>
    public static bool Allowed(PieceKind k, BuildMaterial m) =>
        m != BuildMaterial.Sandbag || k is PieceKind.Wall or PieceKind.HalfWall;

    /// <summary>Small pieces (half wall, pillar) cost and hold about half.</summary>
    private static bool Small(PieceKind k) => k is PieceKind.HalfWall or PieceKind.Pillar;

    public static IEnumerable<(ItemId Id, int Count)> Cost(PieceKind k, BuildMaterial m) =>
        Spec(m).Cost.Select(c => (c.Id, Small(k) ? (c.Count + 1) / 2 : c.Count));

    public static float MaxHp(PieceKind k, BuildMaterial m) => Spec(m).Hp * (Small(k) ? 0.6f : 1f);

    /// <summary>A piece grows to its full strength over its material's build time, from a tenth of it.</summary>
    public static float GrownHp(PieceKind k, BuildMaterial m, double age) =>
        MaxHp(k, m) * (0.1f + 0.9f * (float)Math.Clamp(age / Spec(m).Seconds, 0, 1));

    // ------------------------------------------------------------------------------------
    // support: which pieces hold which
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// Where pieces touch: points on a half-cell lattice. A floor's edges and centre (<see cref="Role.Level"/>),
    /// a wall's foot and top and its two sides, a stair's low and high edges, a pillar's foot and top.
    /// Two pieces sharing a point are joined.
    /// </summary>
    public enum Role : byte { Level, Bottom, Top, Side }

    public readonly record struct Point(int X, int Y, int Z);

    private static Point EdgePoint(int x, int z, int side, int level) => (side & 3) switch
    {
        0 => new(2 * x + 1, level, 2 * z),
        1 => new(2 * x + 2, level, 2 * z + 1),
        2 => new(2 * x + 1, level, 2 * z + 2),
        _ => new(2 * x, level, 2 * z + 1),
    };

    public static IEnumerable<(Point P, Role R)> Touches(Piece p)
    {
        var (x, y, z) = (p.Slot.X, p.Slot.Y, p.Slot.Z);
        switch (p.Kind)
        {
            case PieceKind.Floor:
                for (int s = 0; s < 4; s++) yield return (EdgePoint(x, z, s, 2 * y), Role.Level);
                yield return (new Point(2 * x + 1, 2 * y, 2 * z + 1), Role.Level);
                break;
            case PieceKind.Wall or PieceKind.WindowWall or PieceKind.DoorWall or PieceKind.HalfWall:
            {
                int s = p.Slot.Side;
                yield return (EdgePoint(x, z, s, 2 * y), Role.Bottom);
                if (p.Kind != PieceKind.HalfWall) yield return (EdgePoint(x, z, s, 2 * y + 2), Role.Top);
                if (s == 0)
                {
                    yield return (new Point(2 * x, 2 * y + 1, 2 * z), Role.Side);
                    yield return (new Point(2 * x + 2, 2 * y + 1, 2 * z), Role.Side);
                }
                else
                {
                    yield return (new Point(2 * x, 2 * y + 1, 2 * z), Role.Side);
                    yield return (new Point(2 * x, 2 * y + 1, 2 * z + 2), Role.Side);
                }
                break;
            }
            case PieceKind.Stairs:
                yield return (EdgePoint(x, z, p.Dir + 2, 2 * y), Role.Bottom);
                yield return (EdgePoint(x, z, p.Dir, 2 * y + 2), Role.Top);
                break;
            case PieceKind.Roof:
                for (int s = 0; s < 4; s++) yield return (EdgePoint(x, z, s, 2 * y), Role.Bottom);
                break;
            case PieceKind.Pillar:
                yield return (new Point(2 * x + 1, 2 * y, 2 * z + 1), Role.Bottom);
                yield return (new Point(2 * x + 1, 2 * y + 2, 2 * z + 1), Role.Top);
                break;
        }
    }

    /// <summary>
    /// Stepping from a held piece to its neighbour through a shared point costs nothing when the
    /// neighbour rests on it (a wall on a floor, a floor on a wall's top, stairs on a floor), and one
    /// span unit when it only hangs off it sideways (a floor off a floor, a wall off a wall).
    /// </summary>
    private static int StepCost(Role from, Role to) =>
        from == Role.Top && to is Role.Bottom or Role.Level ? 0
        : from == Role.Level && to == Role.Bottom ? 0
        : 1;

    /// <summary>
    /// How far each piece is from the ground in span units, for every piece that stands (within its
    /// material's <see cref="MaterialSpec.Span"/>). A piece missing from the answer falls. One pass
    /// of a 0-1 shortest path from the grounded pieces: a piece past its span holds nothing up.
    /// </summary>
    public static Dictionary<Slot, int> Support(IReadOnlyCollection<Piece> pieces)
    {
        var at = new Dictionary<Point, List<(int Index, Role Role)>>();
        var list = pieces.ToList();
        for (int i = 0; i < list.Count; i++)
            foreach (var (p, r) in Touches(list[i]))
            {
                if (!at.TryGetValue(p, out var l)) at[p] = l = new();
                l.Add((i, r));
            }

        var dist = new int[list.Count];
        Array.Fill(dist, int.MaxValue);
        var queue = new LinkedList<int>();
        for (int i = 0; i < list.Count; i++)
            if (list[i].Grounded)
            {
                dist[i] = 0;
                queue.AddLast(i);
            }

        var done = new bool[list.Count];
        while (queue.Count > 0)
        {
            int i = queue.First!.Value;
            queue.RemoveFirst();
            if (done[i]) continue;
            done[i] = true;
            if (dist[i] > Spec(list[i].Material).Span) continue;   // past its reach: it holds nothing up
            foreach (var (p, r) in Touches(list[i]))
                foreach (var (j, rj) in at[p])
                {
                    if (j == i || done[j]) continue;
                    int cost = StepCost(r, rj);
                    if (dist[i] + cost >= dist[j]) continue;
                    dist[j] = dist[i] + cost;
                    if (cost == 0) queue.AddFirst(j); else queue.AddLast(j);
                }
        }

        var standing = new Dictionary<Slot, int>();
        for (int i = 0; i < list.Count; i++)
            if (dist[i] <= Spec(list[i].Material).Span) standing[list[i].Slot] = dist[i];
        return standing;
    }

    /// <summary>The pieces that no longer stand: what collapses after a piece is lost.</summary>
    public static List<Piece> Fallen(IReadOnlyCollection<Piece> pieces)
    {
        var standing = Support(pieces);
        return pieces.Where(p => !standing.ContainsKey(p.Slot)).ToList();
    }

    /// <summary>Why <paramref name="piece"/> cannot be added to <paramref name="pieces"/>, or null if it can.</summary>
    public static string? CannotPlace(IReadOnlyCollection<Piece> pieces, Piece piece)
    {
        if (ClassOf(piece.Kind) != piece.Slot.Class) return "That piece does not go there.";
        if (!Allowed(piece.Kind, piece.Material)) return $"{Spec(piece.Material).Name} only make walls.";
        if (pieces.Any(p => p.Slot == piece.Slot)) return "Something is already there.";
        if (piece.Grounded) return null;
        var with = pieces.Append(piece).ToList();
        return Support(with).ContainsKey(piece.Slot) ? null
            : pieces.Count == 0 ? "Too high above the ground."
            : "Nothing holds it up: build closer to the ground or to a support.";
    }
}
