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
    float Load = 0.5f,
    // Taken away, it comes back (#554): a marina's boats are put back at their places a while after
    // one is taken, once nobody is near. A parked car does not: whoever drove it off has it.
    bool Respawns = false)
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
/// One industrial site's yard: the open ground its fleet stands on (#496 phase 3). The Godot side
/// works it out from <c>BuildingTypes.SiteFor</c> and the building's plan box and hands it over,
/// the same way <see cref="DormantSlots.ForParking"/> is handed a tile's <see cref="ParkingBay"/>s
/// — so this file stays free of Godot and of <c>src/Interiors</c>, and stays in tier 0.
/// </summary>
public readonly record struct SiteYard(
    // The building whose yard it is, as `BuildingKey` text, "E_N_Index". It becomes the slots'
    // owner, so `DormantVehicles.SlotOf` sees a three-part owner here and a two-part one for a car
    // park; its first two parts must be the tile.
    string Owner,
    // The `BuildingType` as an int, for the same reason `VehicleSlot.KindId` is one: that enum
    // lives in src/Interiors, which pulls Godot in.
    int SiteType,
    // Tile-local centre of the strip, X east and Z south, Y the ground it stands on.
    float X, float Y, float Z,
    // Radians about +Y, 0 = -Z (north): the way a vehicle in the yard faces, nose first — square
    // to the building's own wall, because that is how a yard is marked out.
    float Heading,
    // The strip, metres: across the building's wall, and out from it.
    float Width, float Depth);

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

    /// <summary>Water under a marina boat's keel enough to float it, m (#383).</summary>
    public const float MarinaMinDepth = 0.7f;

    /// <summary>How far under the still surface a moored boat's keel sits (its origin), m: speedboat, jetski.</summary>
    public const float SpeedboatDraught = 0.28f, JetskiDraught = 0.23f;

    /// <summary>
    /// The boats moored along a tile's harbour jetties (#554, before it #383's <c>MarinaBoats</c>).
    /// A jetty belongs to the tile its ribbon's middle is in, as its deck does; its boats are
    /// <see cref="Jetty.BoatBerths"/> — already a pure, named choice of one place in three, at most
    /// four — wherever there is <see cref="MarinaMinDepth"/> of water. Each slot stands at its keel:
    /// the still level less the boat's draught, which is exactly the state the boats used to be
    /// placed with.
    ///
    /// <para>
    /// The owner is the tile plus the jetty's own name (<c>E_N_m1a2b3c4d</c>, the berths' id prefix)
    /// and the ordinal is the berth's index along it, so a dry berth dropped here keeps the others'
    /// names. A boat respawns (<see cref="VehicleSlot.Respawns"/>): a harbour is restocked.
    /// </para>
    /// </summary>
    /// <param name="water">The still water level and the bed at a point (LV95), or null where dry or unknown.</param>
    public static void ForMarina(TileId id, IReadOnlyList<Jetty> jetties,
        Func<double, double, (float Level, float Bed)?> water, int speedboatKind, int jetskiKind, List<VehicleSlot> into)
    {
        foreach (var jetty in jetties)
        {
            var (me, mn) = jetty.Ribbon.Middle;
            if (TileId.FromLv95(me, mn) != id) continue;
            var berths = jetty.BoatBerths();
            for (int k = 0; k < berths.Count; k++)
            {
                var b = berths[k];
                if (water(b.E, b.N) is not { } w || w.Level - w.Bed < MarinaMinDepth) continue;
                string owner = $"{id.E}_{id.N}_{b.Id[..b.Id.IndexOf('_')]}";
                float draught = b.Speedboat ? SpeedboatDraught : JetskiDraught;
                // the berth's heading is degrees clockwise from grid north; a yaw turns the other way
                float yaw = -(float)(b.Heading * Math.PI / 180);
                into.Add(new VehicleSlot(owner, k, b.E, b.N, w.Level - draught, Wrap(yaw),
                    b.Speedboat ? speedboatKind : jetskiKind, 0, false, Respawns: true));
            }
        }
    }

    /// <summary>
    /// What stands in an industrial site's yard (#496 phase 3), as rows across the strip. The fleet
    /// is the site's own: a haulier's tractors and artics and the trailers dropped beside them, a
    /// dealership's stock rows, a works' staff cars.
    ///
    /// <para>
    /// Rows run across the building's wall and march out from it, at the pitch the longest thing in
    /// the fleet needs, so a 13.6 m artic and a 4 m hatchback are never laid out on the same grid.
    /// A dealership's rows face one way and are packed tight, because a forecourt is arranged to be
    /// looked at; everything else faces either way and leaves gaps, because a working yard is not.
    /// </para>
    ///
    /// <para>
    /// Every roll comes from the slot's own place in its own yard, never from a running counter, so
    /// one site's fleet never shifts when a neighbour's changes — the same rule
    /// <see cref="ForParking"/> follows, and the reason the ordinal can name the vehicle.
    /// </para>
    /// </summary>
    /// <param name="cars">Ordinary car <c>RideKind</c>s. <paramref name="heavies"/>: tractors and
    /// rigids. <paramref name="trailers"/>: <c>TrailerCatalog</c> codes, already loaded.</param>
    public static void ForSite(TileId id, IReadOnlyList<SiteYard> yards,
        IReadOnlyList<int> cars, IReadOnlyList<int> heavies, IReadOnlyList<int> trailers,
        int trailerKind, List<VehicleSlot> into)
    {
        if (cars.Count == 0) return;
        foreach (var yard in yards)
        {
            // how full, and of what: the site's own roll, so two depots on one tile differ
            ulong yh = Hash(Key((long)(yard.X * 100), (long)(yard.Z * 100)), 0x5173);
            var site = (SiteFleet)yard.SiteType;
            bool heavy = site is SiteFleet.Depot or SiteFleet.Warehouse && heavies.Count > 0 && trailers.Count > 0;
            // a forecourt is packed and tidy; a working yard is not
            bool neat = site == SiteFleet.Dealership;
            float fill = neat ? 0.92f : site switch
            {
                SiteFleet.Depot => 0.72f,
                SiteFleet.Mechanic => 0.60f,
                SiteFleet.Warehouse => 0.48f,
                _ => 0.40f,
            };

            // the grid: a bay as wide and deep as the longest thing that parks in it
            float cell = heavy ? 4.2f : 3.2f;
            float row = heavy ? 16.5f : 6.0f;
            int across = (int)(yard.Width / cell);
            int deep = (int)(yard.Depth / row);
            if (across < 1 || deep < 1) continue;

            float cos = MathF.Cos(yard.Heading), sin = MathF.Sin(yard.Heading);
            int ordinal = 0;
            for (int d = 0; d < deep; d++)
                for (int a = 0; a < across; a++, ordinal++)
                {
                    // along the wall, and out from it, in the yard's own frame
                    float u = (a + 0.5f) * cell - yard.Width / 2;
                    float v = (d + 0.5f) * row - yard.Depth / 2;
                    // Yaw 0 faces -Z, so the way out of the wall is (-sin, -cos) and the way along
                    // it is (cos, -sin). +v is further from the building, +u is along its face.
                    float x = yard.X + u * cos - v * sin;
                    float z = yard.Z - u * sin - v * cos;

                    ulong h = Hash(Key((long)(x * 100), (long)(z * 100)), 0xFA17);
                    if ((h & 0xFFFF) / 65535.0 > fill) continue;

                    // a forecourt's stock all faces the way it is meant to be seen from
                    float yaw = neat ? yard.Heading : yard.Heading + ((h >> 17 & 1) == 0 ? 0f : MathF.PI);
                    var slot = new VehicleSlot(yard.Owner, ordinal,
                        id.MinE + x, id.MaxN - z, yard.Y, Wrap(yaw),
                        cars[(int)(h >> 20 & 0xFFFF) % cars.Count],
                        (byte)(neat ? (h >> 36 & 7) : h >> 36 & 3),   // a forecourt is brighter
                        !neat && (h >> 40 & 7) == 0);

                    if (heavy) slot = Heavy(slot, site, h, heavies, trailers, trailerKind);
                    into.Add(slot);
                }
        }
    }

    /// <summary>
    /// Turns a yard slot into part of the fleet where the site has one: a coupled artic, a lone
    /// trailer dropped on its legs, a bare tractor or a rigid. A warehouse is mostly trailers backed
    /// at the dock with a van among them; a depot is the whole mix, because that is what a haulier's
    /// yard holds between runs.
    /// </summary>
    private static VehicleSlot Heavy(VehicleSlot slot, SiteFleet site, ulong h,
        IReadOnlyList<int> heavies, IReadOnlyList<int> trailers, int trailerKind)
    {
        int roll = (int)(h >> 44 & 0xFF);
        // a trailer's own identity, and how loaded it is, in one code (TrailerCatalog)
        int code = trailers[(int)(h >> 24 & 0xFFFF) % trailers.Count];
        int tractor = heavies[(int)(h >> 28 & 0xFFFF) % heavies.Count];
        float load = (h >> 52 & 0xFF) / 255f;

        // a warehouse's yard is trailers at the dock; a depot's is tractors and whole trains too
        int lone = site == SiteFleet.Warehouse ? 190 : 105;
        if (roll < lone) return slot with { KindId = trailerKind, Train = code, Load = 0f };
        if (roll < lone + 70) return slot with { KindId = tractor, Train = code, Load = load };
        if (roll < lone + 115) return slot with { KindId = tractor, Train = 0, Load = load };
        return slot;   // a car among them: somebody drove to work
    }

    /// <summary>
    /// The site types this file needs to tell apart, as the ints <see cref="SiteYard.SiteType"/>
    /// carries. The names and numbers are <c>Interiors.BuildingType</c>'s and must not drift from
    /// it; the unit tests pin both sides.
    /// </summary>
    private enum SiteFleet { Warehouse = 3, Factory = 4, Depot = 5, Mechanic = 6, Dealership = 7 }

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
