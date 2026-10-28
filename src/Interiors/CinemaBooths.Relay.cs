using Godot;
using UnitSport.Net;

namespace UnitSport.Interiors;

/// <summary>
/// A hall's video through the server (#1298 phase 2): a client watching a hall through the relay says so every
/// <see cref="WatchEvery"/> s (<see cref="KeepWatching"/>); the server keeps a <see cref="CinemaRelayTap"/> per hall
/// someone watches, sends its frames (unreliable: a lost one is skipped) and sound chunks (reliable) on their own
/// ENet channel to every watcher still inside that cinema, and closes the tap <see cref="CloseAfter"/> s after the
/// last one left. A listen-server's own client and an offline game get theirs directly.
/// </summary>
public partial class CinemaBooths
{
    /// <summary>The relay's ENet channel: the radio has 3, the concert 4.</summary>
    private const int RelayChannel = 5;

    private const double WatchEvery = 2, WatchLapse = 6, CloseAfter = 10;

    /// <summary>Client: when to say again that it watches through the server (RealClock).</summary>
    private double _watchSent = -1;

    /// <summary>Server: each hall's watchers (peer → when they last said so) and its tap.</summary>
    private readonly Dictionary<(string Plan, int Hall), Dictionary<long, double>> _watchers = new();
    private readonly Dictionary<(string Plan, int Hall), CinemaRelayTap> _taps = new();
    private readonly Dictionary<(string Plan, int Hall), (string Status, bool Failed, bool Ended)> _tapSaid = new();

    // reused every frame, not rebuilt (#221)
    private readonly List<(double Time, byte[] Jpeg)> _relayFrames = new();
    private readonly List<(long Index, byte[] MuLaw)> _relaySound = new();
    private readonly List<(string Plan, int Hall)> _relayKeys = new();
    private readonly List<long> _relayPeers = new();

    private bool ServerSide => !Online || Multiplayer.IsServer();

    // ---- client ----------------------------------------------------------------------------------

