using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.World;
using Course = UnitSport.Terrain.Fixture.Lake;

namespace UnitSport.Items.Fishing;

/// <summary>
/// <c>--fishcheck --chunks fixture:lake</c> (#493): the rod on the lake course, offline, headless, through
/// the real paths (<see cref="ItemController.UseSlot"/> for Use, <see cref="ItemController.ForceUse"/> and
/// <see cref="ItemController.ForceAim"/> for the held buttons).
/// <list type="bullet">
/// <item>A cast landward lands on dry ground and comes back in.</item>
/// <item>A full cast from the beach lands on the lake, 20-28 m out: still water, a float on the surface.</item>
/// <item>A bite comes; Use strikes; the fish is played (reel while calm, let go when it runs) and landed:
/// kept into the pack (dough used up) or released by the rules, recorded in the catch book either way.</item>
/// <item>Aim winds a waiting line in; putting the rod away does too.</item>
/// <item>A cast from the river's bank lands in flowing water: a river.</item>
/// </list>
/// Prints <c>[fishcheck] RESULT: ok</c> or <c>RESULT: FAILED (n)</c>.
/// </summary>
public partial class FishProbe : Node
{
    public static bool Requested => CmdArgs.Has("--fishcheck");

    /// <summary><c>--fishcheck shots</c>: windowed, third person, pictures into <c>test_output/fish/</c>.</summary>
    private static bool ShotsMode => CmdArgs.Value("--fishcheck") == "shots";

    private readonly Func<FootPlayer?> _local;
    private int _failures;
    private FootPlayer _me = null!;
    private ItemController _items = null!;
    private FishingRod Rod => _items.Rod;
    private bool _play;

