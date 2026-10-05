using UnitSport.Terrain.Format;

namespace UnitSport.Vehicles;

/// <summary>
/// One dormant vehicle: where it stands, which way it faces and what it looks like (#499, and
/// #496 phase 3). <b>Pure data, no Godot</b>, so it is linked into the unit tests and pinned there
/// — the industrial plan's own risk list calls a client/server fleet disagreement "a new
/// consistency surface", and this record is that surface.
///
/// <para>
/// <see cref="KindId"/> is a <c>RideKind</c> as an int on purpose: this file must not reach into
/// <c>src/Player</c>, which pulls Godot in and would put the whole thing out of tier 0.
/// </para>
/// </summary>
public readonly record struct VehicleSlot(
    // The owner that produced it (a tile for a car park, a site key for a yard) and the ordinal
    // within that owner. Together they name the vehicle, so every peer promotes the same slot once.
    string Owner, int Ordinal,
    // LV95, with the ground it stands on.
    double E, double N, double Height,
    // Radians about +Y, 0 = -Z (north), as the tile records store a heading.
    float Yaw,
    int KindId, byte Paint, bool Van,
    // A `TrailerCatalog` code with its load, `(index + 1) | load% << 8`, as `VehicleState.Train`
    // carries it (#70, `trucks-buses`): what a lone `RideKind.Trailer` IS, or the trailer coupled to
    // a truck. 0 = none. A coupled artic is ONE slot with this set, never two slots side by side —
    // the pin angles are part of the parked state and reconstructing them from two poses drifts
    // (#496 phase 3 asked for exactly this).
    int Train = 0,
    // THE TRACTOR'S OWN payload, not the trailer's. These are two different vehicles' loads and a
    // coupled artic carries both independently:
    //   the TRAILER's is inside `Train`'s high byte — `ParkedTrailer` reads `TrailerCatalog.Load(code)`
    //   and never sees this field at all;
    //   this one is the tractor's or the rigid's, `Truck(heavy, Train, Load)` — the MAN TGS's swap
    //   body — and is not in the code.
    // Setting this and expecting the trailer to look loaded fails silently and reads as a physics
    // bug, which is why it is spelled out here.
    float Load = 0.5f)
{
    /// <summary>
    /// The node name the server promotes this slot under. Deterministic, so a second wake of the
    /// same slot finds the vehicle already there instead of spawning another
    /// (the check <c>AfricaTwinEgg</c> makes for its bike).
    ///
    /// <para>
    /// <c>slot</c>, not <c>bay</c>: a car park has bays, a haulier's yard and a dealership forecourt
    /// do not, and this layer serves all three. The name is the wake-once key, so it was free to
    /// settle before the first push and impossible afterwards.
    /// </para>
    /// </summary>
    public string NodeName => $"veh_slot_{Owner}_{Ordinal}";
}

/// <summary>
/// Where dormant vehicles stand, as a <b>pure function of the tile's own bytes</b> — so the server
/// and every client work out the same fleet from the same files and not one byte of it is ever sent.
/// Car parks are the first provider (#499); an industrial site's yard is meant to be the second
/// (#496 phase 3) and needs nothing here but its own <c>ForSite</c>.
/// </summary>
public static class DormantSlots
{
    /// <summary>How full a lot can be. A station car park is nearly full, a rural one nearly empty.</summary>
    public const double MinFill = 0.15, MaxFill = 0.85;

    /// <summary>
    /// The dormant fleet of one tile's car parks, in <c>PARK</c> order.
    ///
    /// <para>
    /// The fill is hashed from the tile, so a whole tile's lots share a mood (a town tile is busy, a
    /// hamlet's is not); whether a given bay is taken is hashed from the bay itself, so adding a
    /// vehicle to one lot never shuffles another. A bay that is not
    /// <see cref="ParkingBay.Occupiable"/> — a loading bay, the one kept free by the entrance, one
    /// with a trolley shelter standing in it — is skipped, which is the whole point of that flag.
    /// </para>
    /// </summary>
    public static void ForParking(
        TileId id, IReadOnlyList<ParkingBay> bays, IReadOnlyList<int> carKinds, List<VehicleSlot> into)
    {
        if (bays.Count == 0 || carKinds.Count == 0) return;
        string owner = $"{id.E}_{id.N}";
        double fill = MinFill + (Hash(Key(id.E, id.N), 0x5EED) & 0xFFFF) / 65535.0 * (MaxFill - MinFill);

        for (int i = 0; i < bays.Count; i++)
        {
            var bay = bays[i];
            if (!bay.Occupiable) continue;

            // the bay's own roll, off its position so it never moves when a neighbour changes
            ulong h = Hash(Key((long)(bay.X * 100), (long)(bay.Z * 100)), 0xB47);
            if ((h & 0xFFFF) / 65535.0 > fill) continue;

            // a parked car faces either way out of its bay, as they do
            float yaw = bay.Heading + ((h >> 17 & 1) == 0 ? 0f : MathF.PI);
            into.Add(new VehicleSlot(owner, i,
                id.MinE + bay.X, id.MaxN - bay.Z, bay.Y, Wrap(yaw),
                carKinds[(int)(h >> 20 & 0xFFFF) % carKinds.Count],
                (byte)(h >> 36 & 7),
                // a van or estate in roughly one bay in eight, and never in a motorcycle bay
                (h >> 40 & 7) == 0 && bay.Kind != ParkingBayKind.Motorcycle));
        }
    }

    private static float Wrap(float a)
    {
        const float tau = MathF.PI * 2;
        a %= tau;
        return a < 0 ? a + tau : a;
    }

    /// <summary>FNV-1a over two longs. Never <see cref="string.GetHashCode()"/>: that is randomised per process.</summary>
    private static ulong Key(long a, long b)
    {
        ulong h = 14695981039346656037UL;
        foreach (long v in (long[])[a, b])
            for (int i = 0; i < 8; i++)
            {
                h ^= (byte)(v >> (i * 8));
                h *= 1099511628211UL;
            }
        return h;
    }

    private static ulong Hash(ulong seed, ulong salt)
    {
        ulong h = seed ^ salt;
        h ^= h >> 33; h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33; h *= 0xC4CEB9FE1A85EC53UL;
        return h ^ (h >> 33);
    }
}
