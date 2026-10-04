using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

/// <summary>
/// <c>--brprobe A|B</c> with <c>--connect</c> (driven by <c>tools/brcheck.sh</c>): one whole match over loopback
/// against a server started with <c>--admin-password brcheck --brpace 0.13</c> (a match in under two minutes).
/// <list type="bullet">
/// <item>A logs in as admin, opens a lobby and starts the match once B has joined.</item>
/// <item>Both are dropped in the region with an empty pack, a knife and bandages; the travel menu is locked.</item>
/// <item>B stands outside the zone until it hurts, then next to A.</item>
/// <item>A, an admin, gets no fly camera, debug menu, catalogue or <c>/spawn</c> in the match (#425), and forces the zone to close (<c>/br zone</c>).</item>
/// <item>A stabs B until it is down.</item>
/// <item>B must stay down (eliminated, no revive), spectate, and see the kill in its feed.</item>
/// <item>Both see A win; after the results both are back where they started, with their own packs.</item>
/// </list>
/// Scratch inventories; screenshots in <c>test_output/br_*.png</c>.
/// </summary>
public partial class BrProbe : ChatProbe
{
    public static string? Role => RoleArg("--brprobe");

    public BrProbe(ItemController items) : base(items, "br", "BR", "br_") { }
    public BrProbe() : this(null!) { }

    protected override string Dash => "-";
    /// <summary>Our own body, not the one the camera follows (spectating after the knock-out).</summary>
    protected override FootPlayer? Me => GetParent().GetNodeOrNull<FootPlayer>("Players/" + Multiplayer.GetUniqueId());
    private BrManager? Br => BrManager.Instance;

