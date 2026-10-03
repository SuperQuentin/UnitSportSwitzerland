using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// Every radio lying in the world, at <c>World/Radios</c> on the server and on every client alike
/// (RPCs and the spawner route by node path), spawned through <c>World/RadioSpawner</c>. The
/// <c>VehicleManager</c> pattern: offline it adds and frees <see cref="RadioBody"/> nodes; online a
/// client asks and the server decides — a throw spawns it for everyone with the thrower as the
/// authority over its fall, a pick-up removes it for everyone and only one player can win it, and
/// play/stop set the state the server's synchronizer then carries to all.
///
/// <para>
/// A radio in a player's hand is not a node here: it is <c>FootPlayer.HeldRadio</c>, which the
/// holder writes and the player's own synchronizer carries. This manager hangs a
/// <see cref="RadioSpeaker"/> on every player holding a playing radio (<see cref="UpdateHeld"/>).
/// </para>
/// </summary>
public partial class RadioManager : Node3D, Core.IOriginContainer
{
    public const string NodeName = "Radios";

    /// <summary>How close a player must be to a playing radio to dance to it.</summary>
    public const float DanceRadius = 20f;

    /// <summary>How close to a radio to open it or pick it up.</summary>
    public const float Reach = 2.5f;

    /// <summary>A radio this far from every player, for this long, is cleared.</summary>
    private const float LonelyDistance = 3000f;
    private const double LonelyTime = 600;

    public static RadioManager? Instance { get; private set; }

    /// <summary>Where the players are, for despawning what nobody is near.</summary>
    public Func<IEnumerable<Vector3>>? PlayerPositions { get; set; }

    /// <summary>Client: every player body on this machine, local and remote, for held radios.</summary>
    public Func<IEnumerable<FootPlayer>>? Players { get; set; }

    /// <summary>The node name of the speaker a held radio hangs on its holder.</summary>
    public const string HeldSpeakerName = "HeldRadio";

    /// <summary>Client: the server refused something, with a line for the player.</summary>
    public static event Action<string>? Refused;
    /// <summary>Drops the subscribers a world left behind when it was freed (<see cref="Core.WorldStatics"/>).</summary>
    internal static void ResetEvents() => Refused = null;

    private MultiplayerSpawner? _spawner;
    private int _counter;
    private Action? _pendingPickUp;
    private readonly HashSet<string> _claimed = new();
    private double _housekeeping;

    /// <summary>This peer's origin: radio states carry LV95 (#185), the radios stand in world space.</summary>
    public Core.WorldOrigin Origin { get; private set; } = null!;

