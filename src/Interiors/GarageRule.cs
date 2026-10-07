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

    /// <summary>Share of the blocks whose box takes a ramp that get one, so garages stay a small part of all the blocks (#694: about 3 to 5 %).</summary>
    public const double Share = 0.72;

    /// <summary>The same for shops under flats.</summary>
    public const double MixedRollShare = 0.72;



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
    public const double MixedShare = 0.8;

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

    /// <summary>
    /// How far a garage door stands from a door on another wall, m (on the front wall the keep-out of the ramp rules); was the lane, a stairwell either side of it and
    /// the flats' wall between, so the ramp's column never meets a stairwell's (the stairwell is built
    /// for the door in front of it, and slides to the end of the block when the sliver beside it is thin).
    /// </summary>
    public const float StairClear = 5.0f;

    /// <summary>The flat floor behind the door before the ramp tips down, m.</summary>
    public const float RampApron = 1.0f;

    /// <summary>
    /// Clear run past the ramp's foot, m, to turn into the aisle: no bay stands in it (the car park
    /// leaves the bays in front of the foot out), so a block needs this much behind the foot, not a row.
    /// </summary>
    public const float RampTurn = 4.0f;

    /// <summary>How far from the front wall the ramp reaches its foot, m, in a block whose storeys are <paramref name="storeyHeight"/>.</summary>
    public static float RampFoot(float storeyHeight) => RampApron + RampProfile.Length(storeyHeight);

    /// <summary>The shallowest block a ramp fits: its foot and the room to turn there.</summary>
    public static float RampDepth(float storeyHeight) => RampFoot(storeyHeight) + RampTurn;

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



    // ---- ramp first (#694) ---------------------------------------------------------------------
    // The garage is decided from the plan box and the road alone: no front-door count, no car park to
    // wait for. Two shapes, chosen per block: a ramp square to the front wall (needs depth) or one that
    // runs along the facade (needs width). A single stairwell stands beside it.

    /// <summary>Which ramp a block's box takes: none, square to the front wall, or along the facade.</summary>
    public enum RampKind { None, Square, Along }

    /// <summary>Wall left between the end of the facade and the lane, m.</summary>
    public const float EndMargin = 0.35f;
    /// <summary>Wall between the lane and a stairwell, m.</summary>
    public const float LaneGap = 0.4f;

    /// <summary>The stairwell's column wall to wall: its stair, the passage beside it, the lift.</summary>
    public static float WellWidth(bool lift) => StairWidth + WalkWidth + (lift ? LiftColumn : 0);

    /// <summary>Whether a block of <paramref name="above"/> storeys and a basement has a lift (the generator's rule).</summary>
    public static bool LiftIn(int above) => above + 1 >= 3;

    /// <summary>The narrowest box a square ramp fits beside its stairwell, m (and the car park strip needs <see cref="StripWidth"/>).</summary>
    public static float SquareWidth(bool lift) => Math.Max(Math.Max(StripWidth, EndMargin + RampWidth + LaneGap + WellWidth(lift)),
        EndMargin + RampWidth / 2 + 2.2f + MinBays * BayWidth + 1.3f);

    /// <summary>A garage has room for at least this many bays of this width (a car park strip loses the ramp's mouth: its lane and 2.2 m each side).</summary>
    public const int MinBays = 4;
    public const float BayWidth = 2.5f;

    /// <summary>
    /// Along the facade: the lane is a band as deep as the stairwell up to its half landing (so the corridor behind
    /// the landing runs straight on behind it), 3.6 m wide against that wall; the door stands 2.2 m in from the
    /// band start, and the descent starts AlongTurnIn from it (a car turns in off the door).
    /// </summary>
    public const float AlongDoorX = 2.2f;
    /// <summary>From the end wall to the descent's top: the door's column and the turn off it, m.</summary>
    public const float AlongTurnIn = 6.6f;
    /// <summary>The car park hall past the ramp's foot is at least this long along the facade (a row of bays and the aisle in front of them).</summary>
    public const float AlongParkLength = 9.5f;
    /// <summary>The shallowest box an along-the-facade ramp takes: deep enough that the stairwell keeps its own depth (a row of four bays down the hall's far wall).</summary>
    public const float AlongMinDepth = 13.0f;

    /// <summary>Where the descent ends along the facade, from the end wall, m.</summary>
    public static float AlongFoot(float storeyHeight) => EndMargin + AlongTurnIn + RampProfile.Length(storeyHeight);
    /// <summary>How far from the end wall nothing but the ramp and its car park stands, m.</summary>
    public static float AlongKeepOut(float storeyHeight) => AlongFoot(storeyHeight) + AlongParkLength;
    /// <summary>The narrowest box an along-the-facade ramp fits: its band, the car park and a stairwell beyond.</summary>
    public static float AlongWidth(float storeyHeight, bool lift) => AlongKeepOut(storeyHeight) + LaneGap + WellWidth(lift) + EndMargin;

    /// <summary>
    /// Which ramp a block of <paramref name="above"/> storeys (and the basement the garage gives it) and a
    /// plan box <paramref name="width"/> (along the front wall) by <paramref name="depth"/> takes. Square: the
    /// car park strip behind the stairwell and the depth for the ramp (the #558 rule's geometry). Along: the width
    /// for the band, the car park hall and the stairwell, and depth for a row of bays.
    /// </summary>
    public static RampKind KindOf(int above, float width, float depth, float storeyHeight)
    {
        bool lift = LiftIn(above);
        if (width >= SquareWidth(lift) && HasRamp(depth, storeyHeight)
            && BehindStairwell(width, depth, above + 1, storeyHeight) >= StripDepth) return RampKind.Square;
        if (width >= AlongWidth(storeyHeight, lift) && depth >= AlongMinDepth
            && BehindStairwell(width, depth, above + 1, storeyHeight) >= 5.0f) return RampKind.Along;
        return RampKind.None;
    }

    /// <summary>
    /// The ramp a block of flats (or shops under flats) gets: from the box and the roll, nothing else (the road is
    /// <see cref="GarageLink"/>'s). <see cref="RampKind.None"/> for anything that is not such a block.
    /// </summary>
    public static RampKind RampFor(string key, BuildingType type, int above, float width, float depth, float storeyHeight) =>
        type is BuildingType.Apartments or BuildingType.MixedUse && Rolls(key, type == BuildingType.MixedUse)
            ? KindOf(above, width, depth, storeyHeight) : RampKind.None;

    /// <summary>Whether a block of flats gets an underground garage door: <see cref="RampFor"/>.</summary>
    public static bool Wanted(string key, BuildingType type, int above, float width, float depth, float storeyHeight) =>
        RampFor(key, type, above, width, depth, storeyHeight) != RampKind.None;
}
