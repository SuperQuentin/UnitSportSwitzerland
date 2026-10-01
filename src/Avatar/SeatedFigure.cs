using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Someone riding along in a seat (#158): the figure built around a <see cref="SeatAnchor"/>'s hip,
/// in node space (−Z forward), so its node goes at the anchor (<see cref="FrameOf"/>). On a chair the
/// back is on the backrest, the hands rest on the thighs and the feet on the floor ahead; astride
/// (a pillion) the hands hold the rider's waist and the feet the rear pegs.
/// </summary>
public static class SeatedFigure
{
    /// <summary>The mesh, its hip at the origin. <paramref name="head"/> false for the one sitting in it, in first person.</summary>
    public static ArrayMesh Build(HumanPalette palette, SeatAnchor seat, Headwear hat = Headwear.None, bool head = true)
    {
        var s = new MeshScratch();
        if (seat.Pose == SeatPose.Straddle)
            HumanMeshBuilder.AppendRider(s, palette, Vector3.Zero,
                CarMeshBuilder.Turned(seat.Grip - seat.Hip), CarMeshBuilder.Turned(seat.Peg - seat.Hip));
        else
            HumanMeshBuilder.AppendDriver(s, palette, Lap(seat), 0f, 0f, 0f, body: true, head: head, hat: hat);
        return s.Build();
    }

    /// <summary>
    /// A chair as <see cref="HumanMeshBuilder.AppendDriver"/> poses it, authored around the hip: a
    /// "wheel" lying in the lap for the hands to rest on, both feet flat on the floor ahead, the
    /// knees bent more than a driver's (nothing to reach for).
    /// </summary>
    private static DriverSeat Lap(SeatAnchor seat)
    {
        float ball = seat.Floor - seat.Hip.Y + 0.06f;
        float reach = Mathf.Sqrt(Mathf.Max(0.09f, 0.8f * 0.8f - ball * ball)) * 0.85f;
        return new DriverSeat(Vector3.Zero, seat.Recline,
            WheelCentre: new Vector3(0, 0.17f, 0.3f), WheelAxis: new Vector3(0, 0.94f, -0.34f).Normalized(), WheelRadius: 0.15f,
            Throttle: new Vector3(-0.1f, ball, reach), Brake: new Vector3(-0.1f, ball, reach), Rest: new Vector3(0.1f, ball, reach));
    }

    /// <summary>The eye of someone in the seat, node space: where a passenger's first-person camera goes.</summary>
    public static Vector3 Eye(SeatAnchor seat) => seat.Pose == SeatPose.Straddle
        ? seat.Hip + new Vector3(0, 0.8f, 0.02f)
        : seat.Hip + CarMeshBuilder.Turned(HumanMeshBuilder.DriverEye(Vector3.Zero, seat.Recline));

    /// <summary>
    /// Where a seat is on a vehicle's drawn rig, in the rig's own frame (the body's pitch, kneel and
    /// roll included): the figure's node goes here.
    /// </summary>
    public static Transform3D FrameOf(Node3D rig, SeatAnchor seat) => rig switch
    {
        CarRig car => car.SeatFrame(seat),
        HeavyRig heavy => heavy.SeatFrame(seat),
        _ => new Transform3D(Basis.Identity, seat.Hip),
    };
}
