using Godot;
using UnitSport.Avatar;
using UnitSport.World.Ropeways;

namespace UnitSport.Player;

/// <summary>
/// Riding a ski lift (#912, #915, docs/notes/world/ski-lifts.md). At a bottom station, on foot or on
/// skis, interact boards the next carrier (<see cref="RopewayMotion.Next"/>). The rider is on that
/// carrier from then on: <see cref="LiftLine"/> and <see cref="LiftCarrier"/> are replicated, and
/// every peer places the rider from the shared clock (<see cref="LiftPose"/>), exactly on the chair,
/// not from the interpolated position stream. Before the carrier leaves the rider waits on the
/// station's spot; at the top it is set down past the station, still on skis if it came on skis.
/// On a chair the rider is drawn sitting (<see cref="UpdateLiftSeat"/>); in a cabin it is out of sight.
/// A drag lift (T-bar, rope tow) is let go of with interact again. <see cref="TryLift"/> is the
/// bots' call too, on the body's simulator.
/// </summary>
public partial class FootPlayer
{
    /// <summary>The lift ridden or waited for (<see cref="RopewayLine.Id"/>), 0 for none. Replicated.</summary>
    [Export] public int LiftLine { get; set; }

    /// <summary>The carrier on <see cref="LiftLine"/>: its chair, cabin or bar. Replicated.</summary>
    [Export] public int LiftCarrier { get; set; }

    private RopewayLine? _lift;
    private bool _liftArriving;
    private MeshInstance3D? _liftSeat;
    private (int Line, int Seats, bool Skis, int Look) _liftSeatFor;
    private bool _liftHidWalker;

    /// <summary>The lift this body rides, or waits for at its station.</summary>
    public RopewayLine? Lift => _lift;

    /// <summary>Seconds until the carrier leaves the bottom station, while waiting; 0 once aboard.</summary>
    public double LiftWait => _lift == null ? 0 : RopewayMotion.UntilDeparture(_lift, LiftCarrier, RopewayService.Sim);

    /// <summary>A bottom station within reach, if the body may board: on foot or on skis, standing, not carried.</summary>
    public RopewayLine? LiftInReach()
    {
        if (_lift != null || Carrier != null || RidingWith != 0 || !IsOnFloor()) return null;
        if (_ride != null && _ride is not SnowRide) return null;
        return RopewayService.Instance?.StationNear(GlobalPosition);
    }

    /// <summary>Interact at a station boards; on a drag lift lets go. True when it did something.</summary>
    public bool TryLift()
    {
        if (_lift != null)
        {
            // on a chair or in a cabin there is no getting off half way
            if (_lift.Kind is RopewayKind.TBar or RopewayKind.RopeTow && LiftWait <= 0) LetGoOfLift();
            return true;
        }
        if (LiftInReach() is not { } line) return false;
        Board(line);
        return true;
    }

    private void Board(RopewayLine line)
    {
        var (carrier, _) = RopewayMotion.Next(line, RopewayService.Sim);
        _lift = line;
        LiftLine = line.Id;
        LiftCarrier = carrier;
        _liftArriving = false;
        _motion.Speed = 0f;
        // inside a cabin there is nothing to see; on a chair or a drag lift the rider is
        ShowWhileCarried = line.Kind is not (RopewayKind.Gondola or RopewayKind.CableCar);
        CarriedPose = 0;
        Carrier = LiftFrame;
    }

    /// <summary>The carrier's hold on the body this frame, on its simulator; it starts the arrival at the top.</summary>
    private (Vector3 At, float Yaw, Vector3 Velocity)? LiftFrame()
    {
        if (_lift is not { } line || Terrain?.Origin is not { } origin) return null;
        var pose = LiftPose(line, LiftCarrier, origin);
        var (s, _, up) = RopewayMotion.Carrier(line, LiftCarrier, RopewayService.Sim);
        if (up && s >= line.Length - 2f && !_liftArriving)
        {
            _liftArriving = true;
            Callable.From(ArriveAtTop).CallDeferred();
        }
        return (pose.At, pose.Yaw, pose.Velocity);
    }

