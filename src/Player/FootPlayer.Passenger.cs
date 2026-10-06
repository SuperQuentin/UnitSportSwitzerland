using Godot;
using UnitSport.Core;
using UnitSport.Avatar;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// Riding along in someone else's vehicle (#158), and a vehicle rolling on with nobody at its
/// wheel. The seats are handed out by <see cref="PassengerService"/>; see <c>docs/notes/player/passengers.md</c>.
/// </summary>
public partial class FootPlayer
{
    /// <summary>
    /// The player (peer id) whose vehicle this one rides in as a passenger, or 0. Replicated with
    /// <see cref="SeatIndex"/>: every peer moves and draws a passenger from its host's copy, never
    /// from the passenger's own interpolated position, so it cannot shake in its seat.
    /// </summary>
    [Export] public int RidingWith { get; set; }

    /// <summary>
    /// The seat taken: in <see cref="RidingWith"/>'s vehicle; or, in one's own vehicle, 0 at the
    /// wheel and above 0 sat in it while it rolls on driverless (the driver jumped out).
    /// </summary>
    [Export] public int SeatIndex { get; set; }

    public bool RidingAlong => RidingWith != 0;

    /// <summary>The vehicle this passenger sits in can be walked about in: E stands up into it rather than getting out.</summary>
    public bool HostWalkable => Host is { } host && VehicleOf(host) is { Walkable: true };

    /// <summary>A bus's doors, one bit each: its own while driving it, from the published pose on a copy (the server's too).</summary>
    public byte BusDoors => _ride is Truck own ? own.DoorsOpen : _ride is Steamer gangways ? gangways.DoorsOpen
        : _ride is Airliner jet ? jet.DoorsOpen
        : Ride == RideKind.OnFoot ? (byte)0 : Ride == RideKind.Steamer ? Steamer.DoorsOf(Anim)
        : Airliner.IsAirliner(Ride) ? Airliner.LookOf(Anim).Doors : (byte)((Mathf.RoundToInt(Anim.W) >> 4) & 15);

    /// <summary>One's own vehicle with nobody at the wheel: no input, it rolls on under its own physics.</summary>
    public bool RollingDriverless => _ride != null && SeatIndex != 0;

    private FootPlayer? _host;
    private MeshInstance3D? _seated;
    private (Node3D? Rig, int Seat, bool Head, long Outfit, int Appearance) _seatedFor;
    private bool _walkerHidden;

    /// <summary>The player whose vehicle this one rides in, as this peer has it; null if none (or not here yet).</summary>
    public FootPlayer? Host
    {
        get
        {
            if (RidingWith == 0) return null;
            if (_host != null && IsInstanceValid(_host) && _host.Name == RidingWith.ToString()) return _host;
            return _host = GetParent()?.GetNodeOrNull<FootPlayer>(RidingWith.ToString());
        }
    }

    /// <summary>Everyone riding in this player's vehicle, as this peer sees them.</summary>
    private IEnumerable<FootPlayer> Riders => GetTree().GetNodesInGroup(Group).OfType<FootPlayer>()
        .Where(p => p != this && p.RidingWith.ToString() == Name);

    private static bool OnlineSeats => PassengerService.Instance?.Online == true;

    private static readonly Dictionary<(RideKind, int, long), Rideable> _seatRides = new();

    /// <summary>The vehicle <paramref name="who"/> drives as numbers (seats, chase camera): its own, or one made like it for a remote copy.</summary>
    private static Rideable? VehicleOf(FootPlayer who)
    {
        if (who._ride != null) return who._ride;
        if (who.Ride == RideKind.OnFoot) return null;
        // a copy's own airstairs: their platform is the driver's (Anim), not a shared one's (#417)
        if (who._remoteRide is Airstairs own && who.Ride == RideKind.Airstairs) return own;
        // and a copy's own forklift: its mast is the driver's (Anim), not a shared one's (#583)
        if (who._remoteRide is Forklift lifting && who.Ride == RideKind.Forklift) return lifting;
        // and a copy's own excavator: its arm is the driver's (Anim) (#611)
        if (who._remoteRide is Excavator digging && who.Ride == digging.Kind) return digging;
        // and a copy's own wheel loader: its frame, arm and bucket are the driver's (#612)
        if (who._remoteRide is WheelLoader loading && who.Ride == RideKind.WheelLoader) return loading;
        // and a copy's own roller: its bend and its vibration are the driver's (#614)
        if (who._remoteRide is CompactRoller rolling && who.Ride == RideKind.CompactRoller) return rolling;
        var key = (who.Ride, who.CarSetupId, who.TuningBits);
        if (_seatRides.TryGetValue(key, out var known)) return known;
        var made = CarSetups.Ride(who.Ride, who.CarSetupId, who.TuningBits);
        if (made != null) _seatRides[key] = made;
        return made;
    }

