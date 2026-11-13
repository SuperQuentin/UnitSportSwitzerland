using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--telescopecheck</c> (offline, <c>--systems ui,physics,sky --sun real</c> on a date and hour
/// with the moon up, #1440): sets a telescope up through the item's real path, looks through it
/// with E's own path (<see cref="Telescopes.TryInteract"/>), points it at the moon and zooms to the
/// moon filling the view (a frustum below 1°), locks on and checks the moon stays in the middle while
/// the sky turns, is refused packing it up while looking, steps back, and packs it up with an empty
/// hand (the telescope back in the pack). Windowed it saves <c>test_output/telescope_moon.png</c>.
/// Everything it placed is gone at the end.
/// </summary>
public partial class TelescopeProbe : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--telescopecheck") >= 0;

    private readonly ItemController _items;
    private int _failures;

    public TelescopeProbe(ItemController items) => _items = items;
    public TelescopeProbe() : this(null!) { }

    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override async void _Ready()
    {
        // offline starts as the spectator high up: wait for the world, then walk (T)
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        await Seconds(2.0);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => Me is { } m && m.IsOnFloor() && PlacedObjects.Instance != null && Telescopes.Instance != null, 120))
        {
            Fail("no player on the ground");
            return;
        }
        await Seconds(1.0);
        await ClearLeftovers(Me!, PlacedObjects.Instance!);
        try { await Run(Me!, PlacedObjects.Instance!); }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        // whatever a failure left: the view closed, so nothing refuses the packing up
        TelescopeView.Current?.Close();
        await Seconds(0.3);
        foreach (var id in _placedHere) PlacedObjects.Instance?.RequestRemove(id);
        await Seconds(0.5);
        GD.Print(_failures == 0 ? "[telescopecheck] RESULT: ok" : $"[telescopecheck] RESULT: FAILED ({_failures})");
        await Seconds(0.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>What this run set up: taken away at the end whatever happened.</summary>
    private readonly List<long> _placedHere = new();

    /// <summary>Telescopes an earlier, failed run left where this one starts (the offline list is the real one).</summary>
    private async Task ClearLeftovers(FootPlayer me, PlacedObjects placed)
    {
        var left = placed.All.Values.Where(o => o.Kind == PlacedKind.Telescope
            && o.WorldTransform(placed.Origin).Origin.DistanceTo(me.GlobalPosition) < 12f).ToList();
        foreach (var o in left) placed.RequestRemove(o.Id);
        if (left.Count > 0)
        {
            GD.Print($"[telescopecheck] cleared {left.Count} telescope(s) an earlier run left");
            await Until(() => left.All(o => !placed.All.ContainsKey(o.Id)), 3);
        }
    }

    private async Task Run(FootPlayer me, PlacedObjects placed)
    {
        var inv = _items.Inventory;
        var before = placed.All.Keys.ToHashSet();
        Expect(World.DayNight.Instance?.Sky is { } sky0 && sky0.Moon.Y > 0.2f, "the real sky, the moon well up");

        // 1. set it up through the item's real path
        inv.Add(ItemId.Telescope, 1);
        int slot = Hold(ItemId.Telescope);
        me.LookYaw = 0f;
        me.LookPitch = -0.6f;
        await Seconds(0.3);
        var aim = FlagGhost.Aim(me, Placeables.ForItem(ItemId.Telescope));
        Expect(aim is { Kind: FlagAimKind.Plant, Valid: true }, $"the ghost: a spot for the tripod ({aim.Kind}, {aim.Reason})");
        _items.UseSlot(me, slot);
        Expect(await Until(() => New(placed, before) != null, 4), "the telescope is set up");
        Expect(inv.CountPlain(ItemId.Telescope) == 0, "it left the pack");
        if (New(placed, before) is not { } scope) return;
        _placedHere.Add(scope.Id);
        var node = Telescopes.Instance!.NodeOf(scope.Id);
        Expect(node?.GetNodeOrNull<Node3D>(TelescopeVisual.TubeName) != null, "drawn, with its tube to turn");

        // 2. step up to it and look through it: E's own path
        var at = scope.WorldTransform(placed.Origin).Origin;
        var away = (me.GlobalPosition - at) with { Y = 0 };
        away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Back;
        me.GlobalPosition = new Vector3(at.X, me.GlobalPosition.Y, at.Z) + away * 1.2f;
        me.Velocity = Vector3.Zero;
        await Seconds(0.5);
        Expect(Telescopes.InteractionFor(me) is { Text: var prompt } && prompt.Contains("Look through"), "the prompt at the tripod");
        // a real E press (#1813): offline the view opens in the frame of the press, and E is also
        // its "step back" key, so it must not read the press that opened it
        var opened = new List<ulong>();
        void Track(Node n) { if (n is TelescopeView) opened.Add(Engine.GetProcessFrames()); }
        GetTree().NodeAdded += Track;
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.InteractMount, Pressed = true });
        await Seconds(0.15);
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.InteractMount, Pressed = false });
        await Seconds(1.0);
        GetTree().NodeAdded -= Track;
        Expect(opened.Count > 0, "E takes it: the view opened");
        Expect(TelescopeView.Current != null, $"the view is still open 1 s after E (opened {opened.Count}×)");
        if (TelescopeView.Current == null) Telescopes.TryInteract(me);   // failed: open it directly to check the rest
        Expect(await Until(() => TelescopeView.Current != null, 3), "the view through it opens");
        if (TelescopeView.Current is not { } view) return;
        var cam = GetViewport().GetCamera3D();
        Expect(cam?.Name == "TelescopeCamera", $"its camera is the one drawing ({cam?.Name})");

        // 3. the moon: aimed, zoomed until it fills the view (a frustum: Godot's Fov stops at 1°), locked
        if (World.DayNight.Instance?.Sky is { } sky) view.Aim(sky.Moon);
        while (view.Focal < 2300f) view.Zoom(1);
        await Seconds(0.3);
        Expect(cam != null && cam.Projection == Camera3D.ProjectionType.Frustum && Mathf.Abs(CameraLens.FovOf(cam) - TelescopeOptics.Fov(view.Focal)) < 0.01f,
            $"zoomed to {view.Focal:0} mm: a {CameraLens.FovOf(cam!):0.000}° frustum");
        Expect(view.ToggleLock() && view.Locked, "locked on the moon");
        await Seconds(8.0);   // the sky turns 2° in 8 s at the default day length
        float off = World.DayNight.Instance?.Sky is { } later ? Mathf.RadToDeg(view.Direction.AngleTo(later.Moon)) : 99f;
        Expect(off < 0.02f, $"the moon stays in the middle ({off:0.0000}° off after 8 s)");
        // 3b. the target menu (#1530): next from the moon is Mercury, the menu slews and tracks Jupiter, the sun is not in it
        Expect(view.StepTarget(1) && view.Target == SkyBody.Mercury && view.Locked, "next target after the moon: Mercury, locked");
        Expect(view.SelectTarget(SkyBody.Jupiter) && view.Target == SkyBody.Jupiter, "the menu slews to Jupiter");
        await Seconds(1.0);
        float jupiterOff = World.SkyBodies.Instance?.DirectionOf(SkyBody.Jupiter) is { } jd ? Mathf.RadToDeg(view.Direction.AngleTo(jd)) : 99f;
        Expect(jupiterOff < 0.02f, $"Jupiter stays in the middle ({jupiterOff:0.0000}° off)");
        Expect(!System.Array.Exists(TelescopeTargets.Menu, b => b == SkyBody.Sun), "the sun is not a target");
        view.SelectTarget(SkyBody.Moon);
        await Seconds(0.3);
        var lookAt = -cam!.GlobalBasis.Z;
        Expect(lookAt.AngleTo(view.Direction) < 0.001f, "the camera rides the tube");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(1.0);
            var image = GetViewport().GetTexture().GetImage();
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("res://test_output"));
            image.SavePng("res://test_output/telescope_moon.png");
            GD.Print("[telescopecheck] wrote test_output/telescope_moon.png");
        }

        // 4. not packed up from under someone looking through it
        PlacedResult? refused = null;
        placed.RequestRemove(scope.Id, r => refused = r);
        Expect(await Until(() => refused != null, 3) && refused!.Value.Refused == "Someone is looking through it.",
            $"not packed up while looking ({refused?.Refused})");

        // 5. step back (E), then pack it up with an empty hand
        // as a key press arrives: flushed at the start of a frame, so the view sees it "just pressed"
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.InteractMount, Pressed = true });
        await Seconds(0.2);
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.InteractMount, Pressed = false });
        Expect(await Until(() => TelescopeView.Current == null, 2), "E steps back");
        Expect(GetViewport().GetCamera3D() == me.Camera, "the player's own camera again");
        inv.Select(EmptyHotbarSlot());
        await Seconds(0.6);
        for (int pass = 0; pass < 12; pass++)
        {
            var to = at + Vector3.Up * 0.9f - me.Camera.GlobalPosition;
            me.LookYaw = Mathf.Atan2(-to.X, -to.Z);
            me.LookPitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
            await Seconds(0.2);
            aim = FlagGhost.Aim(me, null);
            if (aim.Kind == FlagAimKind.PickUp) break;
        }
        Expect(aim is { Kind: FlagAimKind.PickUp, Target: PlacedKind.Telescope }, $"an empty hand points at the telescope ({aim.Kind}, {aim.Target})");
        // the pick-up halo hangs on the drawn parts, the turned tube's included (#1813: it was the
        // level set-up mesh at the record's yaw, off the tube and the legs on the earth frame)
        // the ghost and its halo show only with the pointer captured (a test window keeps the game's own word for it)
        if (MouseCapture.Mode != Input.MouseModeEnum.Captured) MouseCapture.Mode = Input.MouseModeEnum.Captured;
        if (aim.Kind == FlagAimKind.PickUp && MouseCapture.Mode == Input.MouseModeEnum.Captured)
        {
            await Seconds(0.2);
            if (DisplayServer.GetName() != "headless")
            {
                await Seconds(0.5);
                GetViewport().GetTexture().GetImage().SavePng("res://test_output/telescope_halo.png");
                GD.Print("[telescopecheck] wrote test_output/telescope_halo.png");
            }
            var tube = node?.GetNodeOrNull<Node3D>(TelescopeVisual.TubeName);
            int onTube = tube != null ? tube.FindChildren(FlagGhost.HaloName, owned: false).Count : 0;
            int onAll = node != null ? node.FindChildren(FlagGhost.HaloName, owned: false).Count : 0;
            Expect(onTube > 0 && onAll > onTube, $"the halo is on the drawn parts, the tube's too ({onTube} on the tube, {onAll} in all)");
        }
        if (aim.Kind == FlagAimKind.PickUp) _items.UseHeld(me);
        Expect(await Until(() => !placed.All.ContainsKey(scope.Id), 4), "it is packed up");
        Expect(inv.CountPlain(ItemId.Telescope) == 1, "and back in the pack");
    }

    private static PlacedObject? New(PlacedObjects placed, HashSet<long> before) =>
        placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.Telescope && !before.Contains(o.Id));

    /// <summary>Moves the first stack of <paramref name="id"/> to the last hotbar slot and selects it.</summary>
    private int Hold(ItemId id)
    {
        var inv = _items.Inventory;
        int slot = Inventory.HotbarSize - 1;
        for (int i = 0; i < Inventory.Size; i++)
        {
            if (inv[i].IsEmpty || inv[i].Id != id || i == slot) continue;
            var other = inv[slot];
            inv.Put(slot, inv[i]);
            inv.Put(i, other);
            break;
        }
        inv.Select(slot);
        return slot;
    }

    private int EmptyHotbarSlot()
    {
        for (int i = 0; i < Inventory.HotbarSize; i++) if (_items.Inventory[i].IsEmpty) return i;
        _items.Inventory.Put(0, ItemStack.Empty);
        return 0;
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await Frame();
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Expect(bool ok, string what)
    {
        GD.Print($"[telescopecheck]   {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[telescopecheck] RESULT: FAILED - {why}");
        GetTree().Quit(1);
    }
}
