using Godot;

namespace UnitSport.Core;

/// <summary>
/// What the device can do, decided once (#63). A phone runs the client only: it cannot start
/// processes (hosting, renderer or VR relaunch, ffmpeg, yt-dlp), cannot patch its own install, and
/// has no SDL. <c>--mobile</c> makes a desktop build behave the same, to check the phone UI without
/// a phone. Rule note: docs/notes/core/platform.md.
/// </summary>
public static class Platform
{
    /// <summary>Android (or iOS), or a desktop run with <c>--mobile</c>.</summary>
    public static readonly bool IsMobile =
        OS.GetName() is "Android" or "iOS" || CmdArgs.Has("--mobile");

    /// <summary>Whether this device can start other processes (hosting, relaunches, bundled tools).</summary>
    public static bool CanSpawnProcesses => !IsMobile;
}
