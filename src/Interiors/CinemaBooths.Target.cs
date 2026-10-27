using Godot;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Interiors;

public partial class CinemaBooths
{
    /// <summary>
    /// The booth panel <paramref name="p"/> stands at and the half of it E works (#1286): its furniture index
    /// and whether it is the screen's (right) half rather than the lights' (left). The one targeting rule for
    /// the use (<see cref="At"/>), the prompt and the outline.
    /// </summary>
    public static (InteriorNode Node, int Panel, bool Screen)? PanelAt(FootPlayer p)
    {
        if (!p.Indoors || InteriorManager.Instance is not { Current: { Type: BuildingType.Cinema } layout, CurrentNode: { } node }) return null;
        int i = Loot.LootService.NearestOf(p, layout, node, t => t == FurnitureType.BoothPanel, Reach);
        if (i < 0) return null;
        var panel = layout.Furniture[i];
        // the player in the panel's own frame: its left half (local -X) the lights, its right the screen
        var local = node.ToLocal(p.GlobalPosition) - new Vector3(panel.X, 0, panel.Z);
        float across = local.Rotated(Vector3.Up, -panel.Turns * Mathf.Pi / 2).X;
        return (node, i, across > 0);
    }

    /// <summary>What E does at the panel and the half of it to outline (#1286), or null.</summary>
    public static Interaction? InteractionFor(FootPlayer p) =>
        PanelAt(p) is { } at && Prompt(p) is { } text
            ? new(Core.InputHints.Prompt(Core.PlayerInput.InteractMount, text), Outline.Piece(at.Node, at.Panel, at.Screen ? 1 : -1))
            : null;
}
