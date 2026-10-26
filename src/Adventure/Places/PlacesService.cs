using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

/// <summary>
/// The places of the Adventure layer (#1224, <c>docs/plans/adventure-places.md</c> 3-5), at
/// <c>World/Adventure/Places</c> on the server and every client (RPCs route by node path). The server finds
/// places and clears fog for every adventuring peer once a second (<see cref="DiscoveryRules"/>), keeps the
/// <c>places</c> and <c>fog</c> sections, syncs viewpoints (<see cref="ServerInteract"/>), applies the
/// <c>"reveal"</c> reward; offline the client plays the server's part, as <see cref="AdventureService"/>.
/// The owner gets its stamps through <c>SectionSync</c>, its fog through <c>FogFull</c> / <c>FogDelta</c>
/// (at most once a second), and the toasts' cues (<c>Stamped</c>, <c>Synced</c>). Docs:
/// <c>docs/notes/adventure/places.md</c>, <c>fog.md</c>.
/// </summary>
public partial class PlacesService : Node
{
    public const string NodeName = "Places";
    private const double TickEvery = 1.0, DeltaEvery = 1.0, FogSaveEvery = 5.0;
    /// <summary>How close the body must be to a marker for the server to accept an interaction (the client asks within 3 m).</summary>
    public const float InteractReachM = 6;
    private const float NearM = 200;

    public static PlacesService? Instance { get; private set; }

    private AdventureService _adventure = null!;
    private bool _server, _networked;
    private bool Authority => _adventure.Authority;

    public PoiCatalogue Catalogue => PoiCatalogueHost.Current;

    /// <summary>Authority: where a peer's body is and how it travels; null when it has none (not spawned, a spectator).</summary>
    public Func<long, PlacesBody?>? BodyOf { get; set; }

    // ---- the authority's state, per peer ----------------------------------------------------------

    private sealed class PeerState
    {
        public string Key = "";
        public readonly DiscoveryTracker Tracker = new();
        public FogMask Fog = new();
        public readonly HashSet<int> Delta = new();
        public double NextDelta, FogSaveDue;
        public bool FogDirty, FullDue;
    }

    private readonly Dictionary<long, PeerState> _peers = new();
    private readonly List<long> _tickPeers = new();
    private readonly List<Poi> _near = new(), _found = new();
    private double _nextTick;

    /// <summary>Authority: discoveries and syncs since start (checks).</summary>
    public int Discoveries { get; private set; }

    // ---- the owner's side -------------------------------------------------------------------------

    /// <summary>Client: this player's fog, as the server last sent it (offline: the fog itself).</summary>
    public FogMask MyFog { get; private set; } = new();
    /// <summary>Client: the fog changed: the tiles (indices of <see cref="FogMask"/>), or null for all of it.</summary>
    public event Action<IReadOnlyCollection<int>?>? FogChanged;
    /// <summary>Client: a place was stamped (the toast).</summary>
    public event Action<Poi>? StampedPlace;
    /// <summary>Client: a viewpoint was synced (the look-out moment).</summary>
    public event Action<Poi>? SyncedView;
    /// <summary>Client: FogFull and FogDelta received (checks).</summary>
    public int FogMessages { get; private set; }

    public static PlacesService Create(AdventureService adventure, bool server, bool networked)
    {
        var s = new PlacesService { Name = NodeName, _adventure = adventure, _server = server, _networked = networked };
        adventure.AddChild(s);
        return s;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        PoiCatalogueHost.Changed -= OnCatalogue;
    }

    public override void _Ready()
    {
        _adventure.SyncsItself(FogProgress.Section.Id);
        PoiCatalogueHost.Changed += OnCatalogue;
        OnCatalogue();
        if (!Authority) return;
        _adventure.Loaded += OnLoaded;
        _adventure.Saving += FlushFog;
        _adventure.RegisterReward("reveal", RevealReward);
        // offline the local player was loaded before this node existed
        foreach (long peer in _adventure.Peers) OnLoaded(peer);
    }

    /// <summary>Places run where the map has places: quests that need them are offered only then (server and clients).</summary>
    private void OnCatalogue()
    {
        if (Catalogue.Count > 0) _adventure.Provides("places");
        else _adventure.Withdraws("places");
        GD.Print($"[places] {(Catalogue.Count > 0 ? $"{Catalogue.Count} places" : "no places")} on this map");
    }

