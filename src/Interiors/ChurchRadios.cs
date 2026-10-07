using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// The radio by the pastor rat in every church (#370), at <c>World/ChurchRadios</c> on the server
/// and on every client (RPCs route by node path). It is furniture
/// (<see cref="FurnitureType.ChurchRadio"/>), so what plays is a table on the server, church plan
/// key to <see cref="RadioPlay"/>, the way open doors are (<c>InteriorManager.SetDoor</c>): a
/// player inside asks, the server checks they are in that church and broadcasts; a joining client
/// gets the table. Playback is the boombox's: a <see cref="RadioSpeaker"/> on the shared clock.
///
/// <para>
/// A church nobody has touched has the chess type beat loaded (<see cref="CdLibrary.RatBeatId"/>),
/// stopped, on repeat: press Play and the rat dances (<see cref="ChurchDancers"/>).
/// </para>
/// </summary>
public partial class ChurchRadios : Node
{
    public const string NodeName = "ChurchRadios";

    /// <summary>How close to the church radio to work it, m.</summary>
    public const float Reach = 1.8f;

    public static ChurchRadios? Instance { get; private set; }

    /// <summary>Server and client: what each church's radio plays (entries only while one plays).</summary>
    private readonly Dictionary<string, RadioPlay> _plays = new();

    /// <summary>Server and client: each church's mode while it plays nothing (the panel's button).</summary>
    private readonly Dictionary<string, RadioMode> _modes = new();

    /// <summary>A play changed (plan key): the dancers and the disco snap to it at once.</summary>
    public event Action<string>? Changed;

    private readonly Random _shuffle = new();
    private double _housekeeping;
    private RadioSpeaker? _speaker;
    private string _speakerPlan = "";

