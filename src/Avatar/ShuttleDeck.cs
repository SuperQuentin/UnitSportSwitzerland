using Godot;
using static UnitSport.Avatar.ShuttleLayout;

namespace UnitSport.Avatar;

/// <summary>
/// The space shuttle's walkable crew module (#1493), from the numbers the model is drawn with
/// (<see cref="ShuttleLayout"/>, the hull's inside from <see cref="ShuttleMeshBuilder.XAt"/>): the mid deck
/// (its floor, the forward lockers, three seats) under the flight deck (its floor with the interdeck opening
/// on the right, the aft stations, four seats, the console, the panel and the overhead), a steep ladder up
/// through the opening (a 59° slope: the walk has no step-up), the aft bulkhead, and the hull's inside as
/// walls in bands that follow its section, so a body floating up into a corner stays inside the drawn skin.
/// The side hatch is not a way to walk: it is 3.45 m up the hull and a metre round, so E at it inside gets out
/// and E outside gets in (<c>FootPlayer.TryShuttleHatch</c>). One section (0), the drawn orbiter's node frame.
/// </summary>
public static class ShuttleDeck
{
    private const float Wall = 0.1f;

    /// <summary>Authored z to a <see cref="DeckBuilder"/> station (its cg is 0: z = −station).</summary>
    private static float At(float z) => -z;

    /// <summary>The flight deck's floor slab is this thick (its underside is the mid deck's ceiling).</summary>
    private const float Slab = 0.1f;

    /// <summary>The forward face of the mid deck's lockers, and the flight deck's forward wall (the panel, the windscreen over it).</summary>
    public const float LockerFaceZ = MidDeckFrontZ - 0.5f, FlightFrontZ = 23.85f;

    /// <summary>Where one stands inside to get out by the hatch (authored): on the mid deck's floor by it.</summary>
    public static readonly Vector3 HatchInside = new(1.9f, MidDeckY + 0.05f, HatchZ);

    private static VehicleDeck? _deck;
    private static SeatAnchor[]? _seats;

    public static VehicleDeck Deck => _deck ??= Build();

    /// <summary>The commander's seat first (the controls), the pilot's, the two specialists', the mid deck's three.</summary>
    public static SeatAnchor[] Seats => _seats ??= BuildSeats();

    private static SeatAnchor[] BuildSeats()
    {
        var seats = new List<SeatAnchor>
        {
            new(0, AircraftMeshBuilder.Flip(CommanderHip), AirlinerRig.PilotRecline, FlightDeckY),
            new(0, AircraftMeshBuilder.Flip(PilotHip), AirlinerRig.PilotRecline, FlightDeckY),
        };
        foreach (var hip in SpecialistHips)
            seats.Add(new SeatAnchor(0, AircraftMeshBuilder.Flip(hip), AirlinerRig.PilotRecline, FlightDeckY));
        foreach (var hip in MidDeckHips)
            seats.Add(new SeatAnchor(0, AircraftMeshBuilder.Flip(hip), AirlinerRig.PilotRecline, MidDeckY));
        return seats.ToArray();
    }

    /// <summary>
    /// Where one stands for seat <paramref name="i"/> (node space): behind a pilot's seat, beside a specialist's
    /// (the left one's on the left, the right one's ahead of the opening), behind a mid deck seat.
    /// </summary>
    public static Vector3? StandSpot(int i)
    {
        Vector3 at = i switch
        {
            0 => new Vector3(CommanderHip.X, FlightDeckY + 0.05f, CommanderHip.Z - 0.75f),
            1 => new Vector3(PilotHip.X, FlightDeckY + 0.05f, PilotHip.Z - 0.75f),
            2 => new Vector3(SpecialistHips[0].X + 0.65f, FlightDeckY + 0.05f, SpecialistHips[0].Z + 0.05f),
            3 => new Vector3(SpecialistHips[1].X - 0.65f, FlightDeckY + 0.05f, LadderTopZ + 0.3f),
            >= 4 and <= 6 => new Vector3(MidDeckHips[i - 4].X, MidDeckY + 0.05f, MidDeckHips[i - 4].Z - 0.65f),
            _ => new Vector3(float.NaN, 0, 0),
        };
        return float.IsNaN(at.X) ? null : AircraftMeshBuilder.Flip(at);
    }

    /// <summary>The hull's inside half width at <paramref name="z"/> and height <paramref name="y"/> (the skin's inner face).</summary>
    public static float InnerX(float z, float y) => ShuttleMeshBuilder.XAt(z, y, ShuttleLayout.Skin);

