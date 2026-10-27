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
/// Who: anyone inside that cinema, as for the panel; the last one wins. Asked from the projector panel
/// (<see cref="CinemaPanelUi"/>) or <c>/cinema play &lt;link&gt;</c>; bots call <see cref="ServeVideo"/> on the server.
/// </para>
/// </summary>
public partial class CinemaBooths
{
    /// <summary>A peer's next link no sooner than this, s: each one starts a decoder on every client in the hall.</summary>
    private const double VideoCooldown = 2;

    /// <summary>Server and client: each cinema's halls' links and their T0, absent until one plays.</summary>
    private readonly Dictionary<string, (string Link, double T0)[]> _videos = new();
    private readonly Dictionary<long, double> _lastVideo = new();

    /// <summary>Client: this machine's decoder of the hall it watches, if any.</summary>
    private CinemaVideoStream? _stream;
    private string _streamPlan = "";
    private int _streamHall = -1;

    /// <summary>A hall's link ("" for none) and the shared-clock moment it started.</summary>
    public (string Link, double T0) VideoOf(string plan, int hall) =>
        hall >= 0 && hall < MaxHalls && _videos.TryGetValue(plan, out var v) && v[hall].Link != null ? v[hall] : ("", 0);

    /// <summary>This client's decoder of a hall, or null when it is not the one watched.</summary>
    public CinemaVideoStream? StreamOf(string plan, int hall) => _streamPlan == plan && _streamHall == hall ? _stream : null;

    // ---- asking ----------------------------------------------------------------------------------

