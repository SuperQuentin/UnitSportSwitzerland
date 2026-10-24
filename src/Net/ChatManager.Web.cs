using Godot;

namespace UnitSport.Net;

/// <summary>
/// What the website's dashboard does through the chat (#1211, <see cref="AdminHttp"/>): console commands whose replies
/// come back to the page, a kick by peer, a restart with a warning. Server only, main thread only.
/// </summary>
public partial class ChatManager
{
    /// <summary>The console's replies while a website command runs (<see cref="ReplyTo"/> adds to it).</summary>
    private List<string>? _capture;

    /// <summary>Whose website command runs as the console: what the chat calls the console meanwhile ("Quentin changed the weather").</summary>
    private string? _consoleAs;

    /// <summary>The replies are captured only, not printed: the status polls ask the same questions every few seconds.</summary>
    private bool _quietCapture;

    /// <summary>
    /// A command from an admin on the website, run as the server console (operator rights), written to the log with
    /// their name. The lines it answered at once; anything later (an update's progress) reaches the log only.
    /// </summary>
    public IReadOnlyList<string> RunWebCommand(string admin, string line)
    {
        line = line.Trim().TrimStart('/');
        GD.Print($"[admin] {admin} (website): /{line}");
        _consoleAs = admin;
        try { return Captured(line, quiet: false); }
        finally { _consoleAs = null; }
    }

    /// <summary>A console question asked for the dashboard's status, neither logged nor printed.</summary>
    public string AskQuietly(string line) => string.Join(" ", Captured(line, quiet: true));

    private List<string> Captured(string line, bool quiet)
    {
        var lines = new List<string>();
        _capture = lines;
        _quietCapture = quiet;
        try { RunConsoleCommand(line); }
        finally { _capture = null; _quietCapture = false; }
        return lines;
    }

    /// <summary>The website's kick: by peer, so a name that changed meanwhile cannot pick someone else. Admins are kept, as in game.</summary>
    public (string Line, bool Refused) KickFromWeb(string admin, long peer, string reason)
    {
        if (_registry?.Find(peer) is not { } target) return ("That player has left.", true);
        if (target.IsAdmin) return ($"{target.Name} is an operator; revoke that first (/admin revoke {target.Name}).", true);
        reason = reason.Length == 0 ? "no reason given" : Scrub(reason);
        GD.Print($"[admin] {admin} (website) kicked {target.Name} ({reason})");
        KickPeer(peer, reason);
        Broadcast($"{target.Name} was kicked by {admin} ({reason})", ChatKind.Admin);
        return ($"{target.Name} was kicked ({reason}).", false);
    }

    /// <summary>The file <c>start-server.sh</c> looks for when the server quits: start again rather than stop (#1211).</summary>
    public static string? RestartMarker =>
        System.Environment.GetEnvironmentVariable("UNITSPORT_DEPLOY_DIR") is { Length: > 0 } dir ? System.IO.Path.Combine(dir, "restart-requested") : null;

    /// <summary>
    /// Restart from the website: everyone warned now (and a minute before), then everyone out and quit, with the marker
    /// that tells <c>start-server.sh</c> to start the same build again. Real minutes, whatever <c>/speed</c> says.
    /// </summary>
    public (string Line, bool Refused) RestartFromWeb(string admin, int minutes)
    {
        if (RestartMarker is not { } marker) return ("This server cannot restart itself: only one started by the deploy's start-server.sh can.", true);
        if (_restarting) return ("The server is already restarting.", true);
        _restarting = true;
        GD.Print($"[admin] {admin} (website): restart in {minutes} min");
        string When(int m) => m == 1 ? "1 minute" : $"{m} minutes";
        if (minutes > 0) Broadcast($"[server] The server restarts in {When(minutes)}. Reconnect a minute after.", ChatKind.Admin);
        if (minutes > 1)
            GetTree().CreateTimer((minutes - 1) * 60, processAlways: true, processInPhysics: false, ignoreTimeScale: true).Timeout +=
                () => Broadcast("[server] The server restarts in 1 minute.", ChatKind.Admin);
        GetTree().CreateTimer(minutes * 60, processAlways: true, processInPhysics: false, ignoreTimeScale: true).Timeout += () =>
        {
            try { System.IO.File.WriteAllText(marker, admin + "\n"); }
            catch (System.Exception e) { GD.PushError($"[admin] cannot write {marker}: {e.Message}"); }
            GD.Print("[admin] restarting: kicking everyone and quitting");
            foreach (var player in _registry?.Players.ToList() ?? new())
                KickPeer(player.PeerId, "The server is restarting. Reconnect in a minute.");
            // the kicks need their 0.2 s to land; the disconnects then save every sleeper
            GetTree().CreateTimer(2, processAlways: true, processInPhysics: false, ignoreTimeScale: true).Timeout += () => GetTree().Quit(0);
        };
        return (minutes == 0 ? "Restarting now." : $"Restarting in {When(minutes)}; everyone was told.", false);
    }

    /// <summary>The players for the dashboard: name, account, rights, ping and where they are (LV95).</summary>
    public List<AdminPlayer> WebPlayers(Func<long, string?> account)
    {
        var list = new List<AdminPlayer>();
        if (_registry is null) return list;
        var enet = Multiplayer.MultiplayerPeer as ENetMultiplayerPeer;
        foreach (var p in _registry.Players)
        {
            int ping = 0;
            if (enet?.GetPeer((int)p.PeerId) is { } link) ping = (int)link.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime);
            double? e = null, n = null;
            if (!p.Bodiless && _origin != null && _players?.GetNodeOrNull<Node3D>(p.PeerId.ToString()) is { } body)
                (e, n) = _origin.ToLv95(body.GlobalPosition);
            list.Add(new AdminPlayer(p.PeerId, p.Name, account(p.PeerId), p.IsAdmin, p.Bodiless, ping, e, n));
        }
        return list;
    }
}
