using Godot;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Interiors.Pinball;

// What E (pad Y, a VR hand reaching out) does at a pinball machine (#1325): step up to it. One targeting
// rule (At) for the prompt, the outline and the use.
public partial class PinballTables
{
    private const float UseReach = 0.9f;

    private (InteriorNode Node, int Index)? At(FootPlayer p)
    {
        if (!p.Indoors || PinballPlay.Current != null || InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return null;
        int index = Loot.LootService.NearestOf(p, layout, node, t => t == FurnitureType.Pinball, UseReach);
        if (index < 0 || node.PinballOf(index) == null) return null;
        // from the front, where the flipper buttons are
        var local = PieceFrame(layout, layout.Furniture[index]).AffineInverse() * node.ToLocal(p.GlobalPosition);
        return local.Z > 0.3f ? (node, index) : null;
    }

    private string Text((InteriorNode Node, int Index) at)
    {
        var title = PinballLayouts.Get(TableOf(at.Node.Layout.Furniture[at.Index])).Title;
        var m = Peek(at.Node.Layout.Key, at.Index);
        if (m != null && m.Players.Contains(MyId) && m.Runner != 0) return $"Back to {title}";
        if (m is { Runner: not 0 }) return $"Watch {title}, or join";
        return $"Play {title}";
    }

    /// <summary>What E does here and what to outline, or null.</summary>
    public static Interaction? InteractionFor(FootPlayer p) =>
        Instance?.At(p) is { } at ? new(Core.InputHints.Prompt(Core.PlayerInput.InteractMount, Instance.Text(at)), OutlineOf(at)) : null;

    /// <summary>The machine whole: a box round the cabinet and backbox (the machine is a node, not in the room's mesh).</summary>
    private static Outline OutlineOf((InteriorNode Node, int Index) at)
    {
        var frame = PieceFrame(at.Node.Layout, at.Node.Layout.Furniture[at.Index]) * new Transform3D(Basis.Identity, new Vector3(0, 0.965f, 0));
        return Outline.Box(at.Node, frame, new Vector3(0.74f, 1.93f, 1.4f));
    }

    /// <summary>E at a machine: step up to it. True when it did something here.</summary>
    public static bool TryUse(FootPlayer p)
    {
        if (Instance is not { } t || t.At(p) is not { } at) return false;
        PinballPlay.Begin(p, at.Node.Layout.Key, at.Index);
        return true;
    }
}
