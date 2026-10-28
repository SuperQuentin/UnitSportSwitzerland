using Godot;

namespace UnitSport.Interiors;

// The server's side of bowling (#1323): the Serve* actions (a player's RPC or a bot's call), the pin
// decks under way, and the state sent to the peers inside a centre.
public partial class BowlingLanes
{
    /// <summary>How near a counter, rack or hood a body must be to use it (beyond the piece's own half size).</summary>
    private const float Reach = 1.6f;

    /// <summary>A deck under way: whose lane, the ball's run, and the frames already sent.</summary>
    private sealed class Rolling
    {
        public string Key = "";
        public int Pair, Lane, RollId;
        public BowlingBall.Run Run = null!;
        public double DeckAt;
        public PinDeck? Deck;
        public int Sent;
        public BowlingSizes.Run Size;
    }

    private readonly Dictionary<(string, int, int), Rolling> _decks = new();
    private double _housekeeping;

    // ---- the actions ---------------------------------------------------------------------------

    /// <summary>
    /// Server (and offline): <paramref name="peer"/> signs in at a shoe counter of centre <paramref name="key"/>
    /// (the shoes go on) and is given a lane: one whose game has not begun and has room, else the emptiest.
    /// Signed in already: signs out (the shoes go back). A bot calls this with its own id.
    /// </summary>
    public bool ServeSignIn(long peer, string key, string name)
    {
        if (Plan(peer, key) is not { } l) return false;
        if (SignedIn(peer) is { } was)
        {
            Remove(was.Key, was.Pair, was.Lane, peer);
            Changed(key);
            return true;
        }
        if (!Near(peer, l, FurnitureType.ShoeCounter)) return Refuse(peer, "Sign in at the shoe counter.");
        (int Pair, int Lane, int Score)? best = null;
        foreach (int pair in PairsOf(l))
        {
            var lanes = PairOf(key, pair);
            for (int i = 0; i < 2; i++)
            {
                var lane = lanes[i];
                if (lane.Bowlers.Count >= MaxBowlers) continue;
                bool fresh = lane.Bowlers.All(b => b.Rolls.Count == 0);
                // a game not begun with friends in it first, then an empty lane, then the emptiest
                int score = lane.Bowlers.Count > 0 && fresh ? 100 - lane.Bowlers.Count : lane.Bowlers.Count == 0 ? 50 : 10 - lane.Bowlers.Count;
                if (best is not { } b || score > b.Score) best = (pair, i, score);
            }
        }
        if (best is not { } pick) return Refuse(peer, "Every lane is full.");
        var target = PairOf(key, pick.Pair)[pick.Lane];
        target.Bowlers.Add(new Bowler { Peer = peer, Name = name, Weight = 0 });
        if (target.Bowlers.Count == 1) { target.Turn = 0; target.OverAt = double.NaN; }
        GD.Print($"[bowling] peer {peer} signed in at {key} pair {pick.Pair} lane {pick.Lane}");
        Changed(key);
        return true;
    }

    /// <summary>
    /// Server: a signed-in bowler takes a house ball from a rack (colour and weight as they choose), or,
    /// when theirs waits on their pair's hood, takes it back from there.
    /// </summary>
    public bool ServeTakeBall(long peer, string key, int color, int weight)
    {
        if (Plan(peer, key) is not { } l) return false;
        if (SignedIn(peer) is not { } me || me.Key != key) return Refuse(peer, "Sign in at the shoe counter first.");
        var b = me.Bowler;
        if (b.Ball == BallAt.Hand) return false;
        if (b.Ball == BallAt.Hood)
        {
            if (!NearHood(peer, l, me.Pair)) return Refuse(peer, "Your ball is on the ball return.");
            b.Ball = BallAt.Hand;
        }
        else if (b.Ball == BallAt.None)
        {
            if (!Near(peer, l, FurnitureType.BallRack)) return Refuse(peer, "Take a ball from a rack.");
            b.Color = (byte)Mathf.PosMod(color, InteriorMeshBuilder.BallColorCount);
            b.Weight = (byte)Mathf.Clamp(weight, 6, 16);
            b.Ball = BallAt.Hand;
        }
        else return Refuse(peer, "Your ball is still on its way back.");
        Changed(key);
        return true;
    }

