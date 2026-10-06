using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// When a block of flats gets an underground garage door (#558), as one pure predicate that the
/// footprint (which cuts the door in the facade) and the interior generator (which plans the
/// basement behind it) both read, so they can never disagree about whether a block has a car park.
///
/// <para>
/// The stairwell numbers are the generator's own (<c>InteriorGenerator</c> aliases these), because
/// the car park is the strip of the basement behind the stairwells and its depth is whatever is
/// left of the building after them. No Godot: tier 0 (<c>GarageRuleTests</c>).
/// </para>
/// </summary>
public static class GarageRule
{
    /// <summary>The door: a car's mouth, narrower than a loading bay (4.5 m) and lower (a van, not a lorry).</summary>
    public const float Width = 3.0f, Height = 2.4f;

    /// <summary>Share of blocks that qualify by size and basement and still get one, so garages stay rare.</summary>
    public const double Share = 0.4;

    /// <summary>A city block with shops under its flats qualifies too, but rarer and only with a bigger frontage.</summary>
    public const double MixedRollShare = 0.2;
    public const int MixedMinFrontDoors = 4;

    /// <summary>A block with fewer front doors is too small a development for an underground garage.</summary>
    public const int MinFrontDoors = 3;

    /// <summary>A car park strip behind the stairwells is this deep (two rows and an aisle) in a block at least this wide.</summary>
    public const float StripDepth = 9.5f, StripWidth = 12f;

    // ---- the stairwell, shared with InteriorGenerator.Apartments ---------------------------------
    public const float FrontLanding = 2.0f;
    /// <summary>The building's corridors, and so the back landing they run off (#576).</summary>
    public const float CorridorWidth = 2.0f;
    public const float StairLane = 1.15f, StairEye = 0.15f;
    public const float StairWidth = 2 * StairLane + StairEye;
    public const float MidLanding = 1.3f;
    public const float StairRiser = 0.175f;
    public const float LiftColumn = 2.0f;
    public const float MinFlatSide = 3.4f;
    public const float WalkWidth = 1.2f;
    /// <summary>Share of tall commercial blocks that are shops under flats.</summary>
    public const double MixedShare = 0.6;

    /// <summary>
    /// Whether a building is planned as a block of flats or a city block with shops under flats
    /// (#557); not a bank. <paramref name="area"/> is the plan box's, m².
    /// </summary>
    public static BuildingType BlockType(string key, float area, BuildingKind kind, int storeys, bool bank)
    {
        if (bank) return BuildingType.None;
        return kind switch
        {
            BuildingKind.Apartment => BuildingType.Apartments,
            BuildingKind.Other when storeys > 3 || area >= 200 => BuildingType.Apartments,
            BuildingKind.Commercial when storeys >= 3 && Fnv.Unit(key + "|mixed") < MixedShare => BuildingType.MixedUse,
            _ => BuildingType.None,
        };
    }

    /// <summary>
    /// Whether the block has a basement, from its own seed: nearly every Swiss block of flats has
    /// one, and any of some size certainly does (the shelter, the law said).
    /// </summary>
    public static int Basement(string key, bool mixed, int above, float area)
    {
        double chance = above >= 4 || area >= 400 ? 1.0 : mixed ? 0.9 : 0.85;
        return new Random(Fnv.Hash(key + "|cellar")).NextDouble() < chance ? 1 : 0;
    }

    /// <summary>
    /// How deep the strip behind the stairwells is, m (0 when the block takes none): the building's
    /// depth less the stairwell's (front landing, stair, half landing, back landing), the way the
    /// generator lays it out. <paramref name="width"/> runs along the front wall.
    /// </summary>
    public static float BehindStairwell(float width, float depth, int floors, float storeyHeight)
    {
        bool stairs = floors > 1;
        float run = 0, mid = 0;
        bool passage = stairs;
        bool lift = floors >= 3;
        if (stairs)
        {
            int steps = (int)MathF.Ceiling(storeyHeight / 2 / StairRiser);
            float tread = 0.28f;
            if (FrontLanding + tread * steps + MidLanding + CorridorWidth > depth)
            {
                tread = (depth - FrontLanding - MidLanding - CorridorWidth) / steps;
                if (tread < 0.22f) return 0;
            }
            run = tread * steps;
            mid = MidLanding;
            if (width < StairWidth + WalkWidth + (lift ? LiftColumn : 0) + MinFlatSide) passage = false;
        }
        // nothing hangs off a back landing that is not there: no strip
        if (!passage && stairs) return 0;
        float sd = FrontLanding + run + mid + CorridorWidth;
        if (depth - sd < 5.0f) sd = depth;
        return depth - sd;
    }

