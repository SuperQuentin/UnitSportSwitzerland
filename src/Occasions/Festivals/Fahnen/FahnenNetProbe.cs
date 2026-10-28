using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Occasions.Festivals.Fahnen;

/// <summary>
/// <c>--fahnenet a|b</c> with <c>--connect --festival test</c> (driven by <c>tools/fahnenetcheck.sh</c>, #1269 part 5): Fahnenschwingen over a
/// dedicated server whose calendar reads 16 August 2027 (day 3 of the Alplerfest, held at the fixture start). Everything is asserted on the REMOTE peers.
/// Lea (A) and Tom (B) stand on the flag ground and ask for a routine in the same slot (A waits for the early part of a slot and says go); each is
/// told its start and seed over the wire, plays the whole routine perfectly through the real game, hands it in over the wire, and is told its
/// result: both are in step (x1.3), both are paid Batzen that arrive in their own section, and each sees its own points on its board.
/// </summary>
public partial class FahnenNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--fahnenet");

    public FahnenNetProbe(ItemController items) : base(items, "fahnenet", "FN", "fahnenet_") { }
    public FahnenNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("aelplerfest-2027") ?? new FestivalInstance();

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && FahnenGame.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        var game = FahnenGame.Instance!;
        const string Id = FestivalCatalog.AelplerfestId;
        Expect(await Until(() => Svc.RunningOf(Id) is { Day: 3 }, 30), "the server runs the Alplerfest on day 3");
        Expect(await Until(() => Me is { } me && me.IsOnFloor() && FahnenGame.AtGround(me), 30), "the player stands on the flag ground (the client sees it open)");
        Expect(FahnenGame.InteractionFor(Me!) is { } p && p.Text.Contains("Swing the flag"), "the prompt is offered");
        game.Stick = () => FahnenCheck.Drive(game);
        game.UseHeld = () => FahnenCheck.Hold(game);

        var scored = new List<(int Score, int Batzen, bool Synced)>();
        Svc.FahneScored += (s, b, y, _, _, _) => scored.Add((s, b, y));
        // B says "ready" every 2 s (A may join the chat after the first one); A waits for it, then for the early part of a slot (it turns over
        // every 5 s of the shared clock), says "go" a few times and asks; B asks as soon as it hears the first go: the same slot
        if (_role == "A")
        {
            Expect(await Heard("B", "ready", 120), "B is on the ground");
            Expect(await Until(() => ClockSync.ServerNow % 5.0 is > 2.3 and < 3.4, 20), "A found the early part of a slot");
            for (int i = 0; i < 3; i++) { Say("go"); if (i == 0) game.Ask(); await Seconds(0.5); }
        }
        else
        {
            for (int i = 0; i < 60 && !await Heard("A", "go", 2); i++) Say("ready");
            Expect(await Heard("A", "go", 1), "B hears A go");
            game.Ask();
        }
        Expect(await Until(() => game.Current == FahnenGame.Phase.Playing, 10), $"the start and the seed came over the wire ({game.Current})");
        Expect(game.FlagShown && Me!.LookTaken, "the flag is in the hands, the look is taken");
        Expect(await Until(() => game.Current == FahnenGame.Phase.Result, 240), $"the result came back over the wire ({game.Current})");
        var r = game.LastResult;
        GD.Print($"[fahnenet {_role}] result {r}");
        Expect(scored.Count == 1 && scored[0].Score == r.Score, $"one result event ({scored.Count})");
        // headless clients run in real time on a loaded machine: most figures on the beat, not all
        Expect(!r.Dropped && r.Hits * 3 >= r.Figures * 2 && r.Figures >= 8, $"most of the {r.Figures} figures landed on the beat ({r.Hits})");
        Expect(r.Synced && r.Score >= 780, $"in step with the other swinger: x1.3 ({r.Score}, synced {r.Synced})");
        int paid = Math.Min(FahnenRules.BatzenFor(r.Score), 100);
        Expect(r.Batzen == paid, $"{paid} Batzen granted ({r.Batzen})");
        Expect(await Until(() => Mine.Batzen == paid && Mine.Points == r.Score, 15), $"they reached this player's own section ({Mine.Batzen}/{Mine.Points})");
        Expect(await Until(() => Svc.BoardOf(Id) is { } b && b.Points == r.Score, 15), $"and its board ({Svc.BoardOf(Id)?.Points})");
        Expect(!Me!.LookTaken && !game.FlagShown, "the flag is put down");
        Say("done");
        Expect(await Heard(_role == "A" ? "B" : "A", "done", 120), "the other swinger finished too");
        await Finish(2.0);
    }
}
