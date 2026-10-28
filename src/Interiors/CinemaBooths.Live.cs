using Godot;
using UnitSport.Net;

namespace UnitSport.Interiors;

/// <summary>
/// A player's live source on a hall's screen (#1298 phase 3): a monitor, a window or a webcam shared from their own
/// machine (<see cref="CinemaCapture"/>). The server writes the hall's link as <c>live:&lt;peer&gt;:&lt;label&gt;</c>
/// (<see cref="CinemaVideo.LiveLink"/>), the streamer's game pushes its frames and sound (stamped with the hall's moment
/// plus <see cref="CinemaVideo.LiveDelay"/>) up to the server, which forwards them to the hall's watchers through the
/// relay's way (<c>CinemaBooths.Relay</c>), the streamer included: everyone sees the same moment. Only the owner's
/// pushes are taken; the share ends when they stop it, someone plays something else, or they are out of that cinema
/// for <see cref="OwnerGone"/> s.
/// </summary>
public partial class CinemaBooths
{
    private const double OwnerGone = 5;
    /// <summary>Most frames and chunks waiting at the server for one hall between two of its frames; a JPEG's largest size.</summary>
    private const int MaxPushed = 64, MaxJpeg = 512 * 1024;

    /// <summary>Server: what each live hall's owner pushed since the last frame, and how long the owner has been away.</summary>
    private readonly Dictionary<(string Plan, int Hall), (List<(double Time, byte[] Jpeg)> Frames, List<(long Index, byte[] MuLaw)> Sound)> _pushed = new();
    private readonly Dictionary<(string Plan, int Hall), double> _ownerAway = new();

    /// <summary>Client: the share this machine runs, the hall and link it is for, and the one it asked for and awaits.</summary>
    private ILiveSource? _capture;
    private (string Plan, int Hall) _captureHall;
    private string _captureLink = "";
    private (string Plan, int Hall, string Label, Func<double, ILiveSource> Make, double Asked)? _pendingShare;
    private readonly List<(double Time, byte[] Jpeg)> _shareFrames = new();
    private readonly List<(long Index, byte[] MuLaw)> _shareSound = new();

    /// <summary>This machine's share, if any: for the panel.</summary>
    public ILiveSource? Sharing => _capture;
    public bool SharingIn(string plan, int hall) => _capture != null && _captureHall == (plan, hall);

    private long LiveId => Online ? Multiplayer.GetUniqueId() : MyId;

    // ---- the streamer ----------------------------------------------------------------------------

    /// <summary>Shares <paramref name="source"/> on a hall: asks the server, and starts capturing once it says so.</summary>
    public void StartSharing(string plan, int hall, CaptureSource source, CinemaVideo.LiveQuality quality, bool pcSound, string? mic) =>
        Share(plan, hall, source.Label, _ => new CinemaCapture(source, quality, pcSound, mic));

    /// <summary>
    /// Plays a video file of this machine on a hall (phase 4): streamed from here through the server, decoded on the clock
    /// from the hall's start, at <paramref name="quality"/>.
    /// </summary>
    public void StartFile(string plan, int hall, string path, CinemaVideo.LiveQuality quality) =>
        Share(plan, hall, $"File: {Path.GetFileName(path)}", t0 => new CinemaFileSource(path, t0, quality));

    private void Share(string plan, int hall, string label, Func<double, ILiveSource> make)
    {
        StopSharing();
        _pendingShare = (plan, hall, label, make, Core.RealClock.Now);
        if (Online) RpcId(1, MethodName.AskLiveRpc, plan, hall, label);
        else ServeLive(MyId, plan, hall, label);
    }

    /// <summary>Stops this machine's share, and the hall's screen goes back to the slides.</summary>
    public void StopSharing()
    {
        _pendingShare = null;
        if (_capture == null) return;
        var (plan, hall) = _captureHall;
        _capture.Dispose();
        _capture = null;
        if (VideoOf(plan, hall).Link == _captureLink) AskVideo(plan, hall, "");
        GD.Print($"[cinema] stopped sharing on hall {hall + 1}");
    }

    /// <summary>Client, every frame: starts the share the server granted, pushes what it captured, ends one the hall dropped.</summary>
    private void PumpCapture()
    {
        if (_pendingShare is { } want)
        {
            var (link, t0) = VideoOf(want.Plan, want.Hall);
            if (link == CinemaVideo.LiveLink(LiveId, want.Label))
            {
                _pendingShare = null;
                _capture = want.Make(t0);
                _captureHall = (want.Plan, want.Hall);
                _captureLink = link;
                GD.Print($"[cinema] sharing {want.Label} on hall {want.Hall + 1}");
            }
            else if (Core.RealClock.Now - want.Asked > 5) { GD.Print("[cinema] the server did not grant the share"); _pendingShare = null; }
        }
        if (_capture == null) return;
        var (plan, hall) = _captureHall;
        var (now, start) = VideoOf(plan, hall);
        if (now != _captureLink)
        {
            // someone played something else, or the server ended it
            _capture.Dispose();
            _capture = null;
            GD.Print($"[cinema] the share on hall {hall + 1} ended");
            return;
        }
        if (_capture.Ended)
        {
            // a file played to its end: the hall back to its slides
            GD.Print($"[cinema] {CinemaVideo.Title(_captureLink)} ended");
            StopSharing();
            return;
        }
        _shareFrames.Clear();
        _shareSound.Clear();
        _capture.Pump(CinemaVideo.PositionAt(ClockSync.ServerNow, start), _shareFrames, _shareSound);
        bool here = !Online || Multiplayer.IsServer();
        foreach (var f in _shareFrames)
            if (here) Pushed(LiveId, plan, hall, f.Time, f.Jpeg, -1, null);
            else RpcId(1, MethodName.PushFrame, plan, hall, f.Time, f.Jpeg);
        foreach (var s in _shareSound)
            if (here) Pushed(LiveId, plan, hall, 0, null, s.Index, s.MuLaw);
            else RpcId(1, MethodName.PushSound, plan, hall, s.Index, s.MuLaw);
    }