    private static VehicleDeck Build()
    {
        var dk = new DeckBuilder(0f);
        float rear = CabinRearZ, md = MidDeckY, fd = FlightDeckY;
        float wide = HalfWidth * 2f;
        float ceiling = TopY - 0.25f;

        // the aft bulkhead, both decks
        dk.Along(At(rear), At(rear - 0.12f), md - 0.2f, TopY + 0.3f, wide);

        // ---- the mid deck: its floor, the lockers forward, its three seats ----
        dk.Along(At(LockerFaceZ), At(rear), md - 0.12f, md, wide);
        dk.Along(At(MidDeckFrontZ), At(LockerFaceZ), md, fd - Slab, wide);
        foreach (var hip in MidDeckHips) Seat(dk, hip, md);

        // ---- the flight deck's floor: whole ahead of the opening, either side of it behind ----
        dk.Along(At(FlightFrontZ + 0.2f), At(LadderTopZ), fd - Slab, fd, wide);
        float inner = HalfWidth - LadderInX;   // from the opening's inboard side to the left wall
        dk.Along(At(LadderTopZ), At(rear), fd - Slab, fd, inner, (HalfWidth + LadderInX) * 0.5f);
        float outer = LadderOutX + HalfWidth;  // from the right wall to the opening's outboard side
        dk.Along(At(LadderTopZ), At(rear), fd - Slab, fd, outer, (LadderOutX - HalfWidth) * 0.5f);
        // a rail along the opening's inboard side, open at its forward end where the ladder tops out
        dk.Along(At(LadderTopZ - 0.3f), At(rear), fd, fd + 1.0f, 0.05f, LadderInX + 0.025f);

        // ---- the ladder: a steep slope from the mid deck's floor (aft) up to the flight deck's (forward), flush ----
        float ladderX = (LadderInX + LadderOutX) * 0.5f, ladderW = LadderInX - LadderOutX;
        dk.RampAlong(At(LadderFootZ + 0.3f), md, At(LadderTopZ), fd, ladderW, ladderX);
        dk.Hold(ladderX, At(LadderFootZ + 0.3f));

        // ---- the flight deck: the aft stations, the seats, the console, the panel and the windscreen, the overhead ----
        dk.Box(new Vector3(0, fd + 0.85f, AftStationZ - 0.15f), new Vector3(2.0f, 1.7f, 0.3f));
        Seat(dk, CommanderHip, fd);
        Seat(dk, PilotHip, fd);
        foreach (var hip in SpecialistHips) Seat(dk, hip, fd);
        dk.Box(new Vector3(0, fd + 0.3f, (PedestalZ0 + PedestalZ1) * 0.5f), new Vector3(PedestalHalf * 2f, 0.6f, PedestalZ1 - PedestalZ0));
        dk.Along(At(FlightFrontZ + 0.3f), At(FlightFrontZ), fd, TopY + 0.3f, wide);
        dk.Box(new Vector3(0, OverheadY + 0.06f, OverheadZ), new Vector3(0.9f, 0.12f, 0.9f));
        // the cabin's roof: the crown's inside, step by step forward
        for (float z = rear; z < FlightFrontZ - 0.01f; z += 0.5f)
        {
            float z1 = Mathf.Min(z + 0.5f, FlightFrontZ);
            float under = Mathf.Min(ShuttleMeshBuilder.TopAt(z), ShuttleMeshBuilder.TopAt(z1)) - 0.2f;
            dk.Along(At(z1), At(z), Mathf.Min(under, ceiling + 0.25f), TopY + 0.4f, wide);
        }

        // ---- the hull's inside: walls in bands of height that follow the section, both decks ----
        HullWalls(dk, rear, LockerFaceZ, md, fd - Slab, 3);
        HullWalls(dk, rear, FlightFrontZ, fd, TopY + 0.1f, 6);

        var aboard = new Aabb(new Vector3(-HalfWidth, md - 0.3f, rear), new Vector3(wide, TopY + 0.3f - (md - 0.3f), FlightFrontZ - rear));
        return dk.Build(0, aboard);
    }

    /// <summary>A seat as two blocks: the seat to the hip, the back behind it to the headrest.</summary>
    private static void Seat(DeckBuilder dk, Vector3 hip, float floor)
    {
        dk.Box(new Vector3(hip.X, (floor + hip.Y) * 0.5f, hip.Z + 0.05f), new Vector3(0.5f, hip.Y - floor, 0.55f));
        dk.Box(new Vector3(hip.X, (hip.Y + hip.Y + 0.85f) * 0.5f, hip.Z - 0.2f), new Vector3(0.5f, 0.85f, 0.14f));
    }

    /// <summary>
    /// The side walls from <paramref name="z0"/> to <paramref name="z1"/>, <paramref name="y0"/> to <paramref name="y1"/>, as
    /// <paramref name="bands"/> bands of height in half metre steps along, each wall's face where the hull's inside is narrowest
    /// in its band and step (under the chine at its bottom, over it at its top, the nose closing forward).
    /// </summary>
    private static void HullWalls(DeckBuilder dk, float z0, float z1, float y0, float y1, int bands)
    {
        float band = (y1 - y0) / bands;
        for (float z = z0; z < z1 - 0.01f; z += 0.5f)
        {
            float za = z, zb = Mathf.Min(z + 0.5f, z1);
            for (int k = 0; k < bands; k++)
            {
                float ya = y0 + k * band, yb = ya + band;
                float w = Mathf.Min(Mathf.Min(InnerX(za, ya), InnerX(za, yb)), Mathf.Min(InnerX(zb, ya), InnerX(zb, yb)));
                // above the crown's inside: the roof boxes close it
                if (w < 0.3f) continue;
                foreach (int side in new[] { 1, -1 })
                    dk.Along(At(zb), At(za), ya, yb, Wall, side * (w + Wall * 0.5f));
            }
        }
    }
}