    /// <summary>
    /// Where a rider of carrier <paramref name="carrier"/> is, on every peer: on its way up, on the
    /// chair (its feet below the seat), on the cabin's floor, or pulled along the snow; otherwise on
    /// the spot of the station it is waiting at (or has just reached).
    /// </summary>
    private (Vector3 At, float Yaw, Vector3 Velocity, bool Aboard) LiftPose(RopewayLine line, int carrier, Core.WorldOrigin origin)
    {
        var (s, _, up) = RopewayMotion.Carrier(line, carrier, RopewayService.Sim);
        if (!up)
        {
            // waiting at the bottom, or set down at the top a moment ago
            float at = s < line.Length / 2 ? 0f : line.Length;
            var (e0, n0, alt0, de0, dn0) = line.At(at);
            var spot = origin.ToWorld(e0, n0, alt0);
            float ground = Terrain != null && Terrain.TryGetHeight(spot, out float g0) ? g0 : alt0;
            return (spot with { Y = ground }, Yaw(de0, dn0), Vector3.Zero, false);
        }
        var (e, n, alt, de, dn) = line.At(s);
        var world = origin.ToWorld(e, n, alt);
        if (line.Kind is RopewayKind.TBar or RopewayKind.RopeTow)
            world.Y = Terrain != null && Terrain.TryGetHeight(world, out float g) ? g : alt - 6f;   // pulled along the snow
        else
            world.Y = alt - line.Hang - (line.Kind == RopewayKind.Chair ? 0.5f : 0f);   // under the seat, on the cabin's floor
        return (world, Yaw(de, dn), new Vector3(de, 0, -dn) * line.Speed, true);
    }

    /// <summary>
    /// Another peer's rider (#915): on its carrier from the clock, not where the position stream
    /// would put it a moment late. Called after the remote copy's interpolation.
    /// </summary>
    private void PlaceRemoteOnLift()
    {
        if (LiftLine == 0 || Origin is not { } origin || RopewayService.Instance is not { } lifts) { _lift = null; return; }
        var (e, n) = origin.ToLv95(Position);
        if (_lift == null || _lift.Id != LiftLine) _lift = lifts.Find(LiftLine, e, n);
        if (_lift == null) return;
        var pose = LiftPose(_lift, LiftCarrier, origin);
        Position = pose.At;
        Rotation = new Vector3(0, pose.Yaw, 0);
    }

    /// <summary>Where this rider should be by the clock, and whether it is aboard (<c>--liftnet</c>, #915); null off a lift.</summary>
    public (Vector3 At, bool Aboard)? LiftExpected()
    {
        if (LiftLine == 0 || _lift == null || Origin is not { } origin) return null;
        var pose = LiftPose(_lift, LiftCarrier, origin);
        return (pose.At, pose.Aboard);
    }

    /// <summary>Drawn sitting on a chair now.</summary>
    public bool LiftSeated => _liftSeat != null && IsInstanceValid(_liftSeat);

    /// <summary>
    /// The rider drawn for the lift, on every peer: sitting on a chair (skis on its feet when on
    /// skis), out of sight in a cabin, the usual figure otherwise.
    /// </summary>
    private void UpdateLiftSeat()
    {
        bool aboard = _lift != null && LiftLine != 0 && Origin is { } origin && LiftPose(_lift, LiftCarrier, origin).Aboard;
        bool chair = aboard && _lift!.Kind == RopewayKind.Chair;
        bool cabin = aboard && _lift!.Kind is RopewayKind.Gondola or RopewayKind.CableCar;
        if (!chair && _liftSeat != null)
        {
            if (IsInstanceValid(_liftSeat)) _liftSeat.QueueFree();
            _liftSeat = null;
        }
        if (chair)
        {
            bool skis = SnowRide.Is(RideKindId);
            var want = (LiftLine, _lift!.Seats, skis, AppearanceBits ^ OutfitBits.GetHashCode());
            if (_liftSeat == null || !IsInstanceValid(_liftSeat) || _liftSeatFor != want)
            {
                if (_liftSeat != null && IsInstanceValid(_liftSeat)) _liftSeat.QueueFree();
                _liftSeat = BuildLiftSeat(skis);
                AddChild(_liftSeat);
                _liftSeatFor = want;
            }
            // side by side on the bench: each rider has its seat, the same on every peer
            int seats = Mathf.Clamp(_lift!.Seats, 1, 8);
            int seat = StableSeat(seats);
            float x = (seat - (seats - 1) * 0.5f) * 0.6f;
            _liftSeat.Transform = new Transform3D(Basis.Identity, new Vector3(x, 0.62f, 0.2f));
        }
        bool hide = chair || cabin;
        if (_visual != null)
        {
            if (hide && _visual.Visible) { _visual.Visible = false; _liftHidWalker = true; }
            else if (!hide && _liftHidWalker) { _visual.Visible = true; _liftHidWalker = false; }
        }
    }

