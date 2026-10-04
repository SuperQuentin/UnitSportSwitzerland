using Godot;
using static UnitSport.Avatar.An124Layout;

namespace UnitSport.Avatar;

/// <summary>
/// The AN-124's walkable hold, upper deck and cockpit, and its seats (#419), from the numbers the model
/// is drawn with (<see cref="An124Layout"/>). One section (0), the drawn aircraft's node frame (which
/// kneeling lowers, so every box comes down with it). The hold is a drive-through <see cref="CargoBay"/>
/// from the nose ramp to the rear ramp; each ramp is a <see cref="DeckPart.DoorShut"/> while shut (the
/// folded nose ramp, the rear ramp's slope and the rear wall) and a <see cref="DeckPart.DoorStep"/>
/// slope to the ground while open, one for standing and one for kneeling (<see cref="DeckDoors"/>).
/// A 45° ladder on the right wall climbs to the upper deck; the cockpit is ahead of it.
/// </summary>
public static class An124Deck
{
    private const float Wall = 0.1f;

    /// <summary>Deck door bits past the real four: the nose ramp standing / kneeling, the rear ramp standing / kneeling.</summary>
    public const int NoseStand = 4, NoseKneel = 5, RearStand = 6, RearKneel = 7;

    /// <summary>The deck's door bits from the aircraft's four: which of a ramp's two slopes is down depends on the kneeling.</summary>
    public static byte DeckDoors(byte doors)
    {
        bool kneel = (doors >> KneelDoor & 1) != 0;
        int d = doors & 15;
        if ((doors >> NoseDoor & 1) != 0) d |= 1 << (kneel ? NoseKneel : NoseStand);
        if ((doors >> RearDoor & 1) != 0) d |= 1 << (kneel ? RearKneel : RearStand);
        return (byte)d;
    }

    /// <summary>The kneeling button inside, this far ahead of the crew door (its own button is 0.25 ahead).</summary>
    public const float KneelButtonAhead = 1.2f;

    private static float At(float z) => -z;

    private static VehicleDeck? _deck;
    private static SeatAnchor[]? _seats;
    public static VehicleDeck Deck => _deck ??= Build();

    /// <summary>The pilot first (the controls), the copilot, the flight engineer, then the upper deck row by row, left to right.</summary>
    public static SeatAnchor[] Seats => _seats ??= BuildSeats();

    private static SeatAnchor[] BuildSeats()
    {
        var seats = new List<SeatAnchor>
        {
            new(0, AircraftMeshBuilder.Flip(PilotHip), 0.2f, UpperFloorY),
            new(0, AircraftMeshBuilder.Flip(CopilotHip), 0.2f, UpperFloorY),
            // the engineer faces his panel on the right wall (node +X), as the freighter's left bench does
            new(0, AircraftMeshBuilder.Flip(EngineerHip), 0.1f, UpperFloorY) { Yaw = -Mathf.Pi * 0.5f },
        };
        for (int row = 0; row < CabinRows; row++)
            foreach (float x in CabinSeatX)
                seats.Add(new SeatAnchor(0, AircraftMeshBuilder.Flip(new Vector3(x, UpperFloorY + 0.5f, RowZ(row))), 0.2f, UpperFloorY));
        return seats.ToArray();
    }

    /// <summary>Where one stands for seat <paramref name="i"/> (node space): behind a cockpit seat, in the aisle beside a cabin row.</summary>
    public static Vector3? StandSpot(int i)
    {
        var seats = Seats;
        if (i < 0 || i >= seats.Length) return null;
        if (i < 2) return seats[i].Hip with { Y = UpperFloorY + 0.05f, Z = -(PilotHip.Z - 0.85f) };
        if (i == 2) return AircraftMeshBuilder.Flip(new Vector3(-0.45f, UpperFloorY + 0.05f, EngineerHip.Z));
        return seats[i].Hip with { X = 0f, Y = UpperFloorY + 0.05f };
    }

