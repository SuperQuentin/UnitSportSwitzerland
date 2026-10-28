using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// What E would act on (#1286): one rule picks the floating prompt's words and the outline together, in
/// <see cref="FootPlayer.TryInteract"/>'s order, so the prompt, the border and what E does never disagree.
/// A world item and a vehicle's door are outlined by their own pointers (<see cref="Highlight"/>,
/// <see cref="Vehicles.VehicleReach"/>): where E takes one of them first, this names nothing; where
/// something comes before them (a crate at the feet, a crane's ladder), <see cref="InteractOutline.Outranks"/>
/// turns their border off.
/// </summary>
public partial class InteriorManager
{
    /// <summary>What E would do for <paramref name="p"/> on foot, and to what; null for nothing (or an item or a vehicle's door).</summary>
    public Interaction? Interacting(FootPlayer p, out Outranking outranks)
    {
        outranks = Outranking.ItemAndVehicle;
        // a ski lift's bottom station (#912), then a Battle Royale crate at your feet (#194), as E takes them first
        if (p.LiftStationInteraction() is { } lift) return lift;
        if (BattleRoyale.BrCrates.Instance?.InteractionFor(p) is { } crate) return crate;
        outranks = Outranking.None;
        // a world item pointed at (outdoors a radio too: indoors E leaves a radio alone)
        if (Highlight.Pointed is DroppedItem || !p.Indoors && Highlight.Pointed != null) return null;
        // a bus's door button in reach, inside or out (#162)
        if (!p.Indoors && p.DoorButtonInteraction() is { } button)
        {
            outranks = Outranking.Vehicle;
            return button;
        }
        if (p.Indoors)
        {
            if (Vehicles.VehicleReach.Current != null || _current == null) return null;
            // an elevator's button or cabin, a flat's door (#557); the church radio; a cinema's booth panel
            // (#1238); a cupboard, unless at the way out, where E is the door
            // a cinema seat from its row's lane (#1279), as E takes it before a cupboard
            return InsideInteraction(p) ?? ChurchRadios.InteractionFor(p) ?? CinemaBooths.InteractionFor(p) ?? p.SeatInteraction()
                ?? (ExitAt(p) is { } exit ? ExitInteraction(exit) : Loot.LootService.Instance?.InteractionFor(p));
        }
        // a building site crane's ladder or its remote's box (#618); a farm stand or a buyer (#494); a
        // chest, a seal's stack, a burrow (#1227); a summit cross, a board (#1224): all before a vehicle
        outranks = Outranking.Vehicle;
        if (p.CraneInteraction() is { } crane) return crane;
        if ((Farming.FarmSales.InteractionFor(p) ?? Adventure.Rewards.RewardWorld.InteractionFor(p)
            ?? Adventure.Places.PoiMarkers.InteractionFor(p) ?? Adventure.Errands.ErrandGivers.InteractionFor(p)
            ?? Occasions.Festivals.Fahnen.FahnenGame.InteractionFor(p)) is { } outdoors) return outdoors;
        outranks = Outranking.None;
        if (Vehicles.VehicleReach.Current != null) return null;
        // a building site's skip or materials faced (#627), a building's door; then another player looked
        // at (#495), a concert's light desk, a stand's seat (#885)
        return Loot.LootService.Instance?.YardInteractionFor(p) ?? OutsideDoorInteraction(p)
            ?? p.FighterInteraction() ?? p.LightDeskInteraction() ?? p.SeatInteraction();
    }

    /// <summary>The way out the player stands at: its leaf (or a barn pair's shutter) to outline.</summary>
    private Interaction ExitInteraction(EntrancePlan exit)
    {
        var node = CurrentNode;
        var leaf = node?.Leaf(exit.Door) ?? node?.Shutter(exit.Door);
        return new(InputHints.Prompt(PlayerInput.InteractMount, _doors.ContainsKey(exit.Door) ? "Close the door" : "Open the door"),
            Outline.Of(leaf));
    }

    /// <summary>
    /// A building's door faced from outside: its leaf when it stands on the facade (a barn's pair, a roll-up
    /// door), else the shut leaf the facade draws, as a box over it (6 to 11 cm in front of the wall).
    /// </summary>
    private Interaction? OutsideDoorInteraction(FootPlayer p)
    {
        if (OutsideEntry(p) is not { } e) return null;
        string door = e.Key.ToString();
        string text = InputHints.Prompt(PlayerInput.InteractMount, _doors.ContainsKey(door) ? "Close the door" : "Open the door");
        if (_links.TryGetValue(door, out var link) && link.Leaf != null) return new(text, Outline.Of(link.Leaf));
        var outward = new Vector3(e.Outward.X, 0, e.Outward.Z).Normalized();
        var basis = new Basis(Vector3.Up.Cross(outward), Vector3.Up, outward);
        var at = e.World + outward * 0.055f + Vector3.Up * (e.Height / 2);
        return new(text, Outline.Box(null, new Transform3D(basis, at), new Vector3(e.Width, e.Height, 0.05f)));
    }

    /// <summary>Points the outline at what E would act on: where the prompt is picked, a few times a second.</summary>
    private static void PointOutline(Interaction? hit, Outranking outranks)
    {
        InteractOutline.Outranks = hit != null ? outranks : Outranking.None;
        InteractOutline.Point(hit?.Target ?? default);
    }
}