    /// <summary>
    /// Server: the bowler up at their lane lets the ball go: <paramref name="release"/> (the packed
    /// <see cref="BowlingBall.Release"/>) at <paramref name="at"/> on the shared clock, which may be a little
    /// in the past (the roller's ball already rolls on their screen). Refused off the approach, out of turn,
    /// without the ball, or while the machine works.
    /// </summary>
    public bool ServeRelease(long peer, string key, int[] release, double at)
    {
        if (Plan(peer, key) is not { } l) return false;
        if (SignedIn(peer) is not { } me || me.Key != key) return false;
        var lane = PairOf(key, me.Pair)[me.Lane];
        double now = Now;
        if (lane.Up?.Peer != peer) return Refuse(peer, "It is not your turn.");
        if (me.Bowler.Ball != BallAt.Hand) return Refuse(peer, "Take your ball first.");
        if (lane.Rolling || lane.CycleBusy(now)) return Refuse(peer, "Wait for the pinsetter.");
        if (BowlingScore.Over(me.Bowler.Rolls)) return false;
        if (!OnApproach(peer, l, me.Pair, me.Lane)) return Refuse(peer, "Stand on your lane's approach.");
        var rel = BowlingBall.Release.Unpack(release);
        // a release from the past is believed for a little (the roller's ping), never more
        at = Math.Clamp(at, now - 0.5, now);
        var g = LaneGeometry.Of(l.Furniture[me.Pair]);
        var run = BowlingBall.Roll(g.Run, rel);
        lane.RollId++;
        lane.Release = rel;
        lane.ReleaseAt = at;
        lane.Roller = peer;
        lane.Rest = null;
        me.Bowler.Ball = BallAt.Rolling;
        _decks[(key, me.Pair, me.Lane)] = new Rolling
        {
            Key = key, Pair = me.Pair, Lane = me.Lane, RollId = lane.RollId, Run = run, DeckAt = at + run.Time, Size = g.Run,
        };
        GD.Print($"[bowling] {key} pair {me.Pair} lane {me.Lane}: peer {peer} rolls (x {rel.X:F2}, {rel.Speed:F1} m/s, axis {rel.Axis:F2})" +
                 $"{(run.Gutter ? ", into the gutter" : "")}");
        Changed(key);
        return true;
    }

    // ---- the decks -----------------------------------------------------------------------------

    public override void _PhysicsProcess(double delta)
    {
        if (_decks.Count == 0 || Online && !Multiplayer.IsServer()) return;
        double now = Now;
        List<(string, int, int)>? done = null;
        foreach (var (id, roll) in _decks)
        {
            var lane = PairOf(roll.Key, roll.Pair)[roll.Lane];
            if (roll.Deck == null)
            {
                if (now < roll.DeckAt) continue;
                if (roll.Run.Gutter) { Finish(roll, lane, 0, lane.Standing, Settled(lane.Standing, roll)); (done ??= new()).Add(id); continue; }
                roll.Deck = new PinDeck(roll.Size, lane.Standing, roll.Run.AtDeck);
                continue;
            }
            roll.Deck.Tick((float)delta);
            SendFrames(roll, false);
            if (!roll.Deck.Done) continue;
            SendFrames(roll, true);
            Finish(roll, lane, roll.Deck.Knocked(), roll.Deck.Standing(), roll.Deck.Record[^1].Bodies);
            roll.Deck.Dispose();
            (done ??= new()).Add(id);
        }
        if (done != null) foreach (var id in done) _decks.Remove(id);
    }

    /// <summary>The deck as a gutter ball leaves it: the pins where they stood, the ball gone into the pit.</summary>
    private static Transform3D[] Settled(ushort standing, Rolling roll)
    {
        var bodies = new Transform3D[11];
        bodies[0] = PinDeck.Gone;
        for (int i = 0; i < 10; i++)
            bodies[i + 1] = (standing & (1 << i)) != 0 ? new Transform3D(Basis.Identity, PinDeck.Spot(roll.Size, i)) : PinDeck.Gone;
        return bodies;
    }

    /// <summary>The roll is over: its pins count, the turn moves on, the machine and the ball return start.</summary>
    private void Finish(Rolling roll, Lane lane, int knocked, ushort standing, Transform3D[] rest)
    {
        double now = Now;
        var b = lane.Bowlers.FirstOrDefault(x => x.Peer == lane.Roller);
        lane.ReleaseAt = double.NaN;
        lane.Rest = rest;
        if (b != null)
        {
            var before = BowlingScore.Next(b.Rolls);
            b.Rolls.Add(Math.Min(knocked, BowlingScore.Standing(b.Rolls)));
            b.Ball = BallAt.Return;
            b.BallBack = now + ReturnTime;
            var after = BowlingScore.Next(b.Rolls);
            bool frameDone = after.Frame != before.Frame;
            bool fresh = frameDone || BowlingScore.FreshRack(b.Rolls);
            lane.CycleKind = fresh ? PinsetterCycle.Kind.Reset : PinsetterCycle.Kind.Pick;
            lane.Standing = fresh ? Rack : standing;
            if (frameDone) NextTurn(lane);
            GD.Print($"[bowling] {roll.Key} pair {roll.Pair} lane {roll.Lane}: {knocked} down, frame {before.Frame + 1}, total {BowlingScore.Total(b.Rolls)}");
        }
        else lane.CycleKind = PinsetterCycle.Kind.Reset;
        lane.CycleStart = now + CycleDelay;
        Changed(roll.Key);
    }

