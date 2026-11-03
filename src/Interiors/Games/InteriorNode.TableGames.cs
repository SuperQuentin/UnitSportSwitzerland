using Godot;
using UnitSport.Interiors.Games;

namespace UnitSport.Interiors;

/// <summary>
/// A venue's playable pieces (#1324, #1326): a <see cref="TableRig"/> per piece whose type has a game in
/// <see cref="GameCatalog"/>, found by the piece's furniture index.
/// </summary>
public partial class InteriorNode
{
    private readonly Dictionary<int, TableRig> _tableRigs = new();

    /// <summary>The rig of furniture index <paramref name="furniture"/>, if this interior has one.</summary>
    public TableRig? TableRigOf(int furniture) => _tableRigs.GetValueOrDefault(furniture);

    public IReadOnlyDictionary<int, TableRig> TableRigs => _tableRigs;

    private static void AddTableRigs(InteriorNode node)
    {
        var l = node.Layout;
        for (int i = 0; i < l.Furniture.Count; i++)
        {
            var f = l.Furniture[i];
            if (GameCatalog.Of(f.Type)?.Rig is not { } make) continue;
            var rig = make(f);
            rig.Name = $"Game{i}";
            rig.Transform = TableGames.PieceFrame(l, f);
            node.AddChild(rig);
            node._tableRigs[i] = rig;
        }
    }
}
