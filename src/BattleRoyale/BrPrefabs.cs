using UnitSport.Build;

namespace UnitSport.BattleRoyale;

// Plain C#, no Godot: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>A gadget that comes with a prefab (#276), set down as a placed object owned by the match.</summary>
public enum PrefabGadget { Zipline, RopeLadder, Trampoline, LaunchPad, CamoNet }

/// <summary>
/// Where a prefab's gadget goes, in the structure's frame (metres, cell 0,0,0's corner at the origin).
/// <see cref="Dir"/>: the way it faces (0 −Z, 1 +X, 2 +Z, 3 −X); a ladder's <see cref="Length"/>; a
/// zipline starts here and its far end is found on the terrain when the prefab is placed.
/// </summary>
public readonly record struct GadgetSpot(PrefabGadget Kind, float X, float Y, float Z, int Dir = 2, float Length = 0);

/// <summary>A ready-made structure: pieces on the building grid and the gadgets that go with it.</summary>
public sealed record Prefab(string Name, Piece[] Pieces, GadgetSpot[] Gadgets);

/// <summary>
/// The structures a Battle Royale match finds standing (#276), made of the same pieces players build
/// (<see cref="BuildGrid"/>), so they break the same way. Every prefab stands by the grid's own support
/// rules (<c>BrPrefabsTests</c>); the pieces on the ground are grounded (legs on a slope).
/// </summary>
public static class BrPrefabs
{
    private const float S = BuildGrid.Cell, H = BuildGrid.Storey;

    private static Piece F(int x, int y, int z, BuildMaterial m, bool g = false) => new(Slot.Floor(x, y, z), PieceKind.Floor, 0, m, g);
    private static Piece E(int x, int y, int z, int side, PieceKind k, BuildMaterial m, bool g = false) => new(Slot.Edge(x, y, z, side), k, 0, m, g);
    private static Piece V(int x, int y, int z, PieceKind k, int dir, BuildMaterial m, bool g = false) => new(Slot.Volume(x, y, z), k, dir, m, g);

    /// <summary>
    /// A two-cell stair tower: flights alternate between the cells, a landing on each storey, window
    /// walls up both long sides, a railed platform on top. <paramref name="top"/>: what waits up there.
    /// </summary>
    public static Prefab Tower(string name, int storeys, PrefabGadget? top)
    {
        var p = new List<Piece> { F(0, 0, 0, BuildMaterial.Wood, true), F(0, 0, -1, BuildMaterial.Wood, true) };
        for (int y = 0; y < storeys; y++)
        {
            bool even = y % 2 == 0;
            // even storeys climb from z 0 toward −Z, odd ones back toward +Z
            p.Add(V(0, y, even ? 0 : -1, PieceKind.Stairs, even ? 0 : 2, BuildMaterial.Wood, y == 0));
            p.Add(F(0, y + 1, even ? -1 : 0, BuildMaterial.Wood));
            foreach (int z in new[] { 0, -1 })
                foreach (int side in new[] { 1, 3 })
                    p.Add(E(0, y, z, side, PieceKind.WindowWall, BuildMaterial.Wood, y == 0));
        }
        if (p.All(q => q.Slot != Slot.Floor(0, storeys, 0))) p.Add(F(0, storeys, 0, BuildMaterial.Wood));
        if (p.All(q => q.Slot != Slot.Floor(0, storeys, -1))) p.Add(F(0, storeys, -1, BuildMaterial.Wood));
        // the railing round the platform
        foreach (int z in new[] { 0, -1 })
            foreach (int side in new[] { 1, 3 })
                p.Add(E(0, storeys, z, side, PieceKind.HalfWall, BuildMaterial.Wood));
        p.Add(E(0, storeys, 0, 2, PieceKind.HalfWall, BuildMaterial.Wood));
        p.Add(E(0, storeys, -1, 0, PieceKind.HalfWall, BuildMaterial.Wood));
        // Dir 2: a launch pad's chevrons point down its own −Z, which is the tower's −Z, the side placed downhill
        var gadgets = top is { } g ? new[] { new GadgetSpot(g, S * 0.5f, storeys * H, 0, 2) } : Array.Empty<GadgetSpot>();
        return new Prefab(name, p.ToArray(), gadgets);
    }

    /// <summary>Aussichtsturm: six storeys (14.4 m), a zipline from the top down the fall line.</summary>
    public static readonly Prefab LookoutTower = Tower("lookout tower", 6, PrefabGadget.Zipline);

    /// <summary>Schanze: a short tower on a steep slope with a launch pad on top, into a wingsuit glide.</summary>
    public static readonly Prefab SkiJump = Tower("ski jump", 2, PrefabGadget.LaunchPad);

    /// <summary>Lawinenverbauung: a row of metal barriers along the contour, each on its own legs.</summary>
    public static readonly Prefab AvalancheBarrier = new("avalanche barrier",
        Enumerable.Range(0, 4).Select(x => E(x, 0, 0, 0, PieceKind.Wall, BuildMaterial.Metal, true)).ToArray(),
        Array.Empty<GadgetSpot>());

    /// <summary>
    /// An army checkpoint: a 3 x 3 ring of sandbag half walls with a gap front and back for the road, two
    /// metal walls on the uphill side, a camo net over the middle.
    /// </summary>
    public static readonly Prefab Checkpoint = MakeCheckpoint();