    private static void NextTurn(Lane lane)
    {
        for (int k = 1; k <= lane.Bowlers.Count; k++)
        {
            int i = (lane.Turn + k) % lane.Bowlers.Count;
            if (!BowlingScore.Over(lane.Bowlers[i].Rolls)) { lane.Turn = i; return; }
        }
        lane.OverAt = Now;
    }

    // ---- housekeeping: returning balls, leavers, the next game ------------------------------------

    public override void _Process(double delta)
    {
        ClientProcess(delta);
        if (Online && !Multiplayer.IsServer()) return;
        _housekeeping -= delta;
        if (_housekeeping > 0) return;
        _housekeeping = 0.25;
        double now = Now;
        var im = InteriorManager.Instance;
        foreach (var (key, pairs) in Centres)
        {
            bool changed = false;
            foreach (var (pair, lanes) in pairs)
                foreach (var lane in lanes)
                {
                    foreach (var b in lane.Bowlers)
                        if (b.Ball == BallAt.Return && now >= b.BallBack) { b.Ball = BallAt.Hood; changed = true; }
                    // a player who left the building is signed out (a bot's director signs its own out)
                    for (int i = lane.Bowlers.Count - 1; i >= 0; i--)
                    {
                        long p = lane.Bowlers[i].Peer;
                        if (p > 0 && im != null && Online && im.SpaceOf(p) != key && !(lane.Rolling && lane.Roller == p))
                        {
                            RemoveAt(lane, i);
                            changed = true;
                        }
                    }
                    if (!double.IsNaN(lane.OverAt) && now > lane.OverAt + 20 && !lane.CycleBusy(now))
                    {
                        // everyone finished a while ago: a new game on the same lane
                        foreach (var b in lane.Bowlers) b.Rolls.Clear();
                        lane.Turn = 0;
                        lane.OverAt = double.NaN;
                        changed = true;
                    }
                }
            if (changed) Changed(key);
        }
    }

    private void Remove(string key, int pair, int lane, long peer)
    {
        var l = PairOf(key, pair)[lane];
        int i = l.Bowlers.FindIndex(b => b.Peer == peer);
        if (i >= 0) RemoveAt(l, i);
        GD.Print($"[bowling] peer {peer} signed out of {key}");
    }

    private static void RemoveAt(Lane lane, int i)
    {
        lane.Bowlers.RemoveAt(i);
        if (lane.Turn > i || lane.Turn >= lane.Bowlers.Count) lane.Turn = lane.Bowlers.Count == 0 ? 0 : lane.Turn > i ? lane.Turn - 1 : 0;
        if (lane.Bowlers.Count == 0) lane.OverAt = double.NaN;
    }

    // ---- where people stand --------------------------------------------------------------------

    /// <summary>The centre's plan if <paramref name="peer"/> is inside it (offline: the one player is), else null.</summary>
    private InteriorLayout? Plan(long peer, string key)
    {
        if (InteriorManager.Instance is not { } im || im.Planned(key) is not { Type: BuildingType.Bowling } l) return null;
        if (Online && im.SpaceOf(peer) != key) { GD.Print($"[bowling] peer {peer} is not in {key}: refused"); return null; }
        return l;
    }

    /// <summary>The lane pairs of a centre, by furniture index.</summary>
    public static IEnumerable<int> PairsOf(InteriorLayout l)
    {
        for (int i = 0; i < l.Furniture.Count; i++)
            if (l.Furniture[i].Type == FurnitureType.LanePair) yield return i;
    }

    /// <summary>A piece's frame in its plan: turned, on its floor.</summary>
    public static Transform3D PieceFrame(InteriorLayout l, FurniturePlan f) =>
        new(new Basis(Vector3.Up, f.Turns * Mathf.Pi / 2), new Vector3(f.X, l.FloorY(f.Floor) + f.Lift, f.Z));

