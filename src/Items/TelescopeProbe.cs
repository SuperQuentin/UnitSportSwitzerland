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
        // across a restart: "place" sets one up and quits leaving it saved, "reload" looks through it in the next run
        string? restart = CmdArgs.Value("--telescopecheck") is "place" or "reload" ? CmdArgs.Value("--telescopecheck") : null;
        if (restart == null) await ClearLeftovers(Me!, PlacedObjects.Instance!);
        try
        {
            if (restart == "place") await PlaceForRestart(Me!, PlacedObjects.Instance!);
            else if (restart == "reload") await LookAfterRestart(Me!, PlacedObjects.Instance!);
            else await Run(Me!, PlacedObjects.Instance!);
        }
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
        Expect(view.ToggleLock() && view.Locked && !view.FollowsBody, "Use: the drive tracks the sky, the moon in the middle (#1813)");
        await Seconds(8.0);   // the sky turns 2° in 8 s at the default day length
        float off = World.DayNight.Instance?.Sky is { } later ? Mathf.RadToDeg(view.Direction.AngleTo(later.Moon)) : 99f;
        Expect(off < 0.02f, $"the moon stays in the middle ({off:0.0000}° off after 8 s)");
        // 3b. the target menu (#1530): next from the moon is Mercury, the menu slews and tracks Jupiter, the sun is not in it
        Expect(view.SelectTarget(SkyBody.Moon) && view.FollowsBody, "the menu follows the moon");
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

        // 3c. steady at the longest end (a pixel is 0.015″) (#1835 follow-up: "shaky when zoomed on the moon's surface"): the moon as drawn
        // against the middle, every drawn frame for 2 s, following it and then on the sky drive (its drift is smooth:
        // the jump from one frame to the next, less the run's mean, is the shake), in pixels
        if (DisplayServer.GetName() != "headless")   // nothing is drawn headless
        {
            while (view.Focal < TelescopeOptics.FocalMax) view.Zoom(1);
            view.SelectTarget(SkyBody.Moon);
            var (followShake, _) = await MoonShake(view);
            view.ToggleLock();
            view.ToggleLock();
            var (driveShake, drift) = await MoonShake(view);
            // following: the camera and the moon from one vector (0.2 px). The drive holds a point of the stars' float frame and
            // the moon is placed from its own float vector: each rounds by up to 6e-8 rad, ~half a pixel at a 10″ view (the stars
            // stay on the drive). Before #1835 following stepped 1 px a frame (yaw and pitch as floats).
            Expect(followShake < 0.5f && driveShake < 1.5f,
                $"steady at {view.Focal:0} mm: following the moon {followShake:0.00} px, on the drive {driveShake:0.00} px a frame (drift {drift:0.00} px a frame)");
        }
        while (view.Focal > 2400f) view.Zoom(-1);
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

        var leftAim = view.Direction;
        float leftFocal = view.Focal;
        // 5. step back (E), then pack it up with an empty hand
        // as a key press arrives: flushed at the start of a frame, so the view sees it "just pressed"
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.InteractMount, Pressed = true });
        await Seconds(0.2);
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.InteractMount, Pressed = false });
        Expect(await Until(() => TelescopeView.Current == null, 2), "E steps back");
        Expect(GetViewport().GetCamera3D() == me.Camera, "the player's own camera again");
        // by eye after stepping back, the tube still on the moon (#1835 follow-up: "with the naked eye the moon
        // disappears when looked at head-on")
        if (DisplayServer.GetName() != "headless" && World.DayNight.Instance?.Sky is { } eyeSky)
        {
            me.LookYaw = Mathf.Atan2(-eyeSky.Moon.X, -eyeSky.Moon.Z);
            me.LookPitch = Mathf.Asin(Mathf.Clamp(eyeSky.Moon.Y, -1f, 1f));
            await Seconds(1.0);
            float lit = MoonLit(out var where);
            Expect(lit > 0.45f, $"the moon by eye, head-on, after stepping back (lit {lit:F2} at {where})");
            if (lit <= 0.45f) GetViewport().GetTexture().GetImage().SavePng("res://test_output/telescope_moon_by_eye.png");
        }
        // looked through again, it is where it was left (#1813: it reset to the tripod's facing)
        Expect(Telescopes.TryInteract(me) && await Until(() => TelescopeView.Current != null, 3), "looked through again");
        if (TelescopeView.Current is { } again)
        {
            float moved = Mathf.RadToDeg(again.Direction.AngleTo(leftAim));
            Expect(moved < 0.01f && Mathf.Abs(again.Focal - leftFocal) < 0.5f,
                $"where it was left ({moved:0.0000}° off, {again.Focal:0} mm of {leftFocal:0})");
            again.Close();
            Expect(await Until(() => TelescopeView.Current == null, 2), "stepped back again");
        }
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

    /// <summary><c>--telescopecheck place</c>: sets one up through the item's path and leaves it, saved, for <c>reload</c>.</summary>
    private async Task PlaceForRestart(FootPlayer me, PlacedObjects placed)
    {
        await ClearLeftovers(me, placed);
        var before = placed.All.Keys.ToHashSet();
        _items.Inventory.Add(ItemId.Telescope, 1);
        int slot = Hold(ItemId.Telescope);
        me.LookYaw = 0f;
        me.LookPitch = -0.6f;
        await Seconds(0.3);
        _items.UseSlot(me, slot);
        Expect(await Until(() => New(placed, before) != null, 4), "the telescope is set up, left for the next run");
        await Seconds(1.5);   // the save is written in the background
    }

    /// <summary>
    /// <c>--telescopecheck reload</c>: the telescope a <c>place</c> run left, loaded from the save, looked through with a
    /// real E press (#1813 follow-up: after a restart one could not), then packed up.
    /// </summary>
    private async Task LookAfterRestart(FootPlayer me, PlacedObjects placed)
    {
        var scope = placed.All.Values.Where(o => o.Kind == PlacedKind.Telescope)
            .OrderBy(o => o.WorldTransform(placed.Origin).Origin.DistanceTo(me.GlobalPosition)).FirstOrDefault();
        Expect(scope != null, "a telescope loaded from the save");
        if (scope == null) return;
        _placedHere.Add(scope.Id);
        var at = scope.WorldTransform(placed.Origin).Origin;
        GD.Print($"[telescopecheck] loaded #{scope.Id} {at.DistanceTo(me.GlobalPosition):0.0} m away, node {(Telescopes.Instance!.NodeOf(scope.Id) is { } n ? $"at {n.GlobalPosition.DistanceTo(at):0.00} m off its record" : "missing")}");
        var away = (me.GlobalPosition - at) with { Y = 0 };
        away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Back;
        me.GlobalPosition = new Vector3(at.X, me.GlobalPosition.Y, at.Z) + away * 1.2f;
        me.Velocity = Vector3.Zero;
        await Seconds(0.8);
        Expect(Telescopes.InteractionFor(me) is { Text: var prompt } && prompt.Contains("Look through"),
            $"the prompt at the loaded tripod ({Telescopes.InteractionFor(me)?.Text ?? "none"}, at {me.GlobalPosition.DistanceTo(at):0.0} m)");
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.InteractMount, Pressed = true });
        await Seconds(0.15);
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.InteractMount, Pressed = false });
        await Seconds(1.0);
        Expect(TelescopeView.Current != null, $"looked through it after a restart ({Telescopes.Instance!.LastRefused})");
        TelescopeView.Current?.Close();
        await Seconds(0.3);
    }

    /// <summary>The worst jump of the drawn moon on screen from one frame to the next less the mean (px), and the mean (px), over 2 s.</summary>
    private async Task<(float Shake, float Drift)> MoonShake(TelescopeView view)
    {
        var moon = GetTree().Root.FindChild("MoonDisc", true, false) as Node3D;
        var points = new List<Vector2>();
        void Sample()
        {
            if (moon == null || GetViewport().GetCamera3D() is not { } c) return;
            if (!c.IsPositionBehind(moon.GlobalPosition)) points.Add(c.UnprojectPosition(moon.GlobalPosition));
        }
        RenderingServer.FramePostDraw += Sample;
        await Seconds(2.0);
        RenderingServer.FramePostDraw -= Sample;
        if (points.Count < 10) return (99f, 0f);
        var steps = new List<Vector2>();
        for (int i = 1; i < points.Count; i++) steps.Add(points[i] - points[i - 1]);
        var mean = steps.Aggregate(Vector2.Zero, (a, b) => a + b) / steps.Count;
        float worst = steps.Max(st => (st - mean).Length());
        GD.Print($"[telescopecheck] moon on screen over {points.Count} frames: from {points[0]} to {points[^1]}, worst jump {worst:0.00} px");
        return (worst, mean.Length());
    }

    /// <summary>The brightest pixel round where the moon is drawn on screen (0 when it is not in view), and where.</summary>
    private float MoonLit(out Vector2 where)
    {
        where = Vector2.Zero;
        if (GetTree().Root.FindChild("MoonDisc", true, false) is not Node3D moon || GetViewport().GetCamera3D() is not { } cam
            || cam.IsPositionBehind(moon.GlobalPosition)) return 0f;
        var image = GetViewport().GetTexture().GetImage();
        where = cam.UnprojectPosition(moon.GlobalPosition) * (new Vector2(image.GetWidth(), image.GetHeight()) / GetViewport().GetVisibleRect().Size);
        float lit = 0;
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                int px = (int)where.X + x, py = (int)where.Y + y;
                if (px < 0 || py < 0 || px >= image.GetWidth() || py >= image.GetHeight()) continue;
                var c = image.GetPixel(px, py);
                lit = Mathf.Max(lit, 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B);
            }
        return lit;
    }
}
