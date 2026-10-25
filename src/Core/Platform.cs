using Godot;

namespace UnitSport.Core;

/// <summary>
/// What the device can do, decided once (#63). A phone runs the client only: it cannot start
/// processes (hosting, renderer or VR relaunch, ffmpeg, yt-dlp), cannot patch its own install, and
/// has no SDL. <c>--mobile</c> makes a desktop build behave the same, to check the phone UI without
/// a phone. A standalone headset (#1112) is such a device too, played in VR instead of by touch.
/// Rule note: docs/notes/core/platform.md.
/// </summary>
public static class Platform
{
    /// <summary>Android (or iOS), or a desktop run with <c>--mobile</c> or <c>--quest</c>.</summary>
    public static readonly bool IsMobile =
        OS.GetName() is "Android" or "iOS" || CmdArgs.Has("--mobile") || CmdArgs.Has("--quest");

    /// <summary>
    /// A standalone headset (Meta Quest, #1112): the Android export with the <c>quest</c> feature
    /// (<c>tools/quest.sh</c>). It starts in VR and stays there; there is no touch screen and no monitor.
    /// <c>--quest</c> makes a desktop run behave the same (with <c>--xrsim</c>, to check it without a headset).
    /// </summary>
    public static readonly bool IsStandaloneVr = OS.HasFeature("quest") || CmdArgs.Has("--quest");

    /// <summary>A touch screen is the input: a phone (or <c>--mobile</c>), not a standalone headset.</summary>
    public static bool IsTouch => IsMobile && !IsStandaloneVr;

    /// <summary>Whether this device can start other processes (hosting, relaunches, bundled tools).</summary>
    public static bool CanSpawnProcesses => !IsMobile;
}
