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
    /// E swimming by one of a walkable ship's boarding ladders (#384, <see cref="SteamerMeshBuilder.LadderAt"/>):
    /// onto it, climbing as on a gadget rope ladder (#275): forward up, back down, Jump lets go. The
    /// ladder is held in the ship's frame as drawn, so it rides a moving, rolling ship; at the top the
    /// climber is put on deck inside the rail, aboard as soon as its deck is here. Others see the climb
    /// (the carried pose, <see cref="CarriedPose"/> 2). True when there was a ladder within reach.
    /// </summary>
    private bool TryClimbAboard()
    {
        if (!IsSwimming || _ride != null || RidingWith != 0) return false;
        if (NearestLadder(ClimbReach) is not { } at) return false;
        LeaveWater();
        _ladderHost = at.Host;
        _ladderSide = at.Side;
        _ladderHeight = 0.5f;   // the feet just under the water, the hands on the rungs above it
        ClimbStep = 0;
        ShowWhileCarried = true;
        CarriedPose = 2;
        Carrier = HoldOnLadder;
        GD.Print($"[steamer] {Name} gets onto the ship's ladder");
        return true;
    }

    /// <summary>For probes: the climb's input (+1 up, -1 down) instead of the keys; null: the keys.</summary>
    public float? ForceLadderClimb { get; set; }

    /// <summary>For probes: on a ship's ladder now.</summary>
    public bool OnShipLadder => _ladderHost != null && Carrier == HoldOnLadder;

    private Node3D? _ladderHost;
    private int _ladderSide;
    private float _ladderHeight;

    /// <summary>The ladder's top over its foot, m (the deck's edge).</summary>
    private static float LadderTop => SteamerMeshBuilder.DeckY - SteamerMeshBuilder.LadderFoot;

    /// <summary>The carrier while on a ship's ladder: where the climber hangs on it this tick, and the climb.</summary>
    private (Vector3 At, float Yaw, Vector3 Velocity)? HoldOnLadder()
    {
        if (_ladderHost is not { } host || !IsInstanceValid(host) || RideOfHost(host) is not Steamer || SectionFrame(host, 0) is not { } frame)
        {
            OffLadder(null);
            return null;
        }
        var t = frame.GlobalTransform;
        float dt = (float)GetPhysicsProcessDeltaTime();
        float input = ForceLadderClimb ?? (PlayerInput.Held(PlayerInput.MoveForward) ? 1 : 0) - (PlayerInput.Held(PlayerInput.MoveBack) ? 1 : 0);
        _ladderHeight = Mathf.Clamp(_ladderHeight + input * LadderClimbSpeed * dt, 0f, LadderTop);
        ClimbStep = Mathf.FloorToInt(_ladderHeight / 0.3f);
        float side = _ladderSide == 0 ? 1f : -1f, z = SteamerMeshBuilder.Z(SteamerMeshBuilder.LadderAt);
        // the feet on the rungs, the body a hand's reach out from them
        var at = t * BoatMeshBuilder.Flip(new Vector3(side * (SteamerMeshBuilder.LadderX + 0.42f), SteamerMeshBuilder.LadderFoot + _ladderHeight, z));
        var toHull = t.Basis * BoatMeshBuilder.Flip(new Vector3(-side, 0, 0));
        var hostVelocity = host switch { VehicleBody v => v.Velocity, FootPlayer p => p.WorldVelocity, _ => Vector3.Zero } with { Y = 0 };
        if (!Input.IsActionJustPressed(PlayerInput.Jump) || ForceLadderClimb != null)
        {
            if (_ladderHeight >= LadderTop - 0.05f && input > 0)
            {
                // over the rail: on deck inside it, by the ladder
                var spot = t * BoatMeshBuilder.Flip(new Vector3(side * (SteamerMeshBuilder.DeckHalf(z) - 0.7f), SteamerMeshBuilder.DeckY + 0.05f, z));
                OffLadder(null);
                Release(spot, Vector3.Zero);
                StandIn(spot, hostVelocity);
                GD.Print($"[steamer] {Name} climbs over the rail onto the deck");
                return null;
            }
            return (at, Mathf.Atan2(-toHull.X, -toHull.Z), hostVelocity + t.Basis.Y * input * LadderClimbSpeed);
        }
        // let go: back into the water beside the ship
        OffLadder(at - toHull.Normalized() * 0.4f);
        return null;
    }

    /// <summary>Off the ladder; with a point, let go there into the water.</summary>
    private void OffLadder(Vector3? intoWater)
    {
        _ladderHost = null;
        if (intoWater is { } w)
        {
            Release(w, Vector3.Zero);
            StartSwimmingAtSurface(w);
        }
        else if (Carrier == HoldOnLadder) Release(GlobalPosition, Vector3.Zero);
    }

    private const float LadderClimbSpeed = 1.1f;

    /// <summary>
    /// The boarding ladder of a walkable ship nearest this player within <paramref name="reach"/>
    /// (level, to a point just off its foot): the ship and which side.
    /// </summary>
    private (Node3D Host, int Side)? NearestLadder(float reach)
    {
        (Node3D, int)? best = null;
        float bestDist = reach;
        void Consider(Node3D host, Rideable? ride)
        {
            if (ride is not Steamer || SectionFrame(host, 0) is not { } frame) return;
            var t = frame.GlobalTransform;
            for (int door = 0; door < Steamer.GangwayCount; door++)
            {
                float side = door == 0 ? 1f : -1f;
                var foot = t * BoatMeshBuilder.Flip(new Vector3(side * (SteamerMeshBuilder.LadderX + 0.6f), SteamerMeshBuilder.LadderFoot,
                    SteamerMeshBuilder.Z(SteamerMeshBuilder.LadderAt)));
                float d = new Vector2(foot.X - GlobalPosition.X, foot.Z - GlobalPosition.Z).Length();
                if (d >= bestDist) continue;
                bestDist = d;
                best = (host, door);
            }
        }
        foreach (var p in GetTree().GetNodesInGroup(Group))
            if (p is FootPlayer other && other != this) Consider(other, RideOfHost(other));
        if (VehicleManager.Instance is { } vehicles)
            foreach (var node in vehicles.GetChildren())
                if (node is VehicleBody v) Consider(v, RideOfHost(v));
        return best;
    }

    /// <summary>How near a ladder's foot a swimmer must be to get onto it, m.</summary>
    private const float ClimbReach = 3.5f;
}
