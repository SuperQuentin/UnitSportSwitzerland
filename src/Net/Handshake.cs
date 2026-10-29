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
    /// 27: ChatManager HandOver/HandedOver/ReceiveInventory, /transfer (#649).
    /// 28: the wheel loader's bucket carries a pallet, in its pose's lift float as the fork loader's (#615).
    /// 29: the site tipper (104) and mixer (105); a mixer's pose has -1 for its rpm with the engine off (#613).
    /// 30: RideKind.MiniDumper, its skip in the pose and the parked flags (#614).
    /// 31: a pallet in a tipper's body or a mini dumper's skip (#615): in their pose and parked flags,
    /// VehicleBody.BedLoad replicated, PalletService's AskBed / Bedded / BedLoaded.
    /// 32: farming (#494): FarmField Subscribe/Work/Cells, AssetKind.Fields 14, FarmStands and FarmSales
    /// nodes and RPCs, PassengerService auger offers, ShopService.RequestDeliver's door, PlacedKind.FarmStand
    /// 11, ShopType.FarmCoop 12, items 300-340, HeavyCatalog 102-103, TrailerCatalog 6-9 and the boat
    /// trailers it builds on (#463).
    /// 33: World/Arrival (Rules: the server's spawn point and teleport rule) (#799).
    /// 34: World/Cranes (AskOperate, Send, AskRelease; Operated, Posed, Released, Carry, Snapshot) (#618).
    /// 35: bots (#837): NPC spawn data carries a driver kind and a name, BrEntrant.Bot/BotKills, BrState.Bots.
    /// 36: seated on a stadium stand (#885): FootPlayer.PoseKind 6 (PoseSit), Anim.X the feet's drop, BodyPose the hip.
    /// </summary>
    /// 37: World/Concerts (AskHost, AskCue, Frame on channel 4; State, Framed) (#885).
    /// 38: an interior plan's entrance may be raised (EntrancePlan.Level, #827): an old peer would put its doorway on the ground floor.
    /// 39: ski slopes (#887): AssetKind.Pistes, World/Snow (weather and slope wear).
    /// 40: ski lifts part 2 (#915): AssetKind.Ropeways 16, FootPlayer LiftLine/LiftCarrier replicated.
    /// 41: the snowboard (#922): RideKind.Snowboard 202, its stance and switch in FootPlayer.TuningBits.
    /// 42: World/Concerts AskRig and RigSet: an imported MVR rig on a hosted set, gzipped (#941).
    /// 43: World/Concerts SongState: a venue's setlist moved by hand, /concert song (#994).
    /// 44: World/TrafficGenerators (AskSet, AskRemove; Set, Remove, Snapshot) (#971).
    /// 45: World/Arrival Rules carries the server's world, a test world picked in the menus (#999).
    /// 46: name tags (#1136): FootPlayer.DeviceKind replicated, World/Chat ReceiveTagNames (sorts after NotifyKicked).
    /// 47: the e-bike (#1159): RideKind.EBike 203 and its low-frame EBikeOpen 204, catalogue bicycles.
    /// 48: a CD's voice and music stems over AssetKind.Cd (TileId.N 1 and 2), the radios' and the concert's voice/music mix (#1153).
    /// 49: World/Accounts (Ask, Present, Welcome, Refuse): the account step after this check (#1123).
    /// 50: World/Adventure (#1223: RequestStart/Abandon/Track/Deliver; SectionSync, QuestNote, Granted, DeliverOk, Refused, Unsaved), World/Bank Credited.
    /// 51: World/Adventure/Places (#1224: RequestInteract; Stamped, Synced, FogFull, FogDelta), AssetKind.AtlasFacts 18.
    /// 52: World/CinemaBooths (#1238: AskSet; Set): a cinema's house lights and screen.
    /// 53: World/Arrival Rules carries the stamina and lift switches; FootPlayer.PoseKind 8 (PoseWallClimb) (#1225).
    /// 54: World/Adventure/Travel (#1226: RequestTravel/Buy/PinAdd/PinEdit/PinRemove; TellMode, TellUnlocked, TellRefused, Depart, PartyPinsState).
    /// 55: World/Adventure/Rewards (#1227: RequestOpen/Pick/SealStep/Offer/Guide/Photo/Fauna/Trial/TrialGate/TrialLeave/Board/
    /// Ghost/GhostPart; TellOpened/Picked/Seal/Refused/Offered/Photo/TrialStarted/TrialGate/TrialEnded/Board/Ghost, GuideLeg),
    /// PlacedKind.TravelFlag 12, ItemEventKind.Gust 11.
    /// 56: World/Invites Ended (kind, name, outcome): the sender of a direct invite learns accepted, declined or lapsed (#1253; 55 is main's).
    /// 57: World/Versteckis (#1268: RequestDisguise/Tag/Sniff/Decoy/Sprint/Moo/Objective; SetGames, Enter, Kit, PutAt, Release, Caught,
    /// Missed, Sniffed, Sounded), the <c>versteckis</c> invite kind, <c>InterestService.Withheld</c>, ItemId 460-467, ItemUse.Round.
    /// 58: World/CinemaBooths AskSet and Set carry the hall (#1279: a panel per hall; 57 is main's).
    /// 59: World/Adventure/Festivals (#1269 part 1: RequestRing/TestDate; TellFestivals, TellBoard, TellConverted, TellRang, TellRefused),
    /// the <c>festivals</c> section, <c>OccasionFacets.Games</c> (1 &lt;&lt; 6) on the occasions wire.
    /// 60: World/Together (#1266: Links, GateOpen, PotState, BreadLost, TellFeast, TellHaul, the Ask* requests), invite kinds rope and fondue.
    /// 61: World/CinemaBooths AskVideoRpc and SetVideo (#1298: a hall's video link and its start on the shared clock).
    /// 62: World/CinemaBooths AskVideoRpc and SetVideo carry the hall's relay switch; WatchRelay, RelayFrame, RelaySound,
    /// RelayStatus on channel 5 (#1298 phase 2: the hall's video through the server).
    /// 63: World/CinemaBooths AskLiveRpc, PushFrame, PushSound (#1298 phase 3: a player's screen, window or webcam on a hall).
    /// 64: World/CinemaBooths AskVideoRpc, AskLiveRpc and SetVideo carry the hall's 3D layout (#1298 phase 6).
    /// 65: World/CinemaBooths SetPool (#1298: the server's own films a showing hall draws from).
    /// 66: World/CinemaBooths AskDisplayRpc and SetDisplay (#1298: a hall's projector fit and ratio).
    /// 67: World/Errands (#1267: RequestAccept/Share/Join; Accepted, Shared, TookBack, Told), the <c>errands</c> section, ItemId 430-444.
    /// 68: World/Adventure/Festivals RequestFahneStart, RequestFahneRoutine, TellFahneStart, TellFahneResult (#1269 part 5: Fahnenschwingen).
    /// 69: World/Alcohol (#1271: ReportDrank, RequestSelfTest, RequestOffer; TellResult), World/Arrival Rules carries <c>--alcohol</c>, the <c>breath</c> invite kind, ItemId 486-488, ItemUse.Breath.
    /// 70: World/Schwing (#1269 Schwingen: RequestJoin/RequestLeave/Act/Tug; Begin, State, Thrown, Ended, TellQueue), the <c>schwing</c> invite kind, FootPlayer.SchwingPose, ItemId 500.
    /// 71: World/Adventure/Festivals alphorn game (#1269 part 4: RequestHornStart/Song; TellHornSeat, TellHornResult).
    /// 72: World/Adventure/Festivals/Stein RequestBegin, RequestThrow, TellBegin, TellNo, TellLanded (#1269 part 3: the stone put).
    /// 73: World/Adventure/Festivals Désalpe RequestDesalpeDress/Start/Vote, TellDesalpe, TellDesalpeSnap, TellDesalpeResult, TellDesalpeVoted (#1269 part 7).
    /// 74: World/WorldEvents (#1339: RequestHelp; PlanOut, MovedOut, StateOut, EndedOut, Told), the <c>errands</c> section's <c>events</c> count.
    /// 75: Interiors/Bowling lanes state carries each pair's house balls on the return (#1336).
    /// 76: World/Adventure/Festivals RequestMarket, TellMarket (#1348: the Christmas market's stalls and Samichlaus), ItemId 501-502.
    /// 77: World/Adventure/Festivals RequestEscalade, TellEscalade (#1351: the Escalade's Marmite, soup pot and Course), ItemId 503-504.
    /// 78: World/Adventure/Festivals RequestSechse, TellSechse (#1358: the Sechseläuten's bet on the Böögg, the purse, the Cervelat), ItemId 510.
    public const int Protocol = 78;

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

    /// <summary>Server: a client of a newer protocol was refused: a newer release is out (#1088, <see cref="ServerAutoUpdate"/>).</summary>
    public event Action? NewerPeerRefused;

    /// <summary>Server: added to the refusal a newer client reads, e.g. that this server updates itself (#1088).</summary>
    public Func<string?>? NewerHint { get; set; }

    public static Handshake CreateServer(ChatManager chat) => new() { Name = NodeName, _server = true, _chat = chat };

    public static Handshake CreateClient() => new() { Name = NodeName };

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>Server: a peer connected; it has <see cref="WaitSeconds"/> to say hello.</summary>
    public void PeerConnected(long peer) => _waiting[peer] = Now;

    /// <summary>Server: a peer left, accepted or not.</summary>
    public void PeerLeft(long peer) => _waiting.Remove(peer);

    /// <summary>Server: a peer that will never say hello and must not be kicked for it: a remote console (<see cref="ConsoleLink"/>).</summary>
    public void Exempt(long peer) => _waiting.Remove(peer);

    /// <summary>Client: connected; says which protocol this build speaks.</summary>
    public void Begin()
    {
        _clientWaited = 0;
        // --fakeprotocol N: a client of another version, for the checks of a refusal (#1088)
        RpcId(1, MethodName.Hello, Core.CmdArgs.Int("--fakeprotocol") ?? Protocol);
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
            string reason = $"This server runs {newer} version of the game (protocol {Protocol}, yours {protocol}).";
            if (protocol > Protocol && NewerHint?.Invoke() is { } hint) reason += " " + hint;
            RpcId(peer, MethodName.Refuse, reason);
            if (protocol > Protocol) NewerPeerRefused?.Invoke();
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
