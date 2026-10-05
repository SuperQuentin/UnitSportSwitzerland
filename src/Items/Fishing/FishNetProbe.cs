using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.World;
using Course = UnitSport.Terrain.Fixture.Lake;

namespace UnitSport.Items.Fishing;

/// <summary>
/// <c>--fishnet A|B</c> with <c>--connect</c> (driven by <c>tools/fishcheck.sh</c>, #493): the rod over
/// loopback on the lake fixture. A stands on the beach with the rod, B a few metres behind.
/// <list type="bullet">
/// <item>B sees A holding the rod (the replicated held item).</item>
/// <item>A casts onto the lake: B gets the <see cref="ItemEventKind.FishCast"/> and draws A's float where
/// A's own float lies (LV95, within a metre).</item>
/// <item>A winds in with Aim: B's float for A goes (<see cref="ItemEventKind.FishEnd"/>).</item>
/// <item>A casts again and puts the rod away: B's float goes with it, no event needed.</item>
/// </list>
/// </summary>
public partial class FishNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--fishnet");

    public FishNetProbe(ItemController items) : base(items, "fishnet", "FI")
    {
        FishJournal.Persist = false;
    }

    public FishNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    /// <summary>A's peer id as B's events name it, from A's first cast.</summary>
    private long _aPeer = -1;
    private int _ends;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => ItemEvents.Instance != null)) { await Finish(0); return; }
        ItemEvents.Received += OnEvent;
        await Seconds(2.0);
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(1.0);
    }

    public override void _ExitTree() => ItemEvents.Received -= OnEvent;

    private void OnEvent(ItemEvent e)
    {
        if (e.Local) return;
        if (e.Kind == ItemEventKind.FishCast) _aPeer = e.Peer;
        if (e.Kind == ItemEventKind.FishEnd) _ends++;
    }

    private static Vector3 At(FootPlayer me, double x, double y)
    {
        var (e, n) = SpawnPoint.ParseTarget();
        var w = me.Origin!.ToWorld(e + x, n + y, 0);
        return w with { Y = (float)Course.Ground(x, y) };
    }

    private async Task StandAt(FootPlayer me, double x, double y)
    {
        await Until(() => !GetParent().GetChildren().OfType<SpawnPoint>().Any() && me.IsOnFloor(), 60);
        me.GlobalPosition = At(me, x, y) + Vector3.Up * 0.5f;
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        await Until(() => me.IsOnFloor(), 10);
        var east = (At(me, 100, 0) - At(me, 0, 0)) with { Y = 0 };
        me.LookYaw = Mathf.Atan2(-east.X, -east.Z);
        me.LookPitch = 0;
        await Seconds(1.0);
    }

    private FishingVisuals? Visuals => ItemEvents.Instance is { } n ? FishingVisuals.Of(n) : null;

    private async Task RunA(FootPlayer me)
    {
        await StandAt(me, Course.ShoreX - 3, 0);
        _items.Inventory.Put(0, new ItemStack(ItemId.FishingRod, 1));
        _items.Inventory.Put(1, new ItemStack(ItemId.DoughBait, 10));
        _items.Inventory.Select(0);
        Say("ready");
        if (!await Until(() => Said("B", "sees rod"), 60)) { Fail("B never saw the rod"); return; }

        await Cast(me, 1.2);
        Expect(_items.Rod.State == FishingRod.Phase.Waiting, $"A's line is out ({_items.Rod.State})");
        if (Visuals?.LocalFloat is { } f)
        {
            var g = me.Origin!.ToGlobal(f);
            Say(FormattableString.Invariant($"float {g.E:F2} {g.N:F2}"));
        }
        if (!await Until(() => Said("B", "float ok") || Said("B", "float off"), 30)) { Fail("B never answered about the float"); return; }

        _items.ForceAim = true;
        await Seconds(0.3);
        _items.ForceAim = false;
        Expect(_items.Rod.State == FishingRod.Phase.Idle, "Aim wound A's line in");
        Say("wound in");
        if (!await Until(() => Said("B", "gone 1"), 30)) { Fail("B kept A's float after the wind-in"); return; }

        await Cast(me, 0.8);
        Say("cast again");
        if (!await Until(() => Said("B", "float again"), 30)) { Fail("B never saw the second cast"); return; }
        _items.Inventory.Select(3);   // the rod put away
        Say("put away");
        if (!await Until(() => Said("B", "gone 2"), 30)) Fail("B kept A's float after the rod was put away");
    }

    private async Task Cast(FootPlayer me, double hold)
    {
        _items.UseSlot(me, 0);
        _items.ForceUse = true;
        await Seconds(hold);
        _items.ForceUse = false;
        await Seconds(0.3);
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Until(() => Said("A", "ready"), 150)) { Fail("A never got ready"); return; }
        await StandAt(me, Course.ShoreX - 8, 3);
        Expect(await Until(() => Body() is { HeldItemId: (int)ItemId.FishingRod }, 20), "B sees A holding the rod");
        Say("sees rod");

        if (!await Until(() => _heard.Any(l => l.Contains("FI A float ")), 40)) { Fail("A never cast"); return; }
        var p = _heard.First(l => l.Contains("FI A float ")).Split("float ")[1].Split(' ');
        double e = double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture);
        double n = double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
        bool drawn = await Until(() => _aPeer >= 0 && Visuals?.FloatOf(_aPeer) != null, 10);
        Expect(drawn, "B draws A's float");
        if (drawn && Visuals!.FloatOf(_aPeer) is { } f)
        {
            var g = me.Origin!.ToGlobal(f);
            double off = Math.Sqrt((g.E - e) * (g.E - e) + (g.N - n) * (g.N - n));
            Expect(off < 1.0, FormattableString.Invariant($"B's float for A lies where A's does ({off:F2} m off)"));
            Expect(WaterField.TryLevelAt(f, out float level) && Mathf.Abs(f.Y - level) < 0.3f, "on B's water surface");
            Say(off < 1.0 ? "float ok" : "float off");
        }
        else Say("float off");

        if (!await Until(() => Said("A", "wound in"), 30)) { Fail("A never wound in"); return; }
        Expect(await Until(() => Visuals?.FloatOf(_aPeer) == null && _ends >= 1, 10), "A's wind-in took B's float away");
        Say("gone 1");

        if (!await Until(() => Said("A", "cast again"), 30)) { Fail("A never cast again"); return; }
        Expect(await Until(() => Visuals?.FloatOf(_aPeer) != null, 10), "B draws A's second cast");
        Say("float again");
        if (!await Until(() => Said("A", "put away"), 30)) { Fail("A never put the rod away"); return; }
        Expect(await Until(() => Visuals?.FloatOf(_aPeer) == null, 10), "the rod put away took B's float of it away");
        Say("gone 2");
    }

    private FootPlayer? Body()
    {
        foreach (var n in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (n is FootPlayer p && p != Me && !p.Npc) return p;
        return null;
    }

    private bool Said(string role, string what) => _heard.Any(l => l.Contains($"FI {role} {what}"));
}
