using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.World.Ropeways;

/// <summary>
/// <c>--liftnet A|B</c> with <c>--connect</c> (driven by <c>tools/liftnetcheck.sh</c>, #915): a lift
/// rider is drawn on its chair on another peer.
/// <list type="bullet">
/// <item>A stands at a chairlift's bottom station (<c>--at</c>), boards it and rides for a while.</item>
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
        var best = near.OrderBy(l => (l.E[0] - e) * (l.E[0] - e) + (l.N[0] - n) * (l.N[0] - n)).FirstOrDefault();
        string where = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"at {e:F0}/{n:F0}, on the floor {me.IsOnFloor()}, {near.Count} lines near");
        foreach (var l in near)
            GD.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"[liftnet {_role}]   line {l.Id} {l.Kind} '{l.Name}': {l.E[0]:F0}/{l.N[0]:F0} -> {l.E[^1]:F0}/{l.N[^1]:F0}"));
        return best == null ? where : string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{where}, nearest bottom station {best.E[0]:F0}/{best.N[0]:F0} ({System.Math.Sqrt((best.E[0] - e) * (best.E[0] - e) + (best.N[0] - n) * (best.N[0] - n)):F1} m, reach {RopewayService.BoardReach})");
    }

    public LiftNetProbe(ItemController items) : base(items, "liftnet", "LN") { }
    public LiftNetProbe() : this(null!) { }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (_role == "A") await RunA(); else await RunB();
        await Finish(2.0);
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
