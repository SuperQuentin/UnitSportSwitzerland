using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors.Games.Pool;

/// <summary>
/// <c>--poolnet A|B</c> with <c>--connect</c> on the real map at a venue with a pool table (#1324,
/// <c>tools/poolnetcheck.sh</c>): eight-ball over the network. A and B walk in by <c>--pooldoor</c> and take the two
/// seats of the same table. A breaks (the server resolves it); B must see the shot replayed and the balls end where
/// A's client has them; then whoever is up shoots, the bot's way, until each has shot and seen the other's shot. B
/// steps back; A, alone, adds a bot, which shoots on its turn on the server. Every peer's table must agree.
/// </summary>
public partial class PoolNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--poolnet");

    private readonly WorldOrigin _origin;

    public PoolNetProbe(ItemController items, WorldOrigin origin) : base(items, "poolnet", "PL") => _origin = origin;

    public PoolNetProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(240)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        if (!await Until(() => TableGames.Instance != null, 10)) { Fail("no World/TableGames"); return; }
        var games = TableGames.Instance!;
        string doorArg = CmdArgs.Value("--pooldoor") ?? "";
        if (!DoorKey.TryParse(doorArg, out var doorKey)) { Fail($"--pooldoor {doorArg} is not a door"); return; }
        var (e, n) = SpawnPoint.ParseTarget();
        var spot = _origin.ToWorld(e, n, 0);
        me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        DoorIndex.Entry? door = null;
        if (!await Until(() => me.IsOnFloor() && (door = DoorIndex.Find(doorKey)) != null, 150)) { Fail($"no door {doorArg} near"); return; }
        if (!await WalkIn(me, interiors, door!.Value)) return;
        var layout = interiors.Current!;
        var node = interiors.CurrentNode!;
        string key = layout.Key;
        int index = layout.Furniture.FindIndex(f => f.Type == FurnitureType.PoolTable);
        if (index < 0) { Fail($"{key} has no pool table"); return; }
        GD.Print($"{Log} in {key}, pool table #{index}");
        var f = layout.Furniture[index];
        // each at an end of the table, inside reach
        var at = node.GlobalTransform * (TableGames.PieceFrame(layout, f) * new Vector3(0, 0.1f, (f.D / 2 + 0.4f) * (_role == "A" ? -1 : 1)));
        me.GlobalPosition = at;
        me.Velocity = Vector3.Zero;
        await Seconds(1.0);
        if (_role == "B") await Heard("A", "seated", 60);
        TableGamePlay.Begin(me, node, index, GameCatalog.Of(FurnitureType.PoolTable)!);
        long myId = Multiplayer.GetUniqueId();
        PoolGame? Game() => games.GameAt(key, index) as PoolGame;
        Expect(await Until(() => Game()?.SeatOf(myId) >= 0, 15), $"{_role} took a seat");
        Say("seated");
        if (_role == "A") Expect(await Until(() => Game()?.Seated == 2, 60), "both seats are taken");

        // turns until each has shot twice and watched the other: the shooter shoots the bot's way, both compare the tables
        var rng = new Random(_role == "A" ? 1 : 2);
        int mine = 0, watched = 0, lastSeen = Game()?.ShotSeq ?? 0;
        double until = Time.GetTicksMsec() / 1000.0 + 300;
        while ((mine < 2 || watched < 2) && Time.GetTicksMsec() / 1000.0 < until && Game() is { State: not PoolGame.Phase.Over } g)
        {
            if (g.ShotSeq != lastSeen)
            {
                lastSeen = g.ShotSeq;
                if (g.SeatOf(myId) != g.Up || g.Call.StartsWith("Foul")) watched++;
                Say($"shot {g.ShotSeq} {Table(g)}");
                string other = _role == "A" ? "B" : "A";
                string? theirs = await HeardStarting(other, $"shot {g.ShotSeq} ", 20);
                Expect(theirs == $"shot {g.ShotSeq} {Table(g)}", $"both see the same table after shot {g.ShotSeq}");
                continue;
            }
            int seat = g.SeatOf(myId);
            if (seat == g.Up && !g.Busy(games.ClockNow + 0.05))
            {
                var move = g.Bot(seat, double.MaxValue, rng);
                if (move is { } m)
                {
                    games.AskAct(key, index, m.Action, m.Args);
                    if (m.Action == PoolGame.ActShoot) mine++;
                    await Until(() => Game()?.ShotSeq != g.ShotSeq || Game()?.InHand != g.InHand, 10);
                    continue;
                }
            }
            await Seconds(0.2);
        }
        Expect(mine >= 1, $"{_role} shot ({mine})");
        Expect(watched >= 1, $"{_role} watched the other's shots ({watched})");
        // the rig on this peer replayed the last shot and rests on the server's table
        var rig = (PoolRig)node.TableRigOf(index)!;
        await Until(() => Game() is { } g2 && games.ClockNow > g2.ShotEnd + 1, 30);
        var last = Game()!;
        float worst = 0;
        for (int i = 0; i < PoolSolver.Balls; i++)
            if (last.Table.On[i] && rig.Shown(i) is { } p) worst = Mathf.Max(worst, p.DistanceTo(last.Table.P[i]));
        Expect(worst < 0.002f, $"{_role}'s balls rest where the server has them (worst {worst * 1000:F2} mm)");
        Say("done turns");
        await Heard(_role == "A" ? "B" : "A", "done turns", 60);

        if (_role == "B")
        {
            TableGamePlay.Current?.QueueFree();
            Expect(await Until(() => Game()?.SeatOf(myId) < 0, 10), "B stepped back from the table");
            Say("left");
        }
        else
        {
            await Heard("B", "left", 30);
            // alone: a bot opponent, which the server plays
            games.AskBot(key, index);
            Expect(await Until(() => Game()?.Seats.Any(TableGames.IsBot) == true, 10), "a bot took the free seat");
            // (a game over racks the next one first)
            if (Game() is { State: PoolGame.Phase.Over }) games.AskAct(key, index, PoolGame.ActShoot, 0, 0, 0);
            int botShots = 0, seen = Game()!.ShotSeq;
            for (int k = 0; k < 40 && botShots == 0; k++)
            {
                var g = Game()!;
                if (g.ShotSeq != seen) { seen = g.ShotSeq; if (TableGames.IsBot(g.Seats[g.Up]) || g.Call.Contains("bot")) botShots++; }
                if (g.Up == g.SeatOf(myId) && !g.Busy(games.ClockNow + 0.05) && g.Bot(g.Up, double.MaxValue, rng) is { } m)
                {
                    games.AskAct(key, index, m.Action, m.Args);
                    await Until(() => Game()?.ShotSeq != g.ShotSeq || Game()?.InHand != g.InHand, 10);
                    continue;
                }
                await Seconds(1);
            }
            Expect(Game()!.Call.Contains("(bot)") || botShots > 0, $"the bot shot on its turn ({Game()!.Call})");
            TableGamePlay.Current?.QueueFree();
            Expect(await Until(() => Game()?.Seated == 0, 10), "the bot left with the last player");
        }
        await Finish(3);
    }

    /// <summary>The balls on the table, to the millimetre: two peers' views compared as text.</summary>
    private static string Table(PoolGame g)
    {
        var parts = new List<string>();
        for (int i = 0; i < PoolSolver.Balls; i++)
            parts.Add(g.Table.On[i] ? $"{i}:{(int)Math.Round(g.Table.P[i].X * 1000)},{(int)Math.Round(g.Table.P[i].Y * 1000)}" : $"{i}:-");
        return $"up{g.Up} {string.Join(" ", parts)}";
    }

    /// <summary>From just outside the door: open it and walk through (as the pinball and bowling checks).</summary>
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