    public FishProbe(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "FishProbe";
        FishJournal.Persist = false;
        FishJournal.Reset();
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print($"[fishcheck] {what}");

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failures++;
            GD.PrintErr($"[fishcheck] FAIL {what}");
        }
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(Func<bool> done, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.05)
        {
            if (done()) return true;
            await Wait(0.05);
        }
        return done();
    }

    private static (double E, double N) Start => SpawnPoint.ParseTarget();

    /// <summary>A course point (metres from the start, X east, Y north) in world space.</summary>
    private static Vector3 At(double x, double y)
    {
        var (e, n) = Start;
        WaterField.TryWorld(e + x, n + y, out var w);
        return w with { Y = (float)Course.Ground(x, y) };
    }

    private static float YawOf(Vector3 d) => Mathf.Atan2(-d.X, -d.Z);

    /// <summary>Plays the fish like a careful angler: reels while it is calm, lets go before and during a run.</summary>
    public override void _Process(double delta)
    {
        if (!_play || _items == null) return;
        _items.ForceUse = Rod.Fight is { } f && !f.SurgeComing && !f.Surging && f.Tension < 0.8f;
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            // a free-camera start: on foot, as --swimcheck does
            if (me == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null || ItemController.Instance is not { } items) { Finish("no local player or items"); return; }
        _me = me;
        _items = items;
        if (!await Until(() => WaterField.TryGetStill(At(Course.ShoreX + 400, 0), out _, out _), 90)) { Finish("the lake's water layer never loaded"); return; }

        if (ShotsMode) _me.DebugThirdPerson(true);
        var inv = _items.Inventory;
        inv.Put(0, new ItemStack(ItemId.FishingRod, 1));
        inv.Put(1, new ItemStack(ItemId.DoughBait, 20));
        inv.Select(0);

        try
        {
            await DryCast();
            await LakeCatch();
            await WindIn();
            await RiverCast();
        }
        catch (Exception e) { _failures++; GD.PrintErr($"[fishcheck] FAIL exception {e}"); }
        _play = false;
        _items.ForceUse = _items.ForceAim = false;
        Finish(null);
    }

    private void Finish(string? fatal)
    {
        if (fatal != null) { _failures++; GD.PrintErr($"[fishcheck] FAIL {fatal}"); }
        Log(_failures == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task Shot(string name)
    {
        if (!ShotsMode) return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var dir = ProjectSettings.GlobalizePath("res://test_output/fish");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, name + ".png");
        Log($"shot {path}: {GetViewport().GetTexture().GetImage().SavePng(path)}");
        var tip = _me.GetNodeOrNull<HeldItemVisual>("HeldItem")?.ItemPoint(FishingVisuals.RodTip);
        var bob = ItemEvents.Instance is { } n ? n.GetNodeOrNull<Node3D>("FishingLines")?.GetChildren().OfType<MeshInstance3D>().Skip(1).FirstOrDefault()?.GlobalPosition : null;
        Log(FormattableString.Invariant($"  player {_me.GlobalPosition}, tip {tip}, float {(ItemEvents.Instance is { } m ? FishingVisuals.Of(m).LocalFloat : null)}, bobber mesh {bob}"));
    }

    private async Task StandAt(double x, double y, Vector3 facing)
    {
        _me.GlobalPosition = At(x, y) + Vector3.Up * 0.3f;
        _me.Velocity = Vector3.Zero;
        _me.LookYaw = YawOf(facing);
        _me.LookPitch = 0;
        await Until(() => _me.IsOnFloor(), 5);
        await Wait(0.2);
    }

    /// <summary>Holds Use for <paramref name="hold"/> seconds and lets go: a cast of that much power.</summary>
    private async Task Cast(double hold)
    {
        _items.UseSlot(_me, 0);
        _items.ForceUse = true;
        await Wait(hold);
        _items.ForceUse = false;
        await Wait(0.1);
    }

    private static Vector3 East => (At(100, 0) - At(0, 0)) with { Y = 0 };
    private static Vector3 North => (At(0, 100) - At(0, 0)) with { Y = 0 };

    private async Task DryCast()
    {
        Log("-- a cast landward");
        await StandAt(Course.ShoreX - 6, 0, -East);
        string? lost = null;
        void OnLost(string why) => lost = why;
        Rod.Lost += OnLost;
        await Cast(0.3);
        Rod.Lost -= OnLost;
        Expect(lost == "dry" && Rod.State == FishingRod.Phase.Idle, $"lands on dry ground and comes back ({lost ?? "nothing"}, {Rod.State})");
    }

    private async Task LakeCatch()
    {
        Log("-- a full cast onto the lake, a bite, the fight");
        await StandAt(Course.ShoreX - 3, 0, East);
        await Cast(1.2);
        Expect(Rod.State == FishingRod.Phase.Waiting, $"the line is out ({Rod.State})");
        Expect(Rod.Spot.Kind is WaterKind.LargeLake or WaterKind.SmallLake, $"still water ({Rod.Spot.Kind}, {Rod.Spot.Basin}, {Rod.Spot.Altitude:F0} m)");
        var fl = ItemEvents.Instance is { } n ? FishingVisuals.Of(n).LocalFloat : null;
        float out_ = fl is { } f ? MathX.FlatLength(f - _me.GlobalPosition) : -1;
        Expect(out_ is > 20 and < 29, $"the float lies {out_:F1} m out");
        Expect(fl is { } f2 && WaterField.TryGetStill(f2, out float still, out _) && Mathf.Abs(f2.Y - still) < 0.2f, "the float sits on the surface");
        await Shot("1_waiting");

        int dough = _items.Inventory.CountPlain(ItemId.DoughBait);
        Catch? landed = null;
        void OnLanded(Catch c) => landed = c;
        Rod.Landed += OnLanded;
        // a few tries: a bite missed (it should not be) or a line snapped casts again
        for (int attempt = 0; attempt < 4 && landed == null; attempt++)
        {
            if (Rod.State == FishingRod.Phase.Idle) await Cast(1.2);
            if (!await Until(() => Rod.State == FishingRod.Phase.Bite, 300)) { Expect(false, "a bite within 300 s"); break; }
            Log($"bite after attempt {attempt}");
            _items.UseSlot(_me, 0);
            Expect(Rod.State == FishingRod.Phase.Fighting, $"Use on a bite hooks it ({Rod.Hooked?.Species.Name}, {Rod.Hooked?.Kg:F2} kg)");
            _play = true;
            await Wait(1.5);
            await Shot("2_fight");
            await Until(() => Rod.State != FishingRod.Phase.Fighting, 400);
            await Wait(0.3);
            await Shot("3_landed");
            _play = false;
            _items.ForceUse = false;
        }
        Rod.Landed -= OnLanded;
        if (landed is not { } c) { Expect(false, "a fish on the bank"); return; }
        Log($"landed {c.Species.Name} {c.Cm:F0} cm {c.Kg:F2} kg: {c.Verdict}");
        Expect(FishJournal.Of(c.Species)?.Landed >= 1, "the catch book has it");
        if (c.Kept)
            Expect(_items.Inventory.CountPlain(c.Species.Item) == 1, $"a kept {c.Species.Name} is in the pack");
        else Expect(c.Species.Item == ItemId.None || _items.Inventory.CountPlain(c.Species.Item) == 0, $"a released {c.Species.Name} is not");
        Expect(_items.Inventory.CountPlain(ItemId.DoughBait) < dough, "the dough went with the fish");
        Expect(Rod.State == FishingRod.Phase.Idle, "the line is in");
    }

    private async Task WindIn()
    {
        Log("-- Aim winds a line in; putting the rod away too");
        await StandAt(Course.ShoreX - 3, 0, East);
        await Cast(0.6);
        Expect(Rod.State == FishingRod.Phase.Waiting, "out");
        _items.ForceAim = true;
        await Wait(0.2);
        _items.ForceAim = false;
        Expect(Rod.State == FishingRod.Phase.Idle, $"Aim wound it in ({Rod.State})");
        await Cast(0.6);
        _items.Inventory.Select(2);
        await Wait(0.3);
        Expect(Rod.State == FishingRod.Phase.Idle, "the rod put away took the line in");
        Expect(ItemEvents.Instance is { } n && FishingVisuals.Of(n).LocalFloat == null, "the float is gone");
        _items.Inventory.Select(0);
        await Wait(0.2);
    }

    private async Task RiverCast()
    {
        Log("-- a cast across the river");
        await StandAt(-300, Course.RiverY - 11, North);
        await Cast(0.05);
        Expect(Rod.State == FishingRod.Phase.Waiting, $"in the water ({Rod.State})");
        Expect(Rod.Spot.Kind is WaterKind.River or WaterKind.MountainStream, $"flowing water ({Rod.Spot.Kind})");
        _items.ForceAim = true;
        await Wait(0.2);
        _items.ForceAim = false;
    }
}
