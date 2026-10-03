using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// The A320's walkable cabin and its seats (#416), from the same numbers the model is drawn with
/// (<see cref="A320Layout"/>): the floor at the sills, the walls with their four door holes (each
/// with its leaf while shut and a sill outside while open), the ceiling and the overhead bins, the
/// seat rows as blocks, the galleys and lavatories, the cockpit's wall with its doorway, the two
/// pilots' seats, the panel and the pedestal. One section (0), the drawn aircraft's node frame.
/// </summary>
public static class A320Deck
{
    private const float Wall = 0.1f;

    /// <summary>Authored z to a <see cref="DeckBuilder"/> station (its cg is 0: z = −station).</summary>
    private static float At(float z) => -z;

    private static VehicleDeck? _deck;
    private static SeatAnchor[]? _seats;

    public static VehicleDeck Deck => _deck ??= Build();

    /// <summary>The captain's seat first (the controls), the first officer's, then the cabin row by row, A to F.</summary>
    public static SeatAnchor[] Seats => _seats ??= BuildSeats();

    private static SeatAnchor[] BuildSeats()
    {
        var seats = new List<SeatAnchor>
        {
            new(0, AircraftMeshBuilder.Flip(A320Layout.CaptainHip), 0.2f, A320Layout.FloorY),
            new(0, AircraftMeshBuilder.Flip(A320Layout.FirstOfficerHip), 0.2f, A320Layout.FloorY),
        };
        for (int row = 0; row < A320Layout.Rows; row++)
            foreach (float x in A320Layout.SeatX)
                seats.Add(new SeatAnchor(0, AircraftMeshBuilder.Flip(new Vector3(x, A320Layout.FloorY + 0.5f, A320Layout.RowZ(row) - 0.3f)),
                    0.28f, A320Layout.FloorY));
        return seats.ToArray();
    }