    private static Prefab MakeCheckpoint()
    {
        var p = new List<Piece>();
        for (int i = -1; i <= 1; i++)
        {
            p.Add(E(-1, 0, i, 3, PieceKind.HalfWall, BuildMaterial.Sandbag, true));   // left side
            p.Add(E(1, 0, i, 1, PieceKind.HalfWall, BuildMaterial.Sandbag, true));    // right side
            if (i == 0) continue;   // the road runs through along Z: its middle cell is open front and back
            p.Add(E(i, 0, -1, 0, PieceKind.HalfWall, BuildMaterial.Sandbag, true));
            p.Add(E(i, 0, 1, 2, PieceKind.HalfWall, BuildMaterial.Sandbag, true));
        }
        p.Add(E(-2, 0, -1, 3, PieceKind.Wall, BuildMaterial.Metal, true));
        p.Add(E(-2, 0, 1, 3, PieceKind.Wall, BuildMaterial.Metal, true));
        return new Prefab("army checkpoint", p.ToArray(), new[] { new GadgetSpot(PrefabGadget.CamoNet, S * 0.5f, 0, S * 0.5f) });
    }

    /// <summary>
    /// A scout camp's log fort (Pfadi): a 2 x 2 ground floor in wood walls (a door and windows), a
    /// railed deck on top, a rope ladder down the front and a trampoline beside it.
    /// </summary>
    public static readonly Prefab ScoutFort = MakeScoutFort();

    private static Prefab MakeScoutFort()
    {
        var p = new List<Piece>();
        for (int x = 0; x <= 1; x++)
            for (int z = 0; z <= 1; z++)
            {
                p.Add(F(x, 0, z, BuildMaterial.Wood, true));
                p.Add(F(x, 1, z, BuildMaterial.Wood));
            }
        for (int i = 0; i <= 1; i++)
        {
            p.Add(E(i, 0, 0, 0, i == 0 ? PieceKind.DoorWall : PieceKind.WindowWall, BuildMaterial.Wood, true));
            p.Add(E(i, 0, 1, 2, PieceKind.WindowWall, BuildMaterial.Wood, true));
            p.Add(E(0, 0, i, 3, PieceKind.Wall, BuildMaterial.Wood, true));
            p.Add(E(1, 0, i, 1, PieceKind.Wall, BuildMaterial.Wood, true));
            p.Add(E(i, 1, 1, 2, PieceKind.HalfWall, BuildMaterial.Wood));
            p.Add(E(0, 1, i, 3, PieceKind.HalfWall, BuildMaterial.Wood));
            p.Add(E(1, 1, i, 1, PieceKind.HalfWall, BuildMaterial.Wood));
        }
        p.Add(E(0, 1, 0, 0, PieceKind.HalfWall, BuildMaterial.Wood));   // the front rail, the ladder beside it
        return new Prefab("scout fort", p.ToArray(), new[]
        {
            // just outside the front wall of the second bay, climbing side outward (Dir 0, −Z)
            new GadgetSpot(PrefabGadget.RopeLadder, S * 1.5f, H, -0.2f, 0, H),
            new GadgetSpot(PrefabGadget.Trampoline, -2.5f, 0, -2.5f),
        });
    }

    /// <summary>
    /// Scaffolding beside a building site: a strip of metal decks on three storeys, posts at both ends,
    /// stairs up one bay (the deck above them left open).
    /// </summary>
    public static Prefab Scaffold(int bays, int storeys)
    {
        var p = new List<Piece>();
        for (int x = 0; x < bays; x++) p.Add(F(x, 0, 0, BuildMaterial.Metal, true));
        for (int y = 0; y < storeys; y++)
        {
            p.Add(V(0, y, 0, PieceKind.Pillar, 0, BuildMaterial.Metal, y == 0));
            p.Add(V(bays - 1, y, 0, PieceKind.Pillar, 0, BuildMaterial.Metal, y == 0));
            p.Add(V(1, y, 0, PieceKind.Stairs, 1, BuildMaterial.Metal, y == 0));   // climbs +X onto the next deck
            for (int x = 0; x < bays; x++)
                if (x != 1) p.Add(F(x, y + 1, 0, BuildMaterial.Metal));          // open over the stairs
        }
        return new Prefab("scaffolding", p.ToArray(), Array.Empty<GadgetSpot>());
    }

    public static readonly Prefab Scaffolding = Scaffold(5, 3);

    /// <summary>Hängebrücke: a wooden deck of <paramref name="bays"/> cells, grounded at both ends, railed both sides.</summary>
    public static Prefab Footbridge(int bays)
    {
        var p = new List<Piece>();
        for (int x = 0; x < bays; x++)
        {
            bool end = x == 0 || x == bays - 1;
            p.Add(F(x, 0, 0, BuildMaterial.Wood, end));
            p.Add(E(x, 0, 0, 0, PieceKind.HalfWall, BuildMaterial.Wood));
            p.Add(E(x, 0, 0, 2, PieceKind.HalfWall, BuildMaterial.Wood));
        }
        return new Prefab("footbridge", p.ToArray(), Array.Empty<GadgetSpot>());
    }

    /// <summary>The longest a footbridge spans: wood reaches 6 cells from each bank.</summary>
    public static readonly Prefab SuspensionBridge = Footbridge(12);

    public static readonly Prefab[] All = { LookoutTower, SkiJump, AvalancheBarrier, Checkpoint, ScoutFort, Scaffolding, SuspensionBridge };

    /// <summary>The far end of a prefab's footprint, in metres along X and Z (for spacing and clearance).</summary>
    public static (float X0, float X1, float Z0, float Z1) Extent(Prefab p) => (
        p.Pieces.Min(q => q.Slot.X) * S, (p.Pieces.Max(q => q.Slot.X) + 1) * S,
        p.Pieces.Min(q => q.Slot.Z) * S, (p.Pieces.Max(q => q.Slot.Z) + 1) * S);
}
