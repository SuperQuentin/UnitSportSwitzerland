using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// On the paddle steamer's bridge (#303): the telegraph, the whistle, the gangways; and climbing
/// aboard it from the water by a gangway. The ship itself is a <see cref="Boat"/> (FootPlayer.Boat.cs);
/// its decks are walked like a bus's (FootPlayer.Deck.cs). See <c>docs/notes/player/steamer.md</c>.
/// </summary>
public partial class FootPlayer
{
    /// <summary>The steamer stood still since its gangways were last opened: getting under way shuts them, once.</summary>
    private bool _steamerAtRest = true;

    /// <summary>
    /// Each step at the helm, before the model: the telegraph moves a step a press (nobody on the
    /// bridge: the engine room stops it), the whistle blows while it is held, the gangways shut as
    /// it gets under way. What the model is asked: the order on the shaft, the wheel.
    /// </summary>
    private BoatControls SteamerHelm(Steamer steamer, in RideInput input, float dt)
    {
        if (SeatIndex != 0) steamer.Order = 0;
        else steamer.StepTelegraph(input.Throttle, input.Brake, dt);
        steamer.Whistling = SeatIndex == 0 && (steamer.WhistleHeld || RideControls == null && PlayerInput.Held(PlayerInput.Horn));
        float speed = new Vector2(steamer.State.Velocity.X, steamer.State.Velocity.Z).Length();
        if (speed < 0.3f) _steamerAtRest = true;
        else if (speed > 1f && _steamerAtRest)
        {
            _steamerAtRest = false;
            steamer.DoorsOpen = 0;
        }
        return EngineOn ? steamer.Helm(input.Steer) : new BoatControls(0f, 0f, input.Steer);
    }

    /// <summary>{car_door} at the helm: both gangways open or shut, stopped only.</summary>
    private bool HandleSteamerInput(InputEvent e, Steamer steamer)
    {
        if (!e.IsActionPressed(PlayerInput.CarDoor) || e.IsEcho() || SeatIndex != 0) return false;
        if (GroundSpeed > 1f) { Announced?.Invoke("Stop to open the gangways", false); return true; }
        steamer.DoorsOpen = steamer.DoorsOpen == 0 ? (byte)3 : (byte)0;
        _steamerAtRest = true;
        return true;
    }

    /// <summary>For checks taking pictures: first or third person, without saving it as the player's setting.</summary>
    public void ViewForCheck(bool thirdPerson)
    {
        if (_thirdPerson == thirdPerson) return;
        _thirdPerson = thirdPerson;
        _pivotY = float.NaN;
        RefreshVisual(force: true);
    }

    /// <summary>For probes: the steamer being driven, or null.</summary>
    public Steamer? SteamerDriven => _ride as Steamer;

    /// <summary>
    /// E swimming beside a walkable boat's gangway (#303): up its ladder onto the deck, inside the
    /// gate, aboard as soon as its deck is here. True when there was one within reach.
    /// </summary>
    private bool TryClimbAboard()
    {
        if (!IsSwimming || _ride != null || RidingWith != 0) return false;
        Node3D? best = null;
        Vector3 spot = default, velocity = default;
        float bestDist = ClimbReach;
        void Consider(Node3D host, Rideable? ride, Vector3 hostVelocity)
        {
            if (ride is not Steamer || SectionFrame(host, 0) is not { } frame) return;
            var t = frame.GlobalTransform;
            for (int door = 0; door < Steamer.GangwayCount; door++)
            {
                float side = door == 0 ? 1f : -1f;
                float at = (SteamerMeshBuilder.GangFrom + SteamerMeshBuilder.GangTo) * 0.5f;
                float edge = SteamerMeshBuilder.DeckHalf(SteamerMeshBuilder.Z(at));
                var outside = t * BoatMeshBuilder.Flip(new Vector3(side * (edge + 0.6f), SteamerMeshBuilder.DeckY, SteamerMeshBuilder.Z(at)));
                float d = new Vector2(outside.X - GlobalPosition.X, outside.Z - GlobalPosition.Z).Length();
                if (d >= bestDist) continue;
                bestDist = d;
                best = host;
                spot = t * BoatMeshBuilder.Flip(new Vector3(side * (edge - 0.6f), SteamerMeshBuilder.DeckY + 0.05f, SteamerMeshBuilder.Z(at)));
                velocity = hostVelocity;
            }
        }
        foreach (var p in GetTree().GetNodesInGroup(Group))
            if (p is FootPlayer other && other != this) Consider(other, RideOfHost(other), other.WorldVelocity with { Y = 0 });
        if (VehicleManager.Instance is { } vehicles)
            foreach (var node in vehicles.GetChildren())
                if (node is VehicleBody v) Consider(v, RideOfHost(v), v.Velocity with { Y = 0 });
        if (best == null) return false;
        LeaveWater();
        StandIn(spot, velocity);
        GD.Print($"[steamer] {Name} climbs aboard by the gangway");
        return true;
    }

    /// <summary>How near a gangway's foot a swimmer must be to climb its ladder, m.</summary>
    private const float ClimbReach = 3.5f;
}
