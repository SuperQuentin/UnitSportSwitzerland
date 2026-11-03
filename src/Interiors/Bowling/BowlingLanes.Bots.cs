using Godot;

namespace UnitSport.Interiors;

// Bots at the lanes (#1323), basic for now (owner, 2026-10-28): partner bots a player adds to their game,
// bowled by the server on their turn, and decoration bowlers (BowlingDeco) on a few lanes of every centre.
// Neither walks: bots do not go indoors yet (the server has no interior collision), so a partner is a
// bowler with no body, drawn by the clients beside the approach.
public partial class BowlingLanes
{
    /// <summary>Partner bots' peer ids: below every real peer's (positive) and the server bots' range.</summary>
    private const long BotBase = -900_000;
    private static readonly string[] BotNames = { "Ueli", "Vreni", "Housi", "Mägi", "Fritz", "Rösli", "Sepp", "Trudi" };
    private long _nextBot;

    /// <summary>A bowler run by the server: no body, no RPC.</summary>
    public static bool IsBot(long peer) => peer <= BotBase;

    /// <summary>
    /// Server: <paramref name="peer"/> adds a bot partner to their lane's game, while it has not begun and has
    /// room. The bot bowls when its turn comes.
    /// </summary>
    public bool ServeAddBot(long peer, string key)
    {
        if (Plan(peer, key) is null || SignedIn(peer) is not { } me || me.Key != key) return Refuse(peer, "Sign in at the shoe counter first.");
        var lane = PairOf(key, me.Pair)[me.Lane];
        if (lane.Bowlers.Count >= MaxBowlers) return Refuse(peer, "The lane is full.");
        if (lane.Bowlers.Any(b => b.Rolls.Count > 0)) return Refuse(peer, "Your game has begun: add bots before the first ball.");
        long id = BotBase - _nextBot++;
        string name = BotNames[(int)(_nextBot - 1) % BotNames.Length] + " (bot)";
        lane.Bowlers.Add(new Bowler
        {
            Peer = id, Name = name, Color = (byte)((_nextBot * 3) % InteriorMeshBuilder.BallColorCount), Weight = 12, Ball = BallAt.Hood,
        });
        GD.Print($"[bowling] peer {peer} added {name} at {key} pair {me.Pair} lane {me.Lane}");
        Changed(key);
        return true;
    }

    /// <summary>Server, each housekeeping pass: a bot whose turn it is takes its ball and bowls, after a pause.</summary>
    private bool StepBots(string key, int pair, Lane lane, double now)
    {
        // no players left on the lane: its bots go too
        if (lane.Bowlers.Count > 0 && lane.Bowlers.All(b => IsBot(b.Peer)))
        {
            foreach (var bot in lane.Bowlers) Leave(lane, bot);
            lane.Bowlers.Clear();
            lane.Turn = 0;
            return true;
        }
        if (lane.Up is not { } up || !IsBot(up.Peer) || lane.Rolling || lane.CycleBusy(now) || BowlingScore.Over(up.Rolls)) return false;
        if (up.Ball is BallAt.Hood or BallAt.None)
        {
            up.Ball = BallAt.Hand;
            // when it bowls: a whole turn on, so every client plays it from the return to the release (DecoThrow, #1336)
            up.BallBack = now + DecoThrow.Release;
            return true;
        }
        if (up.Ball != BallAt.Hand || now < up.BallBack) return false;
        var l = InteriorManager.Instance?.Planned(key);
        if (l == null || pair >= l.Furniture.Count) return false;
        var release = BotRelease(LaneGeometry.Of(l.Furniture[pair]).Run, up.Peer, up.Rolls.Count, lane.Standing);
        ServeRelease(up.Peer, key, release.Pack(), now);
        return false;
    }

    /// <summary>
    /// A bot's ball: a right-hander's pocket line with a skill spread (where it stands, its line, speed and
    /// hook a little off each time), or straight at the pins left for a spare.
    /// </summary>
    public static BowlingBall.Release BotRelease(BowlingSizes.Run run, long seed, int ball, ushort standing)
    {
        var rng = new Random(HashCode.Combine(seed, ball));
        float Spread(float s) => (float)(rng.NextDouble() * 2 - 1) * s;
        if (standing != Rack)
        {
            // a spare: straight at the middle of what is left
            float x = 0, count = 0;
            for (int i = 0; i < 10; i++)
                if ((standing & (1 << i)) != 0) { x += PinDeck.Spot(run, i).X; count++; }
            return new BowlingBall.Release(Mathf.Clamp(x / Math.Max(1, count) + Spread(0.06f), -0.45f, 0.45f), Spread(0.006f), 7.5f + Spread(0.5f), 20f, 0f);
        }
        // the pocket: aimed by running the ball, as a bowler finds their line in practice
        float speed = 7.6f + Spread(0.6f), revs = 28f + Spread(4f), axis = 0.5f + Spread(0.1f);
        float lo = -0.45f, hi = 0.2f;
        for (int i = 0; i < 20; i++)
        {
            float mid = (lo + hi) / 2;
            var r = BowlingBall.Roll(run, new BowlingBall.Release(mid, -0.012f, speed, revs, axis));
            if (r.Gutter || r.AtDeck.Pos.X > -0.065f) hi = mid; else lo = mid;
        }
        return new BowlingBall.Release(lo + Spread(0.05f), -0.012f + Spread(0.004f), speed, revs, axis);
    }

    /// <summary>Whether players (or their bots) bowl at a lane, as this peer knows: the decoration bowler stands aside.</summary>
    public bool LaneBusy(string key, int pair, int lane) =>
        Centres.TryGetValue(key, out var pairs) && pairs.TryGetValue(pair, out var lanes) && lanes[lane].Bowlers.Count > 0;

    /// <summary>
    /// Every bot partner of a centre as this peer knows it: its lane, itself, its place among the lane's bots,
    /// and when its ball left the hand (NaN when it is not rolling).
    /// </summary>
    public IEnumerable<(int Pair, int Lane, Bowler Bowler, int Index, double Rolling)> BotsIn(string key)
    {
        if (!Centres.TryGetValue(key, out var pairs)) yield break;
        foreach (var (pair, lanes) in pairs)
            for (int i = 0; i < 2; i++)
            {
                int n = 0;
                foreach (var b in lanes[i].Bowlers)
                    if (IsBot(b.Peer))
                        yield return (pair, i, b, n++, lanes[i].Rolling && lanes[i].Roller == b.Peer ? lanes[i].ReleaseAt : double.NaN);
            }
    }

    // ---- decoration lanes ----------------------------------------------------------------------

    /// <summary>
    /// The lanes of a centre the decoration bowlers have (<see cref="BowlingDeco"/>): from the plan, the same on
    /// every peer and the server, which keeps players off them while others are free. About one pair in three,
    /// four lanes at most, as (pair furniture index, lane).
    /// </summary>
    public static List<(int Pair, int Lane)> DecoLanes(InteriorLayout l)
    {
        var lanes = new List<(int, int)>();
        foreach (int pair in PairsOf(l))
        {
            // from the plan's own numbers (a string's hash code differs per process)
            uint h = (uint)(pair * 2654435761u ^ (uint)(l.Furniture[pair].X * 100) * 40503u ^ (uint)(l.Furniture[pair].Z * 100));
            if (h % 3 == 0 && lanes.Count < 4) lanes.Add((pair, (int)(h >> 8) % 2));
        }
        return lanes;
    }
}
