using Godot;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Build;

/// <summary>
/// <c>--gadgetcheck [shots]</c> (offline, <c>--systems ui,physics</c>: the flat fixture). Through the real
/// item path: a trampoline (walk onto it: about 12 m up), a camo net and a hay hideout; a zipline's start
/// then a refused end on flat ground. Placed directly: a zipline 8 m down over 30 m (ridden from the top
/// post to the bottom one), a 5 m rope ladder (climbed to the top), a launch pad (about 80 m up, then a
/// wingsuit). The server-side rules (too short, uphill, ladder length) are checked as well. Everything
/// it placed is taken away at the end, so <c>user://placed/offline.json</c> ends as it was.
/// </summary>
public partial class GadgetProbe : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--gadgetcheck") >= 0;
    private static bool Shots => OS.GetCmdlineUserArgs().Contains("shots");

    private readonly ItemController _items;
    private int _failures;

    public GadgetProbe(ItemController items) => _items = items;
    public GadgetProbe() : this(null!) { }

    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public static void Stock(Inventory inv)
    {
        int slot = 0;
        foreach (var id in new[] { ItemId.Trampoline, ItemId.CamoNet, ItemId.HayHideout, ItemId.Zipline, ItemId.RopeLadder, ItemId.LaunchPad })
            inv.Put(slot++, new ItemStack(id, 1));
    }

    public override async void _Ready()
    {
        await Until(() => Structures.Instance?.GroundAt?.Invoke(GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero) != null
                          || GetViewport().GetCamera3D() != null, 60);
        await Seconds(2.0);
        if (Me == null) GetParent<Core.ClientWorld>().ToggleMode();
        if (!await Until(() => Me is { } m && m.IsOnFloor() && PlacedObjects.Instance != null, 120))
        {
            GD.Print("[gadgetcheck] RESULT: FAILED - no player on the ground");
            GetTree().Quit(1);
            return;
        }
        await Seconds(1.0);
        _items.GadgetTool.AlwaysShow = true;
        try { await Run(Me!, PlacedObjects.Instance!); }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        GD.Print(_failures == 0 ? "[gadgetcheck] RESULT: ok" : $"[gadgetcheck] RESULT: FAILED ({_failures})");
        await Seconds(0.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task Run(FootPlayer me, PlacedObjects placed)
    {
        var tool = _items.GadgetTool;
        var origin = placed.Origin;
        var before = placed.All.Keys.ToHashSet();
        var home = me.GlobalPosition;
        float ground = home.Y;

        // ---- the server's rules --------------------------------------------------------------
        PlacedObject Zip(Vector3 high, Vector3 low)
        {
            var (e, n) = origin.ToLv95(high);
            var (le, ln) = origin.ToLv95(low);
            return new PlacedObject(0, PlacedKind.Zipline, "x", le, ln, low.Y, Quaternion.Identity, Gadgets.ZipPayload(e, n, high.Y));
        }
        Expect(Gadgets.Check(Zip(home + new Vector3(0, 3, 0), home + new Vector3(5, 0, 0)), origin) is { } s1 && s1.Contains("short"), "a 6 m zipline is too short");
        Expect(Gadgets.Check(Zip(home, home + new Vector3(30, 4, 0)), origin) is { } s2 && s2.Contains("downhill"), "an uphill zipline is refused");
        Expect(Gadgets.Check(Zip(home + new Vector3(0, 25, 0), home + new Vector3(10, 0, 0)), origin) is { } s3 && s3.Contains("steep"), "a 68° zipline is refused");
        Expect(Gadgets.Check(Zip(home + new Vector3(0, 8, 0), home + new Vector3(30, 0, 0)), origin) == null, "8 m down over 30 m is fine");
        Expect(Gadgets.Check(new PlacedObject(0, PlacedKind.RopeLadder, "x", 0, 0, 0, Quaternion.Identity, "12"), origin) != null, "a 12 m ladder is refused");

        // ---- the real item path: trampoline, net, hideout --------------------------------------
        me.LookPitch = -0.6f;
        foreach (var (slot, yaw, kind) in new[] { (0, 0f, PlacedKind.Trampoline), (1, Mathf.Pi / 2, PlacedKind.CamoNet), (2, Mathf.Pi, PlacedKind.HayHideout) })
        {
            _items.Inventory.Select(slot);
            me.LookYaw = yaw;
            await Seconds(0.3);   // the ghost's last answer is from before the turn
            await Until(() => tool.Last.Valid, 3);
            Expect(tool.Last.Valid, $"{kind}: the ghost is valid ({tool.Last.Reason})");
            if (Shots && kind == PlacedKind.Trampoline) Shot("ghost");
            _items.UseSlot(me, slot);
            Expect(await Until(() => placed.All.Values.Any(o => !before.Contains(o.Id) && o.Kind == kind), 3) && _items.Inventory[slot].IsEmpty,
                $"{kind} set down, the item spent");
        }

        // under the net and in the bale: hidden from the radar (#359); then a flare burns the bale
        var net = placed.All.Values.First(o => !before.Contains(o.Id) && o.Kind == PlacedKind.CamoNet).WorldTransform(origin).Origin;
        var hay = placed.All.Values.First(o => !before.Contains(o.Id) && o.Kind == PlacedKind.HayHideout);
        var hayAt = hay.WorldTransform(origin).Origin;
        Expect(Gadgets.Hidden(net + Vector3.Up * 0.1f) && Gadgets.Hidden(hayAt + Vector3.Up * 0.1f)
            && !Gadgets.Hidden(net + new Vector3(4f, 0.1f, 0)), "hidden under the net and in the bale, not beside them");
        Expect(GadgetTool.TryBurn(me, hayAt + new Vector3(0, 1.6f, 12f), Vector3.Forward), "a flare aimed at the bale sets it alight");
        Expect(await Until(() => !placed.All.ContainsKey(hay.Id), 3), "the hay hideout burnt down");
        if (Shots) { me.LookYaw = Mathf.Pi; await Seconds(1.0); Shot("hay_fire"); }

        // walk onto the trampoline: up it goes
        var tramp = placed.All.Values.First(o => !before.Contains(o.Id) && o.Kind == PlacedKind.Trampoline).WorldTransform(origin).Origin;
        int bounces = tool.Bounces;
        float health = me.Health;
        me.GlobalPosition = tramp + Vector3.Up * 1.2f;
        me.Velocity = Vector3.Zero;
        if (Shots) { me.LookPitch = -0.5f; await Seconds(0.3); Shot("on_trampoline"); }
        float top = 0, vmax = 0;
        double end = Time.GetTicksMsec() / 1000.0 + 3;
        while (Time.GetTicksMsec() / 1000.0 < end)
        {
            top = Mathf.Max(top, me.GlobalPosition.Y - tramp.Y);
            vmax = Mathf.Max(vmax, me.Velocity.Y);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        await Until(() => me.IsOnFloor() && me.Velocity.Y > -0.5f, 4);
        Expect(me.Health >= health - 0.01f, $"landing from the bounce did not hurt ({health:F0} -> {me.Health:F0})");
        Expect(tool.Bounces > bounces && top > 9f, $"the trampoline threw me {top:F1} m up ({tool.Bounces - bounces} bounce(s), {vmax:F1} m/s up at most)");
        me.GlobalPosition = home + Vector3.Up * 0.5f;
        await Until(() => me.IsOnFloor(), 3);
        if (Shots)
        {
            me.GlobalPosition = home + new Vector3(-6, 1, 8);
            me.LookYaw = -0.3f;
            me.LookPitch = -0.25f;
            await Seconds(1.0);
            Shot("set");
            me.GlobalPosition = home + Vector3.Up * 0.5f;
            await Seconds(0.5);
        }

        // the zipline's two Uses: the start, then an end on the flat refused
        _items.Inventory.Select(3);
        me.LookYaw = 0;
        me.LookPitch = -0.6f;
        await Seconds(0.3);
        await Until(() => tool.Last.Valid, 3);
        _items.UseSlot(me, 3);
        await Seconds(0.2);
        Expect(!tool.Last.Valid && tool.Last.Reason.Contains("short"), $"its end right next to the start is refused ({tool.Last.Reason})");
        _items.Inventory.Select(5);

        // ---- placed directly: zipline, ladder, launch pad ----------------------------------------
        var low = home + new Vector3(30, 0, 0);
        var high = home + new Vector3(0, 8, 0);
        Expect(await Place(placed, PlacedKind.Zipline, new Transform3D(Basis.Identity, low), Zip(high, low).Payload), "a zipline strung");
        me.GlobalPosition = high + Vector3.Up * 0.2f;
        tool.Board(me, PlacedKind.Zipline);
        Expect(tool.Riding, "on the zipline");
        if (Shots) { await Seconds(0.8); Shot("zipline"); }
        float fastest = 0;
        end = Time.GetTicksMsec() / 1000.0 + 15;
        while (tool.Riding && Time.GetTicksMsec() / 1000.0 < end)
        {
            fastest = Mathf.Max(fastest, me.Velocity.Length());
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        Expect(!tool.Riding && new Vector2(me.GlobalPosition.X - low.X, me.GlobalPosition.Z - low.Z).Length() < 4f,
            $"rode to the bottom post ({new Vector2(me.GlobalPosition.X - low.X, me.GlobalPosition.Z - low.Z).Length():F1} m from it, top speed {fastest:F1} m/s)");

        var ladderTop = home + new Vector3(-10, 5, 0);
        var facing = new Basis(Vector3.Up, 0);   // climbing side +Z
        Expect(await Place(placed, PlacedKind.RopeLadder, new Transform3D(facing, ladderTop), Gadgets.LadderPayload(5f)), "a rope ladder hung");
        me.GlobalPosition = ladderTop - Vector3.Up * 5f + Vector3.Back * 0.5f;
        await Seconds(0.2);
        tool.Board(me, PlacedKind.RopeLadder);
        Expect(tool.Riding, "on the ladder");
        tool.ForceClimb = 1;
        float reached = 0;
        end = Time.GetTicksMsec() / 1000.0 + 6;
        while (tool.Riding && Time.GetTicksMsec() / 1000.0 < end)
        {
            reached = me.GlobalPosition.Y;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        tool.ForceClimb = null;
        Expect(!tool.Riding && reached > ladderTop.Y - 1.0f, $"climbed to the top ({reached - (ladderTop.Y - 5f):F1} of 5 m)");
        me.GlobalPosition = home + Vector3.Up * 0.5f;
        await Until(() => me.IsOnFloor(), 4);

        var pad = home + new Vector3(0, 0, 12);
        Expect(await Place(placed, PlacedKind.LaunchPad, new Transform3D(Basis.Identity, pad), ""), "a launch pad set");
        me.GlobalPosition = pad + Vector3.Up * 0.4f;
        await Seconds(0.2);
        tool.Board(me, PlacedKind.LaunchPad);
        Expect(tool.Riding, "launching");
        float peak = 0;
        end = Time.GetTicksMsec() / 1000.0 + 5;
        while (tool.Riding && Time.GetTicksMsec() / 1000.0 < end)
        {
            peak = Mathf.Max(peak, me.GlobalPosition.Y - pad.Y);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        Expect(peak > 70f && me.Ride == RideKind.Wingsuit, $"fired {peak:F0} m up into a wingsuit ({me.Ride})");
        if (Shots) { await Seconds(0.5); Shot("launch"); }

        // ---- tidy up ----------------------------------------------------------------------------
        foreach (var o in placed.All.Values.Where(o => !before.Contains(o.Id)).ToList()) placed.RequestRemove(o.Id);
        Expect(await Until(() => placed.All.Keys.All(before.Contains), 3), "everything the probe set down is gone");
    }

    private async Task<bool> Place(PlacedObjects placed, PlacedKind kind, Transform3D at, string payload)
    {
        PlacedResult? result = null;
        placed.RequestPlace(kind, at, payload, r => result = r);
        await Until(() => result != null, 3);
        if (result is { Ok: false } r) GD.Print($"[gadgetcheck] refused: {r.Refused}");
        return result is { Ok: true };
    }

    private void Shot(string name)
    {
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"gadget_{name}.png"));
    }

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (!condition())
        {
            if (Time.GetTicksMsec() / 1000.0 > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Expect(bool ok, string what)
    {
        GD.Print($"[gadgetcheck] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
