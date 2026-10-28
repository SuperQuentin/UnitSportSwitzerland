using Godot;
using UnitSport.Net;

namespace UnitSport.Interiors;

/// <summary>
/// A hall's video (#1298), beside its lights and screen: the server keeps a link and <c>T0</c> per hall (the
/// shared-clock moment it served Play) and sends them to everyone, a joiner included; every desktop client then
/// decodes the link itself (<see cref="CinemaVideoStream"/>) and shows the frame due at <c>ServerNow − T0</c> on
/// the hall's screen, its projector light, beam, port and glow, with the video's sound from the screen
/// (<see cref="CinemaVideoSpeaker"/>) instead of the hall's film music. Only the hall the camera is in (or the
/// nearest, from the booth) is decoded: one ffmpeg pair at a time, none outside a cinema.
///
/// <para>
/// Through the server (phase 2, <c>CinemaBooths.Relay</c>): a hall whose panel says so, a client that cannot decode
/// (a phone, a headset, <c>--cinemanodecode</c>) and a client whose own decoder failed (a blocked domain) take the
/// server's frames and sound instead (<see cref="CinemaRelayFeed"/>), stamped with their moment of the video, so they
/// stay on the same clock as the rest.
/// </para>
/// <para>
/// Who: anyone inside that cinema, as for the panel; the last one wins. Asked from the projector panel
/// (<see cref="CinemaPanelUi"/>) or <c>/cinema play &lt;link&gt;</c>; bots call <see cref="ServeVideo"/> on the server.
/// </para>
/// </summary>
public partial class CinemaBooths
{
    /// <summary>A peer's next link no sooner than this, s: each one starts a decoder on every client in the hall.</summary>
    private const double VideoCooldown = 2;

    /// <summary>Server and client: each cinema's halls' links, their T0 and whether through the server, absent until one plays.</summary>
    private readonly Dictionary<string, (string Link, double T0, bool Relay, CinemaVideo.Stereo Stereo)[]> _videos = new();
    private readonly Dictionary<long, double> _lastVideo = new();

    /// <summary>Client: this machine's feed of the hall it watches, if any: its own decoder or the relay.</summary>
    private ICinemaFeed? _stream;
    private string _streamPlan = "";
    private int _streamHall = -1;

    /// <summary>Client: the display the watched hall's frame was last shown with.</summary>
    private int _shownDisplay;

    /// <summary>Client: the link and T0 whose own decoding failed here: that video comes through the server instead.</summary>
    private (string Link, double T0) _failedHere;

    /// <summary>
    /// A check's hall to watch from anywhere (<see cref="CinemaNetProbe"/>): its feed runs as a player's in the hall
    /// would, with no screen to show it on. Null: the hall the camera is in.
    /// </summary>
    public (string Plan, int Hall)? ProbeWatch { get; set; }

    /// <summary>A shot's cinema to show <see cref="ProbeWatch"/>'s hall on (<see cref="CinemaVideoShot"/>), when no interior is current.</summary>
    public InteriorNode? ProbeNode { get; set; }

    /// <summary>
    /// A hall's link ("" for none) and the shared-clock moment it started: what someone chose, else the film it is showing
    /// since the cinema was first seen (<see cref="ShowingOf"/>; never a player's file or share).
    /// </summary>
    public (string Link, double T0) VideoOf(string plan, int hall) =>
        hall >= 0 && hall < MaxHalls && _videos.TryGetValue(plan, out var v) && v[hall].Link != null ? (v[hall].Link, v[hall].T0) : ShowingOf(plan, hall);

    /// <summary>Whether a hall's video goes through the server for everyone (its panel's switch).</summary>
    public bool RelayOf(string plan, int hall) =>
        hall >= 0 && hall < MaxHalls && _videos.TryGetValue(plan, out var v) && v[hall].Relay;

    /// <summary>Whether a hall's video is a 3D film, and how its eyes are laid out (phase 6).</summary>
    public CinemaVideo.Stereo StereoOf(string plan, int hall) =>
        hall >= 0 && hall < MaxHalls && _videos.TryGetValue(plan, out var v) ? v[hall].Stereo : CinemaVideo.Stereo.Mono;

    /// <summary>This client's feed of a hall, or null when it is not the one watched.</summary>
    public ICinemaFeed? StreamOf(string plan, int hall) => _streamPlan == plan && _streamHall == hall ? _stream : null;

    // ---- asking ----------------------------------------------------------------------------------

