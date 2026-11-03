using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// <c>--fireworkscheck --festival test --date 2027-03-14 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c>
/// (quick tier, #1269 part 2): the check-only festival is on its last day (day 5 of 5) and ends on fireworks. At noon no show is on the books;
/// when the sky clock (overridden here) crosses 21:30 the server fixes a start and a seed, once for that evening; the client plans the
/// same show and joins it where it is; the shells fire in order with their climbs, flashes and sounds delayed by distance; the finale's
/// forty shells never need more than the pools hold; a late joiner skips the shells already gone; the player standing at the site counts as
/// having watched; and when the festival is over the node falls silent. Nothing is built while a show plays: the node's child count never
/// changes. Windowed it saves <c>test_output/fireworks_finale.png</c>. Progress lives in <c>test_output/fireworkscheck</c>.
/// </summary>
public partial class FireworksCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--fireworkscheck");

    private readonly Func<FootPlayer?> _me;

    public FireworksCheck(ItemController items, Func<FootPlayer?> me) : base(items, "fireworkscheck", "", "fireworks_") => _me = me;
    public FireworksCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && Fireworks.Instance != null && Me is { } me && me.IsOnFloor()
                && FestivalService.Instance.Running.Count > 0, 90))
        {
            Fail($"no festival or no fireworks node (service {FestivalService.Instance != null}, node {Fireworks.Instance != null}, body {Me?.Global})");
            return;
        }
        var svc = FestivalService.Instance!;
        var fw = Fireworks.Instance!;
        var run = svc.RunningOf(FestivalCatalog.TestId)!;
        Expect(run.Day == run.Length && run.Length == 5, $"the last day of the festival ({run.Day}/{run.Length})");
        Expect(FestivalShows.HasFireworks(FestivalCatalog.Get(FestivalCatalog.TestId)!.Show), "the festival ends on fireworks");

        // ---- noon: no show -------------------------------------------------------------------------
        double hour = 12;
        svc.Hour = () => hour;
        await Seconds(7);
        Expect(svc.RunningOf(FestivalCatalog.TestId)!.ShowStart == 0 && fw.ShowSeed == 0, "at noon no show is on the books");

        // ---- 21:30: the server fixes a start and a seed --------------------------------------------
        int children = fw.GetChildCount();
        double before = ClockSync.SimNow;
        hour = 22;
        Expect(await Until(() => svc.RunningOf(FestivalCatalog.TestId)!.ShowStart > 0, 15), "the sky crossing 21:30 starts a show");
        run = svc.RunningOf(FestivalCatalog.TestId)!;
        double lead = run.ShowStart - before;
        Expect(lead is > 8 and < 30, $"it starts a few seconds ahead ({lead:F1} s)");
        Expect(run.ShowSeed == FestivalShows.SeedOf(run.Instance, 1), $"its seed is the instance's first ({run.ShowSeed})");
        Expect(await Until(() => fw.ShowSeed == run.ShowSeed, 5), "the node on the client planned the same show");
        var show = FireworkShow.Plan(run.ShowSeed);
        GD.Print($"[fireworkscheck] {show.Shells.Length} shells, {show.Duration / 60:F0} minutes, heading {Mathf.RadToDeg(show.Heading):F0} deg");
        await Seconds(8);
        Expect(svc.RunningOf(FestivalCatalog.TestId)!.ShowStart == run.ShowStart, "the evening has one show, not one every five seconds");

        // ---- the first shells ----------------------------------------------------------------------
        fw.Seek(show.Shells[0].T - 0.3);
        Expect(await Until(() => fw.RocketsFired >= 1, 10), $"the first shell's climb starts ({fw.RocketsFired})");
        Expect(await Until(() => fw.BurstsFired >= 1, 10), $"the first shell bursts ({fw.BurstsFired}, {fw.ShellsSkipped} out of sight)");
        Expect(await Until(() => fw.SoundsPlayed >= 2, 10), $"its launch and its bang are heard, late by the distance ({fw.SoundsPlayed})");
        Expect(fw.Cursor >= 1, $"the cursor moved on ({fw.Cursor})");

        // ---- joining late ---------------------------------------------------------------------------
        int fired = fw.BurstsFired;
        fw.Seek(200);
        await Seconds(0.5);
        Expect(fw.BurstsFired - fired <= 3, $"a peer joining at 200 s does not replay the shells it missed ({fw.BurstsFired - fired})");
        Expect(fw.Cursor >= show.First(200 - 0.5), $"the cursor stands at 200 s, not at the start ({fw.Cursor})");

        // ---- the finale: forty shells in twenty seconds ---------------------------------------------
        fw.Seek(show.Duration - FireworkShow.FinaleSeconds - 0.3);
        fired = fw.BurstsFired;
        int rockets = fw.RocketsFired;
        if (DisplayServer.GetName() != "headless")
        {
            // 220 m behind the fan, looking at it: the shells burst 60-180 m out and 80-170 m up
            var ahead = new Vector3(Mathf.Sin(show.Heading), 0f, -Mathf.Cos(show.Heading));
            Me!.GlobalPosition -= ahead * 220f;
            Me.Velocity = Vector3.Zero;
            Me.LookYaw = -show.Heading;
            Me.LookPitch = 0.4f;
            await Seconds(9);
            GD.Print($"[fireworkscheck] shot {Shot("finale")}");
        }
        Expect(await Until(() => fw.Cursor >= show.Shells.Length || fw.ShowTime < 0, 90), $"the finale runs to the last shell ({fw.Cursor} of {show.Shells.Length})");
        int finale = fw.BurstsFired - fired;
        Expect(finale >= FireworkShow.FinaleShells, $"at least {FireworkShow.FinaleShells} bursts in the finale ({finale})");
        Expect(fw.RocketsFired - rockets >= FireworkShow.FinaleShells - 4, $"a climb for each ({fw.RocketsFired - rockets})");
        Expect(fw.PeakBursts <= Fireworks.Bursts, $"the pools were enough: at most {fw.PeakBursts} bursts alive at once");
        Expect(Fireworks.ParticleBudget <= 6000, $"the particle budget is {Fireworks.ParticleBudget}");
        Expect(fw.GetChildCount() == children, $"nothing was built while the show played ({children} -> {fw.GetChildCount()} children)");

        // ---- watched -------------------------------------------------------------------------------
        Expect(await Until(() => svc.WatchersOf(run.Instance) == 1, 60), "the player at the site has watched the show");

        // ---- the end ------------------------------------------------------------------------------
        Expect(await Until(() => fw.ShowSeed == 0, 30), "the node falls silent when the show is over");
        await Seconds(2);
        Expect(fw.ShowSeed == 0, "and does not replay the show it finished");

        // ---- the festival ends ----------------------------------------------------------------------
        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 3, 16));
        Expect(await Until(() => svc.Running.Count == 0, 15), "the festival is over");
        await Seconds(1);
        Expect(fw.ShowSeed == 0, "no show plays for a festival that is over");
        await Finish(0);
    }
}
