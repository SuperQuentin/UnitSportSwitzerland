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
            // (a loose floor pallet is its own node now: InteriorNode.Create draws it, so it stays)
            if (data.Vertices.Length == 0 && !layout.Furniture.Any(InteriorMeshBuilder.IsLoosePallet)) continue;
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

    private static (InteriorLayout, InteriorMeshBuilder.MeshData) Layout(FurnitureType type, Piece piece)
    {
        var layout = new InteriorLayout { StoreyHeight = 3f };
        layout.Furniture.Add(new FurniturePlan { Type = type, W = piece.W, D = piece.D, H = piece.H });
        return (layout, InteriorMeshBuilder.Build(layout));
    }
}