    /// <summary>The drawn rig a seat is on: the cab's, or a bus's rear half's.</summary>
    private static Node3D? SeatRig(FootPlayer who, SeatAnchor seat) => seat.Section == 0
        ? who._visual
        : who.GetNodeOrNull<Node3D>($"Section{seat.Section}")?.GetNodeOrNull<Node3D>("Visual");

    /// <summary>The vehicle this player sits in, its seat and the rig the seat is on; null when not sitting anywhere.</summary>
    private (FootPlayer Who, Rideable Vehicle, SeatAnchor Seat, Node3D Rig)? WhereSeated()
    {
        // a host sat in its own driverless vehicle: on a remote copy too, which has no ride object
        var who = RidingWith != 0 ? Host : (SeatIndex > 0 && Ride != RideKind.OnFoot ? this : null);
        if (who == null || VehicleOf(who) is not { } vehicle) return null;
        var seats = vehicle.Seats;
        if (SeatIndex <= 0 || SeatIndex >= seats.Length || SeatRig(who, seats[SeatIndex]) is not { } rig) return null;
        return (who, vehicle, seats[SeatIndex], rig);
    }

    // ---- every copy: drawn in the seat --------------------------------------------------------

    /// <summary>
    /// Puts this player's figure in its seat, on the vehicle's rig (a child of it, so it moves with
    /// the body exactly, on every peer), and hides the walking figure a passenger no longer uses.
    /// </summary>
    private void UpdateSeated()
    {
        // a passenger's copy (and one walking about aboard) is placed from its vehicle's: after the
        // vehicle has moved this frame, on every peer
        int priority = DeckPriority;
        if (ProcessPriority != priority) ProcessPriority = priority;
        var at = WhereSeated();
        if (at is not { } s)
        {
            if (_seated != null && IsInstanceValid(_seated)) _seated.QueueFree();
            _seated = null;
            if (_walkerHidden && _visual != null) _visual.Visible = true;
            _walkerHidden = false;
            return;
        }
        // first person: no head of your own in front of the lens
        bool head = !(IsMultiplayerAuthority() && !_thirdPerson);
        if (_seated == null || !IsInstanceValid(_seated) || _seatedFor != (s.Rig, SeatIndex, head, OutfitBits, AppearanceBits))
        {
            if (_seated != null && IsInstanceValid(_seated)) _seated.QueueFree();
            _seated = new MeshInstance3D
            {
                Name = $"Seated_{Name}",
                Mesh = SeatedFigure.Build(FigurePalette(RiderIndex()), s.Seat, Hat, head),
                MaterialOverride = HumanMeshBuilder.FigureMaterial(),
            };
            s.Rig.AddChild(_seated);
            _seatedFor = (s.Rig, SeatIndex, head, OutfitBits, AppearanceBits);
        }
        _seated.Transform = SeatedFigure.FrameOf(s.Rig, s.Seat);
        // a pillion in first person: the helmet would fill the lens
        _seated.Visible = head || s.Seat.Pose != SeatPose.Straddle;
        if (RidingWith != 0 && _visual != null && _visual.Visible)
        {
            _visual.Visible = false;
            _walkerHidden = true;
        }
    }

    // ---- the passenger's own peer ------------------------------------------------------------

    /// <summary>Physics, as a passenger: carried by the host's vehicle, no body of its own.</summary>
    private void RideAlong()
    {
        if (!_body.Disabled) _body.Disabled = true;
        if (Host is not { } host) { Velocity = Vector3.Zero; return; }
        GlobalPosition = host.GlobalPosition;
        Rotation = new Vector3(0, host.GlobalRotation.Y, 0);
        Velocity = host.WorldVelocity;
    }

    /// <summary>
    /// The camera from a seat: from the figure's own eye on the vehicle's body (first person), or
    /// behind the vehicle (third), turned by the free look, which stays where it is put.
    /// </summary>
    /// <summary>
    /// What the seat camera's ray ignores: this body, the vehicle and its section bodies. Rebuilt
    /// only when the vehicle, its train (<see cref="TrainRids"/> is a new array then) or its child
    /// count changes (#221: a new array and a LINQ scan per frame before).
    /// </summary>
    private Godot.Collections.Array<Rid> SeatExclude(FootPlayer who)
    {
        var train = who.TrainRids();
        int children = who.GetChildCount();
        if (_seatExclude != null && who == _seatWho && ReferenceEquals(train, _seatTrain) && children == _seatChildren) return _seatExclude;
        _seatExclude = new Godot.Collections.Array<Rid> { GetRid(), who.GetRid() };
        foreach (var section in who.GetChildren().OfType<CollisionObject3D>()) _seatExclude.Add(section.GetRid());
        _seatWho = who;
        _seatTrain = train;
        _seatChildren = children;
        return _seatExclude;
    }
    private Godot.Collections.Array<Rid>? _seatExclude, _seatTrain;
    private FootPlayer? _seatWho;
    private int _seatChildren;

