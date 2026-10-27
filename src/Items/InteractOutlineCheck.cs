using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Items;

/// <summary>
/// <c>godot --headless --path . -- --outlinecheck --systems ui</c> (#1286): what E's outline draws, through the
/// real code with no world. Interiors planned in hand-made buildings (a cinema, a block of flats with an
/// elevator, a shop) and, for every piece a prompt source targets (a booth panel and each of its halves, a
/// container, a counter, a vending machine, a teller's desk, a church radio): the piece built alone
/// (<see cref="InteriorMeshBuilder.Piece"/>) has triangles, stands inside its own footprint, and a panel's two
/// halves split it. Each elevator's call plate and cabin panel are boxes at their buttons. Then
/// <see cref="InteractOutline"/> on a node, a piece and a box: the border goes on, moves, and comes off,
/// and a proxy is built only when the target changes. Prints a RESULT line and exits 0 or 1.
/// </summary>
public partial class InteractOutlineCheck : Node
{
    public static bool Requested => CmdArgs.Has("--outlinecheck");

    private bool _ok = true;

    private void Check(bool condition, string what)
    {
        GD.Print($"[outlinecheck] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    /// <summary>The furniture a prompt source acts on (CinemaBooths, ChurchRadios, LootService, ShopService).</summary>
    private static bool Targeted(FurnitureType t) =>
        t is FurnitureType.BoothPanel or FurnitureType.ChurchRadio or FurnitureType.ShopCounter or FurnitureType.VendingMachine
            or FurnitureType.TellerDesk || Loot.LootTables.IsLootable(t);

    public override void _Ready()
    {
        var id = new TileId(2600, 1200);
        Venues.Override = new List<VenueRecord>
        {
            new("o1", VenueType.Cinema, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, "", "Cinéma outline"),
        };
        var cinema = Plan("a cinema", BowlingProbe.Box(500, 500, 18, 34, 9, BuildingKind.Commercial));
        Venues.Override = null;
        var flats = Plan("a block of flats", BowlingProbe.Box(500, 500, 24, 12, 15, BuildingKind.Apartment));
        var shop = Plan("a shop", BowlingProbe.Box(500, 500, 16, 12, 4, BuildingKind.Commercial));

        Check(cinema?.Furniture.Any(f => f.Type == FurnitureType.BoothPanel) == true, "the cinema has a booth panel");
        Check(flats?.Lifts.Count > 0, $"the block of flats has an elevator ({flats?.Lifts.Count ?? 0})");
        int pieces = 0;
        foreach (var l in new[] { cinema, flats, shop })
            if (l != null) pieces += Pieces(l);
        Check(pieces > 0, $"{pieces} targeted pieces built alone");
        if (flats != null) Lifts(flats);
        if (cinema != null) Outlines(cinema);

        GD.Print($"[outlinecheck] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
    }

    private InteriorLayout? Plan(string what, Building building)
    {
        var tile = new BuildingTile { Id = new TileId(2600, 1200), Buildings = new List<Building> { building } };
        try { return InteriorGenerator.Generate(tile, 0, null, null); }
        catch (Exception e)
        {
            Check(false, $"{what}: planning threw {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    /// <summary>Every targeted piece of a plan: built alone, inside its footprint; a booth panel's halves too.</summary>
    private int Pieces(InteriorLayout l)
    {
        int built = 0;
        for (int i = 0; i < l.Furniture.Count; i++)
        {
            var f = l.Furniture[i];
            if (!Targeted(f.Type) || InteriorMeshBuilder.IsCarvedOut(l, f)) continue;
            var whole = InteriorMeshBuilder.Piece(l, i);
            string what = $"{l.Type} {f.Type} #{i}";
            if (whole.Length == 0 || whole.Length % 3 != 0) { Check(false, $"{what}: built alone ({whole.Length} vertices)"); continue; }
            built++;
            // in the piece's own frame: inside its footprint (a handle or a lip a few cm proud), from its base up
            // within its storey (a kitchen's wall cabinets and a teller's glass stand over its height)
            var (lo, hi) = Frame(l, f, whole);
            float y0 = l.FloorY(f.Floor) + f.Lift;
            bool inside = lo.X >= -f.W / 2 - 0.15f && hi.X <= f.W / 2 + 0.15f && lo.Z >= -f.D / 2 - 0.15f && hi.Z <= f.D / 2 + 0.15f
                          && lo.Y >= y0 - 0.05f && hi.Y <= l.FloorY(f.Floor) + l.StoreyHeight + 0.05f;
            if (!inside) Check(false, $"{what}: stands in its footprint ({lo:F2} .. {hi:F2} for {f.W:F2} x {f.D:F2} x {f.H:F2})");
            if (f.Type != FurnitureType.BoothPanel) continue;
            var left = InteriorMeshBuilder.Piece(l, i, -1);
            var right = InteriorMeshBuilder.Piece(l, i, 1);
            Check(left.Length > 0 && right.Length > 0,
                $"{what}: the lights' half ({left.Length / 3} triangles) and the screen's ({right.Length / 3}) of {whole.Length / 3}");
            Check(left.Length == 0 || Frame(l, f, left).Hi.X <= 0.001f, $"{what}: the lights' half is its left (-X)");
            Check(right.Length == 0 || Frame(l, f, right).Lo.X >= -0.001f, $"{what}: the screen's half is its right (+X)");
        }
        Check(true, $"{l.Type}: {built} targeted pieces built alone, in their footprints");
        return built;
    }

    /// <summary>A piece's vertices' extent in its own frame: unturned, round its middle.</summary>
    private static (Vector3 Lo, Vector3 Hi) Frame(InteriorLayout l, FurniturePlan f, Vector3[] vertices)
    {
        var unturn = new Basis(Vector3.Up, -f.Turns * Mathf.Pi / 2);
        var pivot = new Vector3(f.X, 0, f.Z);
        Vector3 lo = Vector3.One * float.MaxValue, hi = -lo;
        foreach (var v in vertices)
        {
            var p = unturn * (v - pivot);
            lo = lo.Min(p);
            hi = hi.Max(p);
        }
        return (lo, hi);
    }

    private void Lifts(InteriorLayout l)
    {
        foreach (var lift in l.Lifts)
            for (int floor = lift.Bottom; floor <= lift.Top; floor++)
            {
                float y0 = l.FloorY(floor);
                var (cmin, cmax) = InteriorMeshBuilder.LiftCallPlate(lift, y0);
                var call = lift.CallPoint(y0);
                Check(cmax.X > cmin.X - 1e-4f && cmax.Y - cmin.Y > 0.1f && cmax.Z > cmin.Z - 1e-4f && ((cmin + cmax) / 2).DistanceTo(call) < 0.1f,
                    $"floor {floor}: the call plate's box at the button ({(cmin + cmax) / 2:F2} for {call:F2})");
                var (pmin, pmax) = InteriorMeshBuilder.LiftPanel(lift, y0);
                var panel = lift.PanelPoint(y0 + LiftPlan.ButtonHeight);
                Check(pmax.Y - pmin.Y >= 0.4f && ((pmin + pmax) / 2).DistanceTo(panel) < 0.1f,
                    $"floor {floor}: the cabin panel's box at its middle ({(pmin + pmax) / 2:F2} for {panel:F2})");
            }
    }

    /// <summary>The outline itself: on a node, a piece of an interior (a stand-in node), a box; moved and taken off.</summary>
    private void Outlines(InteriorLayout cinema)
    {
        var world = new Node3D { Name = "OutlineWorld" };
        AddChild(world);
        var leaf = new Node3D { Name = "Leaf" };
        var leafMesh = new MeshInstance3D { Mesh = new BoxMesh() };
        leaf.AddChild(leafMesh);
        world.AddChild(leaf);

        InteractOutline.Point(Outline.Of(leaf));
        Check(leafMesh.MaterialOverlay == Highlight.Material && InteractOutline.Proxy == null, "a node: its meshes take the border, no proxy");

        var interior = InteriorNode.ForCheck(cinema);
        world.AddChild(interior);
        int panel = cinema.Furniture.FindIndex(f => f.Type == FurnitureType.BoothPanel);
        InteractOutline.Point(Outline.Piece(interior, panel, 1));
        var proxy = InteractOutline.Proxy;
        Check(leafMesh.MaterialOverlay == null, "moving off the node takes its border off");
        Check(proxy != null && proxy.GetParent() == interior && proxy.MaterialOverride == Highlight.Material
              && proxy.Mesh is ArrayMesh { } m && m.GetSurfaceCount() == 1,
            "a booth panel's half: a proxy in the interior, drawn only as the border");
        InteractOutline.Point(Outline.Piece(interior, panel, 1));
        Check(InteractOutline.Proxy == proxy, "the same target again: no new proxy (nothing built per frame)");
        InteractOutline.Point(Outline.Piece(interior, panel, -1));
        Check(InteractOutline.Proxy != proxy && proxy!.IsQueuedForDeletion(), "the other half: a new proxy, the old one freed");

        var (cmin, cmax) = (new Vector3(0, 1, 0), new Vector3(0.12f, 1.22f, 0.02f));
        InteractOutline.Point(Outline.Box(world, cmin, cmax));
        Check(InteractOutline.Proxy is { Mesh: BoxMesh box } p && box.Size.IsEqualApprox(cmax - cmin) && p.Position.IsEqualApprox((cmin + cmax) / 2),
            "a box (a lift's call plate): its size, at its middle");

        InteractOutline.Point(default);
        Check(InteractOutline.Current.Kind == OutlineKind.None && InteractOutline.Proxy == null, "nothing: no border, no proxy");
        leaf.QueueFree();
        InteractOutline.Point(Outline.Of(leaf));
        Check(InteractOutline.Current.Kind == OutlineKind.Node, "a node about to be freed is still outlined this frame");
        world.QueueFree();
    }
}
