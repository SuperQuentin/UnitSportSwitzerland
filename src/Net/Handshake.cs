using Godot;

namespace UnitSport.Net;

/// <summary>
/// The version check on connect, at <c>World/Handshake</c> on the server and every client. A
/// client says which wire <see cref="Protocol"/> it speaks before anything else; the server
/// answers <c>Welcome</c> and only then spawns its player and sends it the world, or refuses it.
///
/// <para>
/// Godot numbers a node's RPCs by their names, so an RPC added to any node shifts the others and
/// two versions of the game call each other's methods by mistake. This node's three RPCs
/// (<c>Hello</c>, <c>Refuse</c>, <c>Welcome</c>) must therefore never change, whatever else does:
/// they are how two versions find out they differ. A client older than the check never says hello
/// and is kicked through <see cref="ChatManager.KickPeer"/>, whose message every version shows; a
/// server older than the check never welcomes, and the client leaves with a message.
/// </para>
/// </summary>
public partial class Handshake : Node
{
    public const string NodeName = "Handshake";

    /// <summary>
    /// Bumped by every change to what goes over the wire that an older peer would misread.
    /// 1: everything before the check. 2: positions in LV95, not world space (#185).
    /// 3: one packed NetPose array replaces the replicated BodyPose and TrainPose (#221).
    /// 4: built structures, World/Structures (#274).
    /// 5: gadgets as placed kinds 5-10, with a zipline/ladder payload (#275).
    /// 6: Battle Royale prefabs: Structures.AddPieces, match-owned gadgets (#276).
    /// 7: campfire and field workbench as placed kinds 3-4, the server-set lighting time (#272).
    /// 8: shops and PAUSA vending machines: ShopService buy/sell/vend, card payment (#273).
    /// 9: structures sent by distance, PlacedObjects AskBurn/Burnt, hang and climb pose kinds (#359).
    /// 10: World/ChurchRadios (Ask*/Set), CdInfo's source, MusicStyle.RatDance (#370).
    /// 11: ChatManager.WorldTime carries the world clock (hour0, epoch, day length); ClockSync.Pong
    /// the server's Unix offset (#452).
    /// 12: BattleRoyale RequestPing/Pinged, BrEntrant.Party (#469).
    /// 13: BattleRoyale ReportDowned/RequestRevive/Revived/OutNow/DownNews, BrEntrant.Downed (#475).
    /// 14: item events Horn/Fondue/Smoke, items Alphorn/FonduePot/SmokeCanister (#478).
    /// 15: BattleRoyale RequestRecall/Recalled/RecallNews, BrState.RecallPoints, ItemId.Dogtag (#480).
    /// 16: FootPlayer.NetPose may carry a VR player's two hands after the pose (14 or 17 floats, #439).
    /// 17: item events FishCast/FishEnd, items FishingRod..RoundGoby 194-222 (#493).
    /// 18: the world clock on the wire is (Env0, EnvEpoch, HourShift, MinutesPerDay), EnvEpoch in simulated seconds (#579).
    /// 19: World/Pallets (AskTake/AskDrop/Took/Dropped/Snapshot), RideKind.Forklift and the load in its pose (#583).
    /// 20: RideKind.Excavator and its arm in the pose (#611).
    /// 21: RideKind.WheelLoader, its frame, arm and bucket in the pose (#612).
    /// 22: RideKind.MiniExcavator, its blade in the pose's bucket float (#614).
    /// 23: RideKind.CompactRoller, its bend and its vibration in the pose (#614).
    /// 24: RideKind.Telehandler, its wheels, steering mode and boom in the pose (#614).
    /// 25: RideKind.WheelLoaderForks; the telehandler's and the fork loader's pallet in their pose's lift float (#615).
    /// 26: World/Sleepers (Challenge/Prove, Snapshot/Add/Remove, WakeAt) (#644).
    /// </summary>
    public const int Protocol = 26;

    /// <summary>How long either side waits for the other's half of the check.</summary>
    public const double WaitSeconds = 10;

    private bool _server;
    private ChatManager? _chat;
    private readonly Dictionary<long, double> _waiting = new();
    private double _clientWaited = -1;

    /// <summary>Server: a peer speaks this protocol; it may have its player and the world.</summary>
    public event Action<long>? Accepted;

    /// <summary>Client: the server speaks this protocol.</summary>
    public event Action? Welcomed;

    /// <summary>Client: the server refused, or never answered; the reason, for the player.</summary>
    public event Action<string>? Refused;

    public static Handshake CreateServer(ChatManager chat) => new() { Name = NodeName, _server = true, _chat = chat };

    public static Handshake CreateClient() => new() { Name = NodeName };

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>Server: a peer connected; it has <see cref="WaitSeconds"/> to say hello.</summary>
    public void PeerConnected(long peer) => _waiting[peer] = Now;

    /// <summary>Server: a peer left, accepted or not.</summary>
    public void PeerLeft(long peer) => _waiting.Remove(peer);

    /// <summary>Client: connected; says which protocol this build speaks.</summary>
    public void Begin()
    {
        _clientWaited = 0;
        RpcId(1, MethodName.Hello, Protocol);
    }

    public override void _Process(double delta)
    {
        if (_server)
        {
            foreach (var (peer, since) in _waiting.ToList())
                if (Now - since > WaitSeconds)
                {
                    _waiting.Remove(peer);
                    GD.Print($"[net] peer {peer} never said which protocol it speaks: an older version, kicked");
                    _chat?.KickPeer(peer, "this server runs a newer version of the game. Update yours to play here.");
                }
            return;
        }
        if (_clientWaited < 0) return;
        if (_clientWaited == 0) _clientWaited = Now;   // first frame as a client: start the clock
        if (Now - _clientWaited < WaitSeconds) return;
        _clientWaited = -1;
        Refused?.Invoke("The server did not answer the version check: it runs an older version of the game.");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Hello(int protocol)
    {
        if (!_server) return;
        long peer = Multiplayer.GetRemoteSenderId();
        if (!_waiting.Remove(peer)) return;
        if (protocol != Protocol)
        {
            string newer = protocol < Protocol ? "a newer" : "an older";
            GD.Print($"[net] peer {peer} speaks protocol {protocol}, this server {Protocol}: refused");
            RpcId(peer, MethodName.Refuse, $"This server runs {newer} version of the game (protocol {Protocol}, yours {protocol}).");
            var tree = GetTree();
            tree.CreateTimer(0.3).Timeout += () =>
            {
                if (tree.Root.Multiplayer.MultiplayerPeer is ENetMultiplayerPeer enet) enet.DisconnectPeer((int)peer);
            };
            return;
        }
        RpcId(peer, MethodName.Welcome, Protocol);
        Accepted?.Invoke(peer);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Welcome(int protocol)
    {
        if (_server || _clientWaited < 0) return;
        _clientWaited = -1;
        Welcomed?.Invoke();
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Refuse(string reason)
    {
        if (_server) return;
        _clientWaited = -1;
        Refused?.Invoke(reason);
    }
}