    private void KeepWatching(string plan, int hall)
    {
        double now = Core.RealClock.Now;
        if (now < _watchSent) return;
        _watchSent = now + WatchEvery;
        if (Online && !Multiplayer.IsServer()) RpcId(1, MethodName.WatchRelay, plan, hall);
        else ServeWatch(MyId, plan, hall);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WatchRelay(string plan, int hall)
    {
        if (Multiplayer.IsServer()) ServeWatch(Multiplayer.GetRemoteSenderId(), plan, hall);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable, TransferChannel = RelayChannel)]
    private void RelayFrame(string plan, int hall, double time, byte[] jpeg)
    {
        if (StreamOf(plan, hall) is CinemaRelayFeed feed) feed.PutFrame(time, jpeg);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = RelayChannel)]
    private void RelaySound(string plan, int hall, long index, byte[] mulaw)
    {
        if (StreamOf(plan, hall) is CinemaRelayFeed feed) feed.PutSound(index, mulaw);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RelayStatus(string plan, int hall, string status, bool failed, bool ended)
    {
        if (StreamOf(plan, hall) is CinemaRelayFeed feed) feed.SetStatus(status, failed, ended);
    }

    // ---- server ----------------------------------------------------------------------------------

    /// <summary>
    /// Server: <paramref name="peer"/> watches hall <paramref name="hall"/> of <paramref name="plan"/> through the relay.
    /// Only from inside that cinema (its bandwidth is for the hall's audience), and only while the hall plays a video.
    /// </summary>
    public bool ServeWatch(long peer, string plan, int hall)
    {
        bool inside = Inside(peer, plan);
        if (!inside || hall < 0 || hall >= MaxHalls || VideoOf(plan, hall).Link.Length == 0) return false;
        if (!_watchers.TryGetValue((plan, hall), out var peers)) _watchers[(plan, hall)] = peers = new();
        if (!peers.ContainsKey(peer)) GD.Print($"[cinema] peer {peer} watches hall {hall + 1} of {plan} through the server");
        peers[peer] = Core.RealClock.Now;
        return true;
    }

    /// <summary>Server, every frame: each watched hall's tap pumped and what is due sent to its watchers; the rest closed.</summary>
    private void PumpRelay(double delta)
    {
        if (_watchers.Count == 0 && _taps.Count == 0 && _pushed.Count == 0) return;
        double now = Core.RealClock.Now;
        _relayKeys.Clear();
        _relayKeys.AddRange(_watchers.Keys);
        foreach (var key in _taps.Keys) if (!_watchers.ContainsKey(key)) _relayKeys.Add(key);
        // a live hall nobody watches still checks its owner is there (CinemaBooths.Live)
        foreach (var key in _pushed.Keys) if (!_relayKeys.Contains(key)) _relayKeys.Add(key);
        foreach (var key in _relayKeys)
        {
            var (link, t0) = VideoOf(key.Plan, key.Hall);
            _relayPeers.Clear();
            if (_watchers.TryGetValue(key, out var peers))
            {
                foreach (var (peer, at) in peers)
                    if (now - at < WatchLapse && link.Length > 0
                        && (InteriorManager.Instance is not { } im || !Online || im.SpaceOf(peer) == key.Plan)) _relayPeers.Add(peer);
                if (_relayPeers.Count == 0) _watchers.Remove(key);
                else if (_relayPeers.Count < peers.Count)
                    foreach (var gone in peers.Keys.Except(_relayPeers).ToList()) peers.Remove(gone);
            }
            _taps.TryGetValue(key, out var tap);
            if (tap != null && (tap.Link != link || tap.T0 != t0 || tap.Stereo != StereoOf(key.Plan, key.Hall)))
            {
                tap.Dispose();
                _taps.Remove(key);
                _tapSaid.Remove(key);
                tap = null;
            }
            _relayFrames.Clear();
            _relaySound.Clear();
            bool live = CinemaVideo.IsLive(link);
            // a player's live source: what its owner pushed, no tap (CinemaBooths.Live)
            if (live && !TakePushed(key, link, delta, _relayFrames, _relaySound)) continue;
            if (!live) _pushed.Remove(key);
            if (_relayPeers.Count == 0)
            {
                if (tap != null && (tap.Unwanted += delta) > CloseAfter)
                {
                    tap.Dispose();
                    _taps.Remove(key);
                    _tapSaid.Remove(key);
                    GD.Print($"[cinema] relay of hall {key.Hall + 1} of {key.Plan} closed: nobody watches");
                }
                continue;
            }
            if (tap == null && !live)
            {
                _taps[key] = tap = new CinemaRelayTap(link, t0, CinemaVideo.LiveQuality.Low, StereoOf(key.Plan, key.Hall));
                GD.Print($"[cinema] relay of hall {key.Hall + 1} of {key.Plan} opened: {link}");
            }
            if (tap != null)
            {
                tap.Unwanted = 0;
                tap.Pump(CinemaVideo.PositionAt(ClockSync.ServerNow, t0), _relayFrames, _relaySound);
            }
            (string Status, bool Failed, bool Ended) said = tap != null ? (tap.Status, tap.Failed, tap.Ended) : ("", false, false);
            bool tell = !_tapSaid.TryGetValue(key, out var was) || was != said;
            _tapSaid[key] = said;
            foreach (long peer in _relayPeers)
            {
                if (peer == MyId)
                {
                    // this process's own client (offline, or a listen server's host)
                    if (StreamOf(key.Plan, key.Hall) is not CinemaRelayFeed own) continue;
                    foreach (var f in _relayFrames) own.PutFrame(f.Time, f.Jpeg);
                    foreach (var s in _relaySound) own.PutSound(s.Index, s.MuLaw);
                    if (tell) own.SetStatus(said.Status, said.Failed, said.Ended);
                    continue;
                }
                foreach (var f in _relayFrames) RpcId(peer, MethodName.RelayFrame, key.Plan, key.Hall, f.Time, f.Jpeg);
                foreach (var s in _relaySound) RpcId(peer, MethodName.RelaySound, key.Plan, key.Hall, s.Index, s.MuLaw);
                if (tell) RpcId(peer, MethodName.RelayStatus, key.Plan, key.Hall, said.Status, said.Failed, said.Ended);
            }
        }
    }
}