    public static RadioManager Create(Node world, Core.WorldOrigin origin)
    {
        var manager = new RadioManager { Name = NodeName, Origin = origin };
        world.AddChild(manager);
        manager._spawner = new MultiplayerSpawner
        {
            Name = "RadioSpawner",
            SpawnPath = new NodePath("../" + NodeName),
            SpawnFunction = Callable.From((Variant data) => (Node)RadioBody.Create(RadioState.FromDict(data.AsGodotDictionary()), origin)),
        };
        world.AddChild(manager._spawner);
        Instance = manager;
        return manager;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => NetLink.Online(this);

    // ---- client API ----------------------------------------------------------------------------

    /// <summary>Throws a radio into the world from where the player stands.</summary>
    public void Throw(RadioState state)
    {
        if (!Online)
        {
            AddChild(RadioBody.Create(state with { Owner = 0, Name = "" }, Origin));
            return;
        }
        RpcId(1, MethodName.RequestThrow, state.ToDict());
    }

    /// <summary>Asks to take a radio back. <paramref name="granted"/> runs if nobody was quicker.</summary>
    public void PickUp(RadioBody radio, Action granted)
    {
        if (!Online)
        {
            radio.QueueFree();
            granted();
            return;
        }
        _pendingPickUp = granted;
        RpcId(1, MethodName.RequestPickUp, radio.Name);
    }

    /// <summary>
    /// Puts a CD in and starts it, from the beginning, for everyone. <paramref name="length"/> is
    /// only trusted for a personal CD (id &lt; 0), which the server has never seen.
    /// </summary>
    public void Play(RadioBody radio, int cdId, float length)
    {
        if (!Online) { StartOn(radio, cdId, length); return; }
        RpcId(1, MethodName.RequestPlay, radio.Name, cdId, length);
    }

    public void Stop(RadioBody radio)
    {
        if (!Online) { radio.Playing = false; return; }
        RpcId(1, MethodName.RequestStop, radio.Name);
    }

    /// <summary>The nearest radio within reach of a point, or null.</summary>
    public RadioBody? Nearest(Vector3 point, float reach)
    {
        RadioBody? best = null;
        float bestDist = reach;
        foreach (var node in GetChildren())
            if (node is RadioBody r && !_claimed.Contains(r.Name))
            {
                float d = r.GlobalPosition.DistanceTo(point);
                if (d < bestDist) { bestDist = d; best = r; }
            }
        return best;
    }

    /// <summary>The nearest radio that is playing within <paramref name="radius"/>, or null — the one to dance to.</summary>
    public RadioBody? NearestPlaying(Vector3 point, float radius)
    {
        RadioBody? best = null;
        float bestDist = radius;
        foreach (var node in GetChildren())
            if (node is RadioBody { Playing: true } r && r.Cd != null && r.WantedPosition < r.Length)
            {
                float d = r.GlobalPosition.DistanceTo(point);
                if (d < bestDist) { bestDist = d; best = r; }
            }
        return best;
    }

    /// <summary>Music to dance to: where it plays (a radio lying there, or the player carrying one), which CD, since when.</summary>
    public readonly record struct Music(Node3D Source, int CdId, double StartedAt)
    {
        public bool BeatAt(double now, out float phase, out int beat, out int bar, out MusicStyle style) =>
            RadioBody.BeatOf(CdId, StartedAt, now, out phase, out beat, out bar, out style);
    }

    /// <summary>
    /// The nearest music within <paramref name="radius"/> (#261): a playing radio in the world, or a
    /// player carrying one that plays, in the hand or on the back. Null when none, or when the CD
    /// is unknown here (nothing to take the beat from). With <paramref name="heard"/>, only music
    /// this machine's speaker actually plays: a radio "on" with a CD it cannot load (someone
    /// else's personal CD, a failed fetch) is silent, and must not offer a dance.
    /// </summary>
    public Music? NearestMusic(Vector3 point, float radius, bool heard = false)
    {
        if (heard && !MusicAudible()) return null;
        Music? best = null;
        float bestDist = radius;
        if (NearestPlaying(point, radius) is { } radio && (!heard || radio.Speaker is { Playing: true }))
        {
            best = new Music(radio, radio.CdId, radio.StartedAt);
            bestDist = radio.GlobalPosition.DistanceTo(point);
        }
        // the church radio (#370), inside the church this player is in
        if (Interiors.ChurchRadios.Instance?.Music(point, bestDist) is { } church
            && (!heard || church.Source is RadioSpeaker { Playing: true }))
        {
            best = church;
            bestDist = church.Source.GlobalPosition.DistanceTo(point);
        }
        if (Players == null) return best;
        double now = ClockSync.ServerNow;
        foreach (var p in Players())
        {
            if (!IsInstanceValid(p) || !p.IsInsideTree() || RadioPlay.Decode(p.HeldRadio) is not { } play || !play.Sounding(now)) continue;
            if (CdLibrary.Instance?.Find(play.CdId) == null) continue;
            if (heard && p.GetNodeOrNull<RadioSpeaker>(HeldSpeakerName) is not { Playing: true }) continue;
            float d = p.GlobalPosition.DistanceTo(point);
            if (d < bestDist) { bestDist = d; best = new Music(p, play.CdId, play.StartedAt); }
        }
        return best;
    }

    /// <summary>
    /// Whether music can reach this machine's speakers at all: false when the Music bus, or any
    /// bus it feeds into (Master), is muted — the Music or Master slider at 0 mutes them
    /// (<see cref="Audio.SfxBus.ApplyVolumes"/>).
    /// </summary>
    private static bool MusicAudible()
    {
        int bus = AudioServer.GetBusIndex(Audio.SfxBus.Music);
        for (int hops = 0; bus >= 0 && hops < 16; hops++)
        {
            if (AudioServer.IsBusMute(bus) || AudioServer.GetBusVolumeDb(bus) <= -79f) return false;
            if (bus == 0) return true;
            bus = AudioServer.GetBusIndex(AudioServer.GetBusSend(bus));
        }
        return true;
    }

    // ---- server side ---------------------------------------------------------------------------

    private static void StartOn(RadioBody radio, int cdId, float length)
    {
        radio.CdId = cdId;
        radio.StartedAt = ClockSync.ServerNow;
        radio.Length = length;
        radio.Playing = true;
    }

    /// <summary>
    /// How long a CD lasts, as far as this side can trust it: the library's word for a shared CD,
    /// the player's (bounded) for a personal one; negative when the CD cannot be played here.
    /// </summary>
    internal static float TrustedLength(int cdId, float claimed)
    {
        if (cdId > 0) return CdLibrary.Instance?.All.GetValueOrDefault(cdId) is { } cd ? cd.Duration : -1f;
        if (cdId < 0 && float.IsFinite(claimed) && claimed > 0) return Math.Min(claimed, CdBurner.MaxSeconds + 1);
        return -1f;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestThrow(Godot.Collections.Dictionary data)
    {
        if (!Multiplayer.IsServer() || _spawner == null) return;
        long sender = Multiplayer.GetRemoteSenderId();
        var thrown = RadioState.FromDict(data);
        // a full-strength throw (ThrowAim.MaxSpeed) from a sprint, with room to spare
        if (thrown.Velocity.Length() > 40f || !thrown.Position.IsFinite)
        {
            RpcId(sender, MethodName.ThrowRefused);
            return;
        }
        // the CD it was playing in the hand carries on, if the server can vouch for it
        float length = thrown.Playing ? TrustedLength(thrown.CdId, thrown.Length) : -1f;
        var state = thrown with
        {
            Owner = sender, Name = $"radio_{sender}_{++_counter}", Settled = false,
            Playing = length > 0 && double.IsFinite(thrown.StartedAt) && thrown.StartedAt <= ClockSync.ServerNow + 1,
            Length = Math.Max(length, 0),
        };
        _spawner.Spawn(state.ToDict());
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPickUp(string name)
    {
        if (!Multiplayer.IsServer()) return;
        long sender = Multiplayer.GetRemoteSenderId();
        if (GetNodeOrNull<RadioBody>(name) is not { } radio || !_claimed.Add(name))
        {
            RpcId(sender, MethodName.PickUpRefused);
            return;
        }
        radio.QueueFree();   // the spawner removes it on every client
        _claimed.Remove(name);
        RpcId(sender, MethodName.PickUpGranted);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestPlay(string name, int cdId, float length)
    {
        if (!Multiplayer.IsServer()) return;
        if (GetNodeOrNull<RadioBody>(name) is not { } radio) return;
        length = TrustedLength(cdId, length);
        if (length <= 0) return;
        StartOn(radio, cdId, length);
        GD.Print($"[radio] {name} plays CD {cdId} from {radio.StartedAt:F2}");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestStop(string name)
    {
        if (!Multiplayer.IsServer()) return;
        if (GetNodeOrNull<RadioBody>(name) is { } radio) radio.Playing = false;
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PickUpGranted()
    {
        var granted = _pendingPickUp;
        _pendingPickUp = null;
        granted?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PickUpRefused()
    {
        _pendingPickUp = null;
        Refused?.Invoke("Someone else picked that radio up first.");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ThrowRefused() => Refused?.Invoke("The server refused that throw; the radio is back in your pocket.");

    /// <summary>
    /// Server: a player left, and nobody simulates their radios any more. They are put back
    /// where they were last seen, standing, with the server as their authority.
    /// </summary>
    public void ForgetOwner(long peer)
    {
        if (_spawner == null) return;
        var respawn = new List<RadioState>();
        foreach (var node in GetChildren())
            if (node is RadioBody r && r.Owner == peer)
            {
                respawn.Add(r.Capture() with { Owner = 0, Name = $"radio_srv_{++_counter}", Settled = true });
                r.QueueFree();
            }
        foreach (var state in respawn) _spawner.Spawn(state.ToDict());
    }

    /// <summary>
    /// Ends a CD when it has run out, and clears radios nobody has been near for a long time.
    /// Done by whoever owns the list: the server online, the client offline.
    /// </summary>
    public override void _Process(double delta)
    {
        if (!NetworkManager.DedicatedServer && DisplayServer.GetName() != "headless") UpdateHeld();
        if (!NetLink.IsServer(this)) return;   // a client, or the link is down (#211)
        _housekeeping += delta;
        if (_housekeeping < 1) return;
        double step = _housekeeping;
        _housekeeping = 0;

        var players = PlayerPositions?.Invoke().ToList() ?? new List<Vector3>();
        foreach (var node in GetChildren())
        {
            if (node is not RadioBody r) continue;
            if (r.Playing && r.WantedPosition >= r.Length) Ended(r);
            bool near = players.Count == 0 || players.Any(p => p.DistanceTo(r.GlobalPosition) < LonelyDistance);
            r.LonelyFor = near ? 0 : r.LonelyFor + step;
            if (r.LonelyFor > LonelyTime) r.QueueFree();
        }
    }

    /// <summary>
    /// Client: a speaker on every player carrying a radio that plays — in the hand or on the back
    /// (#261) — none on anyone else. The carrier's own copy too: they hear their radio like everyone near them.
    /// </summary>
    private void UpdateHeld()
    {
        if (Players == null) return;
        foreach (var player in Players())
        {
            if (!IsInstanceValid(player)) continue;
            var speaker = player.GetNodeOrNull<RadioSpeaker>(HeldSpeakerName);
            var play = RadioPlay.Decode(player.HeldRadio);
            if (play is not { } p)
            {
                if (speaker != null) speaker.On = false;   // kept, silent: cheaper than a node per switch
                continue;
            }
            if (speaker == null)
            {
                speaker = new RadioSpeaker { Name = HeldSpeakerName, Position = new Vector3(0, 1.15f, 0) };
                player.AddChild(speaker);
            }
            speaker.CdId = p.CdId;
            speaker.StartedAt = p.StartedAt;
            speaker.Length = p.Length;
            speaker.On = true;
        }
    }
}
