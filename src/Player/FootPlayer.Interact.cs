using Godot;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>
/// The prompt bar's rows of things E acts on, with what to outline (#1286): the words in the bar, the
/// border from <c>InteriorManager.Interacting</c>, both from these calls.
/// </summary>
public partial class FootPlayer
{
    /// <summary>A ski lift's bottom station in reach (#912): "Take" it, its shed outlined.</summary>
    public Interaction? LiftStationInteraction() =>
        LiftInReach() is { } station
            ? new($"Take {UnitSport.World.Ropeways.RopewayService.Called(station)}",
                Outline.Of(UnitSport.World.Ropeways.RopewayLayer.Instance?.BottomStation(station.Key)), InBar: true)
            : null;

    /// <summary>A walkable vehicle's door button within a hand's reach (#162), the button outlined.</summary>
    public Interaction? DoorButtonInteraction()
    {
        if (Vehicle != null || ButtonInReach() is not { } button) return null;
        return new(button.Open ? "Shut the door" : "Open the door", ButtonOutline(button.Host, button.Door), InBar: true);
    }

    /// <summary>The box round a door's button on its host (a bus's push, a ship's bell-pull), facing out of its panel.</summary>
    private static Outline ButtonOutline(Node3D host, int door)
    {
        if (RideOfHost(host) is not { } ride) return default;
        foreach (var deck in ride.Decks)
        {
            if (SectionFrame(host, deck.Section) is not { } frame) continue;
            foreach (var button in deck.Buttons)
            {
                if (button.Door != door) continue;
                var at = frame.GlobalTransform * button.At;
                var normal = (frame.GlobalTransform.Basis * button.Normal).Normalized();
                var up = Mathf.Abs(normal.Y) > 0.9f ? Vector3.Forward : Vector3.Up;
                var across = up.Cross(normal).Normalized();
                return Outline.Box(null, new Transform3D(new Basis(across, normal.Cross(across), normal), at), new Vector3(0.16f, 0.16f, 0.06f));
            }
        }
        return default;
    }

    /// <summary>Another player looked at, online (#495): challenge them or take their challenge, their body outlined.</summary>
    public Interaction? FighterInteraction()
    {
        if (UnitSport.Vehicles.VehicleReach.Current != null || UnitSport.Combat.FightManager.Client is not { } fights || !UnitSport.Net.NetLink.Online(this)) return null;
        // the Älplerfest's ring (#1269) before another player: the queue for a bout
        if (RingInteraction() is { } ring) return ring;
        if (PointedFighter() is not { } rival || NetId(rival.Name) is not { } id) return null;
        // a breathalyzer in hand (#1271): E offers a test
        if (Items.ItemController.Instance?.Inventory.HeldId == Items.ItemId.Breathalyzer && Adventure.Alcohol.AlcoholService.Instance != null)
            return new(Adventure.Alcohol.AlcoholHud.OfferPrompt, Outline.Of(rival.Visual), InBar: true);
        // their birthday today, not wished yet (#1423): E wishes them a happy birthday
        if (Occasions.Birthdays.BirthdayService.Instance is { } birthdays && birthdays.CanWish(id))
            return new(Core.InputHints.Prompt(Core.PlayerInput.InteractMount, birthdays.WishPrompt(id)), Outline.Of(rival.Visual), InBar: true);
        // an aura challenge from them comes first (#914)
        string text = UnitSport.Combat.AuraManager.Client?.ChallengedBy(id) == true ? UnitSport.Combat.AuraManager.Client.Prompt(id)
            : UnitSport.Adventure.Together.TogetherService.LinkPrompt(id, fights.Prompt(id));
        return new(text, Outline.Of(rival.Visual), InBar: true);
    }

    /// <summary>A concert's light desk (#885) at hand: its consoles' box outlined.</summary>
    public Interaction? LightDeskInteraction()
    {
        if (UnitSport.Concert.ConcertDeskUi.At(this) is not { } rig || rig.Plan is not { } plan) return null;
        var console = plan.Console;
        return new("Light desk", Outline.Box(rig, new Transform3D(new Basis(Vector3.Up, console.Yaw), console.Centre), console.Size), InBar: true);
    }

    /// <summary>
    /// A seat in reach, a stand's (#885) or a cinema's (#1279): "Sit down", a box at the hip outlined (a stand models
    /// no seat; a cinema's cushion is folded until sat on).
    /// </summary>
    public Interaction? SeatInteraction()
    {
        if (SitInReach() is not { } seat) return null;
        // the hip's height snapped, so a step's bob builds nothing
        var at = seat.Hip with { Y = Mathf.Snapped(seat.Hip.Y - 0.2f, 0.1f) };
        return new("Sit down", Outline.Box(null, new Transform3D(new Basis(Vector3.Up, seat.Yaw), at), new Vector3(0.5f, 0.4f, 0.45f)), InBar: true);
    }
}
