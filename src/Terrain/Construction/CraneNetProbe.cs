using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.XR;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// <c>--cranenet A|B</c> with <c>--connect</c> on the generated world at a building site (driven by
/// <c>tools/cranenetcheck.sh</c>, #618): a site's crane worked by one player, seen by the other.
/// <list type="bullet">
/// <item>A takes the nearest crane's radio remote at its mast's foot; B sees the crane held by A;</item>
/// <item>A slews it, runs the trolley and lowers the hook onto a pallet of the site's on the real
/// levers; B draws the crane where A has it;</item>
/// <item>A hooks the pallet on; B sees it taken by the server and hanging on the crane's hook;</item>
/// <item>A lifts it onto the top slab and sets it down; B sees a loose pallet with its load on the slab;</item>
/// <item>A lets go; B sees the crane held where A left it, then (the simulation sped up by the admin)
/// back on its schedule, the hold forgotten.</item>
/// </list>
/// </summary>
public partial class CraneNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--cranenet");

    public CraneNetProbe(ItemController items) : base(items, "cranenet", "CR") { }
    public CraneNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150))
        {
            await Finish(0);
            return;
        }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>The words after "CR &lt;role&gt; &lt;what&gt;" in the last such line heard, or null.</summary>
    private string[]? Words(string role, string what)
    {
        string head = $"CR {role} {what}";
        for (int i = _heard.Count - 1; i >= 0; i--)
        {
            int at = _heard[i].IndexOf(head, StringComparison.Ordinal);
            if (at >= 0) return _heard[i][(at + head.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        return null;
    }

    /// <summary>The crane nearest a point, with a site pallet in its jib's reach.</summary>
    private static (SiteCranes.Live Crane, PalletNode Pallet)? CraneWithPallet(Vector3 from)
    {
        foreach (var crane in SiteCranes.All.Values.OrderBy(c => c.Foot.DistanceSquaredTo(from)))
        {
            if (crane.Foot.DistanceTo(from) > 300f) break;
            var foot = crane.Foot;
            var pallet = PalletNode.All.Values
                .Where(p => !p.Taken && p.IsInsideTree() && Pallets.TryParse(p.Id, out var r) && r.Source == PalletSource.Site)
                .Where(p =>
                {
                    float d = new Vector2(p.GlobalPosition.X - foot.X, p.GlobalPosition.Z - foot.Z).Length();
                    return d > CraneMotion.MinTrolley + 1f && d < crane.Job.JibLength - 2f;
                })
                .OrderBy(p => p.GlobalPosition.DistanceSquaredTo(foot)).FirstOrDefault();
            if (pallet != null && CraneDriver.DropOnSlab(crane) != null) return (crane, pallet);
        }
        return null;
    }

    private async Task Tap(string action)
    {
        XrPad.Press(action, true);
        await Seconds(0.1);
        XrPad.Press(action, false);
        await Seconds(0.2);
    }

    private async Task RunA(FootPlayer me)
    {
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        // the site's cranes are drawn with its buildings, its pallets with its machines
        if (!await Until(() => CraneWithPallet(me.GlobalPosition) != null, 90)) { Fail("no crane with a site pallet in its reach near the spawn"); return; }
        var (crane, pallet) = CraneWithPallet(me.GlobalPosition)!.Value;
        var (at, yaw) = crane.AtRemote;
        me.PlaceAt(at + Vector3.Up * 0.3f, yaw);
        await Seconds(2);
        Expect(me.TryInteract() && await Until(() => me.CraneWorking, 10), $"A takes the remote of {crane.Key} ({crane.Data.Spot.Kind}, jib {crane.Job.JibLength:F0} m)");
        if (!me.CraneWorking) return;
        Say($"working {crane.Key}");
        if (!await Heard("B", "seen working", 60)) { Fail("B never saw the crane held"); return; }

        // ---- onto the pallet, on the levers ----------------------------------------------------------
        float off = await CraneDriver.SteerTo(this, crane, pallet.GlobalPosition);
        Expect(off < 0.6f && me.HookAction().Take == pallet.Id, $"the hook is down on {pallet.Id} ({off:F2} m off, slewed to {crane.Last.Yaw:F3})");
        Say($"pose {F(crane.Last.Yaw)} {F(crane.Last.Trolley)} {F(crane.Last.HookY)}");
        if (!await Heard("B", "pose seen", 30)) { Fail("B never drew the crane where A has it"); return; }

        // ---- hooked on --------------------------------------------------------------------------------
        await Tap(PlayerInput.Couple);
        var hold = CraneService.Instance?.MyHold;
        bool hooked = await Until(() => hold?.Carrying > 0, 5);
        Expect(hooked, $"couple hooks {pallet.Id} on ({hold?.Carrying})");
        if (!hooked) return;
        Say($"hooked {pallet.Id} {hold!.Carrying}");
        if (!await Heard("B", "hooked seen", 30)) { Fail("B never saw the pallet on the hook"); return; }

        // ---- onto the top slab --------------------------------------------------------------------------
        var drop = crane.ToWorld(CraneDriver.DropOnSlab(crane)!.Value);
        off = await CraneDriver.SteerTo(this, crane, drop);
        var setAt = me.HookAction().SetAt;
        Expect(setAt is { } s && Mathf.Abs(s.Y - drop.Y) < 0.2f, $"over the top slab and lowered until it stands ({off:F2} m off, {setAt?.Y:F2} at {drop.Y:F2})");
        await Tap(PlayerInput.Couple);
        bool set = await Until(() => hold.Carrying == 0, 5);
        var loose = PalletService.Instance?.Loose.Values.OrderBy(p => p.Id).LastOrDefault();
        Expect(set && loose != null, $"couple sets it down ({(loose == null ? "-" : Pallets.LooseId(loose.Id))})");
        if (loose == null) return;
        Say($"set {Pallets.LooseId(loose.Id)} {loose.Load} {loose.Altitude.ToString("F2", CultureInfo.InvariantCulture)}");
        if (!await Heard("B", "set seen", 30)) { Fail("B never saw it on the slab"); return; }

        // ---- let go: held, then back on its schedule ----------------------------------------------------------
        Expect(me.TryInteract() && !me.CraneWorking, "E hangs the remote back up");
        Say($"released {F(crane.Last.Yaw)} {F(crane.Last.Trolley)} {F(crane.Last.HookY)}");
        if (!await Heard("B", "held seen", 30)) { Fail("B never saw it held"); return; }
        if (!await LogIn("test")) return;   // admin is needed only for /speed
        Chat?.Send("/speed 8");
        if (!await Heard("B", "resumed seen", 90)) Fail("B never saw it back on its schedule");
        Chat?.Send("/speed 1");
        await Seconds(1);
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 120)) { Fail("A never said hello"); return; }
        Say("ready");

        if (!await Heard("A", "working", 180)) { Fail("A never took a crane"); return; }
        string key = Words("A", "working")![0];
        if (!await Until(() => SiteCranes.All.ContainsKey(key), 60)) { Fail($"{key} is not drawn here"); return; }
        var crane = SiteCranes.All[key];
        CraneService.Hold? Hold() => CraneService.Instance?.Holds.GetValueOrDefault(key);
        bool held = await Until(() => Hold() is { Operator: not 0 } h && h.Operator != Multiplayer.GetUniqueId(), 10);
        Expect(held, $"B sees {key} held by A (peer {Hold()?.Operator})");
        Say("seen working");

        // ---- where A has it -------------------------------------------------------------------------------
        if (!await Heard("A", "pose", 240)) { Fail("A never got the hook down"); return; }
        var w = Words("A", "pose")!;
        float yaw = Float(w[0]), trolley = Float(w[1]), hook = Float(w[2]);
        bool there = await Until(() => Mathf.Abs(Mathf.AngleDifference(crane.Last.Yaw, yaw)) < 0.01f
                                       && Mathf.Abs(crane.Last.Trolley - trolley) < 0.05f && Mathf.Abs(crane.Last.HookY - hook) < 0.05f, 5);
        Expect(there, $"B draws A's crane where A has it: yaw {F(crane.Last.Yaw)}/{F(yaw)}, trolley {F(crane.Last.Trolley)}/{F(trolley)}, hook {F(crane.Last.HookY)}/{F(hook)}");
        Say("pose seen");

        // ---- on the hook ------------------------------------------------------------------------------
        if (!await Heard("A", "hooked", 120)) { Fail("A never hooked the pallet"); return; }
        w = Words("A", "hooked")!;
        string id = w[0];
        int carrying = int.Parse(w[1], CultureInfo.InvariantCulture);
        bool onHook = await Until(() => Hold()?.Carrying == carrying && crane.Pivot.GetNodeOrNull<Node3D>("HookPallet") != null, 10);
        bool gone = PalletNode.All.GetValueOrDefault(id) is not { } node || node.Taken || !node.IsInsideTree();
        Expect(onHook && gone && PalletService.Instance?.IsTaken(id) == true,
            $"B sees {id} taken by the server ({PalletService.Instance?.IsTaken(id)}) and hanging on the hook ({Hold()?.Carrying})");
        Say("hooked seen");

        // ---- on the slab ------------------------------------------------------------------------------------
        if (!await Heard("A", "set", 240)) { Fail("A never set it down"); return; }
        w = Words("A", "set")!;
        string looseId = w[0];
        byte load = byte.Parse(w[1], CultureInfo.InvariantCulture);
        double alt = double.Parse(w[2], CultureInfo.InvariantCulture);
        bool drawn = await Until(() => PalletNode.All.GetValueOrDefault(looseId) is { } p && p.Load == load, 10);
        var down = PalletNode.All.GetValueOrDefault(looseId);
        var slab = crane.ToWorld(crane.Job.Drops[0]).Y;
        Expect(drawn && down != null && Mathf.Abs(down.GlobalPosition.Y - slab) < 0.3f && Hold()?.Carrying == 0
               && crane.Pivot.GetNodeOrNull<Node3D>("HookPallet") == null,
            $"B sees {looseId} with its load {load} on the top slab ({down?.GlobalPosition.Y:F2} at slab {slab:F2}, alt {alt:F2}), the hook empty");
        Say("set seen");

        // ---- let go -----------------------------------------------------------------------------------------------
        if (!await Heard("A", "released", 120)) { Fail("A never let go"); return; }
        w = Words("A", "released")!;
        yaw = Float(w[0]);
        hook = Float(w[2]);
        bool left = await Until(() => Hold() is { Operator: 0 } h && !double.IsNaN(h.LeftAt), 10);
        await Seconds(1);
        Expect(left && Mathf.Abs(Mathf.AngleDifference(crane.Last.Yaw, yaw)) < 0.02f && Mathf.Abs(crane.Last.HookY - hook) < 0.1f,
            $"B sees it let go and holding where A left it (yaw {F(crane.Last.Yaw)}/{F(yaw)})");
        Say("held seen");
        bool resumed = await Until(() => Hold() == null, 80);
        double sim = SimClock.SimAt(Net.ClockSync.ServerNow);
        double hour = World.WorldClock.CurrentHour;
        long day = (long)Math.Floor((World.WorldClock.EnvNow + World.WorldClock.HourShift) / 86400.0);
        await Seconds(0.1);
        var scheduled = CraneMotion.Pose(crane.Job, SimClock.SimAt(Net.ClockSync.ServerNow), hour, day);
        Expect(resumed && Mathf.Abs(Mathf.AngleDifference(crane.Last.Yaw, scheduled.Yaw)) < 0.05f,
            $"and back on its schedule, the hold forgotten (yaw {F(crane.Last.Yaw)}, scheduled {F(scheduled.Yaw)}, sim {sim:F0})");
        Say("resumed seen");
    }
}
