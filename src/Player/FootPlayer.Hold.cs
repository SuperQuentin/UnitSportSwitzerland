using Godot;
using UnitSport.Core;
using UnitSport.Avatar;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// A ground vehicle carried inside another (#418): a car, a truck, a bus or a bike driven into a
/// carrier's hold (<see cref="VehicleDeck.CargoBays"/>). See <c>docs/notes/vehicles/vehicles-in-holds.md</c>.
///
/// <para>
/// The walk aboard (<c>FootPlayer.Deck.cs</c>) again, for a driver: the carrier's decks are built here
/// as collision while driving near it, a vehicle whose middle is in a bay it fits is carried by the
/// section's motion each frame (its own physics then runs relative to the hold floor), and it publishes
/// where it is in the carrier's frame (<see cref="DeckOn"/>, <see cref="DeckPos"/>, <see cref="DeckYaw"/>),
/// so every other peer draws it from the carrier's copy. Stopped with the handbrake (a bike: the brake
/// held) it is tied down: locked to its spot on the floor, no physics, until the throttle.
/// </para>
/// </summary>
public partial class FootPlayer
{
    /// <summary>Tied down in a hold: locked to the floor until the throttle (#418).</summary>
    public bool TiedDown => _tied;

    private bool _tied;
    private float _tieHeld;
    /// <summary>Where it was tied down: the spot and heading in the carrier's section frame.</summary>
    private Vector3 _tiePos;
    private float _tieYaw;
    /// <summary>The carrier's tilt under the vehicle, in the vehicle's own yaw frame: what its body is drawn turned by.</summary>
    private Basis _holdTilt = Basis.Identity;

    /// <summary>How long the handbrake (a bike's brake) is held at a standstill before it is tied down, s.</summary>
    private const float TieAfter = 0.3f;

    /// <summary>Driving a ground vehicle of its own, the kind a hold carries (not a craft, not a boat, not an NPC's).</summary>
    private bool InHoldRide => _ride is { IsVehicle: true } and not (Flyer or Boat) && RidingWith == 0 && !Npc;

    /// <summary>The process order after the vehicles a copy is placed from: a carrier first (0), a vehicle carried in it (8), the people aboard either (10).</summary>
    private int DeckPriority => Ride != RideKind.OnFoot && RidingWith == 0 && DeckOn != "" ? 8
        : RidingWith != 0 || DeckOn != "" || _decks.Count > 0 ? 10 : 0;

    /// <summary>A vehicle with at least one hold (a carrier).</summary>
    public static bool HasHolds(Rideable ride)
    {
        foreach (var deck in ride.Decks)
            if (deck.CargoBays.Length > 0) return true;
        return false;
    }

    private static bool InAnyBay(VehicleDeck deck, Vector3 local, float grow)
    {
        foreach (var bay in deck.CargoBays)
            if (bay.Contains(local, grow)) return true;
        return false;
    }

    /// <summary>The size a ride's hull is checked against a bay with: its parked box (across, height, length).</summary>
    private static Vector3 HullSize(Rideable ride) => ride.ParkedBox.Size;