    private void UpdateSeatCamera(float dt)
    {
        if (_camera == null || WhereSeated() is not { } s) return;
        if (!_thirdPerson)
        {
            var frame = s.Rig.GlobalTransform * SeatedFigure.FrameOf(s.Rig, s.Seat);
            var eye = frame * (SeatedFigure.Eye(s.Seat) - s.Seat.Hip);
            var basis = frame.Basis.Orthonormalized() * new Basis(Vector3.Up, _lookYaw) * new Basis(Vector3.Right, _pitch);
            _camera.GlobalTransform = new Transform3D(basis, eye);
            _camera.Fov = Mathf.Lerp(_camera.Fov, Core.GameSettings.Current.CockpitFov, MathX.Damp(3f, dt));
            return;
        }
        // behind and above the vehicle, round it with the look, pulled in short of what is in the way
        var ride = s.Vehicle;
        var centre = s.Who.GlobalPosition + Vector3.Up * ride.EyeHeight;
        float yaw = s.Who.GlobalRotation.Y + _lookYaw;
        var wanted = centre + new Basis(Vector3.Up, yaw) * new Vector3(0, ride.ChaseHeight, ride.ChaseDistance);
        var hit = _camRay.Cast(GetWorld3D().DirectSpaceState, centre, wanted, CameraMask, SeatExclude(s.Who));
        var at = hit.Count > 0 ? centre.Lerp(hit["position"].AsVector3(), 0.85f) : wanted;
        _camera.GlobalTransform = Transform3D.Identity.LookingAt(centre - at, Vector3.Up).Translated(at);
        _camera.RotateObjectLocal(Vector3.Right, _pitch + ride.ChasePitch + 0.1f);
        _camera.Fov = Mathf.Lerp(_camera.Fov, ride.BaseFov, MathX.Damp(3f, dt));
    }

    /// <summary>E beside someone's vehicle: the nearest one being driven, within reach of its door, or null.</summary>
    private FootPlayer? DrivenVehicleInReach(float reach)
    {
        FootPlayer? best = null;
        float bestDist = reach;
        foreach (var p in GetTree().GetNodesInGroup(Group).OfType<FootPlayer>())
        {
            if (p == this || p.RidingAlong || p.Ride == RideKind.OnFoot || VehicleOf(p) is not { IsVehicle: true } vehicle) continue;
            // a vehicle you can walk about in is boarded by walking in (#162)
            if (vehicle.Seats.Length < 2 || vehicle.Walkable) continue;
            // at its door, or right against its side (#261): not anywhere within a few metres of its middle
            var entry = vehicle.EntryPoint;
            float d = entry != Vector3.Zero
                ? p.ToGlobal(entry).DistanceTo(GlobalPosition + Vector3.Up)
                : VehicleReach.HullDistance(p, vehicle.ParkedBox, GlobalPosition + Vector3.Up) + reach - 1.2f;
            if (d < bestDist) { bestDist = d; best = p; }
        }
        return best;
    }

    /// <summary>E in a passenger seat: out, if it is slow enough.</summary>
    private bool TryLeaveSeat()
    {
        var host = Host;
        if (host != null && host.WorldVelocity.Length() > PassengerService.BoardSpeed)
        {
            PassengerService.Say("Too fast to get out.");
            return true;
        }
        PassengerService.Instance?.LeaveSeat();
        StepOut(host?.WorldVelocity ?? Vector3.Zero);
        return true;
    }

    /// <summary>
    /// Out of the seat and onto one's feet beside it (a bus: at its door), going at
    /// <paramref name="velocity"/>: the vehicle's own speed when thrown out.
    /// </summary>
    private void StepOut(Vector3 velocity)
    {
        var host = Host;
        var at = host?.GlobalPosition ?? GlobalPosition;
        var right = (host ?? this).GlobalTransform.Basis.X with { Y = 0 };
        right = right.LengthSquared() > 1e-6f ? right.Normalized() : Vector3.Right;
        var frame = (host ?? this).GlobalTransform;
        float side = 1.5f;
        Rideable? leaving = null;
        if (host != null && VehicleOf(host) is { } vehicle && SeatIndex < vehicle.Seats.Length)
        {
            leaving = vehicle;
            side = vehicle.ParkedBox.Size.X * 0.5f + BodyRadius + 0.4f;
            var seat = vehicle.SeatPosition(SeatIndex);
            if (vehicle is Truck { IsBus: true })
                // a bus is left by its doors, on its right
                at = host.ToGlobal(new Vector3(0, 0, vehicle.EntryPoint.Z));
            else
            {
                at = host.ToGlobal(new Vector3(0, 0, seat.Z));
                if (seat.X < 0) right = -right;   // the left-hand seats get out on the left
            }
        }
        float yaw = (host ?? this).GlobalRotation.Y + _lookYaw;
        if (_seated != null && IsInstanceValid(_seated)) _seated.QueueFree();
        _seated = null;
        RidingWith = 0;
        SeatIndex = 0;
        _host = null;
        ProcessPriority = 0;
        _body.Disabled = false;
        _lookYaw = 0f;
        _viewYaw = yaw;
        Rotation = new Vector3(0, yaw, 0);
        Velocity = velocity;
        GlobalPosition = FindExit(at, right, side, frame, leaving, grounded: true);
        RefreshVisual(force: true);
        _walkerHidden = false;
    }