    private static VehicleDeck Build()
    {
        var dk = new DeckBuilder(0f);
        float floor = A320Layout.FloorY, ceiling = A320Layout.CeilingY;
        float front = A320Layout.CockpitFrontZ, rear = A320Layout.RearBulkheadZ;
        float inner = A320Layout.InnerHalfWidthShoulder;
        float wallX = inner + Wall * 0.5f;

        // the floor, the ceiling, the nose's and the rear bulkhead's walls
        dk.Along(At(front), At(rear), floor - 0.12f, floor, inner * 2f + Wall);
        dk.Along(At(front), At(rear), ceiling, ceiling + 0.1f, inner * 2f + Wall);
        dk.Along(At(front + 0.12f), At(front), floor, ceiling, inner * 2f);
        dk.Along(At(rear), At(rear - 0.12f), floor, ceiling, inner * 2f);

        // the side walls, cut by the doors; each door's leaf while it is shut, a sill outside while it is open
        foreach (int side in new[] { 1, -1 })
        {
            var cuts = new List<(float From, float To, int Door)>();
            for (int i = 0; i < A320Layout.DoorCount; i++)
            {
                var (z, s) = A320Layout.Door(i);
                if (s == side) cuts.Add((z + A320Layout.DoorWidth * 0.5f, z - A320Layout.DoorWidth * 0.5f, i));
            }
            float from = front;
            foreach (var (doorFront, doorRear, door) in cuts.OrderByDescending(c => c.From))
            {
                dk.Along(At(from), At(doorFront), floor, ceiling, Wall, side * wallX);
                // over the door, and the door itself
                dk.Along(At(doorFront), At(doorRear), floor + A320Layout.DoorHeight, ceiling, Wall, side * wallX);
                dk.Along(At(doorFront), At(doorRear), floor, floor + A320Layout.DoorHeight, Wall, side * wallX, DeckPart.DoorShut, door);
                // the sill, out through the skin: what an airstair (#417) or a jet bridge (#424) docks to
                float sillOut = A320Layout.HalfWidth + 0.15f;
                dk.Along(At(doorFront), At(doorRear), floor - 0.12f, floor, sillOut - inner, side * (inner + sillOut) * 0.5f,
                    DeckPart.DoorStep, door);
                // the buttons: inside ahead of the door, and outside on the skin
                float buttonZ = doorFront + 0.25f;
                dk.Button(door, new Vector3(side * (inner - 0.05f), floor + 1.2f, buttonZ), new Vector3(-side, 0, 0));
                dk.Button(door, new Vector3(side * (A320Layout.HalfWidth + 0.02f), floor + 1.2f, buttonZ), new Vector3(side, 0, 0));
                from = doorRear;
            }
            dk.Along(At(from), At(rear), floor, ceiling, Wall, side * wallX);

            // the overhead bins over the seats, clear of the aisle and of a standing head there
            dk.Along(At(A320Layout.FirstRowZ + 0.4f), At(A320Layout.RowZ(A320Layout.Rows - 1) - 0.6f), ceiling - 0.5f, ceiling,
                inner - 0.95f, side * (0.95f + inner) * 0.5f);
        }

        // the seat rows: three seats a block each side, from the floor to the backs' tops
        for (int row = 0; row < A320Layout.Rows; row++)
        {
            float z = A320Layout.RowZ(row);
            foreach (int side in new[] { 1, -1 })
            {
                float x0 = A320Layout.AisleHalfWidth, x1 = A320Layout.SeatX[0] + A320Layout.SeatWidth * 0.5f;
                dk.Box(new Vector3(side * (x0 + x1) * 0.5f, floor + A320Layout.SeatBackHeight * 0.5f, z - 0.3f),
                    new Vector3(x1 - x0, A320Layout.SeatBackHeight, 0.55f));
            }
            if (row % 2 == 0) { dk.Hold(0.3f, At(z - 0.5f)); dk.Hold(-0.3f, At(z - 0.5f)); }
        }

        // the galleys and the lavatories, solid
        dk.Along(At(A320Layout.ForwardLavTo), At(A320Layout.ForwardLavFrom), floor, ceiling, inner - 0.35f, (inner + 0.35f) * 0.5f);
        dk.Along(At(A320Layout.ForwardGalleyTo), At(A320Layout.ForwardGalleyFrom), floor, ceiling, inner - 0.35f, -(inner + 0.35f) * 0.5f);
        foreach (int side in new[] { 1, -1 })
        {
            dk.Along(At(A320Layout.AftLavTo), At(A320Layout.AftLavFrom), floor, ceiling, inner - 0.45f, side * (inner + 0.45f) * 0.5f);
            dk.Along(At(A320Layout.AftGalleyTo), At(A320Layout.AftGalleyFrom), floor, floor + 1.05f, inner - 0.2f, side * (inner + 0.2f) * 0.5f);
        }

        // the cockpit: its wall with a doorway in the middle, the seats, the pedestal and the panel
        float half = A320Layout.CockpitDoorWidth * 0.5f;
        float cw = A320Layout.CockpitWallZ;
        foreach (int side in new[] { 1, -1 })
            dk.Along(At(cw + 0.06f), At(cw - 0.06f), floor, ceiling, inner - half, side * (inner + half) * 0.5f);
        dk.Along(At(cw + 0.06f), At(cw - 0.06f), floor + 2.0f, ceiling, half * 2f);
        foreach (var hip in new[] { A320Layout.CaptainHip, A320Layout.FirstOfficerHip })
            dk.Box(new Vector3(hip.X, floor + 0.25f, hip.Z - 0.1f), new Vector3(0.5f, 0.5f, 0.55f));
        dk.Box(new Vector3(0, floor + 0.4f, 16.0f), new Vector3(0.36f, 0.8f, 1.0f));
        dk.Along(At(front), At(16.45f), floor, floor + 1.25f, inner * 2f);

        var aboard = new Aabb(new Vector3(-inner, floor - 0.3f, rear), new Vector3(inner * 2f, ceiling - floor + 0.3f, front - rear));
        return dk.Build(0, aboard);
    }
}