    /// <summary>Counts too: a failure inside a phase must stop <see cref="Released"/> and print RESULT: FAILED.</summary>
    protected override void Fail(string why)
    {
        _failures++;
        base.Fail(why);
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => Br != null)) return;
        await Seconds(2.0);
        var start = Me!.Global;   // LV95: the match's flight moves the origin (#185)
        if (_role == "A") await RunA(); else await RunB();
        if (_failures == 0) await Released(start);
        await Finish(1.0);
    }

    private async Task RunA()
    {
        Chat!.Send("/login brcheck");
        await Until(() => Permissions.IsAdmin, 10);
        Expect(Permissions.IsAdmin, "logged in as admin");
        Chat.Send("/br open 5 short");
        if (!await Until(() => Br!.State.Phase == BrPhase.Lobby, 20)) { Fail("no lobby"); return; }
        Chat.Send("/br join");
        for (int i = 0; i < 60 && Br!.State.Entrants.Count < 2; i++)
        {
            Say("lobby");
            await Seconds(2.0);
        }
        if (Br!.State.Entrants.Count < 2) { Fail("B never joined"); return; }
        Chat.Send("/br start");
        if (!await Dropped()) return;
        await NoAdminTools(Me!);

        // the loot (#194): crates and vehicles came with the drop; a supply crate, emptied
        var me = Me!;
        await LootSupplyCrate(me);
        if (Sites) await TrySites(me);

        // where the last circle closes: inside every circle until the very end
        var c = Br.Zone!.CentreOf(ZoneSchedule.Phases);
        Br.Teleport(Br.State.AreaE + c.X, Br.State.AreaN + c.Y, "final zone");
        await Seconds(3.0);

        // the first supply drop comes down at phase 2: seen before the duel ends the match
        await Until(() => _sawDrop, 100);   // the zone clock starts when the plane's doors close (#207)
        if (BrCrates.Instance is { } dropped && dropped.All.Where(c => c.Style == CrateStyle.Airdrop).MinBy(Dist) is { } drop)
        {
            await Until(() => dropped.Landed(drop), 60);
            await Seconds(0.6);
            Expect(dropped.GetNodeOrNull<Node3D>($"C{drop.Id}/Beacon") is { Visible: true }, "a landed supply drop shows its beam of light (#231)");
            if (dropped.GetNodeOrNull<Node3D>($"C{drop.Id}") is { } beacon)
            {
                var to = beacon.GlobalPosition + Vector3.Up * 40f - me.Camera.GlobalPosition;
                me.LookYaw = Mathf.Atan2(-to.X, -to.Z);
                me.LookPitch = 0.1f;
                await Seconds(0.8);
                Shot("a_beacon");
            }
        }
        Shot("a_drop");

        // wait for B next to us, then stab it until it goes down
        // LV95: B's world space is not this one (every peer has its own origin, #185)
        Say(Fmt($"posA {me.Global.E:F2} {me.Global.N:F2} {me.Global.Alt:F2}"));
        if (!await Until(() => Said("B", "near"), 90)) { Expect(false, "B came near"); return; }
        await Seconds(1.5);
        var b = GetParent().GetNodeOrNull<FootPlayer>("Players/" + PeerOf("B"));
        if (b == null) { Expect(false, "B's body is here"); return; }
        int knife = SlotOf(ItemId.Knife);
        _items.Inventory.Select(knife);
        for (int i = 0; i < 12 && b.Down == 0; i++)
        {
            var dir = (b.GlobalPosition + Vector3.Up * 1.1f - me.EyePosition).Normalized();
            me.LookYaw = Mathf.Atan2(-dir.X, -dir.Z);
            me.LookPitch = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f));
            await Seconds(0.2);
            _items.UseSlot(me, knife);
            await Seconds(0.6);
        }
        Expect(await Until(() => b.Down != 0, 3), "B is down");
        await LootDeathBox(me);
        await Ended();
    }

    /// <summary>
    /// An admin in the match plays on equal terms (#425): no fly camera, debug menu, catalogue or
    /// <c>/spawn</c>. Then <c>/br zone</c> ends the loot time: the zone closes at once.
    /// </summary>
    private async Task NoAdminTools(FootPlayer me)
    {
        Expect(Permissions.IsAdmin && !DebugMenu.Allowed && !CatalogueUi.Allowed && !Permissions.CanSpawnVehicles,
            "an admin in the match: no debug menu, catalogue or vehicle spawning");
        GetParent<ClientWorld>().ToggleMode();
        await Seconds(0.3);
        Expect(me.IsViewing, $"T is refused: no fly camera in the match ({GetViewport().GetCamera3D()?.GetPath()})");
        Chat!.Send("/spawn knife");
        Expect(await Until(() => _heard.Any(l => l.Contains(ChatManager.NotInMatch)), 5), "/spawn is refused in the match");
        Expect(Br!.ZoneNow is { Phase: 0, Shrinking: false }, $"the zone waits while looting (phase {Br.ZoneNow?.Phase})");
        Chat.Send("/br zone");
        Expect(await Until(() => Br.ZoneNow is { Shrinking: true }, 5), $"/br zone: the zone closes at once (phase {Br.ZoneNow?.Phase})");
    }

    private async Task RunB()
    {
        // with two in, the lobby turns into the countdown at once; --br may even have got B in before this ran
        if (!await Until(() => Br!.State.Phase is BrPhase.Lobby or BrPhase.Countdown || Br.MyEntry != null, 90)) { Fail("no lobby"); return; }
        // B runs with "--br" (#231): it joins by itself
        Expect(BrManager.AutoJoin && await Until(() => Br!.MyEntry != null, 30), "--br joined the lobby by itself");
        if (!await Dropped()) return;

        // outside the zone until it hurts
        var me = Me!;
        var s = Br!.State;
        Br.Teleport(s.AreaE + s.Side * 0.9, s.AreaN, "outside");
        await Seconds(3.0);
        float hp = me.Health;
        bool hurt = await Until(() => me.Health < hp - 0.9f, 60);
        Expect(hurt, $"the zone hurts outside it ({hp:F1} -> {me.Health:F1}, phase {Br.ZoneNow?.Phase})");
        Shot("b_outside");
        // back inside while A gets ready: the wait is long enough to die out there, and a small field's
        // zone (#447) closes fast, so where the last circle closes, as A does
        var last = Br.Zone!.CentreOf(ZoneSchedule.Phases);
        Br.Teleport(s.AreaE + last.X + 5, s.AreaN + last.Y, "back in the zone");   // never on top of A

        // next to A
        if (!await Until(() => _heard.Any(l => l.Contains("BR A posA")), 160)) { Expect(false, "A reported"); return; }
        var p = _heard.Last(l => l.Contains("BR A posA")).Split("posA ")[1].Split(' ');
        double e = double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture);
        double n = double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
        Br.Teleport(e, n + 1.3, "next to A");
        await Seconds(3.0);
        me.LookYaw = Mathf.Pi;
        Say("near");

        if (!await Until(() => me.Eliminated, 40)) { Expect(false, $"B was eliminated (health {me.Health:F1})"); return; }
        Expect(true, "B was eliminated");
        await Seconds(5.0);
        Expect(me.Eliminated && me.KnockedOut, "still down 5 s later: no revive in a match");
        Expect(Br.Feed.Any(f => f.Text.Contains("✕")), $"the kill is in the feed ({string.Join(" | ", Br.Feed.Select(f => f.Text))})");
        await Ended();
    }

    /// <summary>The countdown runs, then this player lands in the region with a fresh match pack.</summary>
    /// <summary>
    /// The cargo plane (#207): aboard and hidden; A jumps as soon as the doors open (refused before),
    /// B waits to be pushed out when they close; each sees the other's body hidden while aboard and
    /// shown once out. Then down to the ground under the jump, at once: a test pace has no time for a glide.
    /// </summary>
    private async Task<bool> Flown()
    {
        var me = Me!;
        var br = Br!;
        if (br.State.Flight is not { } flight) { Fail("no flight in the match state"); return false; }
        Expect(br.Aboard && me.Carrier != null, Fmt($"aboard the cargo plane at {flight.Altitude:F0} m, doors open in {flight.OpensAt - ClockSync.ServerNow:F0} s"));
        await Seconds(1.0);
        var plane = br.PlaneFrame(flight, ClockSync.ServerNow).At;
        Expect(me.GlobalPosition.DistanceTo(plane) < 8f + flight.Speed * 0.25f && !me.Visible,
            Fmt($"held in the hold, hidden ({me.GlobalPosition.DistanceTo(plane):F1} m from the plane)"));
        Shot($"{_role.ToLowerInvariant()}_plane");
        string other = _role == "A" ? "B" : "A";
        var them = GetParent().GetNodeOrNull<FootPlayer>("Players/" + PeerOf(other));
        if (_role == "A")
        {
            Expect(ClockSync.ServerNow >= flight.OpensAt || !br.JumpOut(), "E before the doors open is refused");
            await Until(() => flight.DoorsOpen(ClockSync.ServerNow), 60);
            var exit = flight.At(ClockSync.ServerNow);
            Expect(br.JumpOut() && me.Ride == RideKind.Wingsuit && !br.Aboard, "E with the doors open: out of the ramp in a wingsuit");
            var zone = br.State.Zone();
            Expect(exit.DistanceTo(zone.CentreOf(0)) <= zone.RadiusOf(0) + 5f,
                Fmt($"the doors opened over the first circle: out {exit.DistanceTo(zone.CentreOf(0)):F0} m from its centre, radius {zone.RadiusOf(0):F0} m ({br.State.Field} players)"));
            Expect(me.IsViewing, $"the player's camera right after the jump ({GetViewport().GetCamera3D()?.GetPath()})");
            await Seconds(0.5);
            Expect(them != null && !them.Visible && br.State.Find(PeerOf("B"))?.Jumped == false, "B is still aboard: its body is hidden here");
            // the mouse steers the suit a bit (#207): looking left banks it left
            float yaw0 = me.GlobalRotation.Y;
            // (the look-left action: the same free look the mouse moves, and a test window cannot capture the mouse)
            Input.ActionPress(PlayerInput.LookLeft);
            await Seconds(1.2);
            Input.ActionRelease(PlayerInput.LookLeft);
            await Seconds(0.3);
            float turned = Mathf.AngleDifference(yaw0, me.GlobalRotation.Y);
            Expect(turned > 0.12f, Fmt($"looking left turns the wingsuit left ({Mathf.RadToDeg(turned):F0}°)"));
            Shot("a_wingsuit");
            Expect(await Until(() => br.State.Find(PeerOf("B"))?.Jumped == true && them is { Visible: true }, 90),
                "B was pushed out when the doors closed, and shows again here");
        }
        else
        {
            bool pushed = await Until(() => !br.Aboard, 90);
            Expect(pushed && ClockSync.ServerNow >= flight.ClosesAt - 0.5 && me.Ride == RideKind.Wingsuit,
                Fmt($"pushed out in a wingsuit when the doors closed ({ClockSync.ServerNow - flight.ClosesAt:F1} s after)"));
            Expect(br.State.Find(PeerOf("A"))?.Jumped == true && them is { Visible: true }, "A jumped earlier and shows here");
            await Seconds(1.0);
            Shot("b_pushed");
        }
        // down under the jump, inside the region
        var s = br.State;
        var (e, n) = br.Origin!.ToLv95(me.GlobalPosition);
        double h = s.Side * 0.45;
        e = Math.Clamp(e, s.AreaE - h, s.AreaE + h);
        n = Math.Clamp(n, s.AreaN - h, s.AreaN + h);
        Expect(me.Ride == RideKind.Wingsuit && !me.Eliminated, $"still gliding, unhurt ({me.Ride}, {me.Health:F0} HP)");
        me.Leap(me.GlobalPosition, Vector3.Zero, RideKind.OnFoot);   // out of the wingsuit first: a teleport onto the ground in one is a SPLAT
        br.Teleport(e, n, "under the jump");
        Expect(await Until(() => me.IsOnFloor() && me.Ride == RideKind.OnFoot, 30), $"on the ground, on foot ({me.Ride})");
        Expect(me.IsViewing, $"seen through the player's own camera again ({GetViewport().GetCamera3D()?.GetPath()})");
        return true;
    }

    private async Task<bool> Dropped()
    {
        if (!await Until(() => Br!.InMatch, 60)) { Fail("never dropped"); return false; }
        Expect(_items.Inventory.InMatch && _items.Inventory.Contains(ItemId.Knife) && !_items.Inventory.Contains(ItemId.Binoculars),
            "a match pack: a knife, nothing from free roam");
        Expect(Permissions.InMatch, "the travel menu is locked");
        if (!await Flown()) return false;
        await Seconds(4.0);
        var s = Br!.State;
        var (e, n) = Br.Origin!.ToLv95(Me!.GlobalPosition);
        Expect(s.Area.Contains(e, n), Fmt($"landed in the region ({e:F0}/{n:F0} in {s.AreaName} {s.AreaE:F0}/{s.AreaN:F0})"));
        Expect(await Until(() => Br.MapTexture != null, 40), "the region's map is built (minimap)");
        Expect(await Until(() => BrCrates.Instance?.All.Count(c => c.Style == CrateStyle.Supply) > 20, 8),
            $"supply crates by the roads ({BrCrates.Instance?.All.Count(c => c.Style == CrateStyle.Supply)} supply, "
            + $"{BrCrates.Instance?.All.Count(c => c.Style == CrateStyle.Military)} army)");
        Expect(await Until(() => Vehicles() > 0, 6), $"vehicles parked in the region ({Vehicles()})");
        Br.Waypoint = new Vector2(400, 300);
        await Seconds(1.0);
        Shot($"{_role.ToLowerInvariant()}_dropped");
        if (_role == "A")
        {
            Expect(Br.ToggleMap() && Br.MapOpen, "M opens the match map");
            await Seconds(1.0);
            Shot("a_map");
            Br.ToggleMap();
            Expect(!Br.MapOpen, "M closes it");
        }
        return true;
    }

    private async Task Ended()
    {
        if (!await Until(() => Br!.State.Phase == BrPhase.Ended, 30)) { Expect(false, "the match ended"); return; }
        var s = Br!.State;
        Expect(s.Winner == PeerOf("A") && s.Find(s.Winner)?.Kills == 1, $"A won with one kill (winner {s.Find(s.Winner)?.Name})");
        Expect(_sawDrop && _heard.Any(l => l.Contains("supply drop is coming down")), "a supply drop came down, announced and on the map");
        // careers (#479): each entrant hears its record; the winner tops the board
        Expect(await Until(() => _heard.Any(l => l.Contains("Your Battle Royale record: 1 match")), 8), "the match went into my record");
        if (_role == "A")
        {
            Chat!.Send("/br top");
            Expect(await Until(() => _heard.Any(l => l.Contains("1. BRA — 1 win, 1 kill")), 8), "/br top: the winner leads the board");
        }
        await Seconds(1.0);
        Shot($"{_role.ToLowerInvariant()}_results");
    }

    /// <summary>After the results: back where the player stood, with its own pack.</summary>
    private async Task Released(GlobalPos start)
    {
        if (!await Until(() => !Br!.InMatch, 40)) { Expect(false, "released after the results"); return; }
        Expect(!_items.Inventory.InMatch && _items.Inventory.Contains(ItemId.Binoculars) && !_items.Inventory.Contains(ItemId.Knife),
            "the free-roam pack is back, the knife is gone");
        Expect(!Permissions.InMatch, "the travel menu is open again");
        Expect(await Until(() => BrCrates.Instance?.All.Any() != true && Vehicles() == 0, 10),
            $"the match's crates and vehicles are gone ({BrCrates.Instance?.All.Count()} crates, {Vehicles()} vehicles)");
        await Seconds(5.0);
        var me = Me!;
        Expect(!me.Eliminated && !me.KnockedOut, "standing again");
        float d = (float)me.Global.HorizontalDistanceTo(start);
        Expect(d < 30f, $"back where it started ({d:F0} m away)");
    }

    private bool _sawDrop;

    public override void _Process(double delta)
    {
        if (Br != null && BrMapDraw.Airdrops(Br).Any()) _sawDrop = true;
    }

    private static int Vehicles() =>
        UnitSport.Vehicles.VehicleManager.Instance?.GetChildren().Count(n => n.Name.ToString().StartsWith(BrLoot.VehiclePrefix)) ?? 0;

    /// <summary>A: to the nearest supply crate, E, take everything; the pack grows and the crate goes.</summary>
    private async Task LootSupplyCrate(FootPlayer me)
    {
        var crates = BrCrates.Instance!;
        var (e, n) = Br!.Origin!.ToLv95(me.GlobalPosition);
        var crate = crates.All.Where(c => c.Style == CrateStyle.Supply).MinBy(c => (c.E - e) * (c.E - e) + (c.N - n) * (c.N - n));
        if (crate == null) { Expect(false, "a supply crate to loot"); return; }
        Br.Teleport(crate.E + 1.0, crate.N, "supply crate");
        await Until(() => crates.NearestTo(me)?.Id == crate.Id, 20);
        await Seconds(1.5);   // the server learns where we stand a moment after the teleport
        int before = Count();
        var stacks = crate.Stacks();
        Expect(crates.TryOpen(me) && Loot.LootService.Instance?.IsOpen == true, $"E opens {crate.Label} ({string.Join(", ", stacks.Select(s => $"{s.Count} {s.Id}"))})");
        Loot.LootService.Instance?.TakeAll();
        bool emptied = await Until(() => !crates.All.Any(c => c.Id == crate.Id), 10);
        Expect(emptied && Count() == before + stacks.Sum(s => s.Count), $"took it all: the crate is gone, the pack {before} -> {Count()}");
        Loot.LootService.Instance?.Close();
        Shot("a_crate");
    }

    /// <summary>A: B's death box lies where B fell, with B's knife and bandages in it.</summary>
    private async Task LootDeathBox(FootPlayer me)
    {
        var crates = BrCrates.Instance!;
        if (!await Until(() => crates.All.Any(c => c.Style == CrateStyle.DeathBox), 10)) { Expect(false, "B's death box appeared"); return; }
        var box = crates.All.First(c => c.Style == CrateStyle.DeathBox);
        await Until(() => crates.NearestTo(me)?.Id == box.Id, 10);
        int knives = CountOf(ItemId.Knife);
        Shot("a_deathbox");
        Expect(crates.TryOpen(me), $"E opens {box.Label}: {string.Join(", ", box.Stacks().Select(s => $"{s.Count} {s.Id}"))}");
        Loot.LootService.Instance?.TakeAll();
        Expect(await Until(() => CountOf(ItemId.Knife) == knives + 1, 10), $"B's knife is now A's ({knives} -> {CountOf(ItemId.Knife)})");
        Loot.LootService.Instance?.Close();
    }

    /// <summary>"--brsites": the outdoor sites (#198) are checked too (a real-terrain run: tools/brcheck.sh with SITES=1).</summary>
    private static bool Sites => CmdArgs.Has("--brsites");

    /// <summary>A: the sites exist; crack a bunker; shoot a supply crate open and loot the pile; fire a flare.</summary>
    private async Task TrySites(FootPlayer me)
    {
        var crates = BrCrates.Instance!;
        await Until(() => crates.All.Any(c => c.Style == CrateStyle.HighSeat), 30);
        string Of(CrateStyle st) => $"{crates.All.Count(c => c.Style == st)} {st}";
        Expect(crates.All.Any(c => c.Style == CrateStyle.Bunker) && crates.All.Any(c => c.Style == CrateStyle.HighSeat) && crates.All.Any(c => c.Style == CrateStyle.Wreck),
            $"the outdoor sites: {Of(CrateStyle.Bunker)}, {Of(CrateStyle.HighSeat)}, {Of(CrateStyle.HayStash)}, {Of(CrateStyle.SacBox)}, {Of(CrateStyle.Wreck)}, {Of(CrateStyle.FishingHut)}");

        // the bunker: the dial, then its contents
        if (crates.All.FirstOrDefault(c => c.Style == CrateStyle.Bunker) is { } bunker)
        {
            Br!.Teleport(bunker.E, bunker.N, "bunker");
            await Until(() => crates.NearestTo(me)?.Id == bunker.Id, 25);
            await Seconds(1.5);
            Shot("a_bunker");
            var loot = Loot.LootService.Instance!;
            Expect(crates.TryOpen(me) && loot.LockUi?.IsOpen == true, "E at the bunker door opens the dial");
            loot.SubmitCombination(BrCrates.Combination(bunker.Id, Br.State.Seed));
            Expect(await Until(() => !bunker.Locked && loot.IsOpen, 10), "the right numbers open the door, straight into its contents");
            loot.TakeAll();
            Expect(await Until(() => CountOf(ItemId.HuntingRifle) > 0, 10), "the bunker's hunting rifle is in the pack");
            Shot("a_bunker_open");
            loot.Close();
        }

        // a supply crate shot open: a pile to loot
        if (CountOf(ItemId.HuntingRifle) > 0 && crates.All.Where(c => c.Style == CrateStyle.Supply).MinBy(c => Dist(c)) is { } target)
        {
            Br.Teleport(target.E + 8, target.N, "a crate to shoot");
            await Until(() => crates.InReach(target.Id, me.GlobalPosition, 9f), 25);
            await Seconds(1.5);
            var node = crates.GetNodeOrNull<Node3D>($"C{target.Id}");
            if (node != null)
            {
                int gun = SlotOf(ItemId.HuntingRifle);
                _items.Inventory.Select(gun);
                // the crosshair on the crate: in third person the shot follows the camera's ray
                for (int i = 0; i < 3; i++)
                {
                    var dir = (node.GlobalPosition + Vector3.Up * 0.25f - me.Camera.GlobalPosition).Normalized();
                    me.LookYaw = Mathf.Atan2(-dir.X, -dir.Z);
                    me.LookPitch = Mathf.Asin(Mathf.Clamp(dir.Y, -1f, 1f));
                    await Seconds(0.3);
                }
                _items.UseSlot(me, gun);
                bool shot = await Until(() => target.Style == CrateStyle.Pile, 3);
                // on a steep real slope the third-person crosshair can sit in the hillside: the same
                // break request, traced from the eye straight at the crate
                if (!shot)
                {
                    var eye = me.EyePosition;
                    crates.TryBreak(eye, (node.GlobalPosition + Vector3.Up * 0.25f - eye).Normalized(), 40f);
                }
                Expect(await Until(() => target.Style == CrateStyle.Pile, 5),
                    $"a shot breaks the supply crate open ({(shot ? "the rifle shot" : "a trace from the eye; the crosshair shot missed")})");
                await Seconds(0.5);   // a frame or two: the screenshot is the last frame drawn
                Shot("a_pile");
            }
        }

        // a flare calls a drop
        int drops = crates.All.Count(c => c.Style == CrateStyle.Airdrop);
        _items.Inventory.Add(ItemId.FlareGun, 1);
        _items.UseSlot(me, SlotOf(ItemId.FlareGun));
        Expect(await Until(() => crates.All.Count(c => c.Style == CrateStyle.Airdrop) > drops && _heard.Any(l => l.Contains("fired a flare")), 10),
            "a flare calls a supply drop, announced to everyone");
        await Seconds(2.0);
        Shot("a_flare");
    }

    private float Dist(Crate c)
    {
        var (e, n) = Br!.Origin!.ToLv95(Me!.GlobalPosition);
        return (float)Math.Sqrt((c.E - e) * (c.E - e) + (c.N - n) * (c.N - n));
    }

    private int Count() => Enumerable.Range(0, Inventory.Size).Sum(i => _items.Inventory[i].Count);

    private long PeerOf(string role)
    {
        string name = "BR" + role;
        return Br?.State.Entrants.FirstOrDefault(e => e.Name == name)?.Peer ?? 0;
    }

    private bool Said(string role, string what) => _heard.Any(l => l.Contains($"BR {role} {what}"));

    private static string Fmt(FormattableString s) => FormattableString.Invariant(s);
}
