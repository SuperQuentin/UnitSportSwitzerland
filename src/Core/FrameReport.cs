using Godot;

namespace UnitSport.Core;

/// <summary>
/// The frame a run's world was built in, said once in its log: <c>[frame] earth|flat</c> by the world that decided it
/// (<see cref="ClientWorld"/>, <see cref="ServerWorld"/>, <see cref="TestWorld"/>), or <c>[frame] none</c> at exit by a
/// process that built no world (a tool, a viewer, a probe on the bare shell). <c>TEST_FRAME=earth tools/test.sh</c>
/// (#1683, #1710: <c>guard_frame_check</c> in <c>tools/lib/guard.sh</c>) fails every row whose log does not state the
/// asked frame, so a world that silently fell back to flat never reads as a pass.
/// </summary>
public static class FrameReport
{
    /// <summary>What was said, or null before any world decided.</summary>
    public static string? Said { get; private set; }

    /// <summary>The world in this frame: printed the first time only (a world rebuilt after a leave says nothing new).</summary>
    public static void Say(bool earth, string? why = null)
    {
        if (Said != null) return;
        Said = earth ? "earth" : "flat";
        GD.Print($"[frame] {Said}{(why != null ? $" ({why})" : "")}");
    }

    /// <summary>No world of its own, in either frame (a swarm, a tool): said in place of a world's frame.</summary>
    public static void SayNone(string why)
    {
        if (Said != null) return;
        Said = "none";
        GD.Print($"[frame] none ({why})");
    }

    /// <summary>At exit (<see cref="Main"/>): a process that never built a world says so, rather than nothing.</summary>
    public static void AtExit() => SayNone("no world was built");
}
