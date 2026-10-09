using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

// Furniture in the model viewer (--models, docs/notes/avatar/model-viewer.md): every FurnitureType,
// each one alone in an empty layout, at the size the generator's own room tables give it, so a new
// type or a resized one shows without touching this file.
public static partial class InteriorGenerator
{
    [Showcase("Furniture")]
    private static IEnumerable<(string, Func<Node3D>)> ShowcaseFurniture()
    {
        var sizes = new Dictionary<FurnitureType, Piece>();
        void Take(IEnumerable<Piece>? pieces)
        {
            foreach (var p in pieces ?? Array.Empty<Piece>()) sizes.TryAdd(p.Type, p);
        }
        var room = new RoomPlan { X1 = 8, Z1 = 8 };
        foreach (var type in Enum.GetValues<RoomType>())
        {
            foreach (var kind in Enum.GetValues<BuildingKind>())
                for (int seed = 0; seed < 4; seed++)
                    Take(Pieces(type, room, new Random(seed), kind, vending: true).ToList());
            Take(AptPieces(type));
            Take(HallDressing(type));
        }

        // a type only code places (no row in a table) shows at a metre cube; one that draws nothing
        // in the main mesh (the pastor rat is a figure) is left out
        foreach (var type in Enum.GetValues<FurnitureType>())
        {
            var piece = sizes.GetValueOrDefault(type) ?? new Piece(type, 1f, 1f, 1f, false);
            var (layout, data) = Layout(type, piece);
            // (a loose floor pallet and a parked forklift are nodes of their own now: InteriorNode.Create draws them)
            if (data.Vertices.Length == 0 && !layout.Furniture.Any(f => InteriorMeshBuilder.IsCarvedOut(layout, f))) continue;
            yield return (type.ToString(), () =>
            {
                var node = InteriorNode.Create(layout, data, Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
                // a layout with no entrance still gets the fallback front door (AllEntrances), at
                // the origin, right through the piece: hidden, not freed (the node keeps its leaves)
                foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
                return node;
            });
        }
    }

    /// <summary>
    /// A block of flats' underground garage (#558): the plan of an 80 x 18 m block that rolled a garage
    /// door (<c>FlatCheck.RampTile</c>), its ground floor and basement with the ramp between them.
    /// </summary>
    [Showcase("Terrain", "Garage ramp")]
    private static Node3D ShowcaseGarageRamp()
    {
        var (tile, roads) = FlatCheck.RampTile();
        int index = BuildingFootprint.ComputeDoors(tile, roads, null).First(d => d.Link.Any).Index;
        var layout = Generate(tile, index, roads, null)!;
        return InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout), Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
    }

    private static (InteriorLayout, InteriorMeshBuilder.MeshData) Layout(FurnitureType type, Piece piece)
    {
        var layout = new InteriorLayout { StoreyHeight = 3f };
        layout.Furniture.Add(new FurniturePlan { Type = type, W = piece.W, D = piece.D, H = piece.H });
        return (layout, InteriorMeshBuilder.Build(layout));
    }
}
