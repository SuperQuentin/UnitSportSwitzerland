using Godot;
using UnitSport.Net;

namespace UnitSport.Interiors;

/// <summary>
/// Bowling for real (#1323): every bowling centre's games, owned by the server, shown alike to everyone
/// inside. A node at <c>World/Bowling</c> on the server and on every client (RPCs go by node path).
///
/// <para>
/// The server keeps, per lane (a pair's furniture index and its left or right lane), who bowls there in
/// turn, their balls' rolls, the pins standing, the pinsetter's cycle and where each bowler's ball is.
/// Every action is a <c>Serve*</c> method taking the peer, which a bot calls with its own id: signing in at
/// the shoe counter (the shoes go on), taking a ball from a rack or the hood, and the release. A release
/// is the only thing a roll needs: every peer runs the ball down the lane from it
/// (<see cref="BowlingBall"/>); the pin deck is the server's (<see cref="PinDeck"/>), its poses streamed to
/// the peers inside and replayed a few tenths behind, so the roller's ball leaves the hand at once.
/// </para>
/// <para>
/// State goes only to peers inside the centre (<see cref="InteriorManager.SpaceOf"/>), as a whole centre's
/// blob on every change (a few hundred bytes); a client asks for it on walking in.
/// </para>
/// </summary>
public partial class BowlingLanes : Node
{
    public const string NodeName = "Bowling";
    public const ushort Rack = 0x3FF;
    /// <summary>Bowlers on one lane at most.</summary>
    public const int MaxBowlers = 6;
    /// <summary>House balls each lane keeps on the pair's return at the start (#1336): four on a return.</summary>
    public const int HouseStart = 2;
    /// <summary>How long a ball takes from the pit, through the subway, up into the hood [E: 6-10 s].</summary>
    public const double ReturnTime = 6.5;
    /// <summary>The pinsetter starts this long after the pins settle.</summary>
    public const double CycleDelay = 0.6;

    public static BowlingLanes? Instance { get; private set; }

    public enum BallAt : byte { None, Hand, Rolling, Return, Hood }

    /// <summary>A bowler at a lane: a player or a bot, their house ball and their game.</summary>
    public sealed class Bowler
    {
        public long Peer;
        public string Name = "";
        /// <summary>The house ball's colour (<see cref="InteriorMeshBuilder.Ball"/>) and weight in pounds.</summary>
        public byte Color, Weight;
        public BallAt Ball;
        /// <summary>When a returning ball reaches the hood, on the shared clock.</summary>
        public double BallBack;
        public readonly List<int> Rolls = new();
    }

    /// <summary>One lane's game.</summary>
    public sealed class Lane
    {
        public readonly List<Bowler> Bowlers = new();
        public int Turn;
        public ushort Standing = Rack;
        public PinsetterCycle.Kind CycleKind;
        public double CycleStart = double.NaN;
        /// <summary>Counts the lane's rolls: a client tells a new one from a resent state by it.</summary>
        public int RollId;
        /// <summary>The roll under way: its release and when the ball left the hand.</summary>
        public BowlingBall.Release Release;
        public double ReleaseAt = double.NaN;
        public long Roller;
        /// <summary>The pins as the last roll left them, in the lane's frame (index 0 the ball), until the machine clears them.</summary>
        public Transform3D[]? Rest;
        /// <summary>When everyone's game ended (a new one starts a while after).</summary>
        public double OverAt = double.NaN;
        /// <summary>
        /// House balls this lane keeps on the pair's return (#1336): anyone signed in on the pair without a ball takes one
        /// there. Colour and weight each; two at the start.
        /// </summary>
        public readonly List<(byte Color, byte Weight)> House = new();

        public bool Rolling => !double.IsNaN(ReleaseAt);
        public Bowler? Up => Turn >= 0 && Turn < Bowlers.Count ? Bowlers[Turn] : null;

        public bool CycleBusy(double now) => !double.IsNaN(CycleStart) && now < CycleStart + PinsetterCycle.Length(CycleKind);
    }

    /// <summary>A centre's games, by lane pair (its furniture index), two lanes each.</summary>
    protected readonly Dictionary<string, Dictionary<int, Lane[]>> Centres = new();

