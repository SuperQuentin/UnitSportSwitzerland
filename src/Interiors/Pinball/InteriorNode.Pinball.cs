using Godot;
using UnitSport.Interiors.Pinball;

namespace UnitSport.Interiors;

/// <summary>
/// A venue's pinball machines (#1325): a <see cref="PinballCabinet"/> per <see cref="FurnitureType.Pinball"/>,
/// found by the piece's furniture index. The machine's table comes from the venue's machines (<see cref="PinballTables.TableOf"/>),
/// so every peer sees the same table on the same machine.
/// </summary>
public partial class InteriorNode
{
    private readonly Dictionary<int, PinballCabinet> _pinballs = new();

    /// <summary>The pinball machine of furniture index <paramref name="furniture"/>, if this interior has one.</summary>
    public PinballCabinet? PinballOf(int furniture) => _pinballs.GetValueOrDefault(furniture);

    private static void AddPinballs(InteriorNode node, Material material)
    {
        var l = node.Layout;
        for (int i = 0; i < l.Furniture.Count; i++)
        {
            var f = l.Furniture[i];
            if (f.Type != FurnitureType.Pinball) continue;
            var cabinet = PinballCabinet.Create(PinballTables.TableOf(l, i), material);
            cabinet.Name = $"Pinball{i}";
            cabinet.Transform = PinballTables.PieceFrame(l, f);
            node.AddChild(cabinet);
            node._pinballs[i] = cabinet;
        }
    }
}
