using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A pinsetter's deck cycle (#1323) as a pure function of the time since it started, so every peer
/// shows the same machine from one start time and nothing is sent while it runs. What a bowler sees
/// through the masking's opening:
/// <list type="bullet">
/// <item><see cref="Kind.Pick"/> (after a first ball that left pins): the sweep drops to guard,
/// the table comes down and lifts the standing pins, the sweep pushes the deadwood into the pit and
/// comes back, the table sets the pins down on their spots and goes up, the sweep rises.</item>
/// <item><see cref="Kind.Reset"/> (after a strike or a second ball): the sweep drops and clears the
/// whole deck, the table comes down with a new rack of ten.</item>
/// </list>
/// Heights are in the lane's frame (<see cref="LaneGeometry"/>); the sweep's z runs from its guard
/// spot before the head pin to the pit.
/// </summary>
public static class PinsetterCycle
{
    public enum Kind : byte { Pick, Reset }

    /// <summary>What the table carries: nothing, the pins it picked up, or a new rack.</summary>
    public enum Carry : byte { None, Standing, Rack }

    /// <summary>The machine at one moment.</summary>
    /// <param name="TableY">The table's underside.</param>
    /// <param name="SweepY">The sweep's bottom edge.</param>
    /// <param name="SweepZ">The sweep's face, along the lane.</param>
    /// <param name="Carry">What hangs from the table now.</param>
    /// <param name="Placed">Whether the table has set its pins down (they stand on the deck again).</param>
    /// <param name="Done">The cycle is over: everything at rest.</param>
    public readonly record struct Pose(float TableY, float SweepY, float SweepZ, Carry Carry, bool Placed, bool Done);

    /// <summary>A gripped pin's bottom sits this far under the table's underside: its neck and head are in it.</summary>
    public const float Grip = 0.29f;
    /// <summary>Picked pins are lifted till their bottoms are this high: clear of the deadwood the sweep pushes.</summary>
    const float LiftedBottom = 0.22f;

    // the steps' ends, in seconds from the start
    const float SweepDown = 0.6f;
    const float PickDown = 1.6f, PickGrip = 1.9f, PickUp = 2.7f, PickBack = 4.2f, PickForth = 5.4f, PickSet = 6.4f, PickRest = 7.2f, PickEnd = 7.8f;
    const float ResetBack = 2.1f, ResetForth = 3.3f, ResetSet = 4.5f, ResetRest = 5.3f, ResetEnd = 5.9f;

    public static float Length(Kind kind) => kind == Kind.Pick ? PickEnd : ResetEnd;

    /// <summary>The table's underside at rest: the pins hanging from it hidden behind the masking.</summary>
    public static float TableRest(LaneGeometry g) => g.MaskingBottom + Grip + 0.08f;
    /// <summary>The table's underside when its pins stand on the deck.</summary>
    public static float TableDown => LaneGeometry.DeckTop + Grip;
    public static float SweepRest(LaneGeometry g) => g.MaskingBottom + 0.05f;
    /// <summary>Where the sweep stands guard: in front of the head pin.</summary>
    public static float SweepGuard(LaneGeometry g) => g.HeadPin - 0.35f * g.Scale;
    /// <summary>How far back the sweep goes: over the pit's edge.</summary>
    public static float SweepBack(LaneGeometry g) => g.LaneEnd + 0.25f * g.Scale;

    /// <summary>The idle machine: table up and empty, sweep up at guard.</summary>
    public static Pose Rest(LaneGeometry g) => new(TableRest(g), SweepRest(g), SweepGuard(g), Carry.None, true, true);

    public static Pose At(LaneGeometry g, Kind kind, float t)
    {
        float rest = TableRest(g), down = TableDown, lifted = LiftedBottom + Grip;
        float sUp = SweepRest(g), sDown = LaneGeometry.DeckTop + 0.005f, guard = SweepGuard(g), back = SweepBack(g);
        float sweepY = t < SweepDown ? Mathf.Lerp(sUp, sDown, Ease(t / SweepDown)) : sDown;
        float sweepZ = guard;
        if (kind == Kind.Pick)
        {
            if (t >= PickEnd) return Rest(g);
            float table = rest;
            var carry = Carry.None;
            bool placed = false;
            if (t < PickDown) table = Mathf.Lerp(rest, down, Ease(Mathf.Max(0, t - SweepDown) / (PickDown - SweepDown)));
            else if (t < PickGrip) table = down;
            else if (t < PickForth) { table = Mathf.Lerp(down, lifted, Ease((t - PickGrip) / (PickUp - PickGrip))); carry = Carry.Standing; }
            else if (t < PickSet) { table = Mathf.Lerp(lifted, down, Ease((t - PickForth) / (PickSet - PickForth))); carry = Carry.Standing; }
            else { table = Mathf.Lerp(down, rest, Ease((t - PickSet) / (PickRest - PickSet))); placed = true; }
            if (t >= PickUp && t < PickBack) sweepZ = Mathf.Lerp(guard, back, Ease((t - PickUp) / (PickBack - PickUp)));
            else if (t >= PickBack && t < PickForth) sweepZ = Mathf.Lerp(back, guard, Ease((t - PickBack) / (PickForth - PickBack)));
            if (t >= PickRest) sweepY = Mathf.Lerp(sDown, sUp, Ease((t - PickRest) / (PickEnd - PickRest)));
            return new Pose(table, sweepY, sweepZ, carry, placed, false);
        }
        else
        {
            if (t >= ResetEnd) return Rest(g);
            float table = rest;
            var carry = Carry.Rack;
            bool placed = false;
            if (t >= SweepDown && t < ResetBack) sweepZ = Mathf.Lerp(guard, back, Ease((t - SweepDown) / (ResetBack - SweepDown)));
            else if (t >= ResetBack && t < ResetForth) sweepZ = Mathf.Lerp(back, guard, Ease((t - ResetBack) / (ResetForth - ResetBack)));
            if (t >= ResetForth && t < ResetSet) table = Mathf.Lerp(rest, down, Ease((t - ResetForth) / (ResetSet - ResetForth)));
            else if (t >= ResetSet) { table = Mathf.Lerp(down, rest, Ease((t - ResetSet) / (ResetRest - ResetSet))); carry = Carry.None; placed = true; }
            if (t >= ResetRest) sweepY = Mathf.Lerp(sDown, sUp, Ease((t - ResetRest) / (ResetEnd - ResetRest)));
            return new Pose(table, sweepY, sweepZ, carry, placed, false);
        }
    }

    /// <summary>Smoothstep: a machine starts and stops, it does not jump.</summary>
    static float Ease(float x)
    {
        x = Mathf.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}
