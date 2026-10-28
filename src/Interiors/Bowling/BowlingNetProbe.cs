using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>--bowlingnet A|B|C</c> with <c>--connect</c> on the real map at a bowling centre (#1323,
/// <c>tools/bowlingnetcheck.sh</c>): bowling over the network. A and B walk in by <c>--bowlingdoor</c>, sign in at the
/// shoe counter (the same lane: a game not begun), take a ball from a rack and step onto the approach. A rolls; the
/// server runs the deck; A and B must agree on the pins down and those left standing, B must have had the deck's
/// frames to replay, and B, out of turn, must be refused a roll of its own. C joins after A's roll and must find
/// the same score and standing pins from the state the server sends a peer walking in.
/// </summary>
public partial class BowlingNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--bowlingnet");

    private readonly WorldOrigin _origin;

    public BowlingNetProbe(ItemController items, WorldOrigin origin) : base(items, "bowlnet", "BN") => _origin = origin;

    public BowlingNetProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    /// <summary>A's ball: straight down the left side, no hook, into the 4 and 7: pins are left (A bowls again, B waits).</summary>
    private static readonly BowlingBall.Release Ball = new(0.38f, 0, 7.5f, 25f, 0);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(240)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        if (!await Until(() => BowlingLanes.Instance != null, 10)) { Fail("no World/Bowling"); return; }
        var lanes = BowlingLanes.Instance!;
        string doorArg = CmdArgs.Value("--bowlingdoor") ?? "";
        if (!DoorKey.TryParse(doorArg, out var doorKey)) { Fail($"--bowlingdoor {doorArg} is not a door"); return; }

        var (e, n) = SpawnPoint.ParseTarget();
        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        DoorIndex.Entry? door = null;
        if (!await Until(() => me.IsOnFloor() && (door = DoorIndex.Find(doorKey)) != null, 150)) { Fail($"no door {doorArg} near"); return; }

        if (_role == "C")
        {
            // late: walks in after A's roll and must find it as the others saw it
            string? said = await HeardStarting("A", "rolled ", 240);
            if (said == null) { Fail("never heard A's roll"); return; }
            if (!await WalkIn(me, interiors, door!.Value)) return;
            string key = interiors.Current!.Key;
            Expect(await Until(() => lanes.LaneWith(key, "BowlNetA") is { } l && l.Bowlers.Count >= 1 && l.Bowlers[0].Rolls.Count == 1, 20),
                "C, walking in late, got the lane with A's roll");
            if (lanes.LaneWith(key, "BowlNetA") is { } lane) Compare(said, lane);
            await Finish(2);
            return;
        }

        if (!await WalkIn(me, interiors, door!.Value)) return;
        var layout = interiors.Current!;
        var node = interiors.CurrentNode!;
        string plan = layout.Key;
        if (layout.Type != BuildingType.Bowling) { Fail($"{plan} is not a bowling centre"); return; }

        // to the shoe counter: B after A, so A is first in turn
        if (_role == "B") await Heard("A", "signed", 60);
        Stand(me, node, Piece(layout, FurnitureType.ShoeCounter), new Vector3(0, 0, 1f));
        await Seconds(1.2);
        lanes.AskSignIn(plan, _role);
        if (!await Until(() => lanes.SignedInHere() != null, 10)) { Fail("not signed in"); return; }
        var mine = lanes.SignedInHere()!.Value;
        GD.Print($"{Log} signed in at pair {mine.Pair} lane {mine.Lane}");
        Say($"signed {mine.Pair} {mine.Lane}");
        if (_role == "B")
        {
            var a = await HeardStarting("A", "signed ", 5);
            Expect(a == $"signed {mine.Pair} {mine.Lane}", $"B on A's lane ({a})");
        }
        Expect(await Until(() => me.OutfitBits != 0 && Avatar.Garments.Get(ItemId.BowlingShoes) is { } s
            && new Avatar.Outfit(me.OutfitBits).CodeIn(s.Slot) == s.Code, 5), "the hire shoes are on");

        // a ball from a rack, then the approach
        Stand(me, node, Piece(layout, FurnitureType.BallRack), new Vector3(0, 0, 0.9f));
        await Seconds(1.2);
        lanes.AskTakeBall(plan, _role == "A" ? 2 : 5, 12);
        Expect(await Until(() => lanes.SignedInHere()?.Bowler.Ball == BowlingLanes.BallAt.Hand, 10), "the ball in hand");
        var g = LaneGeometry.Of(layout.Furniture[mine.Pair]);
        Stand(me, node, mine.Pair, new Vector3(g.LaneX(mine.Lane) + (_role == "A" ? 0.4f : -0.4f), 0, g.Foul - 1.6f));
        await Seconds(1.2);

        if (_role == "A")
        {
            Say("ready");
            await Heard("B", "ready", 60);
            lanes.AskRelease(plan, Ball);
            // looked up afresh each time: every state the server sends is a new set of lanes
            BowlingLanes.Lane Lane() => lanes.LaneWith(plan, "BowlNetA")!;
            Expect(await Until(() => Lane() is { } l && l.Bowlers.Count > 0 && l.Bowlers[0].Rolls.Count == 1 && !l.Rolling, 30), "A's roll counted");
            var lane = Lane();
            int knocked = lane.Bowlers[0].Rolls[0];
            GD.Print($"{Log} rolled {knocked} {lane.Standing}");
            for (int t = 0; t < 60 && !(_heard.Any(l => l.Contains("BN B done")) && _heard.Any(l => l.Contains("BN C done"))); t++)
            {
                Say($"rolled {knocked} {lane.Standing}");
                await Until(() => _heard.Any(l => l.Contains("BN B done")) && _heard.Any(l => l.Contains("BN C done")), 3);
            }
            Expect(_heard.Any(l => l.Contains("BN B done")), "B saw the roll");
            Expect(_heard.Any(l => l.Contains("BN C done")), "C, late, found it");
            Expect(Lane().Bowlers[0].Rolls.Count == 1, "B's refused roll changed nothing");
            await Finish(2);
        }
        else
        {
            Say("ready");
            string? said = await HeardStarting("A", "rolled ", 90);
            if (said == null) { Fail("never heard A's roll"); return; }
            BowlingLanes.Lane Lane() => lanes.LaneWith(plan, "BowlNetA")!;
            Expect(await Until(() => Lane() is { } l && l.Bowlers.Count > 0 && l.Bowlers[0].Rolls.Count == 1, 10), "B got A's roll");
            Compare(said, Lane());
            Expect(lanes.FramesSeen(plan, mine.Pair, mine.Lane) > 20, $"B had the deck's frames ({lanes.FramesSeen(plan, mine.Pair, mine.Lane)})");
            // out of turn: refused
            string? told = null;
            lanes.Told += why => told = why;
            await Until(() => !Lane().CycleBusy(Net.ClockSync.SimNow), 15);
            lanes.AskRelease(plan, Ball);
            Expect(await Until(() => told != null, 5) && told!.Contains("turn"), $"B refused out of turn ({told})");
            Say("done");
            await Finish(4);
        }
    }

    private void Compare(string said, BowlingLanes.Lane lane)
    {
        var parts = said.Split(' ');
        Expect(parts.Length == 3 && int.Parse(parts[1]) == lane.Bowlers[0].Rolls[0], $"the same pins down ({said} / {lane.Bowlers[0].Rolls[0]})");
        Expect(parts.Length == 3 && ushort.Parse(parts[2]) == lane.Standing, $"the same pins standing ({said} / {lane.Standing})");
        if (_role == "C") Say("done");
    }

    private static int Piece(InteriorLayout l, FurnitureType type) => l.Furniture.FindIndex(f => f.Type == type);

    /// <summary>The body at <paramref name="local"/> in piece <paramref name="piece"/>'s frame, on its floor.</summary>
    private static void Stand(FootPlayer me, InteriorNode node, int piece, Vector3 local)
    {
        var l = node.Layout;
        var f = l.Furniture[piece];
        if (local.Z > 0 && f.Type != FurnitureType.LanePair) local = local with { Z = f.D / 2 + local.Z };
        var at = node.GlobalTransform * (BowlingLanes.PieceFrame(l, f) * local);
        me.GlobalPosition = at + Vector3.Up * 0.1f;
        me.Velocity = Vector3.Zero;
    }

    /// <summary>From just outside the door: open it and walk through (as <see cref="CinemaNetProbe"/>).</summary>
    private async Task<bool> WalkIn(FootPlayer me, InteriorManager interiors, DoorIndex.Entry door)
    {
        door = DoorIndex.Find(door.Key) ?? door;
        string doorKey = door.Key.ToString();
        var inward = -door.Outward;
        me.LeaveInterior(door.World + door.Outward * 1.2f + Vector3.Up * 0.3f, Mathf.Atan2(-inward.X, -inward.Z));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);
        await Until(() => me.IsOnFloor(), 10);
        bool open = false;
        for (int attempt = 0; attempt < 4 && !open; attempt++)
        {
            if (!interiors.IsOpen(doorKey)) me.TryInteract();
            open = await Until(() => interiors.Links.TryGetValue(doorKey, out var lk) && lk.Passable && lk.Swing >= 1f, 5 + attempt * 2);
        }
        if (!open) { Fail($"the door {doorKey} never opened"); return false; }
        Input.ActionPress(PlayerInput.MoveForward);
        bool inside = await Until(() => me.Indoors && interiors.Current != null, 8);
        await Seconds(0.4);
        Input.ActionRelease(PlayerInput.MoveForward);
        if (!inside) { Fail($"could not walk in through {doorKey}"); return false; }
        await Seconds(1.0);
        return true;
    }
}
