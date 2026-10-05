using Godot;
using static UnitSport.Avatar.FreighterLayout;

namespace UnitSport.Avatar;

/// <summary>
/// The military cargo plane's walkable hold, flight deck and seats (#420), from the numbers the model
/// is drawn with (<see cref="FreighterLayout"/>): the hold's floor and lining walls with the crew door
/// and the two para doors (each a leaf while shut, a step out while open: the crew door folds down
/// into stairs to the ground), the ramp (shut a slope up into the belly, open a slope down to the
/// ground that a car drives up), the troop benches along both walls, the stairs up to the flight deck,
/// its bulkhead, the pilots' seats, the pedestal, the panel, the engineer's station and the crew bunk.
/// The hold between the benches is a <see cref="CargoBay"/> (#418). One section (0), the drawn
/// aircraft's node frame.
/// </summary>
public static class FreighterDeck
{
    private const float Wall = 0.1f;

    /// <summary>Authored z to a <see cref="DeckBuilder"/> station (its cg is 0: z = −station).</summary>
    private static float At(float z) => -z;

    private static VehicleDeck? _deck;
    private static SeatAnchor[]? _seats;

    public static VehicleDeck Deck => _deck ??= Build();

    /// <summary>The captain's seat first (the controls), the first officer's, then the troop seats row by row, left then right.</summary>
    public static SeatAnchor[] Seats => _seats ??= BuildSeats();

    /// <summary>The flight deck's side walls: straight from the hold's front to the barrel's end, then in with the nose.</summary>
    private const float DeckWallX = 2.05f;
    private static float NoseWallX => OuterX(FlightDeckFrontZ, FlightDeckY + 0.8f) - 0.25f;

    private static float TroopHipX => HoldHalfWidth - 0.26f;
    private static float TroopStandX => HoldHalfWidth - BenchDepth - 0.35f;

    private static SeatAnchor[] BuildSeats()
    {
        var seats = new List<SeatAnchor>
        {
            new(0, AircraftMeshBuilder.Flip(CaptainHip), 0.2f, FlightDeckY),
            new(0, AircraftMeshBuilder.Flip(FirstOfficerHip), 0.2f, FlightDeckY),
        };
        // facing across the hold: the left bench (node −X) looks to +X, the right one to −X
        for (int row = 0; row < TroopRows; row++)
            foreach (int side in new[] { 1, -1 })
                seats.Add(new SeatAnchor(0, AircraftMeshBuilder.Flip(new Vector3(side * TroopHipX, FloorY + BenchHeight + 0.05f, TroopZ(row))), 0.1f, FloorY)
                {
                    Yaw = -side * Mathf.Pi * 0.5f,
                });
        return seats.ToArray();
    }

    /// <summary>Where one stands for seat <paramref name="i"/> (node space): behind a pilot's seat, in front of a troop seat.</summary>
    public static Vector3? StandSpot(int i)
    {
        var seats = Seats;
        if (i < 0 || i >= seats.Length) return null;
        if (i < 2) return seats[i].Hip with { Y = FlightDeckY + 0.05f, Z = -(CaptainHip.Z - 0.8f) };
        int row = (i - 2) / 2, side = (i - 2) % 2 == 0 ? 1 : -1;
        return AircraftMeshBuilder.Flip(new Vector3(side * TroopStandX, FloorY + 0.05f, TroopZ(row)));
    }

