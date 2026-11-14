using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Fixture;

namespace UnitSport.World.Ropeways;

/// <summary>
/// <c>--liftnet A|B</c> with <c>--connect</c> (driven by <c>tools/liftnetcheck.sh</c>, #915): a lift
/// rider is drawn on its chair on another peer.
/// <list type="bullet">
/// <item>A stands at a chairlift's bottom station (<c>--at</c>; on a fixture course with no <c>--at</c>, the fun
/// course's chairlift, <see cref="FunCourse.ChairX"/>: #1609), boards it and rides for a while.</item>
/// <item>B, beside the station, watches A's copy: it must carry A's lift and carrier, stand where that
/// carrier puts a rider by the shared clock (within 0.5 m, not the interpolated stream's lag), and be
/// drawn sitting.</item>
/// </list>
/// </summary>
public partial class LiftNetProbe : Core.ChatProbe
{
    public static string? Role => RoleArg("--liftnet");

    /// <summary>For a failure's line: where A stands and the nearest bottom station loaded round it.</summary>
    private string Nearest()
    {
        if (Me is not { Terrain.Origin: { } origin } me || RopewayService.Instance is not { } lifts) return "no player or no lifts";
        var (e, n) = origin.ToLv95(me.GlobalPosition);
        var near = lifts.Near(e, n);
        double Dist(RopewayLine l) { var (le, ln) = l.Point(0); return System.Math.Sqrt((le - e) * (le - e) + (ln - n) * (ln - n)); }
        var best = near.OrderBy(Dist).FirstOrDefault();
        string where = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"at {e:F0}/{n:F0}, on the floor {me.IsOnFloor()}, {near.Count} lines near");
        foreach (var l in near)
            GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[liftnet {_role}]   line {l.Id} {l.Kind} '{l.Name}': {Lv95(l.Point(0))} -> {Lv95(l.Point(^1))}"));
        return best == null ? where : string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{where}, nearest bottom station {Lv95(best.Point(0))} ({Dist(best):F1} m, reach {RopewayService.BoardReach})");
    }

    private static string Lv95((double, double) p)
    {
        var (e, n) = p;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{e:F0}/{n:F0}");
    }

    public LiftNetProbe(ItemController items) : base(items, "liftnet", "LN") { }
    public LiftNetProbe() : this(null!) { }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (!await PlaceOnCourse()) { await Finish(0); return; }
        if (_role == "A") await RunA(); else await RunB();
        await Finish(2.0);
    }

    /// <summary>
    /// On a fixture course with no <c>--at</c> (#1609: the check needs no real tiles): A on the fun course's chairlift
    /// bottom station, B 6 m east of it, once the ground there has loaded. Placed by course metres
    /// (<see cref="World.Fun.FunWorld.At"/>), so the same in the flat and the earth frame.
    /// </summary>
    private async Task<bool> PlaceOnCourse()
    {
        if (Core.SpawnPoint.ParseLv95("--at") != null || CoursePlace.Active == null) return true;
        var at = World.Fun.FunWorld.At(FunCourse.ChairX + (_role == "A" ? 0 : 6), FunCourse.ChairY0 - (_role == "A" ? 0 : 2), 0);
        Vector3 spot = default;
        float ground = 0;
        bool loaded = await Until(() => Me is { Origin: { } o, Terrain: { } t } && t.TryGetHeight(spot = o.ToWorld(at), out ground), 60);
        Expect(loaded, "the ground at the course's chairlift loaded");
        if (!loaded) return false;
        Me!.DebugLaunch(spot with { Y = ground + 0.5f }, Vector3.Zero);
        bool standing = await Until(() => Me is { } me && me.IsOnFloor(), 20);
        Expect(standing, $"{_role} stands by the course's chairlift ({Nearest()})");
        return standing;
    }

    private async Task RunA()
    {
        bool boarded = await Until(() => Me is { } me && me.TryLift(), 90);
        Expect(boarded, $"A boarded the lift at its station ({Nearest()})");
        if (!boarded) return;
        GD.Print($"{Log} on {Me?.Lift?.Kind} {Me?.LiftLine}, carrier {Me?.LiftCarrier}, leaving in {Me?.LiftWait:F1} s");
        Say("boarded");
        await Seconds(35);
        Say("done");
    }

    private async Task RunB()
    {
        if (!await Heard("A", "boarded", 150)) { Expect(false, "A never boarded"); return; }
        int samples = 0, onChair = 0, seated = 0;
        double worst = 0;
        for (int i = 0; i < 60 && !_heard.Contains("A: done"); i++)
        {
            await Seconds(0.5);
            var a = GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => !p.IsMultiplayerAuthority() && p.LiftLine != 0);
            if (a?.LiftExpected() is not { } want || !want.Aboard) continue;
            samples++;
            double d = a.Position.DistanceTo(want.At);
            worst = System.Math.Max(worst, d);
            if (d < 0.5) onChair++;
            if (a.LiftSeated) seated++;
        }
        GD.Print($"{Log} {samples} samples aboard, {onChair} on the chair, {seated} seated, worst {worst:F2} m");
        Expect(samples >= 10, $"B saw A riding ({samples} samples)");
        Expect(onChair == samples, $"A's copy on its chair every time (worst {worst:F2} m)");
        Expect(seated == samples, "A's copy drawn sitting");
    }
}