    /// <summary>
    /// A body's place in a piece's own frame, or null when there is none to judge by: the server cannot
    /// see it, or offline the one player's body is not in the building (a shot queue's free camera is).
    /// </summary>
    private Vector3? InPiece(long peer, InteriorLayout l, FurniturePlan f)
    {
        if (InteriorManager.Instance?.PlayerLocal(peer, l) is not { } at) return null;
        // interiors stand kilometres under the world: a body outside is thousands of metres off the plan's floors
        if (!Online && Mathf.Abs(at.Y) > 50f) return null;
        return PieceFrame(l, f).AffineInverse() * at;
    }

    private bool Near(long peer, InteriorLayout l, FurnitureType type)
    {
        bool seen = false;
        foreach (var f in l.Furniture)
        {
            if (f.Type != type || InPiece(peer, l, f) is not { } at) continue;
            seen = true;
            if (Mathf.Abs(at.X) < f.W / 2 + Reach && Mathf.Abs(at.Z) < f.D / 2 + Reach && Mathf.Abs(at.Y) < 2.5f) return true;
        }
        // no body to judge by (a probe's free camera offline): trusted
        return !seen && !Online;
    }

    private bool NearHood(long peer, InteriorLayout l, int pair)
    {
        var f = l.Furniture[pair];
        if (InPiece(peer, l, f) is not { } at) return !Online;
        var g = LaneGeometry.Of(f);
        return Mathf.Abs(at.X) < 1.9f && at.Z < g.HoodZ + 1.2f + Reach && at.Z > g.Front - 4f;
    }

    private bool OnApproach(long peer, InteriorLayout l, int pair, int lane)
    {
        var f = l.Furniture[pair];
        if (InPiece(peer, l, f) is not { } at) return !Online;
        var g = LaneGeometry.Of(f);
        return Mathf.Abs(at.X - g.LaneX(lane)) < 1.0f && at.Z > g.Front - 0.5f && at.Z < g.Foul + 0.4f;
    }

    private bool Refuse(long peer, string why)
    {
        GD.Print($"[bowling] peer {peer}: {why}");
        if (peer == MyId) Told?.Invoke(why);
        else if (Online && peer > 0) RpcId(peer, MethodName.Tell, why);
        return false;
    }

    // ---- sending -------------------------------------------------------------------------------

    /// <summary>A centre changed: everyone inside gets it whole.</summary>
    private void Changed(string key)
    {
        var data = Pack(key);
        if (!Online) { Applied(key); return; }
        foreach (long peer in PeersInside(key)) RpcId(peer, MethodName.State, key, data);
        Applied(key);
    }

    private IEnumerable<long> PeersInside(string key)
    {
        if (InteriorManager.Instance is not { } im) yield break;
        foreach (int peer in Multiplayer.GetPeers())
            if (im.SpaceOf(peer) == key) yield return peer;
    }

    /// <summary>The deck's new frames to the peers inside, three ticks at a time (or all that is left, at the end).</summary>
    private void SendFrames(Rolling roll, bool all)
    {
        var rec = roll.Deck!.Record;
        int n = rec.Count - roll.Sent;
        if (n <= 0 || n < 3 && !all) return;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)n);
        for (int i = roll.Sent; i < rec.Count; i++)
        {
            w.Write((ushort)Mathf.RoundToInt(rec[i].Time * 1000));
            WritePoses(w, rec[i].Bodies);
        }
        var data = ms.ToArray();
        int first = roll.Sent;
        roll.Sent = rec.Count;
        if (Online)
            foreach (long peer in PeersInside(roll.Key))
                RpcId(peer, MethodName.Frames, roll.Key, roll.Pair, roll.Lane, roll.RollId, roll.DeckAt, first, data);
        // offline (or a listen server's own screen): straight into the playback
        if (!Online || !DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            Frames(roll.Key, roll.Pair, roll.Lane, roll.RollId, roll.DeckAt, first, data);
    }

    // ---- client RPC entry points -------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSignIn(string key)
    {
        if (!Multiplayer.IsServer()) return;
        long peer = Multiplayer.GetRemoteSenderId();
        ServeSignIn(peer, key, NameOf(peer));
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTakeBall(string key, int color, int weight)
    {
        if (Multiplayer.IsServer()) ServeTakeBall(Multiplayer.GetRemoteSenderId(), key, color, weight);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestRelease(string key, int[] release, double at)
    {
        if (Multiplayer.IsServer()) ServeRelease(Multiplayer.GetRemoteSenderId(), key, release, at);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestState(string key)
    {
        if (!Multiplayer.IsServer()) return;
        RpcId(Multiplayer.GetRemoteSenderId(), MethodName.State, key, Pack(key));
    }

    private string NameOf(long peer) => GetParent()?.GetNodeOrNull<Net.ChatManager>(Net.ChatManager.NodeName)?.NameOfPeer(peer) ?? $"Player {peer}";
}
