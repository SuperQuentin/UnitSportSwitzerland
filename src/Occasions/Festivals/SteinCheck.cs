using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// <c>--steincheck --festival test --date 2027-03-14 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c>
/// (quick tier, #1269 part 3): the check-only festival's stone (open from its day 5). Offline this client plays the server's part. Away from
/// the stone there is no prompt; beside it the prompt names the puts left. E opens an attempt (the walk, the items and the jump are taken),
/// jump changes the stone, a perfect put (the lift let go on the meter's peak, four presses on the drum, played through the real input
/// actions) lands the distance the pure rules work out from the very stamps the client sent, pays exactly that, shows a stake and
/// gives the walk back; a lift let go at the trough is a foul that pays nothing but counts as a put; a forged put (stamps from the future)
/// is refused and counts for nothing; a bot takes the same server path and earns nothing, and a skilled bot beats a clumsy one; the fourth put
/// of the day is not allowed. Windowed it saves <c>test_output/stein_*.png</c>. Progress lives in <c>test_output/steincheck</c>.
/// </summary>
public partial class SteinCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--steincheck");

    private readonly Func<FootPlayer?> _me;

    public SteinCheck(ItemController items, Func<FootPlayer?> me) : base(items, "steincheck", "", "stein_") => _me = me;
    public SteinCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static FestivalInstance Inst => FestivalService.Instance!.Mine.Instances.GetValueOrDefault("test-2027") ?? new FestivalInstance();

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && SteinService.Instance != null && SteinGame.Instance != null
                && Me is { } me && me.IsOnFloor() && FestivalService.Instance.Running.Count > 0
                && FestivalService.Instance.Mine.Instances.Count >= 0 && AdventureService.Instance?.Progress(AdventureService.LocalPeer) != null, 90))
        {
            Fail($"no festival or no stone (service {SteinService.Instance != null}, game {SteinGame.Instance != null}, body {Me?.Global})");
            return;
        }
        var svc = SteinService.Instance!;
        var game = SteinGame.Instance!;
        var run = FestivalService.Instance!.RunningOf(FestivalCatalog.TestId)!;
        var open = svc.Open();
        Expect(run.Day == 5 && open != null, $"day {run.Day}: the stone is open ({open != null})");
        if (open is not { } o) { await Finish(0); return; }
        var ground = SteinGround.Of(o.Site);
        var me0 = Me!;
        GD.Print(FormattableString.Invariant($"{Log} stone at {ground.E:F1}/{ground.N:F1}, thrown ({ground.DirE:F2}, {ground.DirN:F2}); body at {me0.Global.E:F1}/{me0.Global.N:F1}"));

        // ---- at a distance nothing is offered ----------------------------------------------------------
        await Jump(ground.E + 30, ground.N);
        await Seconds(0.8);
        Expect(SteinGame.InteractionFor(me0) == null && !SteinGame.TryInteract(me0), "30 m from the stone: no prompt, E does nothing");

        // ---- beside it: the prompt, E ---------------------------------------------------------------------
        await Jump(ground.E - ground.DirE * 0.8, ground.N - ground.DirN * 0.8);
        Expect(await Until(() => SteinGame.InteractionFor(me0) != null, 5), "beside the stone there is a prompt");
        string prompt = SteinGame.InteractionFor(me0)?.Text ?? "";
        Expect(prompt.Contains("Put the stone") && prompt.Contains("3 of 3"), $"it names the puts left ({prompt})");
        var walkBefore = me0.WalkControls;
        Expect(me0.TryInteract() && await Until(() => game.State == SteinGame.Phase.Ready, 5), $"E opens an attempt ({game.State})");
        Expect(me0.GameBusy && me0.WalkControls != null && me0.WalkControls().Wish == Vector3.Zero, "the hands and the walk are taken");
        Expect(game.Attempt is { Class: 0 }, "the Unspunnen stone first");
        Input.ActionPress(PlayerInput.Jump);
        await Seconds(0.1);
        Input.ActionRelease(PlayerInput.Jump);
        Expect(await Until(() => game.State == SteinGame.Phase.Ready && game.Attempt is { Class: 1 }, 5), "jump changes the stone: the 40 kg one");
        for (int i = 0; i < 2; i++)
        {
            Input.ActionPress(PlayerInput.Jump);
            await Seconds(0.1);
            Input.ActionRelease(PlayerInput.Jump);
            await Until(() => game.State == SteinGame.Phase.Ready, 5);
            await Seconds(0.1);
        }
        Expect(game.Attempt is { Class: 0 }, "and round again to the Unspunnen stone");

        // ---- a perfect put ---------------------------------------------------------------------------------
        game.PitchOverride = 41;
        var landings = new List<SteinLanding>();
        svc.Landed += landings.Add;
        await PerfectPut(this, game);
        Expect(await Until(() => landings.Count >= 1, 8), "the put landed");
        if (landings.Count == 0) { await Finish(0); return; }
        var first = landings[0];
        GD.Print(FormattableString.Invariant($"{Log} put {first.Metres:F2} m at {first.Angle:F0} deg, power {first.Power:F2}, +{first.Batzen} Batzen, {first.Points} points"));
        Expect(first.Foul == null && first.Metres > 3.0, $"a perfect put goes far: {first.Metres:F2} m of {SteinRules.Class(0).Par}");
        Expect(first.Power > 0.95, $"the lift and the drum were on the beat (power {first.Power:F2})");
        Expect(Math.Abs(first.Angle - 41) < 0.01, "the push took the camera's angle");
        Expect(first.Batzen == (int)Math.Round(SteinRules.Class(0).MaxBatzen * Math.Min(1.0, first.Metres / SteinRules.Class(0).Par)),
            $"it paid its distance's share ({first.Batzen} Batzen)");
        Expect(Inst.Batzen == first.Batzen && Inst.Points == first.Points && Inst.Played.GetValueOrDefault("stein:5") == 1,
            $"the section holds it ({Inst.Batzen} Batzen, {Inst.Points} points, {Inst.Played.GetValueOrDefault("stein:5")} play)");
        Expect(await Until(() => game.StakesShown >= 1, 8), $"a stake shows where it fell ({game.StakesShown})");
        Expect(await Until(() => game.State == SteinGame.Phase.Idle, 10), "the attempt ends by itself");
        Expect(!me0.GameBusy && me0.WalkControls == walkBefore, "the hands and the walk are given back");
        if (DisplayServer.GetName() != "headless") Shot("landing");

        // ---- a lift let go at the trough is a foul --------------------------------------------------------
        Expect(me0.TryInteract() && await Until(() => game.State == SteinGame.Phase.Ready, 5), "E again for the second put");
        int before = landings.Count;
        await Foul(game);
        Expect(await Until(() => landings.Count > before, 8), "the foul was judged");
        var foul = landings[^1];
        Expect(foul.Foul != null && foul.Batzen == 0 && foul.Points == 0, $"the stone slipped: nothing paid ({foul.Foul})");
        Expect(Inst.Played.GetValueOrDefault("stein:5") == 2 && Inst.Batzen == first.Batzen, "but it was a put today");
        await Until(() => game.State == SteinGame.Phase.Idle, 10);

        // ---- a forged put is refused and counts for nothing ---------------------------------------------------
        var refused = new List<string>();
        svc.Refused += refused.Add;
        var a = svc.ServeBegin(AdventureService.LocalPeer, 0);
        Expect(a != null, "an attempt opens by the server path");
        double now = ClockSync.ServerNow;
        var forged = svc.ServeAttempt(AdventureService.LocalPeer, a!.Id, [now + 30, now + 31, now + 32, now + 33, now + 34, now + 35], 40);
        Expect(forged == null && refused.Count >= 1, $"stamps from the future are refused ({string.Join("; ", refused)})");
        Expect(svc.ServeAttempt(AdventureService.LocalPeer, a.Id, [now, now + 1, now + 2, now + 3, now + 4, now + 5], 40) == null, "and an attempt cannot be put twice");
        a = svc.ServeBegin(AdventureService.LocalPeer, 0);
        double[] sloppy = [a!.Begun + 0.5, a.Begun + 1.0, a.Begun + 1.2, a.Begun + 1.2, a.Begun + 1.3, a.Begun + 1.4];
        Expect(svc.ServeAttempt(AdventureService.LocalPeer, a.Id, sloppy, 40) == null, "presses that are out of order or too fast are refused");
        Expect(Inst.Played.GetValueOrDefault("stein:5") == 2, "none of them used up a put");
        var far = svc.ServeBegin(AdventureService.LocalPeer, 7);
        Expect(far == null, "there is no stone of class 7");

        // ---- a bot takes the same path and earns nothing ---------------------------------------------------------
        int granted = FestivalService.Instance!.GrantedBatzen;
        var rng = new Random(5);
        double skilled = 0, clumsy = 0;
        const int n = 12;
        for (int i = 0; i < n; i++)
        {
            skilled += svc.ServeBot(-5, 0.95, rng)?.Metres ?? 0;
            clumsy += svc.ServeBot(-6, 0.15, rng)?.Metres ?? 0;
        }
        Expect(skilled / n > clumsy / n + 0.4 && skilled / n > 2.5, $"a skilled bot ({skilled / n:F2} m) beats a clumsy one ({clumsy / n:F2} m)");
        Expect(FestivalService.Instance.GrantedBatzen == granted && Inst.Played.GetValueOrDefault("stein:5") == 2, "bots earn no Batzen and use no put of a player");

        // ---- the third put, then the day is done ------------------------------------------------------------------
        Expect(me0.TryInteract() && await Until(() => game.State == SteinGame.Phase.Ready, 5), "E for the third put");
        before = landings.Count;
        await PerfectPut(this, game);
        Expect(await Until(() => landings.Count > before && game.State == SteinGame.Phase.Done, 8), "the third put landed");
        Expect(Inst.Played.GetValueOrDefault("stein:5") == 3, "three puts today");
        await Until(() => game.State == SteinGame.Phase.Idle, 10);
        Expect(SteinGame.InteractionFor(me0)?.Text.Contains("rests until tomorrow") == true, $"the prompt says it rests ({SteinGame.InteractionFor(me0)?.Text})");
        Expect(me0.TryInteract() && game.State == SteinGame.Phase.Idle, "E opens nothing a fourth time");
        Expect(svc.ServeBegin(AdventureService.LocalPeer, 0) == null, "and the server agrees");
        Expect(Inst.Best.GetValueOrDefault("stein") == Math.Max(first.Points, landings[^1].Points), $"the best put is kept ({Inst.Best.GetValueOrDefault("stein")})");

        await Finish(0);
    }

    // ---- the hands --------------------------------------------------------------------------------------------

    /// <summary>The lift let go on the meter's peak and four presses on the drum, through the real input action.</summary>
    internal static async Task PerfectPut(Node node, SteinGame game)
    {
        var a = game.Attempt!;
        Input.ActionPress(PlayerInput.UseItem);
        double down = ClockSync.ServerNow;
        double peak = SteinRules.NextPeak(a.Seed, a.Class, 0.3);
        await WaitUntil(node, down + peak);
        Input.ActionRelease(PlayerInput.UseItem);
        double up = ClockSync.ServerNow;
        for (int n = 1; n <= 4; n++)
        {
            await WaitUntil(node, SteinRules.BeatAt(up, n));
            Input.ActionPress(PlayerInput.UseItem);
            await Wait(node, 0.05);
            Input.ActionRelease(PlayerInput.UseItem);
        }
    }

    /// <summary>The lift let go at the meter's trough.</summary>
    private async Task Foul(SteinGame game)
    {
        var a = game.Attempt!;
        Input.ActionPress(PlayerInput.UseItem);
        double down = ClockSync.ServerNow;
        double trough = SteinRules.NextPeak(a.Seed, a.Class, 0.5 + SteinRules.MeterPeriod(a.Class) / 2) - SteinRules.MeterPeriod(a.Class) / 2;
        await WaitUntil(down + trough);
        Input.ActionRelease(PlayerInput.UseItem);
        double up = ClockSync.ServerNow;
        for (int n = 1; n <= 4; n++)
        {
            await WaitUntil(SteinRules.BeatAt(up, n));
            Input.ActionPress(PlayerInput.UseItem);
            await Seconds(0.05);
            Input.ActionRelease(PlayerInput.UseItem);
        }
    }

    private Task WaitUntil(double serverTime) => WaitUntil(this, serverTime);

    internal static async Task WaitUntil(Node node, double serverTime)
    {
        while (ClockSync.ServerNow < serverTime) await node.ToSignal(node.GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static async Task Wait(Node node, double s) => await node.ToSignal(node.GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private async Task Jump(double e, double n)
    {
        var me = Me!;
        var g = me.Global;
        me.GlobalPosition += new Vector3((float)(e - g.E), 0.2f, (float)-(n - g.N));
        me.Velocity = Vector3.Zero;
        await Seconds(0.3);
    }
}
