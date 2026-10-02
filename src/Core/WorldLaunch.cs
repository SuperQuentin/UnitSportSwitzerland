using Godot;

namespace UnitSport.Core;

/// <summary>What the client is currently doing. Each mode owns the camera while it runs.</summary>
public enum GameMode
{
    /// <summary>Free fly and on-foot exploration — the default.</summary>
    Explore,

    /// <summary>GPX ghost racing: the playback camera and HUD take over.</summary>
    GpxReplay,

    /// <summary>Connected to a dedicated server, walking as a networked player.</summary>
    Multiplayer,
}

/// <summary>
/// Everything a <see cref="ClientWorld"/> needs to know about the session it is built for: the
/// title screen fills one in when the player picks a mode, the command line fills one in when a
/// run boots straight into the world (<c>--connect</c>, <c>--gpx</c>, probes).
/// </summary>
public sealed record WorldLaunch
{
    public GameMode Mode { get; init; } = GameMode.Explore;

    /// <summary>"host" or "host:port", for <see cref="GameMode.Multiplayer"/>.</summary>
    public string Endpoint { get; init; } = "127.0.0.1";

    /// <summary>Tracks to load for <see cref="GameMode.GpxReplay"/>; several make a race.</summary>
    public IReadOnlyList<string> GpxPaths { get; init; } = Array.Empty<string>();

    /// <summary>The server's display name, when it is known (LAN list, saved list, hosting).</summary>
    public string? ServerName { get; init; }

    /// <summary>The name asked for on joining; the server has the last word.</summary>
    public string PlayerName { get; init; } = "";

    /// <summary>The server is the one this client started from the menu (<see cref="Net.HostedServer"/>).</summary>
    public bool Hosted { get; init; }

    /// <summary>Booted from the command line, with no title screen in front of it.</summary>
    public bool FromCommandLine { get; init; }

    /// <summary>The run will talk to a server, so local offline state must not be loaded.</summary>
    public bool Networked => Mode == GameMode.Multiplayer;

    /// <summary>The session the command line asks for: <c>--connect [host]</c>, <c>--gpx path</c> (repeatable), else exploring.</summary>
    public static WorldLaunch FromArgs()
    {
        var args = CmdArgs.All;
        var gpx = new List<string>();
        string? host = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--connect")
                host = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "127.0.0.1";
            else if (args[i] == "--gpx" && i + 1 < args.Length)
                gpx.Add(args[i + 1]);
        }
        return new WorldLaunch
        {
            Mode = host != null ? GameMode.Multiplayer : gpx.Count > 0 ? GameMode.GpxReplay : GameMode.Explore,
            Endpoint = host ?? "127.0.0.1",
            GpxPaths = gpx,
            PlayerName = Net.PlayerRegistry.ParseRequestedName(),
            FromCommandLine = true,
        };
    }
}

/// <summary>How far a world is from playable, read every frame by the loading screen.</summary>
public enum LoadStage
{
    ReadingMap,
    BuildingWorld,
    Connecting,
    SyncingTerrain,
    WaitingForPlayer,
    PlacingYou,
    BuildingTerrain,
    DrawingHorizon,
    Ready,
    Failed,
}
