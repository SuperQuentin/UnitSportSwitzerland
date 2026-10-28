using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A bowling centre's lane pairs come with their moving parts (#1323): a <see cref="LaneRig"/> per
/// <see cref="FurnitureType.LanePair"/>, found by the pair's furniture index.
/// </summary>
public partial class InteriorNode
{
    private readonly Dictionary<int, LaneRig> _laneRigs = new();

    /// <summary>The rig of the lane pair at furniture index <paramref name="furniture"/>, if this interior has one.</summary>
    public LaneRig? LaneRigOf(int furniture) => _laneRigs.GetValueOrDefault(furniture);

    private static void AddLaneRigs(InteriorNode node, Material material)
    {
        var l = node.Layout;
        for (int i = 0; i < l.Furniture.Count; i++)
        {
            var f = l.Furniture[i];
            if (f.Type != FurnitureType.LanePair) continue;
            var piece = new Transform3D(new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2), new Vector3(f.X, l.FloorY(f.Floor) + f.Lift, f.Z));
            var rig = LaneRig.Create(f, piece, material);
            rig.Name = $"LaneRig{i}";
            node.AddChild(rig);
            node._laneRigs[i] = rig;
        }
    }
}