    // ---- authority: peers --------------------------------------------------------------------------

    private PeerState? StateOf(long peer)
    {
        if (_adventure.Progress(peer) is not { } p) return null;
        if (!_peers.TryGetValue(peer, out var s)) _peers[peer] = s = new PeerState();
        if (s.Key != p.Key)
        {
            // a new identity under this peer (proof replaced the session's): its own fog
            s.Key = p.Key;
            s.Fog = FogMask.Decode(p.Get(FogProgress.Section).Mask);
            s.Delta.Clear();
            s.FogDirty = false;
            s.FullDue = true;
            if (!_networked) SetMyFog(s.Fog);
        }
        return s;
    }

    private void OnLoaded(long peer)
    {
        if (StateOf(peer) is not { } s) return;
        s.FullDue = true;
        SendFull(peer, s);
    }

    private void SendFull(long peer, PeerState s)
    {
        if (!s.FullDue) return;
        s.FullDue = false;
        s.Delta.Clear();
        if (!_networked) { SetMyFog(s.Fog); return; }
        if (Connected(peer)) RpcId(peer, MethodName.FogFull, s.Fog.ToBytes());
    }

    /// <summary>Server: a peer left: its tracker goes (the service saves the progress, <see cref="FlushFog"/> first).</summary>
    public void PeerLeft(long peer) => _peers.Remove(peer);

    /// <summary>
    /// A server-sanctioned move (navigation's travel, an accepted teleport invite): lands like a jump, so
    /// nothing within 300 m counts until the body has left by itself. The M search, <c>/tp</c> and admin
    /// teleports are caught by the jump rule anyway.
    /// </summary>
    public void Arrived(long peer, double e, double n)
    {
        if (StateOf(peer) is { } s) DiscoveryRules.Arrived(s.Tracker, e, n);
    }

    /// <summary>Writes a progress's fog into its section before it is saved (the service's <c>Saving</c>).</summary>
    private void FlushFog(PlayerProgress p)
    {
        foreach (var s in _peers.Values)
        {
            if (s.Key != p.Key || !s.FogDirty) continue;
            p.Get(FogProgress.Section).Mask = s.Fog.Encode();
            p.Touch(FogProgress.Section);
            s.FogDirty = false;
        }
    }