    // ---- answers from the server (PassengerService) --------------------------------------------

    /// <summary>Sat in seat <paramref name="seat"/> of <paramref name="host"/>'s vehicle (boarded, or the vehicle changed hands).</summary>
    public void BoardAs(int host, int seat)
    {
        if (_sliding) EndSlide();
        if (Aboard) LeaveDeck(keepVelocity: false);
        _deckWait = 0f;
        bool boarding = RidingWith == 0;
        RidingWith = host;
        SeatIndex = seat;
        _host = null;
        // after the host's copy has moved this frame: the seat and the lens go where the vehicle is now
        ProcessPriority = 10;
        _body.Disabled = true;
        Velocity = Vector3.Zero;
        if (boarding)
        {
            _lookYaw = 0f;
            _pitch = -0.1f;
            DanceId = 0;
        }
    }

    /// <summary>The server handed this player the vehicle it sat in: it is the host now, at the wheel (seat 0) or sat driverless in <paramref name="seat"/>.</summary>
    public void TakeVehicle(VehicleState state, int seat)
    {
        float look = _lookYaw;
        if (Aboard) LeaveDeck(keepVelocity: false);
        _deckWait = 0f;
        if (_seated != null && IsInstanceValid(_seated)) _seated.QueueFree();
        _seated = null;
        RidingWith = 0;
        _host = null;
        ProcessPriority = 0;
        _body.Disabled = false;
        EnterVehicle(state);   // where it is, how it moves, its train, its damage
        SeatIndex = seat;
        // taking it over is not getting in: no door opens
        DoorsOpen = state.DoorsOpen;
        _shutDriverIn = 0f;
        _lookYaw = seat > 0 ? look : 0f;
        _walkerHidden = false;
    }

    /// <summary>The server let this player, sat in its own driverless vehicle, move over to the wheel.</summary>
    public void MoveToWheel()
    {
        if (_ride == null) return;
        SeatIndex = 0;
        _lookYaw = 0f;
    }

    /// <summary>
    /// The server asks this host to hand its vehicle to <paramref name="to"/>, a passenger who wants
    /// the wheel: its state goes over, and this player stays aboard in the seat it sat in.
    /// </summary>
    public void GiveUpVehicle(int to)
    {
        if (_ride is not { IsVehicle: true }) return;
        var state = CaptureVehicle(wrecked: false);
        int seat = SeatIndex;
        float look = _lookYaw;
        PassengerService.Instance?.HandOver(to, state);
        ApplyRide(RideKind.OnFoot, Vector3.Zero);
        BoardAs(to, seat);
        _lookYaw = look;
    }

    /// <summary>Thrown out of a seat: the vehicle was wrecked, or a motorbike's rider got off.</summary>
    public void ThrownOut(Vector3 velocity)
    {
        if (RidingWith == 0) return;
        bool hard = velocity.Length() > 5f;
        StepOut(hard ? velocity * 0.25f + Vector3.Up * 6f : velocity);
        if (hard) _stunTimer = 1.2f;
        // out of a car that sank (#299): up to the surface, swimming (#301)
        SurfaceIfInWater();
    }

    /// <summary>
    /// The server's copy of a host that left the game: its vehicle as the server last saw it, for its
    /// passengers to go on in. Momentum, preset, parts, doors and train; full health.
    /// </summary>
    public VehicleState? VehicleStateOfCopy()
    {
        if (Ride == RideKind.OnFoot || CarSetups.Ride(Ride, CarSetupId, TuningBits) is not { } vehicle) return null;
        return new VehicleState(Ride, Global, Rotation.Y, WorldVelocity, vehicle.MaxHealth, true, false, 0f, VehicleState.Now,
            Tuning: TuningBits, DoorsOpen: DoorsOpen, Setup: CarSetupId, Train: TrailerCode,
            Angles: new Vector3(TrainPose.X, TrainPose.Y, TrainPose.Z));
    }
}
