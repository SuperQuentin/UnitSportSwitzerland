using System.Collections.Generic;
using Godot;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// The colonies' airlocks online (#2000, at <c>World/ColonyDoors</c> on the server and every client): the server owns each
/// airlock's cycle. A client's E (or a hangar door opening for its rover) asks the server (<see cref="Ask"/>); the server
/// runs the cycle on its own <see cref="ColonyAirlocks"/> where it has built the colony (round its bots and players), keeps
/// which way every airlock last cycled in a table (the colony may not be built there), and sends each cycle to every client
/// (<see cref="Cycle"/>), which runs it on its own doors from the same start: the same 0.9 s slides and 3 s pump, so the
/// doors move alike everywhere and a bot walks through a door the clients see open. A client that builds a colony asks for
/// its table (<see cref="AskState"/>) and settles its doors on it. Offline nothing here runs: the airlocks are the game's own.
/// </summary>
public partial class ColonyDoors : Node
{
    public const string NodeName = "ColonyDoors";

    public static ColonyDoors? Instance { get; private set; }

    /// <summary>Which way each colony's airlocks last cycled (true: out), on the server; a client's copy as it was sent.</summary>
    private readonly Dictionary<string, bool[]> _table = new();

    public ColonyDoors() => Name = NodeName;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    private bool Online => Multiplayer.MultiplayerPeer != null && Multiplayer.MultiplayerPeer is not OfflineMultiplayerPeer;

    /// <summary>Whether this peer asks the server for a cycle instead of running it: a client online.</summary>
    public static bool Routes => Instance is { Online: true } i && !i.Multiplayer.IsServer();

    /// <summary>Whether this peer is the server online, which tells the clients of every cycle it runs.</summary>
    public static bool Serves => Instance is { Online: true } i && i.Multiplayer.IsServer();

    private bool[] TableOf(string colony, int locks)
    {
        if (!_table.TryGetValue(colony, out var t) || t.Length < locks)
        {
            var grown = new bool[locks];
            if (t != null) System.Array.Copy(t, grown, t.Length);
            _table[colony] = t = grown;
        }
        return t;
    }

    // ---- a client asks ---------------------------------------------------------------------------------------------

    /// <summary>A client's request for a cycle of <paramref name="colony"/>'s airlock <paramref name="index"/>.</summary>
    public static void Ask(string colony, int index, bool outward) =>
        Instance?.RpcId(1, MethodName.AskCycle, colony, index, outward);

    /// <summary>A client that has just built a colony asks for its airlocks' table.</summary>
    public static void AskStateOf(string colony) => Instance?.RpcId(1, MethodName.AskState, colony);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskCycle(string colony, int index, bool outward)
    {
        if (!Multiplayer.IsServer() || index < 0 || index > 64 || MoonColonies.Find(colony) is null) return;
        // the server's own doors where it has the colony; refused while a cycle runs there, as the E of a player offline
        if (ColonyAirlocks.Of(colony) is { } set)
        {
            set.Request(index, outward);
            return;
        }
        Started(colony, index, outward);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AskState(string colony)
    {
        if (!Multiplayer.IsServer() || MoonColonies.Find(colony) is null) return;
        _table.TryGetValue(colony, out var t);
        RpcId(Multiplayer.GetRemoteSenderId(), MethodName.State, colony, Pack(t ?? []));
    }

    // ---- the server tells ------------------------------------------------------------------------------------------

    /// <summary>The server ran (or, without the colony built, recorded) a cycle: into the table and out to every client.</summary>
    public static void Started(string colony, int index, bool outward)
    {
        if (Instance is not { } i || !Serves) return;
        i.TableOf(colony, index + 1)[index] = outward;
        i.Rpc(MethodName.Cycle, colony, index, outward);
    }

    /// <summary>The way the server last cycled an airlock (its table), for a colony it builds again.</summary>
    public static bool? Last(string colony, int index) =>
        Instance is { } i && i._table.TryGetValue(colony, out var t) && index < t.Length ? t[index] : null;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Cycle(string colony, int index, bool outward)
    {
        TableOf(colony, index + 1)[index] = outward;
        ColonyAirlocks.Of(colony)?.Run(index, outward);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void State(string colony, byte[] packed)
    {
        var t = TableOf(colony, packed.Length);
        for (int k = 0; k < packed.Length; k++) t[k] = packed[k] != 0;
        if (ColonyAirlocks.Of(colony) is { } set)
            for (int k = 0; k < packed.Length; k++) set.Settle(k, t[k]);
    }

    private static byte[] Pack(bool[] t)
    {
        var b = new byte[t.Length];
        for (int k = 0; k < t.Length; k++) b[k] = t[k] ? (byte)1 : (byte)0;
        return b;
    }
}
