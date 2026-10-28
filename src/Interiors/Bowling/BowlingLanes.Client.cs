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
        StateChanged?.Invoke(key);
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
        if (im?.Current is not { Type: BuildingType.Bowling } here) { _asked = ""; return; }
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
            Hood(rig, lanes);
        }
    }

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
            if (k + 1 >= play.Frames.Count && lane is { Rolling: false } && lane.RollId == play.RollId) play.Ended = true;
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

    /// <summary>The balls waiting on a pair's hood: both lanes' bowlers whose ball is back.</summary>
    private readonly List<int> _hood = new();

    private void Hood(LaneRig rig, Lane[]? lanes)
    {
        _hood.Clear();
        if (lanes != null)
            foreach (var lane in lanes)
                foreach (var b in lane.Bowlers)
                    if (b.Ball == BallAt.Hood) _hood.Add(b.Color);
        rig.SetHoodBalls(_hood);
    }
}
