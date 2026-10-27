using Godot;
using UnitSport.Avatar;

namespace UnitSport.Interiors;

/// <summary>
/// Staff at a venue's counters (#1232): the clerk behind a bowling centre's shoe counter, the bartender
/// behind its bar. Each is a <see cref="SkinnedFigure"/> shown once from a look picked by the counter's
/// place in the plan (so every peer sees the same person), standing in the aisle between the back wall
/// and the counter, facing the customers. Nothing per frame: they stand, as the plan built them.
/// No collision: the counter in front of them already keeps everyone out.
/// </summary>
public partial class InteriorNode
{
    /// <summary>Where a piece's staff stand, in its own frame (front +Z): the aisle behind the counter.</summary>
    private static float? StaffAt(FurniturePlan p) => p.Type switch
    {
        FurnitureType.ShoeCounter or FurnitureType.BarCounter or FurnitureType.BoxOffice or FurnitureType.ConcessionStand
            or FurnitureType.PrizeCounter
            => -p.D / 2 + 0.45f + (p.D - 0.45f - 0.8f) / 2,
        _ => null,
    };

    private static void AddStaff(InteriorNode node)
    {
        var layout = node.Layout;
        Material? material = null;
        foreach (var p in layout.Furniture)
        {
            if (StaffAt(p) is not { } z) continue;
            material ??= HumanMeshBuilder.FigureMaterial();
            var basis = new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2);
            int hash = (int)(Occasions.OccasionHash.Unit((int)(p.X * 100), (int)(p.Z * 100), 17, (uint)p.Type) * 997);
            var figure = new SkinnedFigure
            {
                Name = $"Staff_{p.Type}",
                MaterialOverride = material,
                // a figure is authored looking down -Z: turned round to face the counter's front (+Z)
                Transform = new Transform3D(basis * new Basis(Vector3.Up, Mathf.Pi),
                    new Vector3(p.X, layout.FloorY(p.Floor) + p.Lift, p.Z) + basis * new Vector3(0, 0, z)),
            };
            figure.Show(new FigureLook(HumanPalette.ForRider(hash)));
            HumanMeshBuilder.PosePosed(figure, HumanPose.Standing);
            node.AddChild(figure);
        }
    }
}