    /// <summary>
    /// Plays <paramref name="link"/> in a hall from now ("" stops it: the slides again), through the server for everyone
    /// when <paramref name="relay"/>. The same link again with only the switch changed keeps its place.
    /// </summary>
    public void AskVideo(string plan, int hall, string link, bool relay = false, CinemaVideo.Stereo stereo = CinemaVideo.Stereo.Mono)
    {
        if (Online) RpcId(1, MethodName.AskVideoRpc, plan, hall, link, relay, (int)stereo);
        else ServeVideo(MyId, plan, hall, link, relay, stereo);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskVideoRpc(string plan, int hall, string link, bool relay, int stereo)
    {
        if (Multiplayer.IsServer()) ServeVideo(Multiplayer.GetRemoteSenderId(), plan, hall, link, relay, (CinemaVideo.Stereo)Math.Clamp(stereo, 0, 2));
    }

    /// <summary>
    /// Server (and offline): <paramref name="peer"/> plays <paramref name="link"/> on hall <paramref name="hall"/> of
    /// cinema <paramref name="plan"/> from now (through the server for everyone when <paramref name="relay"/>), or stops
    /// it with "". Refused unless they are inside it, the link is a web link (<see cref="CinemaVideo.Allowed"/>), and
    /// their last new link was <see cref="VideoCooldown"/> ago. The same link with only the switch changed keeps its
    /// T0. What a bot calls, with its own peer id.
    /// </summary>
    public bool ServeVideo(long peer, string plan, int hall, string link, bool relay = false, CinemaVideo.Stereo stereo = CinemaVideo.Stereo.Mono)
    {
        bool inside = Inside(peer, plan);
        if (!inside) { GD.Print($"[cinema] peer {peer} is not in {plan}: no video"); return false; }
        if (hall < 0 || hall >= MaxHalls) return false;
        link = link.Trim();
        if (link.Length > 0 && !CinemaVideo.Allowed(link)) { GD.Print($"[cinema] peer {peer}: not a web link, refused"); return false; }
        var (was, wasT0) = VideoOf(plan, hall);
        // only the switch changed: the video goes on where it is
        bool same = link.Length > 0 && link == was;
        double wall = Core.RealClock.Now;   // a person's pace, not the world's speed
        if (link.Length > 0 && !same && _lastVideo.TryGetValue(peer, out double last) && wall - last < VideoCooldown) return false;
        if (link.Length > 0 && !same) _lastVideo[peer] = wall;
        double t0 = same ? wasT0 : ClockSync.ServerNow;
        relay &= link.Length > 0;
        if (link.Length == 0) stereo = CinemaVideo.Stereo.Mono;
        SetVideo(plan, hall, link, t0, relay, (int)stereo);
        if (Online) Rpc(MethodName.SetVideo, plan, hall, link, t0, relay, (int)stereo);
        GD.Print(link.Length > 0
            ? $"[cinema] hall {hall + 1} of {plan} plays {link} from {t0:F2}{(relay ? " through the server" : "")} (peer {peer})"
            : $"[cinema] hall {hall + 1} of {plan}: video stopped (peer {peer})");
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetVideo(string plan, int hall, string link, double t0, bool relay, int stereo)
    {
        if (hall < 0 || hall >= MaxHalls || link.Length > CinemaVideo.MaxLink) return;
        if (!_videos.TryGetValue(plan, out var v)) _videos[plan] = v = new (string, double, bool, CinemaVideo.Stereo)[MaxHalls];
        v[hall] = (link, t0, relay, (CinemaVideo.Stereo)Math.Clamp(stereo, 0, 2));
    }

    /// <summary>A shot's hall holds a link as if the server had sent it (<see cref="CinemaVideoShot"/>).</summary>
    public void SetVideoForShot(string plan, int hall, string link, double t0, CinemaVideo.Stereo stereo = CinemaVideo.Stereo.Mono) =>
        SetVideo(plan, hall, link, t0, false, (int)stereo);

    /// <summary>Server: a newly joined client gets every hall's video.</summary>
    private void SendVideosTo(long peer)
    {
        foreach (var (plan, halls) in _videos)
            for (int h = 0; h < halls.Length; h++)
                if (!string.IsNullOrEmpty(halls[h].Link)) RpcId(peer, MethodName.SetVideo, plan, h, halls[h].Link, halls[h].T0, halls[h].Relay, (int)halls[h].Stereo);
    }

    // ---- client: playing it ----------------------------------------------------------------------

    /// <summary>
    /// Client, every frame inside a cinema: plays the video of hall <paramref name="here"/> (the camera's) when its
    /// screen is on, hands each new frame to <paramref name="node"/>, and returns the halls whose screens show a video
    /// now (a bit each): their slides and film music give way. Decoded here, or through the server when the hall says
    /// so, this machine cannot decode, or its own decoding of this video failed. A video that ended, or failed both
    /// ways, gives the hall back its slides.
    /// </summary>
    private int PumpVideo(InteriorNode? node, int here, int screens)
    {
        string plan = node?.Layout.Key ?? "";
        bool on = node != null && here >= 0 && (screens >> here & 1) != 0;
        if (ProbeWatch is { } probe)
        {
            (plan, here, on) = (probe.Plan, probe.Hall, true);
            node ??= ProbeNode;
        }
        var (link, t0) = on ? VideoOf(plan, here) : ("", 0);
        bool want = link.Length > 0 && !NetworkManager.DedicatedServer;
        bool relay = want && (RelayOf(plan, here) || CinemaVideo.IsLive(link) || !CinemaVideoStream.CanDecode || _failedHere == (link, t0));
        var stereo = StereoOf(plan, here);
        if (_stream != null && (!want || _streamPlan != plan || _streamHall != here || _stream.Link != link || _stream.T0 != t0
                                || _stream is CinemaRelayFeed != relay || _stream.Stereo != stereo))
        {
            _stream.Dispose();
            _stream = null;
            node?.ShowVideo(_streamHall, null, default, null);
            _streamHall = -1;
        }
        if (!want) return 0;
        if (_stream == null)
        {
            _stream = relay ? new CinemaRelayFeed(link, t0, stereo) : new CinemaVideoStream(link, t0, stereo);
            _streamPlan = plan;
            _streamHall = here;
            _watchSent = -1;
            GD.Print($"[cinema] hall {here + 1}: {(relay ? "through the server" : "decoding")} {link}");
        }
        if (relay) KeepWatching(plan, here);
        double position = CinemaVideo.PositionAt(ClockSync.ServerNow, t0);
        // a new frame, or the projector set otherwise (a still share sends no new frame to show it on)
        int display = DisplayOf(plan, here);
        if ((_stream.Pump(position) | display != _shownDisplay) && _stream.Picture is { } frame)
        {
            _shownDisplay = display;
            node?.ShowVideo(here, frame, _stream.Tint, _stream, display);
        }
        if (_stream.Failed && !relay)
        {
            // this machine could not get it (a blocked domain, a site that refuses it): the server's copy instead
            GD.Print($"[cinema] hall {here + 1}: {_stream.Status}; through the server instead");
            _failedHere = (link, t0);
            return 0;
        }
        if (_stream.Ended || _stream.Failed)
        {
            node?.ShowVideo(here, null, default, null);
            return 0;
        }
        return _stream.Picture != null ? 1 << here : 0;
    }

    /// <summary>
    /// <c>/cinema play &lt;link&gt;</c>, <c>/cinema stop</c>, <c>/cinema relay on|off</c>, <c>/cinema</c> (#1298): the
    /// projector panel's video from the chat, for the hall the camera is in (or the nearest), on this client, which asks
    /// the server as the panel does. Null for a line that is not one.
    /// </summary>
    public static string? RunLocal(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts[0].Equals("/cinema", StringComparison.OrdinalIgnoreCase)) return null;
        // the server's film list is the server's: the line goes on to it
        if (parts.Length > 1 && parts[1].Equals("pool", StringComparison.OrdinalIgnoreCase)) return null;
        // a hall's number first: an admin's lights and film (the cinema showcase's /cinema <hall> ..., #1307), the server's
        if (parts.Length > 1 && int.TryParse(parts[1], out _)) return null;
        if (Instance is not { } booths) return "No cinemas here.";
        if (InteriorManager.Instance?.CurrentNode is not { } node || node.Layout.Type != BuildingType.Cinema) return "Go into a cinema first.";
        var cam = booths.GetViewport().GetCamera3D();
        int hall = cam != null ? node.HallNear(node.ToLocal(cam.GlobalPosition)) : -1;
        if (hall < 0) return "This cinema has no hall.";
        string plan = node.Layout.Key;
        string verb = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        var (link, t0) = booths.VideoOf(plan, hall);
        if (booths.DisplayCommand(plan, hall, verb, parts.Length > 2 ? parts[2] : "") is { } display) return display;
        switch (verb)
        {
            case "play" when parts.Length > 2:
                if (!CinemaVideo.Allowed(parts[2])) return "That is not a web link (https://…).";
                var (lights, _) = booths.StateOf(plan, hall);
                booths.Ask(plan, hall, Pack(lights, true));
                booths.AskVideo(plan, hall, parts[2], booths.RelayOf(plan, hall), CinemaVideo.Guess(parts[2]));
                return $"Hall {hall + 1}: playing {CinemaVideo.Title(parts[2])}";
            case "stop":
                booths.AskVideo(plan, hall, "");
                return $"Hall {hall + 1}: back to the slides";
            case "relay" when parts.Length > 2 && parts[2] is "on" or "off":
                if (link.Length == 0) return $"Hall {hall + 1} shows no video.";
                booths.AskVideo(plan, hall, link, parts[2] == "on", booths.StereoOf(plan, hall));
                return $"Hall {hall + 1}: {(parts[2] == "on" ? "through the server for everyone" : "each player fetches it")}";
            case "":
                var stream = booths.StreamOf(plan, hall);
                return link.Length == 0 ? $"Hall {hall + 1} shows no video. /cinema play <link>, /cinema stop, /cinema relay on|off"
                    : $"Hall {hall + 1}: {link}, at {CinemaVideo.PositionAt(ClockSync.ServerNow, t0):F0} s"
                      + (stream == null ? "" : $" ({(stream is CinemaRelayFeed ? "through the server, " : "")}"
                          + $"{(stream.Status.Length > 0 ? stream.Status : "playing")}, frame at {stream.CurrentTime:F2} s, {stream.Restarts} restarts)");
            default:
                return "Usage: /cinema play <link>  |  /cinema stop  |  /cinema relay on|off  |  /cinema fit contain|cover|stretch  |  /cinema ratio full|16:9|…  |  /cinema pool  |  /cinema";
        }
    }

    private static readonly bool LocalFiles = Core.CmdArgs.Has("--cinemavideolocal");
}
