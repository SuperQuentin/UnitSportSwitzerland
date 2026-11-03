using Godot;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Interiors.Games;

// What E (pad Y, a VR hand reaching out) does at a playable piece (#1324, #1326): step up to it and play. One
// targeting rule (At) for the prompt, the outline and the use.
public partial class TableGames
{
    private (InteriorNode Node, int Index, GameSpec Spec)? At(FootPlayer p)
    {
        if (!p.Indoors || TableGamePlay.Current != null || InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return null;
        int index = Loot.LootService.NearestOf(p, layout, node, GameCatalog.Playable, GameCatalog.MaxReach);
        if (index < 0 || node.TableRigOf(index) == null || GameCatalog.Of(layout.Furniture[index].Type) is not { } spec) return null;
        var f = layout.Furniture[index];
        var local = PieceFrame(layout, f).AffineInverse() * node.ToLocal(p.GlobalPosition);
        return spec.UseFrom(local, f) ? (node, index, spec) : null;
    }

    private string Text((InteriorNode Node, int Index, GameSpec Spec) at)
    {
        var g = GameAt(at.Node.Layout.Key, at.Index);
        if (g != null && g.SeatOf(MyId) >= 0) return $"Back to the {at.Spec.Title.ToLowerInvariant()}";
        if (g is { Seated: > 0 } && g.FreeSeat() < 0) return $"Watch the {at.Spec.Title.ToLowerInvariant()}";
        if (g is { Seated: > 0 }) return $"Join the {at.Spec.Title.ToLowerInvariant()}";
        return $"Play {at.Spec.Title.ToLowerInvariant()}";
    }

    /// <summary>What E does here and what to outline, or null.</summary>
    public static Interaction? InteractionFor(FootPlayer p) =>
        Instance?.At(p) is { } at ? new(Core.InputHints.Prompt(Core.PlayerInput.InteractMount, Instance.Text(at)), OutlineOf(at)) : null;

    private static Outline OutlineOf((InteriorNode Node, int Index, GameSpec Spec) at)
    {
        var f = at.Node.Layout.Furniture[at.Index];
        var (centre, size) = at.Spec.Box(f);
        return Outline.Box(at.Node, PieceFrame(at.Node.Layout, f) * new Transform3D(Basis.Identity, centre), size);
    }

    /// <summary>E at a playable piece: step up to it. True when it did something here.</summary>
    public static bool TryUse(FootPlayer p)
    {
        if (Instance is not { } t || t.At(p) is not { } at) return false;
        TableGamePlay.Begin(p, at.Node, at.Index, at.Spec);
        return true;
    }
}
