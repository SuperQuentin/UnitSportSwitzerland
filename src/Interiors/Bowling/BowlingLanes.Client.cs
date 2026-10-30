using Godot;
using UnitSport.Net;

namespace UnitSport.Interiors;

// A client's side of bowling (#1323): asking, and showing the centre it stands in on its lane rigs: the
// ball rolling from its release, the deck's frames replayed a little behind, the machine and the hood.
public partial class BowlingLanes
{
    /// <summary>A refusal for this player ("It is not your turn."), for the HUD to show.</summary>
    public event Action<string>? Told;

    /// <summary>A centre's state was applied here: the HUD and the score screens redraw.</summary>
    public event Action<string>? StateChanged;

    /// <summary>One lane's roll as shown here: its run, the deck's frames as they come, how far it got.</summary>
    private sealed class Playback
    {
        public int RollId;
        public BowlingBall.Run Run = null!;
        public double ReleaseAt, DeckAt = double.NaN;
        public bool Mine;
        public byte Color;
        public readonly List<(float Time, Transform3D[] Bodies)> Frames = new();
        public bool Ended;
    }

    private readonly Dictionary<(string, int, int), Playback> _plays = new();
    /// <summary>The cycles already started on the rigs, so a resent state does not restart one.</summary>
    private readonly Dictionary<(string, int, int), double> _cycles = new();
    private string _asked = "";

    /// <summary>
    /// How far behind the shared clock the deck is shown: its frames come from the server a tick batch
    /// and a ping late. The roller's own ball starts at no delay and slows into it on the lane.
    /// </summary>
    private double Lag => Online && !Multiplayer.IsServer() ? 0.15 + (double.IsNaN(ClockSync.Rtt) ? 0.1 : ClockSync.Rtt) : 0.06;

    // ---- asking --------------------------------------------------------------------------------

    public void AskSignIn(string key, string name)
    {
        if (Online) RpcId(1, MethodName.RequestSignIn, key);
        else ServeSignIn(MyId, key, name);
    }

    public void AskTakeBall(string key, int color, int weight)
    {
        if (Online) RpcId(1, MethodName.RequestTakeBall, key, color, weight);
        else ServeTakeBall(MyId, key, color, weight);
    }

    public void AskAddBot(string key)
    {
        if (Online) RpcId(1, MethodName.RequestAddBot, key);
        else ServeAddBot(MyId, key);
    }

    public void AskRelease(string key, BowlingBall.Release release)
    {
        double at = Now;
        // the ball leaves the hand now, here: shown at once, before the server has heard of it
        if (SignedIn(MyId) is { } me && me.Key == key)
        {
            var lane = PairOf(key, me.Pair)[me.Lane];
            Begin(key, me.Pair, me.Lane, lane.RollId + 1, BowlingBall.Release.Unpack(release.Pack()), at, me.Bowler.Color, true);
        }
        if (Online) RpcId(1, MethodName.RequestRelease, key, release.Pack(), at);
        else ServeRelease(MyId, key, release.Pack(), at);
    }

    /// <summary>The lane of centre <paramref name="key"/> a bowler named <paramref name="name"/> is on, as this peer knows it.</summary>
    public Lane? LaneWith(string key, string name)
    {
        foreach (var lanes in Centres.GetValueOrDefault(key)?.Values ?? Enumerable.Empty<Lane[]>())
            foreach (var lane in lanes)
                if (lane.Bowlers.Any(b => b.Name == name)) return lane;
        return null;
    }

    /// <summary>How many of the deck's frames this peer got for the lane's latest roll (the net check's).</summary>
    public int FramesSeen(string key, int pair, int lane) => _plays.TryGetValue((key, pair, lane), out var p) ? p.Frames.Count : 0;