    /// <summary>
    /// Every frame, driving: the carriers' decks near, placed where they are drawn; carried by the one
    /// whose bay this vehicle is in (and fits), or let go out of it.
    /// </summary>
    private void CarryInHold(float dt)
    {
        _deckScan -= dt;
        if (_deckScan <= 0) { _deckScan = 0.5; ScanDecks(holdsOnly: true); }
        PlaceDecks(dt);

        // carried first, then looked at: a carrier moving fast (or put somewhere) has left the spot the
        // vehicle stood on last frame behind, and that spot is not in its bay any more
        if (Aboard && _decks.TryGetValue(DeckOn, out var carrying) && SectionFrame(carrying.Host, DeckSection) is { } was && was.IsInsideTree())
        {
            var moved = was.GlobalTransform.Orthonormalized();
            if (_tied)
            {
                // locked to its spot on the floor: exactly where the carrier is drawn, every frame
                GlobalPosition = moved * _tiePos;
                float yaw = YawOf(moved) + _tieYaw;
                Rotation = new Vector3(0, yaw, 0);
                _motion.Yaw = yaw;
            }
            else if (_deckCarried)
            {
                // the section's motion since the last frame, as a walker aboard is carried (Deck.cs)
                var delta = moved * _carriedFrom.AffineInverse();
                float turn = MathX.WrapAngle(YawOf(moved) - YawOf(_carriedFrom));
                GlobalPosition = delta * GlobalPosition;
                Rotation = new Vector3(0, Rotation.Y + turn, 0);
                _motion.Yaw += turn;
                _viewYaw += turn;
                Velocity = new Basis(Vector3.Up, turn) * Velocity;
            }
            _carriedFrom = moved;
            _deckCarried = true;
        }

        if (BayHere() is { } bay)
        {
            if (bay.Key != DeckOn || bay.Section != DeckSection)
            {
                // into the hold (or across into another): from now on in its frame
                DeckOn = bay.Key;
                DeckSection = bay.Section;
                _carriedFrom = bay.Frame;
                _deckCarried = true;
                _tied = false;
            }
        }
        else if (Aboard) LeaveHold();

        if (!Aboard || !_decks.TryGetValue(DeckOn, out var mine) || SectionFrame(mine.Host, DeckSection) is not { } section)
        {
            _holdTilt = Basis.Identity;
            return;
        }
        var now = section.GlobalTransform.Orthonormalized();
        _carriedFrom = now;
        _deckCarried = true;
        DeckPos = now.AffineInverse() * GlobalPosition;
        DeckYaw = MathX.WrapAngle(Rotation.Y - YawOf(now));
        // the floor tilts with the carrier (a climbing freighter): the body is drawn on it, not level
        _holdTilt = (new Basis(Vector3.Up, Rotation.Y).Inverse() * now.Basis * new Basis(Vector3.Up, DeckYaw)).Orthonormalized();
    }

    /// <summary>The bay this vehicle stands in and fits, among the carriers' decks built here: the one it is in first.</summary>
    private (string Key, int Section, Transform3D Frame)? BayHere()
    {
        if (_ride == null) return null;
        var hull = HullSize(_ride);
        var feet = GlobalPosition;
        foreach (var set in _decks.Values)
            foreach (var (deck, _, _) in set.Sections)
            {
                if (deck.CargoBays.Length == 0 || SectionFrame(set.Host, deck.Section) is not { } node || !node.IsInsideTree()) continue;
                var frame = node.GlobalTransform.Orthonormalized();
                var local = frame.AffineInverse() * feet;
                bool current = set.Key == DeckOn && deck.Section == DeckSection;
                foreach (var bay in deck.CargoBays)
                    if (bay.Takes(_ride.Kind) && bay.Fits(hull) && bay.Contains(local, current ? 0.3f : 0f)) return (set.Key, deck.Section, frame);
            }
        return null;
    }

    /// <summary>Out of the hold (driven out, or got out): the world's again.</summary>
    private void LeaveHold()
    {
        DeckOn = "";
        _deckCarried = false;
        _tied = false;
        _tieHeld = 0f;
        _holdTilt = Basis.Identity;
    }

    /// <summary>
    /// Every physics step before the ride's, in a hold: ties the vehicle down when it is stopped with
    /// the handbrake, lets it go on the throttle. True while tied: no ride physics this step.
    /// </summary>
    private bool HoldPhysics(float dt)
    {
        if (!Aboard || !InHoldRide)
        {
            _tied = false;
            _tieHeld = 0f;
            return false;
        }
        var input = SeatIndex == 0 ? RideInputNow() : new RideInput(0f, 0f, 0f, false);
        if (_tied)
        {
            // a pedal lets it go (the brake too, at a standstill it is reverse; a bike's brake is what tied it)
            if (input.Throttle > 0.15f || _ride is { CanHop: false } && input.Brake > 0.15f && !input.Handbrake) { _tied = false; _tieHeld = 0f; return false; }
            IgnoreGuests();
            Velocity = Vector3.Zero;
            _motion.Speed = 0f;
            _shortfall = 0f;
            UpdateRideCamera(dt);
            return true;
        }
        bool stopped = Mathf.Abs(_motion.Speed) < 0.5f;
        bool holding = input.Handbrake || _ride is { CanHop: true } && input.Brake > 0.5f;
        _tieHeld = stopped && holding ? _tieHeld + dt : 0f;
        if (_tieHeld >= TieAfter) TieDown();
        return false;
    }

    /// <summary>Locks the vehicle to its spot on the hold floor, as it stands now.</summary>
    private void TieDown()
    {
        _tied = true;
        _tiePos = DeckPos;
        _tieYaw = DeckYaw;
        _motion.Speed = 0f;
        Velocity = Vector3.Zero;
    }

