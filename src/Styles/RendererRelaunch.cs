using System.Collections.Generic;
using System.Linq;
using Godot;

namespace UnitSport.Styles;

/// <summary>
/// Realistic+ needs the Forward+ renderer, and Godot reads the rendering method only at startup:
/// the game starts again with <c>--rendering-method forward_plus</c> (docs/notes/styles/realistic-plus.md).
/// Asked from the settings when the player picks it, or done once at boot for a saved
/// Realistic+ launched on Mobile. The relaunch carries <see cref="Flag"/>, and a run with it
/// never relaunches again: a machine without Forward+ just draws Realistic+ as Realistic−.
/// </summary>
public static class RendererRelaunch
{
    public const string Flag = "--forward-plus-relaunch";

    /// <summary>Whether this run is itself a relaunch (and so must not relaunch).</summary>
    public static bool IsRelaunch => System.Array.IndexOf(OS.GetCmdlineUserArgs(), Flag) >= 0;

    /// <summary>Whether a saved style wants Forward+ and this run is not on it.</summary>
    public static bool Wanted =>
        StyleKit.NeedsForwardPlus(StyleKit.Style) && !StyleKit.OnForwardPlus && !IsRelaunch
        && DisplayServer.GetName() != "headless";

    /// <summary>Starts the game again on Forward+, with this run's own arguments. True if it started.</summary>
    public static bool Relaunch()
    {
        var args = new List<string> { "--rendering-method", "forward_plus" };
        // run from the editor binary (`godot --path .`): it has to be told the project again
        if (OS.HasFeature("editor")) args.AddRange(new[] { "--path", ProjectSettings.GlobalizePath("res://") });
        args.Add("--");
        args.AddRange(OS.GetCmdlineUserArgs().Where(a => a != Flag));
        args.Add(Flag);
        int pid = OS.CreateProcess(OS.GetExecutablePath(), args.ToArray());
        GD.Print($"[style] relaunching on Forward+ for {StyleKit.Style}: pid {pid}");
        if (pid <= 0) GD.PushError("[style] could not start the game again");
        return pid > 0;
    }
}