    protected bool Online => NetLink.Online(this);
    protected long MyId => Multiplayer.MultiplayerPeer is { } p and not OfflineMultiplayerPeer ? Multiplayer.GetUniqueId() : 1;
    protected static double Now => ClockSync.SimNow;

    public static BowlingLanes Create(Node world)
    {
        var node = new BowlingLanes { Name = NodeName };
        world.AddChild(node);
        Instance = node;
        return node;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        foreach (var decks in _decks.Values) decks.Deck?.Dispose();
        _decks.Clear();
    }

    /// <summary>The lanes of pair <paramref name="pair"/> in centre <paramref name="key"/>, made on first use.</summary>
    protected Lane[] PairOf(string key, int pair)
    {
        if (!Centres.TryGetValue(key, out var pairs)) Centres[key] = pairs = new();
        if (!pairs.TryGetValue(pair, out var lanes)) pairs[pair] = lanes = NewPair(key, pair);
        return lanes;
    }

    private readonly Dictionary<(string, int), Lane[]> _fresh = new();

    /// <summary>
    /// How a pair nobody has bowled at yet looks, for showing (#1336): never changed, never sent. A client shows its
    /// return's house balls before the server has a state for the pair.
    /// </summary>
    protected Lane[] Fresh(string key, int pair)
    {
        if (!_fresh.TryGetValue((key, pair), out var lanes)) _fresh[(key, pair)] = lanes = NewPair(key, pair);
        return lanes;
    }

    /// <summary>A pair as the centre opens it: two empty lanes, and the house balls on the return, the same on every peer (#1336).</summary>
    private static Lane[] NewPair(string key, int pair)
    {
        var lanes = new[] { new Lane(), new Lane() };
        for (int i = 0; i < 2; i++)
            for (int k = 0; k < HouseStart; k++)
            {
                uint h = (uint)Core.Fnv.Hash($"{key}#{pair}#{i}#{k}");
                lanes[i].House.Add(((byte)(h % InteriorMeshBuilder.BallColorCount), (byte)(10 + h / 7 % 5)));
            }
        return lanes;
    }

    /// <summary>The lane <paramref name="peer"/> bowls at, if they signed in anywhere.</summary>
    public (string Key, int Pair, int Lane, Bowler Bowler)? SignedIn(long peer)
    {
        foreach (var (key, pairs) in Centres)
            foreach (var (pair, lanes) in pairs)
                for (int l = 0; l < 2; l++)
                    foreach (var b in lanes[l].Bowlers)
                        if (b.Peer == peer) return (key, pair, l, b);
        return null;
    }

    /// <summary>Whether <paramref name="peer"/> wears bowling shoes: signed in at a lane.</summary>
    public bool WearsShoes(long peer) => SignedIn(peer) != null;

    /// <summary>The lane this peer's own player bowls at, if signed in.</summary>
    public (string Key, int Pair, int Lane, Bowler Bowler)? SignedInHere() => SignedIn(MyId);

    /// <summary>Whether this peer's own player wears the hire shoes.</summary>
    public bool WearsShoesHere() => WearsShoes(MyId);

    // ---- the wire: a whole centre in a few hundred bytes ------------------------------------------