    /// <summary>
    /// Got into a vehicle parked in a hold (<see cref="VehicleState.Carrier"/>): where it stands in the
    /// carrier now (the state's world position may be a heartbeat old), and still tied down.
    /// </summary>
    private void ResumeHold(VehicleState state)
    {
        if (!state.InHold || !InHoldRide || HostNamed(state.Carrier) is not { } host || SectionFrame(host, state.CarrierSection) is not { } node)
            return;
        var frame = node.GlobalTransform.Orthonormalized();
        GlobalPosition = frame * state.CarrierPos;
        float yaw = YawOf(frame) + state.CarrierYaw;
        Rotation = new Vector3(0, yaw, 0);
        _motion.Yaw = yaw;
        _motion.Speed = 0f;
        Velocity = Vector3.Zero;
        DeckOn = state.Carrier;
        DeckSection = state.CarrierSection;
        DeckPos = state.CarrierPos;
        DeckYaw = state.CarrierYaw;
        _carriedFrom = frame;
        _deckCarried = true;
        TieDown();
        _deckScan = 0;
    }

    /// <summary>The vehicle being driven as it stands in its hold, for the state it is parked with; none outside one.</summary>
    private (string Key, int Section, Vector3 Pos, float Yaw) HoldPlace =>
        InHoldRide && Aboard ? (DeckOn, DeckSection, DeckPos, DeckYaw) : ("", 0, Vector3.Zero, 0f);

    /// <summary>For checks: the carriers whose decks are built round this driver.</summary>
    public string HoldsSeen => string.Join(" ", _decks.Values.Select(s => s.Key));

    // ---- shared with parked vehicles (VehicleBody.Hold.cs) ---------------------------------------

    /// <summary>A carrier by its key (<see cref="KeyOf"/>): a player driving it, or a parked vehicle; null when not here.</summary>
    public static Node3D? CarrierNamed(Node from, string key)
    {
        if (key.StartsWith("v:")) return VehicleManager.Instance?.GetNodeOrNull<VehicleBody>(key[2..]);
        foreach (var node in from.GetTree().GetNodesInGroup(Group))
            if (node is FootPlayer p && p.Name == key) return p;
        return null;
    }

    /// <summary>
    /// The carrier (a player driving one, or a parked one) whose bay a vehicle of hull
    /// <paramref name="hull"/> standing at <paramref name="feet"/> is in: its key, section and that
    /// section's frame now. For a parked vehicle whose carrier changed hands (a new key).
    /// </summary>
    public static (Node3D Host, string Key, int Section, Transform3D Frame)? CarrierAt(Node from, Vector3 feet, Vector3 hull, Node? self, RideKind kind = 0)
    {
        IEnumerable<Node3D> hosts = from.GetTree().GetNodesInGroup(Group).OfType<FootPlayer>().Cast<Node3D>()
            .Concat(VehicleManager.Instance?.GetChildren().OfType<VehicleBody>() ?? Enumerable.Empty<VehicleBody>());
        foreach (var host in hosts)
        {
            if (host == self || RideOfHost(host) is not { } ride || !HasHolds(ride)) continue;
            foreach (var deck in ride.Decks)
            {
                if (deck.CargoBays.Length == 0 || SectionFrame(host, deck.Section) is not { } node || !node.IsInsideTree()) continue;
                var frame = node.GlobalTransform.Orthonormalized();
                var local = frame.AffineInverse() * feet;
                foreach (var bay in deck.CargoBays)
                    if (bay.Takes(kind) && bay.Fits(hull) && bay.Contains(local, 0.3f)) return (host, KeyOf(host), deck.Section, frame);
            }
        }
        return null;
    }

    /// <summary>The frame a carrier's section is drawn in now; the carrier's own node when it draws none (a headless server's copy).</summary>
    public static Transform3D HoldFrame(Node3D host, int section) =>
        (SectionFrame(host, section) is { } node && node.IsInsideTree() ? node.GlobalTransform : host.GlobalTransform).Orthonormalized();

    /// <summary>The bodies of a carrier its cargo must not collide with: its own (a driven truck's sections too).</summary>
    public static IEnumerable<PhysicsBody3D> CarrierBodies(Node3D host)
    {
        if (host is PhysicsBody3D body) yield return body;
        if (host is FootPlayer p)
            foreach (var section in p._sections) yield return section;
    }
}