    /// <summary>
    /// Whether the basement has a car park the generator will plan as a strip across the back (the
    /// deep-block case; the biggest-region fallback is not counted, so this is the safe side).
    /// </summary>
    public static bool HasCarPark(string key, bool mixed, int above, float width, float depth, float storeyHeight)
    {
        int below = Basement(key, mixed, above, width * depth);
        if (below == 0 || width < StripWidth) return false;
        // a block too narrow for the passage has no back landing and so no strip: BehindStairwell
        // gives it the stairwell's full depth, which is less than a strip
        return BehindStairwell(width, depth, above + below, storeyHeight) >= StripDepth;
    }

    // ---- the ramp (#558, PR 2) -----------------------------------------------------------------

    /// <summary>The ramp's lane, wall to wall: a car 1.9 m wide, mirrors out, with a hand each side.</summary>
    public const float RampWidth = 3.6f;

    /// <summary>The flat floor behind the door before the ramp tips down, m.</summary>
    public const float RampApron = 2.0f;

    /// <summary>Clear run past the ramp's foot to turn into the aisle, m, and the depth of a row of bays beyond it.</summary>
    public const float RampTurn = 3.5f, BayRow = 5.1f;

    /// <summary>How far from the front wall the ramp reaches its foot, m, in a block whose storeys are <paramref name="storeyHeight"/>.</summary>
    public static float RampFoot(float storeyHeight) => RampApron + RampProfile.Length(storeyHeight);

    /// <summary>The shallowest block a ramp fits: its foot, the room to turn there, and a row of bays behind.</summary>
    public static float RampDepth(float storeyHeight) => RampFoot(storeyHeight) + RampTurn + BayRow;

    /// <summary>Whether a block is deep enough for the ramp down to its car park.</summary>
    public static bool HasRamp(float depth, float storeyHeight) => depth >= RampDepth(storeyHeight);


    /// <summary>
    /// Every block rolls a garage, for a live check on a synthetic course (<c>fixture:garage</c>), which
    /// cannot lean on a hash of the building's key. Set by the fixture source, on the server and every
    /// client alike; false on the real map and in a generated world.
    /// </summary>
    public static bool AlwaysRolls { get; set; }

    /// <summary>Whether the key rolls a garage, the same on every peer.</summary>
    public static bool Rolls(string key, bool mixed = false) =>
        AlwaysRolls || Fnv.Unit(key + "|garage") < (mixed ? MixedRollShare : Share);

    /// <summary>
    /// Whether a block of flats gets an underground garage door: flats (not shops under them), at
    /// least <see cref="MinFrontDoors"/> front doors, a basement car park and the roll. Flats with
    /// shops under them (<see cref="BuildingType.MixedUse"/>) qualify too: at least
    /// <see cref="MixedMinFrontDoors"/> front doors and a <see cref="MixedRollShare"/> roll.
    /// <paramref name="width"/> is the plan box's length along the front wall.
    /// </summary>
    public static bool Wanted(string key, BuildingType type, int above, float width, float depth,
        float storeyHeight, int frontDoors) =>
        type switch
        {
            BuildingType.Apartments => frontDoors >= MinFrontDoors
                && HasCarPark(key, false, above, width, depth, storeyHeight) && HasRamp(depth, storeyHeight) && Rolls(key),
            BuildingType.MixedUse => frontDoors >= MixedMinFrontDoors
                && HasCarPark(key, true, above, width, depth, storeyHeight) && HasRamp(depth, storeyHeight) && Rolls(key, mixed: true),
            _ => false,
        };
}