    // ---- authority: the tick ------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (!Authority) return;
        double now = GameClock.Now;
        if (now >= _nextTick)
        {
            double dt = _nextTick == 0 ? TickEvery : now - _nextTick + TickEvery;
            _nextTick = now + TickEvery;
            if (Catalogue.Count > 0 || BodyOf != null) Tick(Math.Max(dt, 0.1), now);
        }
        foreach (var (peer, s) in _peers)
        {
            if (s.FullDue && !_networked) SendFull(peer, s);
            if (s.Delta.Count > 0 && now >= s.NextDelta)
            {
                s.NextDelta = now + DeltaEvery;
                SendDelta(peer, s);
            }
            if (s.FogDirty && now >= s.FogSaveDue && _adventure.Progress(peer) is { } p && p.Key == s.Key)
            {
                p.Get(FogProgress.Section).Mask = s.Fog.Encode();
                p.Touch(FogProgress.Section);
                s.FogDirty = false;
            }
        }
    }

    private void Tick(double dt, double now)
    {
        _tickPeers.Clear();
        foreach (long peer in _adventure.Peers) _tickPeers.Add(peer);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (long peer in _tickPeers)
        {
            if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p || StateOf(peer) is not { } s) continue;
            if (BodyOf?.Invoke(peer) is not { } body) continue;
            Catalogue.Near(body.E, body.N, NearM, _near);
            var places = p.Get(PlacesProgress.Section);
            int jumps = s.Tracker.Jumps;
            bool earns = DiscoveryRules.Step(s.Tracker, body, dt, _near, places.Has, _found);
            if (s.Tracker.Jumps != jumps)
                GD.Print(FormattableString.Invariant($"[places] {Short(p.Key)} jumped to {body.E:F0}/{body.N:F0} (ride {body.RideKind}): nothing counts within {DiscoveryRules.JumpClearM:F0} m"));
            foreach (var poi in _found) Discover(peer, p, s, poi, PlacesRules.ModeOf(body.RideKind));
            if (earns && s.Fog.Reveal(body.E, body.N, DiscoveryRules.FogRadiusM, s.Delta) > 0) FogTouched(s, now);
        }
        Net.ServerStats.Ran("places", t0);
    }

    private void FogTouched(PeerState s, double now)
    {
        if (!s.FogDirty) s.FogSaveDue = now + FogSaveEvery;
        s.FogDirty = true;
    }

    private void SendDelta(long peer, PeerState s)
    {
        if (!_networked)
        {
            var tiles = new List<int>(s.Delta);
            s.Delta.Clear();
            FogChanged?.Invoke(tiles);
            return;
        }
        // an owner that has no full mask yet (unproven, not told) gets it all when it may hear
        if (s.FullDue || !Connected(peer)) return;
        int[] tiles2 = new int[s.Delta.Count], masks = new int[s.Delta.Count];
        int i = 0;
        foreach (int t in s.Delta) { tiles2[i] = t; masks[i] = s.Fog.MaskOf(t); i++; }
        s.Delta.Clear();
        RpcId(peer, MethodName.FogDelta, tiles2, masks);
    }

    /// <summary>
    /// Stamps a place for a peer: the <c>places</c> section written and touched, <see cref="PoiDiscovered"/>
    /// raised, the owner told (the toast), the fog round it revealed, then the canton milestones.
    /// </summary>
    private void Discover(long peer, PlayerProgress p, PeerState s, Poi poi, StampMode mode)
    {
        var places = p.Get(PlacesProgress.Section);
        if (places.Has(poi)) return;
        places.Found[poi.Id] = new Stamp(StampDay.Today, (byte)mode);
        p.Touch(PlacesProgress.Section);
        Discoveries++;
        GD.Print($"[places] {Short(p.Key)} found {poi.Id} ({StampDay.ModeText((byte)mode)})");
        _adventure.Events.Raise(new PoiDiscovered(peer, p.Key, poi.Id, poi.KindTag, poi.Canton));
        if (Connected(peer)) RpcId(peer, MethodName.Stamped, poi.Id);
        else if (!_networked) Stamped(poi.Id);
        if (s.Fog.Reveal(poi.E, poi.N, Math.Max(DiscoveryRules.FogRadiusM, poi.DiscoverRadius), s.Delta) > 0) FogTouched(s, GameClock.Now);
        Milestones(peer, p, poi.Canton);
    }

    private void Milestones(long peer, PlayerProgress p, string canton)
    {
        var (steps, chapter) = AtlasProgress.Check(Catalogue, p.Get(PlacesProgress.Section), canton);
        if (steps.Count == 0 && chapter == null) return;
        p.Touch(PlacesProgress.Section);
        foreach (int step in steps)
        {
            GD.Print($"[places] {Short(p.Key)}: {canton} at {step} %");
            _adventure.Events.Raise(new CantonMilestone(peer, p.Key, canton, step));
        }
        if (chapter != null) _adventure.Events.Raise(new ChapterCompleted(peer, p.Key, chapter));
    }

    // ---- authority: viewpoints and stamp posts ---------------------------------------------------------

    /// <summary>A POI one can walk up to and use: a viewpoint (cross, tower, cable-car top, road pass) or a landmark's stamp post.</summary>
    public static bool HasMarker(PoiCatalogue cat, Poi poi) => poi.Is(PoiFlags.Viewpoint) || cat.IsLandmark(poi);

    /// <summary>
    /// A peer uses a marker (the client's <c>RequestInteract</c>, a bot's call): the peer adventures, the
    /// POI has a marker and the peer's published position is within <see cref="InteractReachM"/>. Then it is
    /// stamped if new, a viewpoint is synced (its disc revealed, <see cref="ViewpointSynced"/>), and
    /// <see cref="Interacted"/> is raised. Null when done, else why not.
    /// </summary>
    public string? ServerInteract(long peer, string poiId)
    {
        if (!_adventure.Active(peer) || _adventure.Progress(peer) is not { } p || StateOf(peer) is not { } s) return "No adventuring now.";
        if (Catalogue.Resolve(poiId) is not { } poi || !HasMarker(Catalogue, poi)) return "Nothing to use there.";
        if (BodyOf?.Invoke(peer) is not { } body) return "No body.";
        double de = body.E - poi.E, dn = body.N - poi.N;
        if (de * de + dn * dn > (double)InteractReachM * InteractReachM) return $"Too far from {poi.Name}.";
        var places = p.Get(PlacesProgress.Section);
        if (!places.Has(poi)) Discover(peer, p, s, poi, PlacesRules.Reveals(body.RideKind) ? PlacesRules.ModeOf(body.RideKind) : StampMode.Given);
        if (poi.Is(PoiFlags.Viewpoint) && places.Synced.Add(poi.Id))
        {
            p.Touch(PlacesProgress.Section);
            RevealDisc(s, poi.E, poi.N, poi.RevealRadius > 0 ? poi.RevealRadius : 2000);
            GD.Print($"[places] {Short(p.Key)} synced {poi.Id}");
            _adventure.Events.Raise(new ViewpointSynced(peer, p.Key, poi.Id, poi.Canton));
            if (Connected(peer)) RpcId(peer, MethodName.Synced, poi.Id);
            else if (!_networked) Synced(poi.Id);
        }
        _adventure.Events.Raise(new Interacted(peer, p.Key, poi.Id));
        return null;
    }

    private void RevealDisc(PeerState s, double e, double n, float radiusM)
    {
        if (s.Fog.Reveal(e, n, radiusM, s.Delta) > 0) FogTouched(s, GameClock.Now);
        // a big change at once: sent now, not in a second
        s.NextDelta = 0;
    }

    /// <summary>The <c>"reveal"</c> reward: <c>&lt;POI id or alias&gt;[@&lt;km&gt;]</c>; the POI's reveal radius, or 2 km.</summary>
    private string? RevealReward(long peer, PlayerProgress p, string arg)
    {
        if (StateOf(peer) is not { } s) return "no progress";
        string id = arg;
        float? km = null;
        int at = arg.LastIndexOf('@');
        if (at > 0)
        {
            id = arg[..at];
            if (!float.TryParse(arg[(at + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float k) || k <= 0)
                return $"bad radius in '{arg}'";
            km = k;
        }
        if (Catalogue.Resolve(id) is not { } poi) return $"no place '{id}' on this map";
        RevealDisc(s, poi.E, poi.N, km is { } r ? r * 1000 : poi.RevealRadius > 0 ? poi.RevealRadius : 2000);
        return null;
    }

    // ---- the owner's requests ------------------------------------------------------------------------

    /// <summary>Uses a marker (E at it, a VR grip): offline at once, online through the server.</summary>
    public void AskInteract(string poiId)
    {
        if (Authority) { if (ServerInteract(AdventureService.LocalPeer, poiId) is { } why) GD.Print($"[places] {why}"); }
        else RpcId(1, MethodName.RequestInteract, poiId);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestInteract(string poiId)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (ServerInteract(peer, poiId) is { } why) GD.Print($"[places] peer {peer}: {why}");
    }

    // ---- RPCs: server -> owner ------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Stamped(string poiId)
    {
        if (Catalogue.Resolve(poiId) is not { } poi) return;
        // a place one tracked is reached: nothing to track any more
        if (_adventure.TrackedPlace is { } t && Math.Abs(t.E - poi.E) < 1 && Math.Abs(t.N - poi.N) < 1) _adventure.UntrackPlace();
        StampedPlace?.Invoke(poi);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Synced(string poiId)
    {
        if (Catalogue.Resolve(poiId) is { } poi) SyncedView?.Invoke(poi);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FogFull(byte[] deflated)
    {
        try { SetMyFog(FogMask.FromBytes(deflated)); }
        catch (Exception e) { GD.PushWarning($"[places] fog from the server: {e.Message}"); return; }
        FogMessages++;
        GD.Print($"[places] fog: {MyFog.Count} cells revealed ({deflated.Length} B)");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void FogDelta(int[] tiles, int[] masks)
    {
        if (tiles.Length != masks.Length) return;
        for (int i = 0; i < tiles.Length; i++) MyFog.Set(tiles[i], (ushort)masks[i]);
        FogMessages++;
        FogChanged?.Invoke(tiles);
    }

    private void SetMyFog(FogMask fog)
    {
        MyFog = fog;
        FogChanged?.Invoke(null);
    }

    private bool Connected(long peer) => _networked && peer > 0 && Multiplayer.MultiplayerPeer != null && Multiplayer.GetPeers().Contains((int)peer);

    private static string Short(string key) => key.Length > 13 ? key[..13] : key;
}