    /// <summary>Plays <paramref name="link"/> in a hall from now ("" stops it: the slides again).</summary>
    public void AskVideo(string plan, int hall, string link)
    {
        if (Online) RpcId(1, MethodName.AskVideoRpc, plan, hall, link);
        else ServeVideo(MyId, plan, hall, link);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskVideoRpc(string plan, int hall, string link)
    {
        if (Multiplayer.IsServer()) ServeVideo(Multiplayer.GetRemoteSenderId(), plan, hall, link);
    }

    /// <summary>
    /// Server (and offline): <paramref name="peer"/> plays <paramref name="link"/> on hall <paramref name="hall"/> of
    /// cinema <paramref name="plan"/> from now, or stops it with "". Refused unless they are inside it, the link is a
    /// web link (<see cref="CinemaVideo.Allowed"/>), and their last one was <see cref="VideoCooldown"/> ago. What a
    /// bot calls, with its own peer id.
    /// </summary>
    public bool ServeVideo(long peer, string plan, int hall, string link)
    {
        bool inside = plan.Length > 0 && (InteriorManager.Instance is { } im ? im.SpaceOf(peer) == plan : !Online);
        if (!inside) { GD.Print($"[cinema] peer {peer} is not in {plan}: no video"); return false; }
        if (hall < 0 || hall >= MaxHalls) return false;
        link = link.Trim();
        if (link.Length > 0 && !CinemaVideo.Allowed(link)) { GD.Print($"[cinema] peer {peer}: not a web link, refused"); return false; }
        double wall = Time.GetTicksMsec() / 1000.0;
        if (link.Length > 0 && _lastVideo.TryGetValue(peer, out double last) && wall - last < VideoCooldown) return false;
        if (link.Length > 0) _lastVideo[peer] = wall;
        double t0 = ClockSync.ServerNow;
        SetVideo(plan, hall, link, t0);
        if (Online) Rpc(MethodName.SetVideo, plan, hall, link, t0);
        GD.Print(link.Length > 0 ? $"[cinema] hall {hall + 1} of {plan} plays {link} from {t0:F2} (peer {peer})" : $"[cinema] hall {hall + 1} of {plan}: video stopped (peer {peer})");
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetVideo(string plan, int hall, string link, double t0)
    {
        if (hall < 0 || hall >= MaxHalls || link.Length > CinemaVideo.MaxLink) return;
        if (!_videos.TryGetValue(plan, out var v)) _videos[plan] = v = new (string, double)[MaxHalls];
        v[hall] = (link, t0);
    }

    /// <summary>A shot's hall holds a link as if the server had sent it (<see cref="CinemaVideoShot"/>).</summary>
    public void SetVideoForShot(string plan, int hall, string link, double t0) => SetVideo(plan, hall, link, t0);

    /// <summary>Server: a newly joined client gets every hall's video.</summary>
    private void SendVideosTo(long peer)
    {
        foreach (var (plan, halls) in _videos)
            for (int h = 0; h < halls.Length; h++)
                if (!string.IsNullOrEmpty(halls[h].Link)) RpcId(peer, MethodName.SetVideo, plan, h, halls[h].Link, halls[h].T0);
    }

    // ---- client: playing it ----------------------------------------------------------------------

    /// <summary>
    /// Client, every frame inside a cinema: decodes the video of hall <paramref name="here"/> (the camera's) when its
    /// screen is on, hands each new frame to <paramref name="node"/>, and returns the halls whose screens show a video
    /// now (a bit each): their slides and film music give way. A video that ended or cannot play here gives the hall
    /// back its slides.
    /// </summary>
    private int PumpVideo(InteriorNode? node, int here, int screens)
    {
        string plan = node?.Layout.Key ?? "";
        var (link, t0) = node != null && here >= 0 && (screens >> here & 1) != 0 ? VideoOf(plan, here) : ("", 0);
        bool want = link.Length > 0 && CinemaVideoStream.CanDecode && !NetworkManager.DedicatedServer;
        if (_stream != null && (!want || _streamPlan != plan || _streamHall != here || _stream.Link != link || _stream.T0 != t0))
        {
            _stream.Dispose();
            _stream = null;
            node?.ShowVideo(_streamHall, null, default, null);
            _streamHall = -1;
        }
        if (!want || node == null) return 0;
        if (_stream == null)
        {
            _stream = new CinemaVideoStream(link, t0);
            _streamPlan = plan;
            _streamHall = here;
            GD.Print($"[cinema] hall {here + 1}: decoding {link}");
        }
        double position = CinemaVideo.PositionAt(ClockSync.ServerNow, t0);
        if (_stream.Pump(position) && _stream.Current is { } frame) node.ShowVideo(here, frame, _stream.Tint, _stream);
        if (_stream.Ended || _stream.Failed)
        {
            node.ShowVideo(here, null, default, null);
            return 0;
        }
        return _stream.Current != null ? 1 << here : 0;
    }

    /// <summary>
    /// <c>/cinema play &lt;link&gt;</c>, <c>/cinema stop</c>, <c>/cinema</c> (#1298): the projector panel's video from the
    /// chat, for the hall the camera is in (or the nearest), on this client, which asks the server as the panel does.
    /// Null for a line that is not one.
    /// </summary>
    public static string? RunLocal(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !parts[0].Equals("/cinema", StringComparison.OrdinalIgnoreCase)) return null;
        if (Instance is not { } booths) return "No cinemas here.";
        if (InteriorManager.Instance?.CurrentNode is not { } node || node.Layout.Type != BuildingType.Cinema) return "Go into a cinema first.";
        var cam = booths.GetViewport().GetCamera3D();
        int hall = cam != null ? node.HallNear(node.ToLocal(cam.GlobalPosition)) : -1;
        if (hall < 0) return "This cinema has no hall.";
        string plan = node.Layout.Key;
        string verb = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        switch (verb)
        {
            case "play" when parts.Length > 2:
                if (!CinemaVideo.Allowed(parts[2])) return "That is not a web link (https://…).";
                var (lights, _) = booths.StateOf(plan, hall);
                booths.Ask(plan, hall, Pack(lights, true));
                booths.AskVideo(plan, hall, parts[2]);
                return $"Hall {hall + 1}: playing {CinemaVideo.Title(parts[2])}";
            case "stop":
                booths.AskVideo(plan, hall, "");
                return $"Hall {hall + 1}: back to the slides";
            case "":
                var (link, t0) = booths.VideoOf(plan, hall);
                var stream = booths.StreamOf(plan, hall);
                return link.Length == 0 ? $"Hall {hall + 1} shows no video. /cinema play <link>, /cinema stop"
                    : $"Hall {hall + 1}: {link}, at {CinemaVideo.PositionAt(ClockSync.ServerNow, t0):F0} s"
                      + (stream == null ? "" : $" ({(stream.Status.Length > 0 ? stream.Status : "playing")}, frame at {stream.CurrentTime:F2} s, {stream.Restarts} restarts)");
            default:
                return "Usage: /cinema play <link>  |  /cinema stop  |  /cinema";
        }
    }

    private static readonly bool LocalFiles = Core.CmdArgs.Has("--cinemavideolocal");

}