    /// <summary>A seat on the bench from the player's name: the same on every peer (not string.GetHashCode, which is per process).</summary>
    private int StableSeat(int seats)
    {
        int h = 0;
        foreach (char c in Name.ToString()) h = h * 31 + c;
        return (int)((uint)h % (uint)seats);
    }

    private MeshInstance3D BuildLiftSeat(bool skis)
    {
        var seat = new SeatAnchor(0, Vector3.Zero, 0.12f, -0.55f) { Pose = SeatPose.Seated };
        var figure = new MeshInstance3D
        {
            Name = "LiftSeat",
            Mesh = SeatedFigure.Build(FigurePalette(RiderIndex()), seat, Hat, head: !(IsMultiplayerAuthority() && !_thirdPerson)),
            MaterialOverride = HumanMeshBuilder.FigureMaterial(),
        };
        if (skis)
            foreach (float side in new[] { -0.11f, 0.11f })
                figure.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(0.09f, 0.03f, 1.7f) },
                    Position = new Vector3(side, -0.6f, -0.65f),
                    MaterialOverride = HumanMeshBuilder.FigureMaterial(),
                });
        return figure;
    }

    /// <summary>
    /// How much higher and further back the chase camera sits on a chair or in a cabin: above the
    /// cable, which a camera at a skier's chase height would have right in front of it.
    /// </summary>
    private (float Up, float Back) LiftCamera => _lift == null || LiftWait > 0 ? (0f, 0f) : _lift.Kind switch
    {
        RopewayKind.Chair => (3.2f, 3f),
        RopewayKind.Gondola => (4f, 7f),
        RopewayKind.CableCar => (5f, 10f),
        _ => (0f, 0f),
    };

    /// <summary>The yaw that faces a flat direction (east, north).</summary>
    private static float Yaw(float de, float dn) => Mathf.Atan2(-de, dn);

    private void ClearLift()
    {
        _lift = null;
        LiftLine = 0;
        LiftCarrier = 0;
    }

    /// <summary>At the top: off past the station, still on skis if on skis, at a walking pace.</summary>
    private void ArriveAtTop()
    {
        if (_lift is not { } line || Terrain?.Origin is not { } origin) { ClearLift(); return; }
        var (e, n, alt, de, dn) = line.At(line.Length);
        var at = origin.ToWorld(e + de * 4, n + dn * 4, alt);
        at.Y = Terrain.TryGetHeight(at, out float g) ? g + 0.1f : alt;
        var on = new Vector3(de, 0, -dn) * 2f;
        ClearLift();
        if (_ride is SnowRide) ReleaseRiding(at, on, Yaw(de, dn));
        else Release(at, Vector3.Zero);
    }

    /// <summary>Let go of a T-bar or a rope tow where it is, on skis, gliding on at its speed.</summary>
    private void LetGoOfLift()
    {
        if (_lift is not { } line) return;
        var fwd = -GlobalTransform.Basis.Z with { Y = 0 };
        ClearLift();
        if (_ride is SnowRide) ReleaseRiding(GlobalPosition, fwd.Normalized() * line.Speed, Rotation.Y);
        else Release(GlobalPosition, Vector3.Zero);
    }

    /// <summary>
    /// Out of a carrier still riding (skis off a lift): the ride kept, its speed and heading taken
    /// afresh. <see cref="Release"/> would keep the speed the ride had when it was picked up.
    /// </summary>
    public void ReleaseRiding(Vector3 at, Vector3 velocity, float yaw)
    {
        Release(at, velocity);
        Rotation = new Vector3(0, yaw, 0);
        _viewYaw = yaw;
        _motion = new RideMotion { Speed = Core.MathX.FlatLength(velocity), Yaw = yaw };
    }
}
