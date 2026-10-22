namespace UnitSport.Player;

/// <summary>What the player is currently travelling as. Replicated, so it must stay stable.</summary>
public enum RideKind
{
    OnFoot = 0,
    RoadBike = 1,
    Skis = 2,
    // the flying things (Flight.cs); appended, never reordered, because this travels as an int
    Wingsuit = 3,
    Parachute = 4,
    Paraglider = 5,
    Helicopter = 6,
    Plane = 7,
    // 8..63 are cars: CarCatalog.All[kind - CarCatalog.First]. The catalog is append-only.
    // 64..95 are motorbikes: MotorbikeCatalog.All[kind - MotorbikeCatalog.First], append-only too;
    // entries 32 onwards continue at 129..192 (MotorbikeCatalog.First2, #410).
    // 96..119 are trucks and buses (and the pickup, 101, #463): HeavyCatalog.All[kind - HeavyCatalog.First], append-only too.
    /// <summary>
    /// Not a mount: a trailer standing in the world on its own (<c>Vehicles.VehicleState.Train</c>
    /// says which). Nobody rides it; a truck backs under it and couples.
    /// </summary>
    Trailer = 120,
    // 121.. are boats (#302): BoatCatalog.All[kind - BoatCatalog.First], append-only; the steamer (#303) is 123.
    Jetski = 121,
    Speedboat = 122,
    /// <summary>The CGN Belle Époque paddle steamer (#303), walkable.</summary>
    Steamer = 123,
    /// <summary>Play as a feral pigeon (#217, <c>Pigeon</c>). Not a boat: a new boat must skip 124.</summary>
    Pigeon = 124,
    /// <summary>The Airbus A320 (#414, #416): an <c>Airliner</c>, walkable.</summary>
    A320 = 125,
    /// <summary>A mobile airstairs truck (#417): <c>Airstairs</c>, docks to aircraft doors.</summary>
    Airstairs = 126,
    /// <summary>The military cargo plane (#420, the Battle Royale's model): an <c>Airliner</c>, walkable, a ramp and a hold.</summary>
    Freighter = 127,
    /// <summary>The Antonov AN-124 Ruslan (#419): an <c>Airliner</c>, walkable, a visor, two ramps, kneeling, a drive-through hold.</summary>
    An124 = 128,
    // 129..192 are motorbikes again (the second range, MotorbikeCatalog.First2).
    /// <summary>A counterbalance forklift (#583): a <c>Forklift</c>, a mast that lifts pallets.</summary>
    Forklift = 193,
    /// <summary>A tracked excavator (#611): an <c>Excavator</c>, a slewing house and a three-joint arm.</summary>
    Excavator = 194,
    /// <summary>An articulated wheel loader (#612): a <c>WheelLoader</c>, frame steering, a lift arm and a bucket.</summary>
    WheelLoader = 195,
    /// <summary>A 2.7 t mini excavator (#614): an <c>Excavator</c> at the mini's size, with a dozer blade.</summary>
    MiniExcavator = 196,
    /// <summary>A compact tandem roller (#614): a <c>CompactRoller</c>, frame steering and vibrating drums.</summary>
    CompactRoller = 197,
    /// <summary>A telehandler (#614): a <c>Telehandler</c>, a telescopic boom with forks and three steering modes.</summary>
    Telehandler = 198,
    /// <summary>A wheel loader with a fork carriage instead of its bucket (#615): a <c>WheelLoader</c> that lifts pallets.</summary>
    WheelLoaderForks = 199,
    /// <summary>A tracked mini dumper (#614): a <c>MiniDumper</c>, a skip that tips forward.</summary>
    MiniDumper = 200,
    /// <summary>The flying saucer (#861): a <c>Ufo</c>, hover flight, landing legs, a ramp and a tractor beam.</summary>
    Ufo = 201,
    /// <summary>A snowboard (#922): a <c>Snowboard</c>, on the ski model like the skis.</summary>
    Snowboard = 202,
    /// <summary>An electric bike (#1159): a <c>Bicycle</c> on <c>BicycleCatalog.MoustacheSamedi</c>.
    /// Every catalogue bicycle is its own kind; ask <c>BicycleCatalog.IsBicycle</c>, not <c>== RoadBike</c>.</summary>
    EBike = 203,
    /// <summary>The same e-bike with a low frame (#1159): <c>BicycleCatalog.MoustacheSamediOpen</c>.</summary>
    EBikeOpen = 204,
    // The next other mount is 205.
}