    private static VehicleDeck Build()
    {
        var dk = new DeckBuilder(0f);
        float floor = FloorY, ceiling = HoldCeilingY, inner = HoldHalfWidth, wallX = inner + Wall * 0.5f;
        float front = NoseHingeZ, rear = RearWallZ;
        float ladderIn = LadderX + LadderWidth * 0.5f, ladderOut = LadderX - LadderWidth * 0.5f;

        // the hold's floor, hinge to hinge
        dk.Along(At(front), At(RampHingeZ), floor - 0.15f, floor, inner * 2f + Wall);
        // its ceiling, which is the upper level's floor: one slab, a hole over the ladder
        dk.Along(At(front), At(UpperRearZ), ceiling, UpperFloorY, inner * 2f + Wall);
        dk.Along(At(LadderFootZ), At(rear), ceiling, UpperFloorY, inner * 2f + Wall);
        float rightEdge = -(inner + Wall * 0.5f);
        dk.Along(At(UpperRearZ), At(LadderFootZ), ceiling, UpperFloorY, inner + Wall * 0.5f - ladderIn, (inner + Wall * 0.5f + ladderIn) * 0.5f);
        dk.Along(At(UpperRearZ), At(LadderFootZ), ceiling, UpperFloorY, ladderOut - rightEdge, (ladderOut + rightEdge) * 0.5f);

        // the nose: the folded ramp stands across the front while shut; open, a slope to the ground (standing or kneeling)
        dk.Along(At(front), At(front - 0.3f), floor, ceiling, inner * 2f + Wall, 0f, DeckPart.DoorShut, NoseDoor);
        dk.RampAlong(At(NoseToeZ(false)), 0f, At(front), floor, inner * 2f, 0f, DeckPart.DoorStep, An124Deck.NoseStand);
        dk.RampAlong(At(NoseToeZ(true)), KneelDrop, At(front), floor, inner * 2f, 0f, DeckPart.DoorStep, An124Deck.NoseKneel);
        // the tail: the ramp's slope up into the belly and the rear wall over it while shut; open, a slope down
        dk.RampAlong(At(RampHingeZ), floor, At(RampClosedEndZ), RampTop(RampClosedEndZ), inner * 2f, 0f, DeckPart.DoorShut, RearDoor);
        dk.Along(At(rear), At(rear - 0.12f), RampTop(RampClosedEndZ) + 0.1f, ceiling, inner * 2f + Wall, 0f, DeckPart.DoorShut, RearDoor);
        dk.RampAlong(At(RearToeZ(false)), 0f, At(RampHingeZ), floor, inner * 2f, 0f, DeckPart.DoorStep, RearStand);
        dk.RampAlong(At(RearToeZ(true)), KneelDrop, At(RampHingeZ), floor, inner * 2f, 0f, DeckPart.DoorStep, RearKneel);

        // the side walls, the left one cut by the crew door
        float doorRear = CrewDoorZ - DoorWidth * 0.5f, doorFront = CrewDoorZ + DoorWidth * 0.5f;
        dk.Along(At(front), At(rear), floor, ceiling, Wall, -wallX);
        dk.Along(At(doorRear), At(rear), floor, ceiling, Wall, wallX);
        dk.Along(At(front), At(doorFront), floor, ceiling, Wall, wallX);
        dk.Along(At(doorFront), At(doorRear), floor + DoorHeight, ceiling, Wall, wallX);
        dk.Along(At(doorFront), At(doorRear), floor, floor + DoorHeight, Wall, wallX, DeckPart.DoorShut, CrewDoor);
        float sillOut = OuterX(CrewDoorZ, floor) + 0.1f;
        dk.Along(At(doorFront), At(doorRear), floor - 0.12f, floor, sillOut - inner, (inner + sillOut) * 0.5f, DeckPart.DoorStep, CrewDoor);

        // buttons inside, at a hand's height on the walls: the crew door and the kneeling by it, the
        // visor at the front, the rear ramp at the back (both walls)
        var inL = new Vector3(-1, 0, 0);
        dk.Button(CrewDoor, new Vector3(inner - 0.05f, floor + 1.2f, doorFront + 0.25f), inL);
        dk.Button(KneelDoor, new Vector3(inner - 0.05f, floor + 1.2f, doorFront + KneelButtonAhead), inL);
        // and outside by the door, for whoever stands on airstairs at its sill
        dk.Button(CrewDoor, new Vector3(OuterX(doorFront + 0.3f, floor + 1.2f) + 0.02f, floor + 1.2f, doorFront + 0.3f), -inL);
        foreach (int side in new[] { 1, -1 })
        {
            dk.Button(NoseDoor, new Vector3(side * (inner - 0.05f), floor + 1.2f, front - 0.6f), new Vector3(-side, 0, 0));
            dk.Button(RearDoor, new Vector3(side * (inner - 0.05f), floor + 1.2f, RampHingeZ + 0.6f), new Vector3(-side, 0, 0));
            // outside on the gear fairings' ends, reached from the ground: the front ones the visor, the
            // kneeling (and the crew door on the left), the back ones the rear ramp
            float fx = side * (FairingOutX - 0.3f);
            dk.Button(NoseDoor, new Vector3(fx, OutsideButtonY, FairingFrontZ + 0.02f), Vector3.Back);
            dk.Button(KneelDoor, new Vector3(fx - side * 0.4f, OutsideButtonY, FairingFrontZ + 0.02f), Vector3.Back);
            if (side > 0) dk.Button(CrewDoor, new Vector3(fx - 0.8f, OutsideButtonY, FairingFrontZ + 0.02f), Vector3.Back);
            dk.Button(RearDoor, new Vector3(fx, OutsideButtonY, FairingRearZ - 0.02f), Vector3.Forward);
        }

        // the ladder: a 45° slope from the hold's floor to the upper deck's rear edge, a rail on its open side
        dk.RampAlong(At(LadderFootZ), floor, At(UpperRearZ), UpperFloorY, LadderWidth, LadderX);
        dk.Along(At(UpperRearZ), At(LadderFootZ), UpperFloorY, UpperFloorY + 1.0f, 0.05f, ladderIn + 0.05f);

        // the upper level: its aft wall behind the hole, side walls, ceiling
        float upperRear = LadderFootZ - 0.1f;
        dk.Along(At(upperRear), At(upperRear - 0.1f), UpperFloorY, UpperCeilingY, UpperWallX * 2f + Wall);
        foreach (int side in new[] { 1, -1 })
            dk.Along(At(CabinFrontZ), At(upperRear), UpperFloorY, UpperCeilingY, Wall, side * (UpperWallX + Wall * 0.5f));
        dk.Along(At(CockpitFloorFrontZ), At(upperRear), UpperCeilingY, UpperCeilingY + 0.1f, UpperWallX * 2f + Wall);
        // the cabin's seats: a block per pair of seats, the aisle between
        for (int row = 0; row < CabinRows; row++)
            foreach (int side in new[] { 1, -1 })
                dk.Box(new Vector3(side * 1.2f, UpperFloorY + 0.22f, RowZ(row) - 0.05f), new Vector3(1.3f, 0.44f, 0.5f));

        // the cockpit: its wall with a doorway, the floor on over the visor, side walls in with the nose
        float nose = OuterX(CockpitFloorFrontZ, UpperFloorY + 0.8f) - 0.3f;
        dk.Along(At(CockpitFloorFrontZ), At(front), ceiling, UpperFloorY, nose * 2f + 0.6f);
        foreach (int side in new[] { 1, -1 })
        {
            dk.Along(At(CabinFrontZ + 0.1f), At(CabinFrontZ), UpperFloorY, UpperCeilingY, UpperWallX - 0.45f, side * (UpperWallX + 0.45f) * 0.5f);
            dk.Wall(side * UpperWallX, At(CabinFrontZ), side * nose, At(CockpitFloorFrontZ), UpperFloorY, UpperCeilingY, Wall);
        }
        foreach (var hip in new[] { PilotHip, CopilotHip, EngineerHip })
            dk.Box(new Vector3(hip.X, UpperFloorY + 0.25f, hip.Z), new Vector3(0.52f, 0.5f, 0.55f));
        dk.Box(new Vector3(0, UpperFloorY + 0.3f, PilotHip.Z + 0.5f), new Vector3(0.4f, 0.6f, 0.9f));
        dk.Along(At(CockpitFloorFrontZ), At(PanelZ), UpperFloorY, UpperFloorY + 1.1f, nose * 2f);
        dk.Along(At(CockpitFloorFrontZ + 0.12f), At(CockpitFloorFrontZ), UpperFloorY, UpperCeilingY, nose * 2f + Wall);
        dk.Box(new Vector3(-2.15f, UpperFloorY + 0.7f, EngineerHip.Z), new Vector3(0.5f, 1.4f, 1.4f));

        // the hold vehicles drive through and are carried in (#418): beside the ladder, hinge to hinge
        dk.CargoBay(new Vector3((BayLeftX + BayRightX) * 0.5f, floor + (ceiling - floor) * 0.5f, (RampHingeZ + front) * 0.5f),
            new Vector3(BayLeftX - BayRightX, ceiling - floor, front - RampHingeZ - 0.2f));

        var aboard = new Aabb(new Vector3(-inner - 0.05f, floor - 0.3f, rear), new Vector3(inner * 2f + 0.1f, UpperCeilingY - floor + 0.3f, CockpitFloorFrontZ - rear));
        return dk.Build(0, aboard);
    }
}
