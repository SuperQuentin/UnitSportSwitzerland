using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Fahnen;

/// <summary>
/// <c>--fahnecheck --festival test --date 2027-08-16 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c>
/// (quick tier, #1269 part 5): offline, this client plays the server's part. The Älplerfest (held at the fixture's start under
/// <c>--festival test</c>) is on its day 3: the flag ground is open, the prompt is offered on it and not off it. The check plays a perfect routine
/// through the real game (the stick and the use key are scripted from the shared clock): the flag appears, the look is taken, the server grades what the
/// client recorded and pays Batzen and party points, together with a bot that swung in the same slot (the in-step bonus), while the bot
/// earns nothing; a second routine alone pays without the bonus and stays under the daily cap; on day 2 the ground is closed.
/// Windowed it saves <c>test_output/fahne_swing.png</c>. Progress lives in <c>test_output/fahnecheck</c>.
/// </summary>
public partial class FahnenCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--fahnecheck");

    private readonly Func<FootPlayer?> _me;
    private const long Bot = -7;

    public FahnenCheck(ItemController items, Func<FootPlayer?> me) : base(items, "fahnecheck", "", "fahne_") => _me = me;
    public FahnenCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static Vector2 Drive(FahnenGame game)
    {
        double t = game.Clock;
        foreach (var slot in game.Routine.Figures)
        {
            if (slot.Kind == FahnenFigure.Hochwurf) continue;
            double end = FahnenRules.Seconds(slot.StartBeat + slot.Beats);
            if (t < end - 1.2 || t >= end - 0.02) continue;
            var path = GestureRing.Ideal(FahnenRules.StrokeFor(slot.Kind));
            int i = Math.Clamp((int)((t - (end - 1.2)) / 1.18 * path.Length), 0, path.Length - 1);
            double a = path[i] * Math.Tau / GestureRing.Sectors;
            return new Vector2((float)Math.Sin(a), (float)-Math.Cos(a));
        }
        return Vector2.Zero;
    }

    private static bool Hold(FahnenGame game)
    {
        double t = game.Clock, spb = FahnenRules.SecondsPerBeat;
        foreach (var slot in game.Routine.Figures)
        {
            if (slot.Kind != FahnenFigure.Hochwurf) continue;
            double begin = FahnenRules.Seconds(slot.StartBeat), land = begin + 5 * spb;
            if (t >= begin && t < begin + 2 * spb) return true;   // held two beats: three in the air
            if (t >= land && t < land + 0.2) return true;         // caught on the beat it lands
        }
        return false;
    }

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && FahnenGame.Instance != null && Me is { } me && me.IsOnFloor()
                && FestivalService.Instance.RunningOf(FestivalCatalog.AelplerfestId) != null, 90))
        {
            Fail($"no Alplerfest or no game (service {FestivalService.Instance != null}, game {FahnenGame.Instance != null}, body {Me?.Global})");
            return;
        }
        var svc = FestivalService.Instance!;
        var game = FahnenGame.Instance!;
        var adventure = AdventureService.Instance!;
        const string Id = FestivalCatalog.AelplerfestId;
        var run = svc.RunningOf(Id)!;
        Expect(run.Day == 3 && run.Length == 11, $"day 3 of 11 of the Alplerfest ({run.Day}/{run.Length})");
        Expect(run.Site is { Relocated: false }, $"held at the fixture's start ({run.Site})");
        Expect(FestivalCatalog.Get(Id)!.IsOpen(FestivalCatalog.FahneGame, run.Day), "the flag swingers are open on day 3");
        Expect(FahnenGame.AtGround(Me!), "the player stands on the flag ground");
        Expect(FahnenGame.InteractionFor(Me!) is { } prompt && prompt.Text.Contains("Swing the flag"), $"the prompt is offered ({FahnenGame.InteractionFor(Me!)?.Text})");
        var decor = FestivalOccasion.SiteOf(Id);
        Expect(decor != null, "the occasion dresses the site (the stage stands at the ground)");

        // ---- a perfect routine, with a bot in the same slot --------------------------------------------
        adventure.SessionFor(Bot);
        var wasPosition = adventure.PositionOf;
        adventure.PositionOf = p => p == Bot ? Me!.Global : wasPosition?.Invoke(p);
        game.Stick = () => Drive(game);
        game.UseHeld = () => Hold(game);
        game.Ask();
        Expect(game.Current == FahnenGame.Phase.Playing, $"the server planned a routine at once ({game.Current})");
        Expect(svc.ServeFahneBot(Bot, 1.0) == null, "a bot swings in the same slot");
        Expect(game.FlagShown && Me!.LookTaken && FahnenGame.Swinging, "the flag is in the hands and the look is taken");
        Expect(game.Routine.Figures.Length is >= 8 and <= 12 && game.Routine.Figures[0].Kind == FahnenFigure.Ufzug, $"{game.Routine.Figures.Length} figures, the raise first");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(9);
            GD.Print($"[fahnecheck] shot {Shot("swing")}");
        }
        Expect(await Until(() => game.Current == FahnenGame.Phase.Result, 240), $"the routine ends in a result ({game.Current})");
        var r = game.LastResult;
        GD.Print($"[fahnecheck] first routine: {r}");
        Expect(game.GradedFigures == game.Routine.Figures.Length && !r.Dropped, $"every figure was played and none dropped ({game.GradedFigures})");
        Expect(r.Hits == r.Figures && r.Figures == game.Routine.Figures.Length, $"every figure landed Good or better ({r.Hits}/{r.Figures})");
        Expect(r.Synced && r.Score >= 1150, $"in step with the bot: x1.3 ({r.Score}, synced {r.Synced})");
        int paid = Math.Min(FahnenRules.BatzenFor(r.Score), 100);
        Expect(r.Batzen == paid && svc.BatzenOf(AdventureService.LocalPeer, Id) == paid, $"{paid} Batzen paid ({r.Batzen} / held {svc.BatzenOf(AdventureService.LocalPeer, Id)})");
        Expect(svc.CrewPoints(AdventureService.LocalPeer, Id) == r.Score, $"the party board holds the {r.Score} points ({svc.CrewPoints(AdventureService.LocalPeer, Id)})");
        Expect(!Me!.LookTaken && !FahnenGame.Swinging && !game.FlagShown, "the flag is put down and the look is given back");
        Expect(adventure.Progress(Bot)?.Get(FestivalBook.Section).Instances.Values.Sum(i => i.Batzen) is null or 0, "the bot earned no Batzen");

        // ---- a second routine alone: no bonus, under the daily cap --------------------------------------
        await Seconds(1);
        game.Ask();
        Expect(await Until(() => game.Current == FahnenGame.Phase.Result, 240), "the second routine ends in a result");
        r = game.LastResult;
        Expect(!r.Synced && r.Score is >= 900 and <= 1000, $"alone: no bonus ({r.Score}, synced {r.Synced})");
        var inst = svc.Mine.Instances.GetValueOrDefault(run.Instance) ?? new FestivalInstance();
        Expect(inst.Earned <= 100, $"the daily cap of 100 holds ({inst.Earned})");
        Expect(inst.Best.GetValueOrDefault(FestivalCatalog.FahneGame) >= 1150, $"the best play is kept ({inst.Best.GetValueOrDefault(FestivalCatalog.FahneGame)})");

        // ---- off the ground, and on a day the game is closed --------------------------------------------
        game.Stick = () => Vector2.Zero;
        game.UseHeld = () => false;
        Me!.GlobalPosition += new Vector3(80f, 0f, 0f);
        Me.Velocity = Vector3.Zero;
        await Seconds(0.3);
        Expect(!FahnenGame.AtGround(Me) && FahnenGame.InteractionFor(Me) == null, "no prompt away from the ground");
        string? refused = null;
        svc.Refused += why => refused = why;
        Expect(svc.ServeFahneStart(AdventureService.LocalPeer) != null && refused?.Contains("flag ground") == true, $"the server refuses a swing off the ground ({refused})");
        Me.GlobalPosition -= new Vector3(80f, 0f, 0f);
        await Seconds(0.3);

        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 8, 15));
        Expect(await Until(() => svc.RunningOf(Id)?.Day == 2, 15), "the date moves to day 2");
        Expect(svc.FahneGroundNow() == null && FahnenGame.InteractionFor(Me) == null, "the flag ground is closed on day 2");
        refused = null;
        Expect(svc.ServeFahneStart(AdventureService.LocalPeer) != null && refused?.Contains("not out yet") == true, $"the server refuses a swing before day 3 ({refused})");
        await Finish(0);
    }
}
