using Godot;

namespace UnitSport.Player;

/// <summary>
/// Every player in the tree (the local one, remote copies, race NPCs) with where it is and how it
/// moves, taken once per physics tick and shared by every caller that tick (#221). Replaces a
/// <c>GetNodesInGroup(FootPlayer.Group)</c> scan (a new Godot array, plus LINQ) per caller per tick:
/// traffic, combat, and later the slipstream and overlap checks of every rider.
/// </summary>
public static class PlayerSnapshot
{
    /// <summary>One player as it stood when the tick's snapshot was taken.</summary>
    /// <param name="Vel"><see cref="FootPlayer.WorldVelocity"/>: a remote's replicated velocity.</param>
    public readonly record struct Sample(FootPlayer Player, Vector3 Pos, Vector3 Vel, RideKind Ride);

    private static readonly List<Sample> Samples = new();
    private static ulong _tick = ulong.MaxValue;
    private static readonly StringName Group = FootPlayer.Group;

    /// <summary>
    /// This physics tick's players. Built by the first caller of the tick, so positions are those
    /// at that moment: a player stepped later in the same tick shows where it was (one tick, at
    /// most). Read it from physics code only; never keep the list past the call.
    /// </summary>
    /// <summary>The origin moved (#185): the next caller takes positions in the new world space.</summary>
    public static void Forget() => _tick = ulong.MaxValue;

    public static List<Sample> Of(SceneTree tree)
    {
        ulong tick = Engine.GetPhysicsFrames();
        if (tick == _tick) return Samples;
        _tick = tick;
        Samples.Clear();
        foreach (var node in tree.GetNodesInGroup(Group))
            if (node is FootPlayer p)
                Samples.Add(new Sample(p, p.GlobalPosition, p.WorldVelocity, p.Ride));
        return Samples;
    }
}
