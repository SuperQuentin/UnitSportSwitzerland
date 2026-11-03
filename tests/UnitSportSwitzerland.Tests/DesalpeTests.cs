using UnitSport.Occasions.Festivals;
using UnitSport.Occasions.Festivals.Desalpe;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Désalpe (#1269 part 7): the route, the herd's steering, a whole drive, the score, the wire and the vote. Pure.</summary>
public class DesalpeTests
{
    private static readonly Driver[] NoDrivers = [];

    // ---- the festival ------------------------------------------------------------------------------

    [Fact]
    public void The_festival_is_in_the_catalogue_and_sound()
    {
        var def = FestivalCatalog.Get(FestivalCatalog.DesalpeId);
        Assert.NotNull(def);
        Assert.Empty(def!.Problems());
        Assert.Equal(16, def.Length);
        Assert.True(def.IsOpen(DesalpeRules.DressGame, 1));
        Assert.False(def.IsOpen(DesalpeRules.DriveGame, 2));
        Assert.True(def.IsOpen(DesalpeRules.DriveGame, 3));
        Assert.False(def.IsOpen(DesalpeRules.VoteGame, 3));
        Assert.True(def.IsOpen(DesalpeRules.VoteGame, 4));
    }

    [Fact]
    public void The_festival_runs_from_20_september_to_5_october_and_relocates_like_the_others()
    {
        var def = FestivalCatalog.Desalpe();
        var span = FestivalCalendar.SpanOf("09-20", "10-05", 2027)!.Value;
        Assert.Equal(new DateOnly(2027, 9, 20), span.Start);
        Assert.Equal(new DateOnly(2027, 10, 5), span.End);
        // the real place first; a region without Charmey or Urnäsch moves it to its largest town
        var charmey = FestivalSites.Resolve(def, (e, n) => Math.Abs(e - 2578750) < 5000 && Math.Abs(n - 1163750) < 5000, []);
        Assert.False(charmey!.Relocated);
        var elsewhere = FestivalSites.Resolve(def, (e, n) => e > 2600000 && e < 2700000, [new TownRef("Bern", 2601000, 1200000, 900), new TownRef("Thun", 2620000, 1180000, 500)]);
        Assert.True(elsewhere!.Relocated);
        Assert.Contains("Bern", elsewhere.Text);
    }

    // ---- dress -------------------------------------------------------------------------------------

    [Fact]
    public void A_dress_byte_round_trips_and_cycles()
    {
        for (int shape = 1; shape <= DesalpeRules.Shapes; shape++)
            for (int colour = 0; colour < DesalpeRules.Colours; colour++)
            {
                byte d = DesalpeRules.Dress(shape, colour);
                Assert.True(DesalpeRules.Valid(d));
                Assert.Equal(shape, DesalpeRules.ShapeOf(d));
                Assert.Equal(colour, DesalpeRules.ColourOf(d));
            }
        Assert.Equal(0, DesalpeRules.Dress(0, 3));
        Assert.False(DesalpeRules.Valid(19));
        byte x = 0;
        var seen = new List<int>();
        for (int i = 0; i < 4; i++) { x = DesalpeRules.NextShape(x); seen.Add(DesalpeRules.ShapeOf(x)); }
        Assert.Equal([1, 2, 3, 0], seen);
        byte c = DesalpeRules.Dress(2, 5);
        Assert.Equal(DesalpeRules.Dress(2, 0), DesalpeRules.NextColour(c));
        Assert.Equal(DesalpeRules.Dress(1, 0), DesalpeRules.NextColour(0));
    }

    [Fact]
    public void The_leitkuh_wears_the_big_bell_and_most_cows_the_small_one()
    {
        Assert.Equal(3, DesalpeRules.BellSize(0));
        var sizes = Enumerable.Range(1, DesalpeRules.Cows - 1).Select(DesalpeRules.BellSize).ToList();
        Assert.DoesNotContain(3, sizes);
        Assert.Contains(2, sizes);
        Assert.True(sizes.Count(s => s == 1) > sizes.Count(s => s == 2));
    }

    // ---- the route ---------------------------------------------------------------------------------

    [Fact]
    public void The_route_ends_at_the_fair_and_is_a_function_of_the_seed()
    {
        var a = DesalpeRoute.Plan(7, 700);
        var b = DesalpeRoute.Plan(7, 700);
        var c = DesalpeRoute.Plan(8, 700);
        Assert.Equal(a.Points, b.Points);
        Assert.NotEqual(a.Points, c.Points);
        Assert.Equal((0, 0), (Math.Round(a.End.X, 6), Math.Round(a.End.Y, 6)));
        Assert.Equal(700, a.Length, 3);
        var mid = a.At(a.Length / 2);
        Assert.True(a.Project(mid.X, mid.Y, out double d) is var s && Math.Abs(s - a.Length / 2) < 0.01 && d < 0.01);
        Assert.Equal(1.0, Math.Sqrt(a.Tangent(100).X * a.Tangent(100).X + a.Tangent(100).Y * a.Tangent(100).Y), 6);
    }

    // ---- the flock ---------------------------------------------------------------------------------

    private static (double X, double Y) Behind(HerdFlock f, double metres)
    {
        var (tx, ty) = f.Route.Tangent(f.LeaderS);
        return (f.X[0] - tx * metres, f.Y[0] - ty * metres);
    }

    [Fact]
    public void Nobody_driving_means_the_herd_stays_on_the_alp()
    {
        var f = new HerdFlock(DesalpeRoute.Plan(3, 400), DesalpeRules.Cows, 3);
        for (int i = 0; i < 600; i++) f.Step(0.1f, NoDrivers);
        Assert.True(f.LeaderS < 5);
        Assert.False(f.Pushed);
    }

    [Fact]
    public void A_driver_behind_the_herd_takes_it_down_the_route_together()
    {
        var f = new HerdFlock(DesalpeRoute.Plan(3, 300), DesalpeRules.Cows, 3);
        var drivers = new Driver[1];
        double worstSeparation = double.MaxValue;
        for (int i = 0; i < 6000 && !f.LeaderArrived; i++)
        {
            var (bx, by) = Behind(f, 10);
            drivers[0] = new Driver(bx, by, DesalpeRules.ZoneFoot);
            f.Step(0.1f, drivers);
            if (i % 20 == 0)
                for (int a = 0; a < f.Count; a++)
                    for (int b = a + 1; b < f.Count; b++)
                        if (f.State[a] != CowState.Lost && f.State[b] != CowState.Lost)
                            worstSeparation = Math.Min(worstSeparation, Math.Sqrt((f.X[a] - f.X[b]) * (f.X[a] - f.X[b]) + (f.Y[a] - f.Y[b]) * (f.Y[a] - f.Y[b])));
        }
        Assert.True(f.LeaderArrived, "the Leitkuh reaches the fair");
        Assert.True(worstSeparation > 0.4, $"cows never stacked ({worstSeparation:0.00} m)");
        // the route was followed: no cow of the herd ever far from it (checked at the end for those that came)
        for (int i = 0; i < f.Count; i++) if (f.State[i] == CowState.Herd) { f.Route.Project(f.X[i], f.Y[i], out double off); Assert.True(off < 25, $"cow {i} {off:0} m off the route"); }
    }

    [Fact]
    public void The_point_of_balance_a_driver_behind_sends_a_cow_forward_and_one_ahead_sends_it_back()
    {
        var route = DesalpeRoute.Plan(5, 500);
        // forward: a cow walking along +heading with a driver 3 m behind its shoulder
        var f = new HerdFlock(route, 3, 5);
        f.Step(0.1f, NoDrivers);
        int cow = 1;
        f.X[cow] = 1000; f.Y[cow] = 1000; f.Vx[cow] = 1; f.Vy[cow] = 0;
        f.X[0] = 1000; f.Y[0] = 1000;
        double x0 = f.X[cow];
        f.Step(0.1f, [new Driver(f.X[cow] - 3, f.Y[cow], 5f)]);
        Assert.True(f.X[cow] > x0, "a driver behind pushes the cow on");
        f = new HerdFlock(route, 3, 5);
        cow = 1;
        f.X[cow] = 1000; f.Y[cow] = 1000; f.Vx[cow] = 1; f.Vy[cow] = 0;
        f.X[0] = 1000; f.Y[0] = 1000;
        x0 = f.X[cow];
        double vx0 = f.Vx[cow];
        f.Step(0.1f, [new Driver(f.X[cow] + 3, f.Y[cow], 5f)]);
        Assert.True(f.Vx[cow] < vx0, "a driver ahead of the shoulder slows and turns the cow back");
    }

    [Fact]
    public void A_cow_with_little_drive_strays_at_a_junction_and_a_flanking_driver_brings_it_back()
    {
        var f = new HerdFlock(DesalpeRoute.Plan(11, 500), DesalpeRules.Cows, 11);
        var drivers = new Driver[1];
        int strayed = 0;
        for (int i = 0; i < 20000 && !f.LeaderArrived && strayed == 0; i++)
        {
            var (bx, by) = Behind(f, 10);
            drivers[0] = new Driver(bx, by, DesalpeRules.ZoneFoot);
            f.Step(0.1f, drivers);
            strayed = Enumerable.Range(0, f.Count).Count(c => f.State[c] == CowState.Stray);
        }
        Assert.True(strayed > 0, "a cow wandered off at a junction");
        int stray = Enumerable.Range(0, f.Count).First(c => f.State[c] == CowState.Stray);
        // let it settle in the field, then drive it back from the far side
        for (int i = 0; i < 400; i++) f.Step(0.1f, NoDrivers);
        Assert.Equal(CowState.Stray, f.State[stray]);
        Assert.True(f.Distance(stray, f.X[0], f.Y[0]) < DesalpeFlockLimit, "still within reach of the herd");
        f.Route.Project(f.X[stray], f.Y[stray], out double off);
        Assert.True(off > HerdFlock.RejoinM);
        bool back = false;
        for (int i = 0; i < 1500 && !back; i++)
        {
            f.Route.Project(f.X[stray], f.Y[stray], out off);
            // stand on the field side of the cow, on the line from the route through it, just inside its zone
            var s = f.Route.Project(f.X[stray], f.Y[stray], out _);
            var (rx, ry) = f.Route.At(s);
            double dx = f.X[stray] - rx, dy = f.Y[stray] - ry, dl = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.1);
            f.Step(0.1f, [new Driver(f.X[stray] + dx / dl * 3.0, f.Y[stray] + dy / dl * 3.0, DesalpeRules.ZoneFoot)]);
            back = f.State[stray] == CowState.Herd;
        }
        Assert.True(back, "driven back to the road, it is part of the herd again");
    }

    private const double DesalpeFlockLimit = HerdFlock.LostM;

    [Fact]
    public void A_cow_150_metres_from_the_leitkuh_is_lost()
    {
        var f = new HerdFlock(DesalpeRoute.Plan(2, 400), 4, 2);
        f.X[2] += 300;
        f.Step(0.1f, NoDrivers);
        Assert.Equal(CowState.Lost, f.State[2]);
        Assert.Equal(CowState.Herd, f.State[1]);
    }

    // ---- a whole drive -----------------------------------------------------------------------------

    private static DesalpeRun Drive(int seed, double length, bool followFlank = true)
    {
        var run = new DesalpeRun(seed, 1, length);
        run.Start();
        var drivers = new Driver[2];
        for (int i = 0; i < 60000 && run.Phase == DesalpePhase.Driving; i++)
        {
            var f = run.Flock;
            var (bx, by) = Behind(f, 9);
            var (tx, ty) = f.Route.Tangent(f.LeaderS);
            drivers[0] = new Driver(bx, by, DesalpeRules.ZoneFoot);
            // the second one goes after a stray from the field side, else holds the left flank
            int stray = Enumerable.Range(0, f.Count).Where(c => f.State[c] == CowState.Stray).DefaultIfEmpty(-1).First();
            if (stray >= 0 && followFlank)
            {
                f.Route.Project(f.X[stray], f.Y[stray], out _);
                var s = f.Route.Project(f.X[stray], f.Y[stray], out _);
                var (rx, ry) = f.Route.At(s);
                double dx = f.X[stray] - rx, dy = f.Y[stray] - ry, dl = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.1);
                drivers[1] = new Driver(f.X[stray] + dx / dl * 3, f.Y[stray] + dy / dl * 3, DesalpeRules.ZoneFoot);
            }
            else drivers[1] = new Driver(f.X[0] - tx * 4 + ty * 12, f.Y[0] - ty * 4 - tx * 12, DesalpeRules.ZoneFoot);
            run.Tick(0.1f, drivers);
        }
        return run;
    }

    [Fact]
    public void A_driven_herd_arrives_and_scores()
    {
        var run = Drive(21, 300);
        Assert.Equal(DesalpePhase.Arrived, run.Phase);
        var r = run.Result!;
        Assert.True(r.Arrived >= r.Total - 5, $"most cows arrived ({r.Arrived}/{r.Total}) states {string.Join(",", Enumerable.Range(0, 14).Select(i => run.Flock.State[i] + ":" + Math.Round(run.Flock.Distance(i, run.Route.End.X, run.Route.End.Y))))} leader {run.Flock.LeaderS:0}/{run.Route.Length:0} t {run.Elapsed:0}");
        Assert.True(r.Score >= r.Arrived * 50);
        Assert.True(r.Seconds > 100 && r.Seconds < run.Par * 3);
    }

    [Fact]
    public void A_herd_nobody_drives_ends_on_the_timeout_with_nothing()
    {
        var run = new DesalpeRun(4, 1, 200);
        run.Start();
        for (int i = 0; i < 60000 && run.Phase == DesalpePhase.Driving; i++) run.Tick(1f, NoDrivers);
        Assert.Equal(DesalpePhase.Arrived, run.Phase);
        Assert.Equal(0, run.Result!.Arrived);
        Assert.Equal(0, run.Result.Score);
    }

    [Fact]
    public void Dressing_is_for_the_alp_only()
    {
        var run = new DesalpeRun(4, 1, 200);
        Assert.True(run.TryDress(2, DesalpeRules.Dress(1, 2)));
        Assert.False(run.TryDress(99, 1));
        Assert.False(run.TryDress(2, 200));
        Assert.True(run.Start());
        Assert.False(run.TryDress(3, 1));
        Assert.Equal(1, DesalpeRules.Decorated(run.Dress));
        // a later run keeps the dress it is given
        var next = new DesalpeRun(4, 2, 200, run.Dress);
        Assert.Equal(DesalpeRules.Dress(1, 2), next.Dress[2]);
    }

    // ---- score -------------------------------------------------------------------------------------

    [Fact]
    public void The_score_counts_cows_dress_and_time()
    {
        double par = 600;
        Assert.Equal(0, DesalpeRules.Score(0, 0, 100, par));
        Assert.Equal(14 * 50, DesalpeRules.Score(14, 0, par, par));
        Assert.Equal(14 * 50 + 14 * 10, DesalpeRules.Score(14, 14, par * 2, par));
        Assert.Equal(14 * 50 + 100, DesalpeRules.Score(14, 0, par * 0.5, par));
        Assert.True(DesalpeRules.Score(14, 14, par * 0.5, par) > DesalpeRules.Score(14, 14, par * 0.8, par));
        // a perfect drive pays well under the daily cap of its game
        int batzen = DesalpeRules.BatzenFor(DesalpeRules.Score(14, 14, 0, par));
        Assert.InRange(batzen, 90, 120);
    }

    // ---- the wire ----------------------------------------------------------------------------------

    [Fact]
    public void A_snapshot_round_trips_and_malformed_ones_are_refused()
    {
        var f = new HerdFlock(DesalpeRoute.Plan(9, 400), DesalpeRules.Cows, 9);
        for (int i = 0; i < 50; i++) f.Step(0.1f, [new Driver(f.X[0] - 5, f.Y[0], 5f)]);
        var bytes = DesalpeWire.Pack(DesalpePhase.Driving, f);
        Assert.Equal(DesalpeWire.Header + DesalpeRules.Cows * DesalpeWire.PerCow, bytes.Length);
        Assert.True(DesalpeWire.Unpack(bytes, out var phase, out _, out var cows));
        Assert.Equal(DesalpePhase.Driving, phase);
        Assert.Equal(f.Count, cows.Length);
        for (int i = 0; i < cows.Length; i++)
        {
            Assert.Equal(f.X[i], cows[i].X, 2);
            Assert.Equal(f.Y[i], cows[i].Y, 2);
            Assert.Equal(f.State[i], cows[i].State);
            double dyaw = Math.Abs(Math.IEEERemainder(cows[i].Yaw - f.Yaw[i], Math.Tau));
            Assert.True(dyaw < 0.03, $"yaw {cows[i].Yaw} vs {f.Yaw[i]}");
        }
        Assert.False(DesalpeWire.Unpack(bytes[..^1], out _, out _, out _));
        Assert.False(DesalpeWire.Unpack([], out _, out _, out _));
        var bad = (byte[])bytes.Clone();
        bad[0] = 9;
        Assert.False(DesalpeWire.Unpack(bad, out _, out _, out _));
        bad = (byte[])bytes.Clone();
        BitConverter.GetBytes(float.NaN).CopyTo(bad, DesalpeWire.Header);
        Assert.False(DesalpeWire.Unpack(bad, out _, out _, out _));
    }

    // ---- the vote ----------------------------------------------------------------------------------

    private static DesalpeResult Result(int score) => new(score, 14, 10, 14, 0, 500, 12);

    [Fact]
    public void One_vote_each_never_for_ones_own_herd()
    {
        var v = new DesalpeVote();
        var mine = v.Add("Lea", Result(800), new byte[14], ["lea"]);
        var theirs = v.Add("Tom", Result(700), new byte[14], ["tom"]);
        Assert.Equal("Not for your own herd.", v.TryVote("lea", mine.Id));
        Assert.Null(v.TryVote("lea", theirs.Id));
        Assert.Equal("You have voted already.", v.TryVote("lea", theirs.Id));
        Assert.Equal("That herd is not in the ring.", v.TryVote("max", 99));
        Assert.Equal("Nobody votes.", v.TryVote("", theirs.Id));
        Assert.Equal(1, theirs.Votes);
        Assert.True(v.HasVoted("lea"));
    }

    [Fact]
    public void The_top_three_win_ties_go_to_the_first_herd_and_a_small_ring_shares_the_prize()
    {
        var v = new DesalpeVote();
        var e = Enumerable.Range(0, 4).Select(i => v.Add($"herd{i}", Result(500), new byte[14], [$"d{i}"])).ToList();
        Assert.Equal(4, v.Winners().Count + 1);
        v.TryVote("a", e[3].Id); v.TryVote("b", e[3].Id); v.TryVote("c", e[2].Id); v.TryVote("d", e[1].Id);
        var s = v.Standings();
        Assert.Equal(e[3].Id, s[0].Id);
        Assert.Equal(e[1].Id, s[1].Id);   // one vote each for 1 and 2: the herd that came in first
        Assert.Equal(e[2].Id, s[2].Id);
        Assert.DoesNotContain(e[0], v.Winners());
        var small = new DesalpeVote();
        small.Add("x", Result(1), new byte[14], ["x"]);
        small.Add("y", Result(1), new byte[14], ["y"]);
        Assert.Equal(2, small.Winners().Count);
    }
}
