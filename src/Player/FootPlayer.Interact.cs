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
        if (UnitSport.Vehicles.VehicleReach.Current != null || UnitSport.Combat.FightManager.Client is not { } fights || !UnitSport.Net.NetLink.Online(this)
            || PointedFighter() is not { } rival || NetId(rival.Name) is not { } id) return null;
        // an aura challenge from them comes first (#914)
        string text = UnitSport.Combat.AuraManager.Client?.ChallengedBy(id) == true ? UnitSport.Combat.AuraManager.Client.Prompt(id) : fights.Prompt(id);
        return new(text, Outline.Of(rival.Visual), InBar: true);
    }

    /// <summary>A concert's light desk (#885) at hand: its consoles' box outlined.</summary>
    public Interaction? LightDeskInteraction()
    {
        if (UnitSport.Concert.ConcertDeskUi.At(this) is not { } rig || rig.Plan is not { } plan) return null;
        var console = plan.Console;
        return new("Light desk", Outline.Box(rig, new Transform3D(new Basis(Vector3.Up, console.Yaw), console.Centre), console.Size), InBar: true);
    }

    /// <summary>A stand's seat in reach (#885): "Sit down", the place (no seat is modelled, a box at the hip) outlined.</summary>
    public Interaction? SeatInteraction()
    {
        if (SeatInReach() is not { } seat || Origin is not { } origin) return null;
        var at = origin.ToWorld(seat.E, seat.N, 0) with { Y = GlobalPosition.Y + seat.Hip - 0.2f };
        return new("Sit down", Outline.Box(null, new Transform3D(new Basis(Vector3.Up, seat.Yaw), at), new Vector3(0.5f, 0.4f, 0.45f)), InBar: true);
    }
}