    protected byte[] Pack(string key)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var pairs = Centres.GetValueOrDefault(key);
        w.Write((short)(pairs?.Count ?? 0));
        if (pairs != null)
            foreach (var (pair, lanes) in pairs)
            {
                w.Write((short)pair);
                foreach (var lane in lanes)
                {
                    w.Write((byte)lane.Turn);
                    w.Write(lane.Standing);
                    w.Write((byte)lane.CycleKind);
                    w.Write(lane.CycleStart);
                    w.Write(lane.RollId);
                    w.Write(lane.ReleaseAt);
                    w.Write(lane.Roller);
                    foreach (int v in lane.Release.Pack()) w.Write(v);
                    w.Write(lane.Rest != null);
                    if (lane.Rest != null) WritePoses(w, lane.Rest);
                    w.Write((byte)lane.House.Count);
                    foreach (var (c, wt) in lane.House) { w.Write(c); w.Write(wt); }
                    w.Write((byte)lane.Bowlers.Count);
                    foreach (var b in lane.Bowlers)
                    {
                        w.Write(b.Peer);
                        w.Write(b.Name);
                        w.Write(b.Color);
                        w.Write(b.Weight);
                        w.Write((byte)b.Ball);
                        w.Write(b.BallBack);
                        w.Write((byte)b.Rolls.Count);
                        foreach (int r in b.Rolls) w.Write((byte)r);
                    }
                }
            }
        return ms.ToArray();
    }

    protected void Unpack(string key, byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data));
        var pairs = new Dictionary<int, Lane[]>();
        int count = r.ReadInt16();
        for (int p = 0; p < count; p++)
        {
            int pair = r.ReadInt16();
            var lanes = new[] { new Lane(), new Lane() };
            foreach (var lane in lanes)
            {
                lane.Turn = r.ReadByte();
                lane.Standing = r.ReadUInt16();
                lane.CycleKind = (PinsetterCycle.Kind)r.ReadByte();
                lane.CycleStart = r.ReadDouble();
                lane.RollId = r.ReadInt32();
                lane.ReleaseAt = r.ReadDouble();
                lane.Roller = r.ReadInt64();
                var rel = new int[5];
                for (int i = 0; i < 5; i++) rel[i] = r.ReadInt32();
                lane.Release = BowlingBall.Release.Unpack(rel);
                if (r.ReadBoolean()) lane.Rest = ReadPoses(r);
                int house = r.ReadByte();
                for (int k = 0; k < house; k++) lane.House.Add((r.ReadByte(), r.ReadByte()));
                int n = r.ReadByte();
                for (int i = 0; i < n; i++)
                {
                    var b = new Bowler
                    {
                        Peer = r.ReadInt64(), Name = r.ReadString(), Color = r.ReadByte(), Weight = r.ReadByte(),
                        Ball = (BallAt)r.ReadByte(), BallBack = r.ReadDouble(),
                    };
                    int rolls = r.ReadByte();
                    for (int k = 0; k < rolls; k++) b.Rolls.Add(r.ReadByte());
                    lane.Bowlers.Add(b);
                }
            }
            pairs[pair] = lanes;
        }
        Centres[key] = pairs;
    }

    /// <summary>The ball and ten pins: positions in millimetres, turns in 1/127ths, 10 bytes each.</summary>
    protected static void WritePoses(BinaryWriter w, Transform3D[] bodies)
    {
        foreach (var t in bodies)
        {
            var o = t.Origin;
            w.Write((short)Mathf.Clamp(Mathf.RoundToInt(o.X * 1000), short.MinValue, short.MaxValue));
            w.Write((short)Mathf.Clamp(Mathf.RoundToInt(o.Y * 1000), short.MinValue, short.MaxValue));
            w.Write((short)Mathf.Clamp(Mathf.RoundToInt((o.Z - 10f) * 1000), short.MinValue, short.MaxValue));
            var q = t.Basis.GetRotationQuaternion();
            w.Write((sbyte)Mathf.RoundToInt(q.X * 127));
            w.Write((sbyte)Mathf.RoundToInt(q.Y * 127));
            w.Write((sbyte)Mathf.RoundToInt(q.Z * 127));
            w.Write((sbyte)Mathf.RoundToInt(q.W * 127));
        }
    }

    protected static Transform3D[] ReadPoses(BinaryReader r)
    {
        var bodies = new Transform3D[11];
        for (int i = 0; i < 11; i++)
        {
            var o = new Vector3(r.ReadInt16() / 1000f, r.ReadInt16() / 1000f, r.ReadInt16() / 1000f + 10f);
            var q = new Quaternion(r.ReadSByte() / 127f, r.ReadSByte() / 127f, r.ReadSByte() / 127f, r.ReadSByte() / 127f);
            bodies[i] = new Transform3D(new Basis(q.LengthSquared() > 0.01f ? q.Normalized() : Quaternion.Identity), o);
        }
        return bodies;
    }
}
