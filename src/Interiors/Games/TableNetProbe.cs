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
        string? venue = CmdArgs.Value("--tablevenue");
        if (CmdArgs.Value("--at") != null)
        {
            var (e, n) = SpawnPoint.ParseTarget();
            var spot = _origin.ToWorld(e, n, 0);
            me.GlobalPosition = new Vector3(spot.X, me.GlobalPosition.Y + 2, spot.Z);
            me.Velocity = Vector3.Zero;
            me.RequestReplacement();
        }
        DoorIndex.Entry? door = null;
        if (venue != null)
        {
            // a venue by name: its first door the index holds
            var building = await interiors.BuildingFor(venue);
            if (building == null && InteriorManager.FindVenue(venue) is { } vi)
            {
                // a test world's venues are not in its tiles' venue index (as --enter finds them): the door nearest the venue's place
                var rec = Venues.At(vi);
                var place = _origin.ToWorld(rec.E, rec.N, 0);
                if (await Until(() => DoorIndex.All().Any(), 60))
                    building = DoorIndex.All().OrderBy(d => new Vector2(d.World.X - place.X, d.World.Z - place.Z).Length()).First().Key.Building;
            }
            if (building is not { } b0) { Fail($"no venue \"{venue}\""); return; }
            var want = b0;
            if (!await Until(() => me.IsOnFloor() && (door = DoorIndex.All().Where(d => d.Key.Building == want).OrderBy(d => d.Key.Slot).Cast<DoorIndex.Entry?>().FirstOrDefault()) != null, 150))
            { Fail($"no door of {venue} ({want}) near"); return; }
            var building0 = want;
            GD.Print($"{Log} {venue}: building {building0}, door {door!.Value.Key}");
        }
        else
        {
            if (!DoorKey.TryParse(doorArg, out var doorKey)) { Fail($"--tabledoor {doorArg} is not a door"); return; }
            if (!await Until(() => me.IsOnFloor() && (door = DoorIndex.Find(doorKey)) != null, 150)) { Fail($"no door {doorArg} near"); return; }
        }
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
        if (_role == "B") await Heard("A", "seated", 300);
        if (spec.Make(f).MaxSeats == 1)
        {
            await Solo(games, node, index, spec, me);
            return;
        }
        TableGamePlay.Begin(me, node, index, spec);
        long myId = Multiplayer.GetUniqueId();
        TableGame? Game() => games.GameAt(key, index);
        Expect(await Until(() => Game()?.SeatOf(myId) >= 0, 15), $"{_role} took a seat");
        Say("seated");
        // the other may load the world a good while later
        if (_role == "A") Expect(await Until(() => Game()?.Seated == 2, 300), "both seats are taken");
        else await Until(() => Game()?.Seated == 2, 60);

        if (Game() is { Live: true })
        {
            await Live(games, key, index, myId);
            return;
        }
        var rng = new Random(_role == "A" ? 1 : 2);
        int mine = 0, compared = 0;
        // every state this peer holds, recorded each frame: waiting on the other's echo must not skip one,
        // or the numbering slips and every later change compares against the wrong one (#1501, flaky under load)
        var states = new List<string> { State(Game()) };
        bool recording = true;
        _ = Record();
        async Task Record()
        {
            while (recording && IsInsideTree())
            {
                if (Game() is { } now && State(now) is var s && s != states[^1]) states.Add(s);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        double until = Time.GetTicksMsec() / 1000.0 + 300;
        string other = _role == "A" ? "B" : "A";
        while (compared < Changes && Time.GetTicksMsec() / 1000.0 < until)
        {
            if (compared + 1 < states.Count)
            {
                compared++;
                string now = states[compared];
                Say($"state {compared} {now}");
                string? heard = await HeardStarting(other, $"state {compared} ", 20);
                Expect(heard == $"state {compared} {now}", $"both hold the same game after change {compared}");
                continue;
            }
            var g = Game();
            if (g == null) { await Seconds(0.2); continue; }
            int seat = g.SeatOf(myId);
            if (seat >= 0 && g.Up == seat && !g.Busy(games.ClockNow + 0.1) && g.Bot(seat, double.MaxValue, rng) is { } move)
            {
                games.AskAct(key, index, move.Action, move.Args);
                mine++;
                int seen = states.Count;
                await Until(() => states.Count > seen, 10);
                continue;
            }
            await Seconds(0.2);
        }
        recording = false;
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

    /// <summary>
    /// A live game (table football): each player in turn pushes their rods for a moment through the controls; the other
    /// must see those rods slide in the streamed poses, and the ball move. Both read the same score from the state.
    /// B steps back; A adds a bot, whose rods the server moves.
    /// </summary>
    private async Task Live(TableGames games, string key, int index, long myId)
    {
        TableGame? Game() => games.GameAt(key, index);
        string other = _role == "A" ? "B" : "A";
        int mySeat = Game()!.SeatOf(myId), myTeam = mySeat % 2;
        // what a seat's push moves, as this peer sees it: table football's rods (pushed away from the player), air
        // hockey's mallet (pushed up the table)
        float RodsOf(int team)
        {
            switch (Game())
            {
                case Foosball.FoosballGame f:
                {
                    float sum = 0;
                    for (int rod = 0; rod < 8; rod++) if (Foosball.FoosballGame.Team[rod] == team) sum += f.Slide[rod];
                    return sum;
                }
                case AirHockey.AirHockeyGame h:
                    return h.MalletAt[team].Y;
            }
            return 0;
        }
        // air hockey: a coin starts the game (the check pays none)
        if (Game() is AirHockey.AirHockeyGame && _role == "A") games.AskAct(key, index, AirHockey.AirHockeyGame.ActCoin);
        Vector2 Ball() => Game() switch { Foosball.FoosballGame f => f.BallAt, AirHockey.AirHockeyGame h => h.PuckAt, _ => Vector2.Zero };
        bool InPlay() => Game() switch { Foosball.FoosballGame f => f.InPlay, AirHockey.AirHockeyGame h => h.InPlay, _ => false };
        Expect(await Until(InPlay, 20), $"{_role} sees the game start");
        var ballFrom = Ball();
        bool ran = await Until(() => Ball().DistanceTo(ballFrom) > 0.02f, 10);
        // a still puck waits for a mallet: the push below moves it
        if (Game() is Foosball.FoosballGame) Expect(ran, $"{_role} sees the ball run");
        foreach (string pusher in new[] { "A", "B" })
        {
            if (pusher == _role)
            {
                await Heard(other, "watching", 20);
                Input.ActionPress(PlayerInput.MoveForward);
                await Seconds(1.0);
                Input.ActionRelease(PlayerInput.MoveForward);
                Say("pushed");
            }
            else
            {
                int team = 1 - myTeam;
                float before = RodsOf(team);
                Say("watching");
                await Heard(pusher, "pushed", 20);
                // the pusher's rods moved away from them (red's towards +z, blue's towards −z); a mallet up the table
                float moved = (RodsOf(team) - before) * (team == 0 ? 1 : -1);
                Expect(Mathf.Abs(moved) > 0.05f, $"{_role} saw {pusher}'s {(Game() is AirHockey.AirHockeyGame ? "mallet" : "rods")} move ({Mathf.Abs(moved) * 100:F1} cm)");
            }
        }
        await Seconds(1.0);
        string score = Game() switch { Foosball.FoosballGame g => $"{g.Goals[0]}:{g.Goals[1]}", AirHockey.AirHockeyGame h => $"{h.Goals[0]}:{h.Goals[1]}", _ => "" };
        Say($"score {score}");
        string? theirs = await HeardStarting(other, "score ", 20);
        Expect(theirs == $"score {score}", $"both read the same score ({score} / {theirs})");
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
            float before = RodsOf(1 - myTeam);
            bool moved = await Until(() => Mathf.Abs(RodsOf(1 - myTeam) - before) > 0.03f, 20);
            Expect(moved, "the bot plays on the server (its side moves)");
            TableGamePlay.Current?.QueueFree();
            Expect(await Until(() => Game()?.Seated == 0, 10), "the bot left with the last player");
        }
        await Finish(3);
    }

    /// <summary>
    /// A one-player machine (the claw): A steps up and plays the bot's way (a coin, steering, a drop); B, beside it, can
    /// only watch: it must see the claw move and drop, and both must hold the same game when the go is over.
    /// </summary>
    private async Task Solo(TableGames games, InteriorNode node, int index, GameSpec spec, FootPlayer me)
    {
        string key = node.Layout.Key;
        TableGame? Game() => games.GameAt(key, index);
        long myId = Multiplayer.GetUniqueId();
        string other = _role == "A" ? "B" : "A";
        if (_role == "A")
        {
            TableGamePlay.Begin(me, node, index, spec);
            Expect(await Until(() => Game()?.SeatOf(myId) == 0, 15), "A took the machine");
            Say("seated");
            await Heard("B", "watching", 300);
            var rng = new Random(1);
            // one go, the bot's hands on A's client: a coin, steering by the stick, a drop
            var g = (Claw.ClawGame)Game()!;
            games.AskAct(key, index, Claw.ClawGame.ActCoin);
            Expect(await Until(() => Game() is Claw.ClawGame { State: Claw.ClawGame.Phase.Steer }, 10), "the coin started a go");
            double until = Time.GetTicksMsec() / 1000.0 + 40;
            while (Game() is Claw.ClawGame { State: Claw.ClawGame.Phase.Steer } c && Time.GetTicksMsec() / 1000.0 < until)
            {
                var copy = new Claw.ClawGame();
                copy.Unpack(c.Pack());
                if (copy.Bot(0, 0, rng) is { Action: Claw.ClawGame.ActDrop }) { games.AskAct(key, index, Claw.ClawGame.ActDrop); break; }
                games.SendInput(key, index, Claw.ClawGame.Pack(copy.Pile.Count > 0 ? (copy.Pile[0].At - copy.ClawAt).Normalized() : Vector2.Zero));
                await Seconds(0.1);
            }
            Expect(await Until(() => Game() is Claw.ClawGame { State: Claw.ClawGame.Phase.Idle }, 20), $"the go ended: {(Game() as Claw.ClawGame)?.Call}");
            Say("go over");
        }
        else
        {
            await Heard("A", "seated", 300);
            var from = (Game() as Claw.ClawGame)?.ClawAt ?? Vector2.Zero;
            Say("watching");
            Expect(await Until(() => Game() is Claw.ClawGame c && c.ClawAt.DistanceTo(from) > 0.05f, 30), "B sees the claw move");
            Expect(await Until(() => Game() is Claw.ClawGame { Down: > 0.5f }, 40), "B sees it drop");
            await Heard("A", "go over", 60);
        }
        await Seconds(1);
        Say($"game {State(Game())}");
        string? theirs = await HeardStarting(other, "game ", 20);
        Expect(theirs == $"game {State(Game())}", "both hold the same game after the go");
        Say("done");
        await Heard(other, "done", 30);
        if (_role == "A")
        {
            TableGamePlay.Current?.QueueFree();
            Expect(await Until(() => Game()?.Seated == 0, 10), "A stepped back");
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
