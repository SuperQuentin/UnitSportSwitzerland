namespace UnitSport.Terrain.Format;

/// <summary>Who a lane of an approach is for (#353).</summary>
public enum ApproachLaneKind : byte { Car = 0, Bike = 1 }

/// <summary>
/// One lane of an approach (#353), as the network stage built it. Distances are metres before
/// the record's stop line, along the road; offsets are metres right of the original approach
/// lane's centre (<see cref="RoadApproach.LaneCentre"/>), as the driver sees it.
/// </summary>
/// <param name="Offset">Where the lane's centre lies at the stop line.</param>
/// <param name="FullFrom">From here to the stop line the lane is there at full width (the end of its taper).</param>
/// <param name="TaperFrom">
/// Where it starts: a lane that carries the original lane on moves off it here (0 offset), a pocket
/// opens here beside the lane it branches from (equal to <paramref name="FullFrom"/> where it
/// appears at once beside a closing hatch).
/// </param>
/// <param name="Moves">The movements its arrows show (all the approach has where it has no arrows).</param>
/// <param name="StopBehind">Its cars or bikes stop this far behind the stop line: a bike box's depth for the car lanes behind it, minus an advanced bike stop line's lead.</param>
public readonly record struct ApproachLane(float Offset, float FullFrom, float TaperFrom, SignalMoves Moves, ApproachLaneKind Kind, float StopBehind = 0f)
{
    /// <summary>Bytes per lane in the section: four f32 and two u8.</summary>
    public const int RecordSize = 18;
}

/// <summary>
/// The lanes of one approach to a junction (#353, section <c>LANE</c>): every approach with a
/// left-turn pocket (#123, signalised or not), a right-turn pocket (#348) or traffic lights
/// (#348). Tile-local like <see cref="RoadSignal.Stops"/>, in the junction's home tile.
/// The game (<c>LaneGraph</c>) ties it to the road end nearest its stop point along the arm,
/// as it does a signal's approaches. Rules: docs/notes/tools/turn-lanes.md, traffic-signals.md.
/// </summary>
public sealed class RoadApproach
{
    /// <summary>The stop line's middle (traffic lights, as <see cref="RoadSignal.Stops"/>), else the middle of the approach lanes at the pocket's stop bar.</summary>
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }

    /// <summary>The arm's outward heading, as <see cref="SignalArm.Heading"/> (radians, LV95: east 0, north π/2).</summary>
    public float Heading { get; init; }

    /// <summary>Index of the junction's record in the tile's <see cref="RoadTile.Signals"/>, -1 without lights; and the plan arm.</summary>
    public short Signal { get; init; } = -1;
    public byte SignalArm { get; init; }

    /// <summary>Turns forbidden from this approach (OSM turn restrictions, #347).</summary>
    public SignalMoves Banned { get; init; }

    /// <summary>
    /// The original approach lane's centre, metres right of the road's centre line (the segment
    /// the game drives along): the lanes' offsets are from it.
    /// </summary>
    public float LaneCentre { get; init; }

    /// <summary>Left to right as the driver sees them, car and bike lanes.</summary>
    public List<ApproachLane> Lanes { get; init; } = new();

    /// <summary>1 is the first; a reader skips a section of a version it does not know.</summary>
    public const byte SectionVersion = 1;

    public static void Write(BinaryWriter w, IReadOnlyList<RoadApproach> approaches)
    {
        w.Write((uint)approaches.Count);
        w.Write(SectionVersion);
        foreach (var a in approaches)
        {
            w.Write(a.X); w.Write(a.Y); w.Write(a.Z);
            w.Write(a.Heading);
            w.Write(a.Signal);
            w.Write(a.SignalArm);
            w.Write((byte)a.Banned);
            w.Write(a.LaneCentre);
            w.Write(checked((byte)a.Lanes.Count));
            foreach (var l in a.Lanes)
            {
                w.Write(l.Offset); w.Write(l.FullFrom); w.Write(l.TaperFrom); w.Write(l.StopBehind);
                w.Write((byte)l.Moves);
                w.Write((byte)l.Kind);
            }
        }
    }

    /// <summary>Null when the section is of a version this reader does not know (the caller skips it).</summary>
    public static List<RoadApproach>? Read(BinaryReader r)
    {
        uint n = r.ReadUInt32();
        byte version = r.ReadByte();
        if (version is < 1 or > SectionVersion) return null;
        var list = new List<RoadApproach>((int)n);
        for (uint k = 0; k < n; k++)
        {
            float x = r.ReadSingle(), y = r.ReadSingle(), z = r.ReadSingle(), heading = r.ReadSingle();
            short signal = r.ReadInt16();
            byte arm = r.ReadByte();
            var banned = (SignalMoves)r.ReadByte();
            float centre = r.ReadSingle();
            int count = r.ReadByte();
            var lanes = new List<ApproachLane>(count);
            for (int i = 0; i < count; i++)
            {
                float offset = r.ReadSingle(), full = r.ReadSingle(), taper = r.ReadSingle(), behind = r.ReadSingle();
                lanes.Add(new ApproachLane(offset, full, taper, (SignalMoves)r.ReadByte(), (ApproachLaneKind)r.ReadByte(), behind));
            }
            list.Add(new RoadApproach
            {
                X = x, Y = y, Z = z, Heading = heading, Signal = signal, SignalArm = arm, Banned = banned, LaneCentre = centre, Lanes = lanes,
            });
        }
        return list;
    }
}
