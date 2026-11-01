using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors.Pinball;

/// <summary>
/// <c>--pinballnet A|B</c> with <c>--connect</c> on the real map at a venue with a pinball machine (#1325,
/// <c>tools/pinballnetcheck.sh</c>): pinball over the network. A and B walk in by <c>--pinballdoor</c> and stand at the
/// same machine. A steps up and a game starts on A's client (a robot at its flippers); B, watching, must get A's
/// balls and flippers streamed; B presses Start in the first ball and joins as player 2; when A's ball ends the
/// server hands the table to B's client, which runs it from the game's log, and A watches. At the game's end both
/// must read the same scores on the backbox, and they must be the scores the last runner's own rules counted:
/// the server's mirror scored the same game from the events alone.
/// </summary>
public partial class PinballNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--pinballnet");

    private readonly WorldOrigin _origin;

    public PinballNetProbe(ItemController items, WorldOrigin origin) : base(items, "pinnet", "PN") => _origin = origin;

    public PinballNetProbe() : this(null!, null!) { }

    protected override bool EchoSay => false;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(240)) return;
        var me = Me!;
        var interiors = InteriorManager.Instance!;
        if (!await Until(() => PinballTables.Instance != null, 10)) { Fail("no World/Pinball"); return; }
        var tables = PinballTables.Instance!;
        string doorArg = CmdArgs.Value("--pinballdoor") ?? "";
        if (!DoorKey.TryParse(doorArg, out var doorKey)) { Fail($"--pinballdoor {doorArg} is not a door"); return; }
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
        int index = layout.Furniture.FindIndex(f => f.Type == FurnitureType.Pinball);
        if (index < 0) { Fail($"{key} has no pinball machine"); return; }
        GD.Print($"{Log} in {key}, machine #{index}: {PinballLayouts.Get(PinballTables.TableOf(layout, index)).Title}");
        Stand(me, node, layout, index, _role == "A" ? -0.1f : 0.5f);
        await Seconds(1.0);

        if (_role == "B") await Heard("A", "playing", 90);
        PinballPlay.Begin(me, key, index);
        if (!await Until(() => PinballPlay.Current != null, 5)) { Fail("could not step up"); return; }
        var play = PinballPlay.Current!;
        play.Robot = true;

        if (_role == "A")
        {
            Expect(await Until(() => play.Running, 15), "A's client runs the table");
            Say("playing");
            Expect(await Until(() => tables.PlayersOf(key, index).Count == 2, 60), "B joined as player 2");
            // A's ball ends: the server hands the table to B's client; A watches B's balls
            Expect(await Until(() => !play.Running && tables.Peek(key, index)?.Runner is { } r && r != 0 && r != Multiplayer.GetUniqueId(), 300), "the turn passed to B");
            int before = tables.Peek(key, index)?.Frames ?? 0;
            Expect(await Until(() => (tables.Peek(key, index)?.Frames ?? 0) > before + 40, 60), "A watched B's frames");
        }
        else
        {
            Expect(await Until(() => (tables.Peek(key, index)?.Frames ?? 0) > 40, 60), $"B watched A's balls ({tables.Peek(key, index)?.Frames} frames)");
            Expect(await Until(() => tables.ScreenOf(key, index) is { Phase: PinballRules.Phase.Playing, Scores.Length: 1 }, 20), "B reads A's game on the backbox");
            play.PressStart();
            Expect(await Until(() => tables.PlayersOf(key, index).Count == 2, 20), "B joined as player 2");
            Expect(await Until(() => play.Running, 300), "B's client was handed the table");
            GD.Print($"{Log} running: player {play.LastRules?.Up + 1}, ball {play.LastRules?.Current?.Ball}, player 1 had {play.LastRules?.Players[0].Score}");
        }

        // to the end of the game: the backbox (the server's mirror) and the last runner's own count agree
        Expect(await Until(() => tables.ScreenOf(key, index) is { Phase: PinballRules.Phase.GameOver }, 900), "the game ended");
        var screen = tables.ScreenOf(key, index);
        string scores = screen == null ? "" : string.Join(",", screen.Scores);
        GD.Print($"{Log} final {scores}");
        Say($"final {scores}");
        string other = _role == "A" ? "B" : "A";
        string? theirs = await HeardStarting(other, "final ", 30);
        Expect(theirs == $"final {scores}", $"both read the same scores ({scores} / {theirs})");
        if (play.LastRules is { State: PinballRules.Phase.GameOver } mine)
        {
            string counted = string.Join(",", mine.Players.Select(p => p.Score));
            Expect(counted == scores, $"the server scored what the last runner's rules counted ({counted} / {scores})");
        }

        // the machine's high scores: both games on its table under the players' names, here and on the backbox
        string Highs() => tables.HighsOf(key, index) is { } h ? string.Join(",", h.Entries.Select(x => $"{x.Name}:{x.Score}")) : "";
        bool BothListed() => screen != null && screen.Scores.All(s => tables.HighsOf(key, index)?.Entries.Any(x => x.Score == s) == true)
            && tables.HighsOf(key, index)!.Entries.Any(x => x.Name.StartsWith("PinNetA")) && tables.HighsOf(key, index)!.Entries.Any(x => x.Name.StartsWith("PinNetB"));
        Expect(await Until(BothListed, 10), $"the high scores list both players ({Highs()})");
        var cabinet = node.PinballOf(index);
        Expect(await Until(() => cabinet != null && cabinet.Display.Shown.Contains("PinNet"), 12), "the backbox shows the high scores in attract");
        if (_role == "B")
        {
            // as a late joiner: out of the venue (the client forgets it) and back in: the server sends the table again
            play.QueueFree();
            await Seconds(0.5);
            if (!await WalkIn(me, interiors, door!.Value)) return;
            Expect(await Until(BothListed, 15), $"walking in again, the high scores came with the machine ({Highs()})");
        }
        GD.Print($"{Log} high scores {Highs()}");
        await Finish(3);
    }

    /// <summary>The body in front of the machine (beside it by <paramref name="side"/>), facing it.</summary>
    private static void Stand(FootPlayer me, InteriorNode node, InteriorLayout l, int index, float side)
    {
        var at = node.GlobalTransform * (PinballTables.PieceFrame(l, l.Furniture[index]) * new Vector3(side, 0.1f, PinballCabinet.Front + 0.4f));
        me.GlobalPosition = at;
        me.Velocity = Vector3.Zero;
    }

    /// <summary>From just outside the door: open it and walk through (as the cinema's and bowling's checks).</summary>
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
            open = await UntilHeld(() => interiors.Links.TryGetValue(doorKey, out var lk) && lk.Passable && lk.Swing >= 1f, DoorSettle, 5 + attempt * 2);
        }
        if (!open) { Fail($"the door {doorKey} never opened"); return false; }
        bool inside = await WalkInThrough(me, interiors.Links[doorKey].Outside, () => me.Indoors && interiors.Current != null);
        if (!inside) { Fail($"could not walk in through {doorKey}"); return false; }
        await Seconds(1.0);
        return true;
    }
}
