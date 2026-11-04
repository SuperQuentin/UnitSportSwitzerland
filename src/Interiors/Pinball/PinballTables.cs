using Godot;
using UnitSport.Net;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// Every venue's pinball machines (#1325), a node at <c>World/Pinball</c> on the server and on every client
/// (RPCs go by node path).
///
/// <para>
/// <b>The player's client runs the table</b>: the solver and the rules (<see cref="PinballGame"/>) step at 1 ms
/// on the machine of whoever is up, with no lag. It streams 20 times a second what the others need: the
/// switch events (with flipper presses, Start and skips, which together are the whole game) and the balls'
/// and flippers' poses. <b>The server keeps the score and the turn</b>: it applies the events to its own
/// rules (<see cref="PinballGame.Mirror"/>: the same rules from the same events give the same score), keeps
/// the game's log, hands the table to the next player's client when the turn passes (their client rebuilds the
/// game from the log, <see cref="PinballGame.Resume"/>), and sends the watchers the poses, the events (for the
/// sounds) and, on change, the backbox and the lamps (<see cref="PinballScreen"/>). Only peers inside the venue
/// hear of it (<see cref="InteriorManager.SpaceOf"/>); a client asks on walking in.
/// </para>
/// <para>
/// Up to four players a game, real pinball's way: Start at the machine during the first ball adds a player.
/// A player who leaves the venue loses their turns; a player up who leaves ends their own ball (the server
/// skips them). Every action is a <c>Serve*</c> method taking the peer, which a bot could call.
/// </para>
/// </summary>
public partial class PinballTables : Node
{
    public const string NodeName = "Pinball";
    /// <summary>How often the player up's client sends a frame [s].</summary>
    public const double FrameEvery = 0.05;
    /// <summary>The player up silent this long (crashed, gone) is skipped [s].</summary>
    public const double RunnerTimeout = 6;

    public static PinballTables? Instance { get; private set; }

    /// <summary>One machine's game as this peer knows it.</summary>
    public sealed class Machine
    {
        public int Table;
        public int Seed;
        /// <summary>The players' peers in turn order (the rules' player index); the one whose client runs the table.</summary>
        public readonly List<long> Players = new();
        public long Runner;
        /// <summary>Server: the rules' mirror, the game's whole log, players asked to join and not yet added.</summary>
        public PinballRules? Rules;
        public readonly List<PinballSolver.Event> Log = new();
        public readonly Queue<long> Joining = new();
        public double LastFrame;
        /// <summary>Client: the frames heard of this machine (the net check's).</summary>
        public int Frames;
        /// <summary>The machine's high scores (the server's, or as it sent them).</summary>
        public PinballHighScores Highs = new();
        /// <summary>Server: this game's scores went to the high-score table.</summary>
        public bool Recorded;
        /// <summary>What the backbox and lamps show (packed), and the poses of the last two frames with their times.</summary>
        public byte[] Screen = Array.Empty<byte>();
        public PinballGame.Poses? Now, Before;
        public double NowAt, BeforeAt;
        public bool Playing => Rules?.State == PinballRules.Phase.Playing || Runner != 0;
    }

    protected readonly Dictionary<(string Key, int Index), Machine> Machines = new();

    protected bool Online => NetLink.Online(this);
    protected long MyId => Multiplayer.MultiplayerPeer is { } p and not OfflineMultiplayerPeer ? Multiplayer.GetUniqueId() : 1;
    protected static double Clock => ClockSync.SimNow;

    public static PinballTables Create(Node world)
    {
        var node = new PinballTables { Name = NodeName };
        world.AddChild(node);
        Instance = node;
        return node;
    }

    public override void _Ready()
    {
        // a server keeps scores by id (#1359): the ones still under a player's name become its own once it is known
        if (PlayerIds.Server is { } ids && ids != _claimFrom) { _claimFrom = ids; ids.Identified += ClaimLegacy; }
    }

    private PlayerIds? _claimFrom;

    public override void _ExitTree()
    {
        if (_claimFrom != null) _claimFrom.Identified -= ClaimLegacy;
        _claimFrom = null;
        if (Instance == this) Instance = null;
    }

    /// <summary>
    /// The table of pinball piece <paramref name="index"/>: one of the four, from the venue's machines in the plan
    /// (<see cref="PinballLayouts.TableIn"/>: all different up to four, never two alike side by side), the same on every peer.
    /// </summary>
    public static int TableOf(InteriorLayout l, int index)
    {
        var machines = new List<(float X, float Z)>();
        int k = 0;
        for (int i = 0; i < l.Furniture.Count; i++)
        {
            var f = l.Furniture[i];
            if (f.Type != FurnitureType.Pinball) continue;
            if (i == index) k = machines.Count;
            machines.Add((f.X, f.Z));
        }
        return machines.Count == 0 ? 0 : PinballLayouts.TableIn(machines, k);
    }

    /// <summary>A piece's frame in its plan: turned, on its floor.</summary>
    public static Transform3D PieceFrame(InteriorLayout l, FurniturePlan f) =>
        new(new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2), new Vector3(f.X, l.FloorY(f.Floor) + f.Lift, f.Z));

    /// <summary>The machine at (<paramref name="key"/>, <paramref name="index"/>), made on first use.</summary>
    protected Machine MachineOf(string key, int index, int table)
    {
        if (!Machines.TryGetValue((key, index), out var m)) Machines[(key, index)] = m = new Machine { Table = table };
        return m;
    }

    /// <summary>The machine this peer runs (it is up), if any.</summary>
    public (string Key, int Index)? Running()
    {
        foreach (var (id, m) in Machines)
            if (m.Runner == MyId && m.Runner != 0) return id;
        return null;
    }

    public Machine? Peek(string key, int index) => Machines.GetValueOrDefault((key, index));

    /// <summary>The players' peers of a machine's game, in turn order (the check's and a bot's view).</summary>
    public IReadOnlyList<long> PlayersOf(string key, int index) => Peek(key, index)?.Players ?? (IReadOnlyList<long>)Array.Empty<long>();

    /// <summary>What a machine's backbox shows as this peer knows it.</summary>
    /// <summary>A machine's high scores as this peer knows them.</summary>
    public PinballHighScores? HighsOf(string key, int index) => Peek(key, index)?.Highs;

    public PinballScreen? ScreenOf(string key, int index) =>
        Peek(key, index) is { Screen.Length: > 0 } m ? PinballScreen.Unpack(m.Screen) : null;
}