    private static VehicleDeck Build()
    {
        var dk = new DeckBuilder(0f);
        float floor = FloorY, ceiling = HoldCeilingY, inner = HoldHalfWidth;
        float wallX = inner + Wall * 0.5f;
        float front = HoldFrontZ, rear = RearWallZ;

        // the hold's floor up to the ramp's hinge, the ceiling over everything
        dk.Along(At(front), At(RampHingeZ), floor - 0.12f, floor, inner * 2f + Wall);
        dk.Along(At(FlightDeckFrontZ), At(rear), ceiling, ceiling + 0.1f, DeckWallX * 2f + Wall);
        // the ramp: open, a slope from the ground up to the hinge; shut, a slope up aft into the belly
        dk.RampAlong(At(RampToeZ), 0f, At(RampHingeZ), floor, inner * 2f, 0f, DeckPart.DoorStep, RampDownDeck);
        // in the air it stops level with the floor (a drop, #420)
        dk.Along(At(RampHingeZ), At(RampHingeZ - RampLength), floor - 0.12f, floor, inner * 2f, 0f, DeckPart.DoorStep, RampLevelDeck);
        dk.RampAlong(At(RampHingeZ), floor, At(RampClosedEndZ), RampTop(RampClosedEndZ), inner * 2f, 0f, DeckPart.DoorShut, RampDoor);
        // the rear wall over the ramp's end
        float wallBottom = RampTop(RampClosedEndZ) + 0.1f;
        dk.Along(At(rear), At(rear - 0.12f), wallBottom, ceiling, inner * 2f + Wall);

        // the side walls, cut by the doors
        foreach (int side in new[] { 1, -1 })
        {
            var cuts = new List<int> { side > 0 ? ParaDoorL : ParaDoorR };
            if (side > 0) cuts.Add(CrewDoor);
            cuts.Sort((a, b) => Door(a).Z.CompareTo(Door(b).Z));
            float from = rear;
            foreach (int door in cuts)
            {
                float z = Door(door).Z, doorRear = z - DoorWidth * 0.5f, doorFront = z + DoorWidth * 0.5f;
                dk.Along(At(doorRear), At(from), floor, ceiling, Wall, side * wallX);
                dk.Along(At(doorFront), At(doorRear), floor + DoorHeight, ceiling, Wall, side * wallX);
                dk.Along(At(doorFront), At(doorRear), floor, floor + DoorHeight, Wall, side * wallX, DeckPart.DoorShut, door);
                float sillOut = OuterX(z, floor) + 0.1f;
                if (door == CrewDoor)
                {
                    // its leaf folds down into stairs: a slope from the sill to the ground
                    float foot = OuterX(z, floor) + Mathf.Sqrt(DoorHeight * DoorHeight - floor * floor);
                    dk.RampAcross(At(z), side * foot, 0f, side * inner, floor, DoorWidth - 0.05f, DeckPart.DoorStep, door);
                }
                else
                    dk.Along(At(doorFront), At(doorRear), floor - 0.12f, floor, sillOut - inner, side * (inner + sillOut) * 0.5f, DeckPart.DoorStep, door);
                // the buttons: inside ahead of the door, outside on the skin at a hand's height from the ground
                float buttonZ = doorFront + 0.25f;
                dk.Button(door, new Vector3(side * (inner - 0.05f), floor + 1.2f, buttonZ), new Vector3(-side, 0, 0));
                dk.Button(door, new Vector3(side * (OuterX(buttonZ, 1.45f) + 0.02f), 1.45f, buttonZ), new Vector3(side, 0, 0));
                from = doorFront;
            }
            dk.Along(At(front), At(from), floor, ceiling, Wall, side * wallX);

            // the ramp's buttons: inside on the wall by its hinge, outside on the flank by the tail
            dk.Button(RampDoor, new Vector3(side * (inner - 0.05f), floor + 1.2f, RampHingeZ + 0.5f), new Vector3(-side, 0, 0));
            dk.Button(RampDoor, new Vector3(side * (OuterX(RampHingeZ + 0.5f, 1.45f) + 0.02f), 1.45f, RampHingeZ + 0.5f), new Vector3(side, 0, 0));

            // the troop bench: a block along the wall, seat height
            float z0 = TroopZ(0) + TroopPitch * 0.5f, z1 = TroopZ(TroopRows - 1) - TroopPitch * 0.5f;
            dk.Along(At(z0), At(z1), floor, floor + BenchHeight, BenchDepth, side * (inner - BenchDepth * 0.5f));
            for (float z = z0 - 1f; z > z1; z -= 2.2f) dk.Hold(side * (inner - BenchDepth - 0.05f), At(z));

            // the flight deck's walls: straight, then in with the nose
            dk.Along(At(BarrelFront), At(front), FlightDeckY, ceiling, Wall, side * DeckWallX);
            dk.Wall(side * DeckWallX, At(BarrelFront), side * NoseWallX, At(FlightDeckFrontZ), FlightDeckY, ceiling, Wall);
        }

        // the stairs up to the flight deck, flush with its floor's edge, and the floor itself (solid to the hold's)
        dk.RampAlong(At(StairFootZ), floor, At(front), FlightDeckY, StairWidth);
        dk.Along(At(FlightDeckFrontZ), At(front), floor, FlightDeckY, DeckWallX * 2f);
        // the bulkhead with its doorway over the stairs
        float half = StairWidth * 0.5f;
        foreach (int side in new[] { 1, -1 })
            dk.Along(At(front + 0.08f), At(front), FlightDeckY, ceiling, DeckWallX - half, side * (DeckWallX + half) * 0.5f);

        // the flight deck: seats, pedestal, panel, the nose wall; the engineer's station (right) and the bunk (left)
        foreach (var hip in new[] { CaptainHip, FirstOfficerHip })
            dk.Box(new Vector3(hip.X, FlightDeckY + 0.25f, hip.Z - 0.05f), new Vector3(0.52f, 0.5f, 0.55f));
        dk.Box(new Vector3(0, FlightDeckY + 0.3f, 9.75f), new Vector3(0.38f, 0.6f, 0.9f));
        dk.Along(At(FlightDeckFrontZ), At(PanelZ - 0.05f), FlightDeckY, FlightDeckY + 1.15f, NoseWallX * 2f);
        dk.Along(At(FlightDeckFrontZ + 0.12f), At(FlightDeckFrontZ), FlightDeckY, ceiling, NoseWallX * 2f + Wall);
        dk.Box(new Vector3(-1.45f, FlightDeckY + 0.75f, 7.6f), new Vector3(0.6f, 1.5f, 1.6f));
        dk.Box(new Vector3(1.4f, FlightDeckY + 0.28f, 7.2f), new Vector3(0.7f, 0.56f, 1.9f));

        // the hold vehicles are carried in (#418): between the benches, from the ramp's hinge to the stairs
        dk.CargoBay(new Vector3(0, floor + 1.25f, (RampHingeZ + StairFootZ) * 0.5f), new Vector3(BayWidth, 2.5f, StairFootZ - RampHingeZ - 0.2f));

        var aboard = new Aabb(new Vector3(-DeckWallX, floor - 0.3f, rear), new Vector3(DeckWallX * 2f, ceiling - floor + 0.3f, FlightDeckFrontZ - rear));
        return dk.Build(0, aboard);
    }
}