    // ---- what the server sends ---------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void State(string key, byte[] data)
    {
        Unpack(key, data);
        Applied(key);
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Tell(string why) => Told?.Invoke(why);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Frames(string key, int pair, int lane, int rollId, double deckAt, int first, byte[] data)
    {
        if (!_plays.TryGetValue((key, pair, lane), out var play) || play.RollId != rollId) return;
        play.DeckAt = deckAt;
        using var r = new BinaryReader(new MemoryStream(data));
        int n = r.ReadInt16();
        for (int i = 0; i < n; i++)
        {
            float time = r.ReadUInt16() / 1000f;
            var bodies = ReadPoses(r);
            if (first + i == play.Frames.Count) play.Frames.Add((time, bodies));
        }
    }

    /// <summary>A centre's state is in: rolls begun elsewhere start showing, the machines and the hoods follow it.</summary>
    private void Applied(string key)
    {
        if (!Centres.TryGetValue(key, out var pairs)) return;
        foreach (var (pair, lanes) in pairs)
            for (int i = 0; i < 2; i++)
            {
                var lane = lanes[i];
                var id = (key, pair, i);
                if (lane.Rolling && (!_plays.TryGetValue(id, out var play) || play.RollId != lane.RollId))
                {
                    var roller = lane.Bowlers.FirstOrDefault(b => b.Peer == lane.Roller);
                    Begin(key, pair, i, lane.RollId, lane.Release, lane.ReleaseAt, roller?.Color ?? 0, false);
                }
            }
        _version++;
        StateChanged?.Invoke(key);
    }

    private int _version;
    private readonly Dictionary<(string, int), int> _scoresShown = new();

    /// <summary>A lane's screen text: per bowler their name and total, then their frames; the one up marked.</summary>
    private static string ScoreText(Lane lane)
    {
        if (lane.Bowlers.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lane.Bowlers.Count; i++)
        {
            var b = lane.Bowlers[i];
            string name = b.Name.Length > 12 ? b.Name[..12] : b.Name;
            sb.Append(i == lane.Turn && double.IsNaN(lane.OverAt) ? "▶ " : "   ").Append(name).Append("   ").Append(BowlingScore.Total(b.Rolls)).Append('\n');
            sb.Append("   ");
            foreach (var f in BowlingScore.Frames(b.Rolls))
                sb.Append(f.Marks.Length == 0 ? "·" : string.Concat(f.Marks)).Append(' ');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private void Begin(string key, int pair, int lane, int rollId, BowlingBall.Release release, double at, byte color, bool mine)
    {
        var im = InteriorManager.Instance;
        if ((im?.BuiltNode(key)?.Layout ?? im?.Planned(key)) is not { } layout || pair < 0 || pair >= layout.Furniture.Count) return;
        var g = LaneGeometry.Of(layout.Furniture[pair]);
        _plays[(key, pair, lane)] = new Playback
        {
            RollId = rollId, Run = BowlingBall.Roll(g.Run, release), ReleaseAt = at, Mine = mine, Color = color,
        };
    }

    // ---- showing it ----------------------------------------------------------------------------

    private void ClientProcess(double delta)
    {
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase) && Online && Multiplayer.IsServer()) return;
        var im = InteriorManager.Instance;
        if (im?.Current is not { Type: BuildingType.Bowling } here)
        {
            // out of the centre a client hears no more of it (the server signs a leaver out): forget it,
            // so the shoes come off and nothing stale shows on coming back
            if (_asked.Length > 0 && Online && !Multiplayer.IsServer()) { Centres.Clear(); _plays.Clear(); _cycles.Clear(); }
            _asked = "";
            return;
        }
        if (_asked != here.Key)
        {
            _asked = here.Key;
            if (Online && !Multiplayer.IsServer()) RpcId(1, MethodName.RequestState, here.Key);
        }
        if (im.BuiltNode(here.Key) is not { } node) return;
        double now = Now;
        var pairs = Centres.GetValueOrDefault(here.Key);
        foreach (int pair in PairsOf(here))
        {
            if (node.LaneRigOf(pair) is not { } rig) continue;
            var lanes = pairs?.GetValueOrDefault(pair);
            for (int i = 0; i < 2; i++)
            {
                var lane = lanes?[i];
                Show(rig, here.Key, pair, i, lane, now);
            }
            // a pair nobody has bowled at yet has no state: its return shows the house balls it starts with (#1336)
            Hood(rig, lanes ?? Fresh(here.Key, pair));
            if (lanes != null && _scoresShown.GetValueOrDefault((here.Key, pair), -1) != _version)
            {
                _scoresShown[(here.Key, pair)] = _version;
                for (int i = 0; i < 2; i++) rig.SetScore(i, ScoreText(lanes[i]));
            }
        }
        HeldBalls(here.Key);
    }

    // ---- balls in hands ------------------------------------------------------------------------

    private readonly Dictionary<long, MeshInstance3D> _held = new();
    private readonly HashSet<long> _holding = new();

    /// <summary>
    /// Every bowler of the centre holding their ball has it at the wrist, as a node under their body moved
    /// each frame (nothing rebuilt); seen from their own eye only in the mirror, as a held item is.
    /// </summary>
    private void HeldBalls(string key)
    {
        _holding.Clear();
        if (Centres.TryGetValue(key, out var pairs))
            foreach (var lanes in pairs.Values)
                foreach (var lane in lanes)
                    foreach (var b in lane.Bowlers)
                        if (b.Ball == BallAt.Hand && BodyOf(b.Peer) is { HandLocal: { } hand } body)
                        {
                            _holding.Add(b.Peer);
                            if (!_held.TryGetValue(b.Peer, out var ball) || !IsInstanceValid(ball) || ball.GetParent() != body)
                            {
                                if (ball != null && IsInstanceValid(ball)) ball.QueueFree();
                                _held[b.Peer] = ball = new MeshInstance3D { Name = "BowlingBall", Mesh = LaneRig.BallMeshOf(b.Color) };
                                ball.MaterialOverride = InteriorManager.Instance?.BuiltNode(key) is { } node ? node.LitMaterial : null;
                                body.AddChild(ball);
                            }
                            uint layers = body.BodyFromEye && !XR.XrSession.Active ? Avatar.OwnBody.Layer : Avatar.OwnBody.World;
                            if (ball.Layers != layers) ball.Layers = layers;
                            // the ball in the palm: a hand's length past the wrist, below it
                            ball.Transform = new Transform3D(hand.Basis, hand.Origin + hand.Basis * new Vector3(0, -0.11f, -0.06f));
                            // a VR hand gripping it for a throw: in the controller itself
                            if (b.Peer == MyId && XrBowling.HeldAt is { } inGrip) ball.GlobalTransform = inGrip;
                        }
        foreach (var (peer, ball) in _held)
            if (!_holding.Contains(peer) && IsInstanceValid(ball) && ball.Visible) ball.Visible = false;
            else if (_holding.Contains(peer) && IsInstanceValid(ball) && !ball.Visible) ball.Visible = true;
    }

    /// <summary>What the screen calls the lane's last roll (#1336), from its roller's rolls and the pins it left.</summary>
    private static string? WordOf(Lane? lane)
    {
        if (lane == null) return null;
        foreach (var b in lane.Bowlers)
            if (b.Peer == lane.Roller) return BowlingScore.Word(b.Rolls, lane.Standing);
        return null;
    }

    private Player.FootPlayer? BodyOf(long peer) =>
        peer == MyId ? InteriorManager.Instance?.LocalPlayer?.Invoke()
            : GetParent()?.GetNodeOrNull<Node>("Players")?.GetNodeOrNull<Player.FootPlayer>(peer.ToString());

    private void Show(LaneRig rig, string key, int pair, int i, Lane? lane, double now)
    {
        var id = (key, pair, i);
        if (_plays.TryGetValue(id, out var play) && !play.Ended)
        {
            double lag = Lag;
            double age = now - play.ReleaseAt;
            // the roller's ball leaves at once and eases into the deck's delay along the lane
            double shown = play.Mine ? age - lag * Math.Clamp(age / Math.Max(0.5, play.Run.Time), 0, 1) : age - lag;
            if (shown < 0) { rig.HideBall(i); return; }
            if (shown < play.Run.Time || play.Run.Gutter || double.IsNaN(play.DeckAt) && play.Frames.Count == 0)
            {
                if (shown >= play.Run.Time && play.Run.Gutter) { rig.HideBall(i); }
                else
                {
                    var (pos, turn) = play.Run.At((float)Math.Min(shown, play.Run.Time));
                    rig.ShowBall(i, pos, turn, play.Color, onDeck: false);
                }
                if (play.Run.Gutter && lane is { Rolling: false } && shown >= play.Run.Time) play.Ended = true;
                return;
            }
            // on the deck: the server's frames, a lag behind
            double deckTime = now - lag - play.DeckAt;
            if (play.Frames.Count == 0) return;
            int k = play.Frames.FindLastIndex(f => f.Time <= deckTime);
            if (k < 0) k = 0;
            var a = play.Frames[k];
            var b = k + 1 < play.Frames.Count ? play.Frames[k + 1] : a;
            float w = b.Time > a.Time ? Mathf.Clamp((float)((deckTime - a.Time) / (b.Time - a.Time)), 0, 1) : 0;
            rig.ShowDeck(i, a.Bodies, b.Bodies, w, play.Color);
            if (k + 1 >= play.Frames.Count && lane is { Rolling: false } && lane.RollId == play.RollId)
            {
                play.Ended = true;
                // the impact again on the lane's screen, slowly
                rig.PlayReplay(i, play.Frames, play.Color, WordOf(lane));
            }
            return;
        }
        if (lane == null) return;
        // the machine: started once per cycle, from the state's start time
        if (!double.IsNaN(lane.CycleStart) && (!_cycles.TryGetValue(id, out double was) || was != lane.CycleStart))
        {
            _cycles[id] = lane.CycleStart;
            if (lane.Rest != null) rig.ShowDeck(i, lane.Rest, lane.Rest, 0, 0);
            rig.HideBall(i);
            if (now < lane.CycleStart + PinsetterCycle.Length(lane.CycleKind)) rig.StartCycle(i, lane.CycleKind, lane.CycleStart);
            else rig.SetStanding(i, lane.Standing);
        }
        else if (double.IsNaN(lane.CycleStart) && !_cycles.ContainsKey(id) && lane.Standing != Rack)
        {
            _cycles[id] = double.NaN;
            rig.SetStanding(i, lane.Standing);
        }
    }

    /// <summary>
    /// Where <paramref name="peer"/>'s ball waits in the queue on pair <paramref name="pair"/>'s return (#1336), in the
    /// order <see cref="Hood"/> lays it: after the house balls, by when each came back; -1 when it is not there.
    /// </summary>
    public int ReturnSlotOf(string key, int pair, long peer)
    {
        if (!Centres.TryGetValue(key, out var pairs) || !pairs.TryGetValue(pair, out var lanes)) return -1;
        int slot = 0;
        double mine = double.NaN;
        foreach (var lane in lanes)
        {
            slot += lane.House.Count;
            foreach (var b in lane.Bowlers)
                if (b.Peer == peer && b.Ball == BallAt.Hood) mine = b.BallBack;
        }
        if (double.IsNaN(mine)) return -1;
        foreach (var lane in lanes)
            foreach (var b in lane.Bowlers)
                if (b.Peer != peer && b.Ball == BallAt.Hood && b.BallBack < mine) slot++;
        return slot;
    }

    /// <summary>The balls waiting on a pair's hood: both lanes' bowlers whose ball is back.</summary>
    private readonly List<int> _hood = new();
    private readonly List<(double At, int Color)> _back = new();

    private void Hood(LaneRig rig, Lane[]? lanes)
    {
        _hood.Clear();
        _back.Clear();
        if (lanes != null)
        {
            // the house balls were there first, at the stop; the bowlers' balls queue behind them as they came back
            foreach (var lane in lanes)
                foreach (var (c, _) in lane.House) _hood.Add(c);
            foreach (var lane in lanes)
                foreach (var b in lane.Bowlers)
                    if (b.Ball == BallAt.Hood) _back.Add((b.BallBack, b.Color));
            _back.Sort((a, b) => a.At.CompareTo(b.At));
            foreach (var (_, c) in _back) _hood.Add(c);
        }
        rig.SetHoodBalls(_hood);
    }
}