    public static ChurchRadios Create(Node world)
    {
        var node = new ChurchRadios { Name = NodeName };
        world.AddChild(node);
        Instance = node;
        return node;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    private long MyId => Multiplayer.MultiplayerPeer is { } p and not OfflineMultiplayerPeer ? Multiplayer.GetUniqueId() : 1;

    // ---- what plays ------------------------------------------------------------------------------

    /// <summary>What church <paramref name="plan"/>'s radio plays now, or null when it is silent.</summary>
    public RadioPlay? PlayOf(string plan) =>
        _plays.TryGetValue(plan, out var play) && play.Sounding(ClockSync.ServerNow) ? play : null;

    /// <summary>The CD it last played, sounding or not (#734): what a tap puts back on; 0 for none.</summary>
    public int LastCdOf(string plan) => _lastCd.GetValueOrDefault(plan);

    /// <summary>Each church's last CD on this peer (every peer sees every play go by), kept through a Stop.</summary>
    private readonly Dictionary<string, int> _lastCd = new();

    /// <summary>Its mode: the playing CD's, else the one chosen, else repeat (the chess type beat loops).</summary>
    public RadioMode ModeOf(string plan) =>
        _plays.TryGetValue(plan, out var play) ? play.Mode : _modes.GetValueOrDefault(plan, RadioMode.Repeat);

    /// <summary>Whether church <paramref name="plan"/> plays the chess type beat: what turns its figures and its lights on.</summary>
    public static bool RatBeatPlaying(string plan, out RadioPlay play)
    {
        play = default;
        if (Instance?.PlayOf(plan) is not { } p || !CdLibrary.IsRatBeat(p.CdId)) return false;
        play = p;
        return true;
    }

    /// <summary>
    /// Where the church radio of <paramref name="layout"/> stands, interior-local, at the height of
    /// its speakers; false when this plan has none.
    /// </summary>
    public static bool RadioSpot(InteriorLayout layout, out Vector3 at)
    {
        foreach (var f in layout.Furniture)
        {
            if (f.Type != FurnitureType.ChurchRadio) continue;
            at = new Vector3(f.X, layout.FloorY(f.Floor) + f.Lift + f.H - 0.15f, f.Z);
            return true;
        }
        at = default;
        return false;
    }

    // ---- the player at it ------------------------------------------------------------------------

    /// <summary>The plan key of the church whose radio <paramref name="p"/> stands at, or null.</summary>
    public static string? At(FootPlayer p)
    {
        if (!p.Indoors || InteriorManager.Instance is not { Current: { } layout, CurrentNode: { } node }) return null;
        int i = Loot.LootService.NearestOf(p, layout, node, t => t == FurnitureType.ChurchRadio, Reach);
        return i >= 0 ? layout.Key : null;
    }

    /// <summary>E at the church radio: opens the radio panel on it. False when not at one.</summary>
    public static bool TryOpen(FootPlayer p)
    {
        if (At(p) is not { } plan || RadioUi.Instance is not { } ui) return false;
        // a tap of E switches it on or off, a hold opens its panel (#734), like a radio in the world
        RadioTap.Begin(Core.PlayerInput.InteractMount, () => RadioTap.ToggleChurch(plan), () => ui.OpenChurch(plan),
            () => IsInstanceValid(p) && At(p) == plan);
        return true;
    }

    /// <summary>The prompt at the church radio, or null.</summary>
    public static string? PromptFor(FootPlayer p) =>
        At(p) == null ? null : $"{Core.InputHints.Tag(Core.PlayerInput.InteractMount)} {RadioTap.Prompt}";

    // ---- client: asking --------------------------------------------------------------------------

    public void Play(string plan, int cdId, float length)
    {
        if (Online) RpcId(1, MethodName.AskPlay, plan, cdId, length);
        else ServePlay(MyId, plan, cdId, length);
    }

    public void Stop(string plan)
    {
        if (Online) RpcId(1, MethodName.AskStop, plan);
        else ServeStop(MyId, plan);
    }

    /// <summary>Moves church <paramref name="plan"/>'s song to <paramref name="at"/> seconds in (#734).</summary>
    public void Seek(string plan, double at)
    {
        if (Online) RpcId(1, MethodName.AskSeek, plan, at);
        else ServeSeek(MyId, plan, at);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskSeek(string plan, double at)
    {
        if (Multiplayer.IsServer()) ServeSeek(Multiplayer.GetRemoteSenderId(), plan, at);
    }

    private void ServeSeek(long peer, string plan, double at)
    {
        if (!Inside(peer, plan) || !_plays.TryGetValue(plan, out var play) || !double.IsFinite(at)) return;
        Broadcast(plan, (play with { StartedAt = ClockSync.ServerNow - Math.Clamp(at, 0, play.Length - 0.5) }).Encode());
    }

    public void SetMode(string plan, RadioMode mode)
    {
        if (Online) RpcId(1, MethodName.AskMode, plan, (int)mode);
        else ServeMode(MyId, plan, (int)mode);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskPlay(string plan, int cdId, float length)
    {
        if (Multiplayer.IsServer()) ServePlay(Multiplayer.GetRemoteSenderId(), plan, cdId, length);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskStop(string plan)
    {
        if (Multiplayer.IsServer()) ServeStop(Multiplayer.GetRemoteSenderId(), plan);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskMode(string plan, int mode)
    {
        if (Multiplayer.IsServer()) ServeMode(Multiplayer.GetRemoteSenderId(), plan, mode);
    }

    // ---- server ----------------------------------------------------------------------------------

    /// <summary>Only someone inside that church works its radio (offline, with no interiors, the probe's church).</summary>
    private bool Inside(long peer, string plan) =>
        plan.Length > 0 && (InteriorManager.Instance is { } im ? im.SpaceOf(peer) == plan : !Online);

    private void ServePlay(long peer, string plan, int cdId, float length)
    {
        if (!Inside(peer, plan)) { GD.Print($"[church] peer {peer} is not in {plan}: refused"); return; }
        length = RadioManager.TrustedLength(cdId, length);
        if (length <= 0) return;
        Broadcast(plan, new RadioPlay(cdId, ClockSync.ServerNow, length, ModeOf(plan)).Encode());
    }

    private void ServeStop(long peer, string plan)
    {
        if (!Inside(peer, plan)) return;
        Broadcast(plan, "");
    }

    private void ServeMode(long peer, string plan, int mode)
    {
        if (!Inside(peer, plan)) return;
        var m = RadioQueue.Clamp(mode);
        _modes[plan] = m;
        if (_plays.TryGetValue(plan, out var play)) Broadcast(plan, (play with { Mode = m }).Encode());
        else if (Online) Rpc(MethodName.SetModeOnly, plan, (int)m);
    }

    private void Broadcast(string plan, string play)
    {
        Set(plan, play);
        if (Online) Rpc(MethodName.Set, plan, play);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Set(string plan, string play)
    {
        if (RadioPlay.Decode(play) is { } p)
        {
            _plays[plan] = p;
            _modes[plan] = p.Mode;
            _lastCd[plan] = p.CdId;
        }
        else _plays.Remove(plan);
        Changed?.Invoke(plan);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetModeOnly(string plan, int mode) => _modes[plan] = RadioQueue.Clamp(mode);

    /// <summary>Server: a newly joined client hears what every church plays.</summary>
    public void SendTo(long peer)
    {
        foreach (var (plan, play) in _plays) RpcId(peer, MethodName.Set, plan, play.Encode());
    }

    public override void _Process(double delta)
    {
        if (!NetworkManager.DedicatedServer && DisplayServer.GetName() != "headless") UpdateSpeaker();
        if (!NetLink.IsServer(this)) return;
        _housekeeping += delta;
        if (_housekeeping < 1) return;
        _housekeeping = 0;
        double now = ClockSync.ServerNow;
        foreach (var (plan, play) in _plays.ToList())
        {
            // nobody left inside: the church falls silent (and the next visitor finds the beat loaded)
            if (!Occupied(plan)) { Broadcast(plan, ""); continue; }
            if (play.Sounding(now)) continue;
            int next = RadioQueue.Following(play.CdId, play.Mode, RadioQueue.Order(CdLibrary.Instance, withPersonal: !Online), _shuffle);
            float length = next == 0 ? -1f : next == play.CdId ? play.Length : RadioManager.TrustedLength(next, CdLibrary.Instance?.Find(next)?.Duration ?? 0f);
            // a repeat starts where the last ended on the clock, so the beat runs on without a seam
            double at = now - (play.StartedAt + play.Length) < 2 ? play.StartedAt + play.Length : now;
            Broadcast(plan, length > 0 ? new RadioPlay(next, at, length, play.Mode).Encode() : "");
        }
    }

    private bool Occupied(string plan)
    {
        if (InteriorManager.Instance is not { } im) return !Online;   // the probe's church, offline
        if (!Online) return im.SpaceOf(MyId) == plan;
        if (im.SpaceOf(MyId) == plan) return true;
        foreach (long peer in Multiplayer.GetPeers())
            if (im.SpaceOf(peer) == plan) return true;
        return false;
    }

    // ---- client: the sound -----------------------------------------------------------------------

    /// <summary>One speaker, at the radio of the church this player is in, while it plays.</summary>
    private void UpdateSpeaker()
    {
        var im = InteriorManager.Instance;
        string plan = im?.Current?.Key ?? "";
        RadioPlay? play = plan.Length > 0 ? PlayOf(plan) : null;
        if (play is not { } p || im?.CurrentNode is not { } node || !RadioSpot(node.Layout, out var at))
        {
            if (_speaker != null) _speaker.On = false;
            return;
        }
        if (_speaker == null || !IsInstanceValid(_speaker) || _speakerPlan != plan || _speaker.GetParent() != node)
        {
            if (_speaker != null && IsInstanceValid(_speaker)) _speaker.QueueFree();
            _speaker = SpeakerAt(node, at);
            _speakerPlan = plan;
        }
        _speaker.CdId = p.CdId;
        _speaker.StartedAt = p.StartedAt;
        _speaker.Length = p.Length;
        _speaker.On = true;
    }

    /// <summary>
    /// A speaker on <paramref name="node"/> at the radio's interior-local spot. The spot goes in
    /// before AddChild: the speaker's <see cref="Audio.Hearing"/> takes its offset in _Ready, and a
    /// spot set after left the sound at the interior's origin, the middle of the church (#375).
    /// </summary>
    public static RadioSpeaker SpeakerAt(Node3D node, Vector3 at)
    {
        var speaker = new RadioSpeaker { Name = "ChurchRadio", Position = at };
        node.AddChild(speaker);
        return speaker;
    }

    /// <summary>The church radio this player hears as music to dance to, if any.</summary>
    public RadioManager.Music? Music(Vector3 point, float radius)
    {
        if (_speaker == null || !IsInstanceValid(_speaker) || !_speaker.On || !_speaker.IsInsideTree()) return null;
        if (_speaker.GlobalPosition.DistanceTo(point) > radius || CdLibrary.Instance?.Find(_speaker.CdId) == null) return null;
        return new RadioManager.Music(_speaker, _speaker.CdId, _speaker.StartedAt);
    }
}