    // ---- the server ------------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskLiveRpc(string plan, int hall, string label)
    {
        if (Multiplayer.IsServer()) ServeLive(Multiplayer.GetRemoteSenderId(), plan, hall, label);
    }

    /// <summary>
    /// Server: <paramref name="peer"/> shares a live source labelled <paramref name="label"/> on a hall, from now, through
    /// the server for everyone. Inside that cinema only, at most one new source every <see cref="VideoCooldown"/> s.
    /// </summary>
    public bool ServeLive(long peer, string plan, int hall, string label)
    {
        bool inside = plan.Length > 0 && (InteriorManager.Instance is { } im ? im.SpaceOf(peer) == plan : !Online);
        if (!inside || hall < 0 || hall >= MaxHalls) { GD.Print($"[cinema] peer {peer} may not share on {plan}: not inside"); return false; }
        double wall = Core.RealClock.Now;
        if (_lastVideo.TryGetValue(peer, out double last) && wall - last < VideoCooldown) return false;
        _lastVideo[peer] = wall;
        string link = CinemaVideo.LiveLink(peer, label);
        double t0 = ClockSync.ServerNow;
        SetVideo(plan, hall, link, t0, true);
        if (Online) Rpc(MethodName.SetVideo, plan, hall, link, t0, true);
        _pushed.Remove((plan, hall));
        _ownerAway.Remove((plan, hall));
        GD.Print($"[cinema] hall {hall + 1} of {plan} shows peer {peer}'s {CinemaVideo.LiveOf(link).Label}");
        return true;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable, TransferChannel = RelayChannel)]
    private void PushFrame(string plan, int hall, double time, byte[] jpeg)
    {
        if (Multiplayer.IsServer()) Pushed(Multiplayer.GetRemoteSenderId(), plan, hall, time, jpeg, -1, null);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = RelayChannel)]
    private void PushSound(string plan, int hall, long index, byte[] mulaw)
    {
        if (Multiplayer.IsServer()) Pushed(Multiplayer.GetRemoteSenderId(), plan, hall, 0, null, index, mulaw);
    }

    /// <summary>Server: a frame or a sound chunk from <paramref name="peer"/>, kept for the hall's watchers if the hall is theirs.</summary>
    private void Pushed(long peer, string plan, int hall, double time, byte[]? jpeg, long index, byte[]? mulaw)
    {
        if (hall < 0 || hall >= MaxHalls || CinemaVideo.LiveOf(VideoOf(plan, hall).Link).Peer != peer) return;
        if (!_pushed.TryGetValue((plan, hall), out var q)) _pushed[(plan, hall)] = q = (new(), new());
        if (jpeg != null && jpeg.Length <= MaxJpeg && q.Frames.Count < MaxPushed) q.Frames.Add((time, jpeg));
        if (mulaw != null && mulaw.Length == CinemaVideo.RelayChunk && q.Sound.Count < MaxPushed) q.Sound.Add((index, mulaw));
    }

    /// <summary>
    /// Server, from <see cref="PumpRelay"/>: a live hall's frames and chunks since the last frame into the lists to send;
    /// false once its owner has been out of that cinema for <see cref="OwnerGone"/> s (the share is ended).
    /// </summary>
    private bool TakePushed((string Plan, int Hall) key, string link, double delta, List<(double, byte[])> frames, List<(long, byte[])> sound)
    {
        long owner = CinemaVideo.LiveOf(link).Peer;
        bool present = !Online || InteriorManager.Instance is not { } im || im.SpaceOf(owner) == key.Plan;
        double away = present ? 0 : _ownerAway.GetValueOrDefault(key) + delta;
        _ownerAway[key] = away;
        if (away > OwnerGone)
        {
            GD.Print($"[cinema] peer {owner} left: hall {key.Hall + 1} of {key.Plan} stops their share");
            SetVideo(key.Plan, key.Hall, "", 0, false);
            if (Online) Rpc(MethodName.SetVideo, key.Plan, key.Hall, "", 0.0, false);
            _pushed.Remove(key);
            _ownerAway.Remove(key);
            return false;
        }
        if (_pushed.TryGetValue(key, out var q))
        {
            frames.AddRange(q.Frames);
            sound.AddRange(q.Sound);
            q.Frames.Clear();
            q.Sound.Clear();
        }
        return true;
    }
}
