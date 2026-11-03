using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors.Games;

/// <summary>
/// <c>--tablenet A|B --tablegame &lt;FurnitureType&gt;</c> with <c>--connect</c> on the real map at a venue with that piece
/// (#1324, <c>tools/tablenetcheck.sh</c>): any turn game over the network. A and B walk in by <c>--tabledoor</c> and take
/// seats at the same piece; whoever is up plays the bot's move (<see cref="TableGame.Bot"/>) until each has acted
/// <see cref="Turns"/> times and seen the other act; after every change both compare the whole packed state. B steps
/// back; A, alone, adds a bot, which the server plays; the bot leaves with A.
/// </summary>
public partial class TableNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--tablenet");
    /// <summary>The changes both peers compare: each sees every one, so both stop at the same one.</summary>
    private const int Changes = 8;

    private readonly WorldOrigin _origin;

    public TableNetProbe(ItemController items, WorldOrigin origin) : base(items, "tablenet", "TN") => _origin = origin;

    public TableNetProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(240)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        if (!await Until(() => TableGames.Instance != null, 10)) { Fail("no World/TableGames"); return; }
        var games = TableGames.Instance!;
        if (!Enum.TryParse<FurnitureType>(CmdArgs.Value("--tablegame") ?? "", out var type) || GameCatalog.Of(type) is not { } spec) { Fail("--tablegame names no playable piece"); return; }
        string doorArg = CmdArgs.Value("--tabledoor") ?? "";
        if (!DoorKey.TryParse(doorArg, out var doorKey)) { Fail($"--tabledoor {doorArg} is not a door"); return; }
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
        int index = layout.Furniture.FindIndex(f => f.Type == type);
        if (index < 0) { Fail($"{key} has no {type}"); return; }
        GD.Print($"{Log} in {key}, {spec.Title} #{index}");
        var f = layout.Furniture[index];
        // in front of the piece, inside its reach (a table's ends, a board's throw line)
        var local = new Vector3(_role == "A" ? -0.3f : 0.3f, 0.1f, f.D / 2 + 0.5f);
        if (!spec.UseFrom(local, f)) local = new Vector3(0, 0.1f, (f.D / 2 + 0.4f) * (_role == "A" ? -1 : 1));
        me.GlobalPosition = node.GlobalTransform * (TableGames.PieceFrame(layout, f) * local);
        me.Velocity = Vector3.Zero;
        await Seconds(1.0);
        if (_role == "B") await Heard("A", "seated", 60);
        TableGamePlay.Begin(me, node, index, spec);
        long myId = Multiplayer.GetUniqueId();
        TableGame? Game() => games.GameAt(key, index);
        Expect(await Until(() => Game()?.SeatOf(myId) >= 0, 15), $"{_role} took a seat");
        Say("seated");
        // the other may load the world a good while later
        if (_role == "A") Expect(await Until(() => Game()?.Seated == 2, 300), "both seats are taken");
        else await Until(() => Game()?.Seated == 2, 60);

        var rng = new Random(_role == "A" ? 1 : 2);
        int mine = 0, compared = 0;
        string last = State(Game());
        double until = Time.GetTicksMsec() / 1000.0 + 300;
        string other = _role == "A" ? "B" : "A";
        while (compared < Changes && Time.GetTicksMsec() / 1000.0 < until)
        {
            var g = Game();
            if (g == null) { await Seconds(0.2); continue; }
            string now = State(g);
            if (now != last)
            {
                last = now;
                compared++;
                Say($"state {compared} {now}");
                string? heard = await HeardStarting(other, $"state {compared} ", 20);
                Expect(heard == $"state {compared} {now}", $"both hold the same game after change {compared}");
                continue;
            }
            int seat = g.SeatOf(myId);
            if (seat >= 0 && g.Up == seat && !g.Busy(games.ClockNow + 0.1) && g.Bot(seat, double.MaxValue, rng) is { } move)
            {
                games.AskAct(key, index, move.Action, move.Args);
                mine++;
                await Until(() => State(Game()) != last, 10);
                continue;
            }
            await Seconds(0.2);
        }
        Expect(mine >= 2 && compared >= 4, $"{_role} played and watched ({mine} moves, {compared} changes compared)");
        Say("done turns");
        await Heard(other, "done turns", 60);

        if (_role == "B")
        {
            TableGamePlay.Current?.QueueFree();
            Expect(await Until(() => Game()?.SeatOf(myId) < 0, 10), "B stepped back");
            Say("left");
        }
        else
        {
            await Heard("B", "left", 30);
            games.AskBot(key, index);
            Expect(await Until(() => Game()?.Seats.Any(TableGame.IsBot) == true, 10), "a bot took the free seat");
            string before = State(Game());
            bool botMoved = false;
            for (int k = 0; k < 60 && !botMoved; k++)
            {
                var g = Game()!;
                int seat = g.SeatOf(myId);
                int bot = Array.FindIndex(g.Seats, TableGame.IsBot);
                if (g.Up == seat && !g.Busy(games.ClockNow + 0.1) && g.Bot(seat, double.MaxValue, rng) is { } move)
                {
                    games.AskAct(key, index, move.Action, move.Args);
                    await Until(() => Game()!.Up == bot, 10);
                    before = State(Game());
                    // the server plays the bot's turn
                    botMoved = await Until(() => State(Game()) != before, 15);
                    continue;
                }
                await Seconds(0.5);
            }
            Expect(botMoved, "the bot played its turn on the server");
            TableGamePlay.Current?.QueueFree();
            Expect(await Until(() => Game()?.Seated == 0, 10), "the bot left with the last player");
        }
        await Finish(3);
    }

    /// <summary>The whole game as this peer holds it, as text: two peers' views compared.</summary>
    private static string State(TableGame? g) => g == null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(g.Pack()))[..16];

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
