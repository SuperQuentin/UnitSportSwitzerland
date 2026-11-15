using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--moonnetcheck A|B</c> with <c>--connect</c> (driven by <c>tools/moonnetcheck.sh</c>, #1852 C3b, docs/plans/planets.md
/// section 13): online between bodies, everything checked on the remote peer.
/// <list type="bullet">
/// <item>B sets a telescope up on the Earth and looks through it; A, an admin, takes the fly camera to the Moon
/// (<c>/goto</c>, the server's check), down over the Apollo 15 site, and steps in on the generated ground;</item>
/// <item>B's far-marker list has A on the Moon where A says it stands; B locks the telescope on A (the menu's players
/// after the bodies): A's label is in the middle of the eyepiece, on the Moon's disc;</item>
/// <item>B goes to the Moon beside A the same way; each sees the other's copy on the Moon (their place within 1 m of
/// what the owner publishes), walking (the gait, a ground speed in the pose) and jumping (rising past 2 m);</item>
/// <item>A adds two bots there (<c>/bot add 2</c>): the server runs them in its Moon region on its own collision, and
/// both clients see them stand on the Moon's ground.</item>
/// </list>
/// Both log in as admin (<c>--admin-password test</c>). Needs the Moon's data (<c>--bodies</c>) on the clients and the server.
/// </summary>
public partial class MoonNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--moonnetcheck");

    /// <summary>Where on the Moon: the Apollo 15 site (lit on 2027-07-15).</summary>
    private const string Site = "moon 26.13 3.63 4";

    public MoonNetProbe(ItemController items) : base(items, "moonnet", "MN", "moonnet_") { }

    /// <summary>In a window (<c>WINDOWED=1</c>): the view saved as <c>test_output/moonnet_&lt;role&gt;_&lt;name&gt;.png</c>, facing <paramref name="face"/> first.</summary>
    private async Task Picture(string name, Node3D? face = null)
    {
        if (DisplayServer.GetName() == "headless") return;
        if (face != null && Me is { } me && IsInstanceValid(face))
        {
            var d = face.GlobalPosition - me.GlobalPosition;
            me.LookYaw = Mathf.Atan2(-d.X, -d.Z);
            me.LookPitch = -0.15f;
        }
        await Seconds(1.0);
        GD.Print($"{Log} picture {Shot(_role + "_" + name)}");
    }
    public MoonNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ProcessPriority = 1000;
        if (!await Joined(150, () => PlacedObjects.Instance != null && Telescopes.Instance != null))
        {
            await Finish(0);
            return;
        }
        try
        {
            if (!await LogIn("test")) return;
            if (_role == "A") await RunA(Me!);
            else await RunB(Me!);
        }
        finally
        {
            TelescopeView.Current?.Close();
            Input.ActionRelease(PlayerInput.Jump);
            await Finish(3);
        }
    }

    private string Other => _role == "A" ? "B" : "A";

    private ClientWorld? World => GetParent() as ClientWorld;

    private WorldOrigin? Origin => Me?.Origin;

    /// <summary>The other player's copy here (a human's, not this peer's own).</summary>
    private FootPlayer? OtherCopy()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer { Npc: false } p && !p.IsMultiplayerAuthority() && FootPlayer.NetId(p.Name) is > 0 && IsInstanceValid(p)) return p;
        return null;
    }

    private System.Collections.Generic.List<FootPlayer> BotCopies() => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>()
        .Where(p => IsInstanceValid(p) && FootPlayer.NetId(p.Name) is long id && PlayerReplication.IsBotId(id)).ToList();

    // ---- A -------------------------------------------------------------------------------------------

    private async Task RunA(FootPlayer me)
    {
        if (!await Heard("B", "looking", 120)) { Fail("B never looked through its telescope"); return; }
        if (!await ToTheMoon(me, 0)) return;
        // where I stand, for B to compare its far marker with (body-fixed doubles of the Moon)
        var at = Origin!.ToGlobal(me.GlobalPosition);
        var (x, y, z) = at.ToWire();
        Say(FormattableString.Invariant($"at {at.WireBody} {x:F1} {y:F1} {z:F1}"));
        GD.Print($"{Log} standing on the Moon at {at}");
        if (!await Heard("B", "seen", 60)) { Fail("B never saw my marker"); return; }
        if (!await Heard("B", "landed", 180)) { Fail("B never came to the Moon"); return; }
        await SeeOther(me);
        // two bots by me: the server runs them on its Moon ground
        Chat!.Send("/bot add 2");
        await SeeBots(me);
        Say("done");
        await Heard("B", "done", 60);
    }

    // ---- B -------------------------------------------------------------------------------------------

    private async Task RunB(FootPlayer me)
    {
        if (!await SetUpTelescope(me)) return;
        Say("looking");
        var line = await HeardStarting("A", "at ", 240);
        if (line == null) { Fail("A never stood on the Moon"); return; }
        var parts = line[(line.IndexOf("at ", StringComparison.Ordinal) + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var told = GlobalPos.FromWire(Double(parts[1]), Double(parts[2]), Double(parts[3]), (BodyId)int.Parse(parts[0]));
        await ReadMarker(told);
        Say("seen");
        TelescopeView.Current?.Close();
        await Until(() => TelescopeView.Current == null, 3);
        // beside A: 12 m east of where it stands
        if (!await ToTheMoon(me, 12)) return;
        Say("landed");
        await SeeOther(me);
        await SeeBots(me);
        Say("done");
        await Heard("A", "done", 120);
    }

    private static double Double(string s) => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A telescope set up in front of this player and looked through (the server lets one viewer at a time).</summary>
    private async Task<bool> SetUpTelescope(FootPlayer me)
    {
        var placed = PlacedObjects.Instance!;
        foreach (var old in placed.All.Values.Where(o => o.Kind == PlacedKind.Telescope).ToList()) placed.RequestRemove(old.Id);
        await Until(() => placed.All.Values.All(o => o.Kind != PlacedKind.Telescope), 5);
        var before = placed.All.Keys.ToHashSet();
        // both join on one spot: 8 m west of A first, so A never stands where the tripod goes
        var start = me.GlobalPosition;
        await WalkUntil(me, new Vector3(-1, 0, 0), () => me.GlobalPosition.DistanceTo(start) > 8, 10);
        _items.Inventory.Add(ItemId.Telescope, 1);
        int slot = SlotOf(ItemId.Telescope);
        me.LookYaw = Mathf.Pi / 2;   // west, away from A
        me.LookPitch = -0.6f;
        if (!await Until(() => FlagGhost.Aim(me, Placeables.ForItem(ItemId.Telescope)) is { Kind: FlagAimKind.Plant, Valid: true }, 6))
        {
            Fail($"no place for the telescope in front of me ({FlagGhost.Aim(me, Placeables.ForItem(ItemId.Telescope))})");
            return false;
        }
        _items.UseSlot(me, slot);
        if (!await Until(() => placed.All.Values.Any(o => o.Kind == PlacedKind.Telescope && !before.Contains(o.Id)), 6))
        {
            Fail("the server never set my telescope up");
            return false;
        }
        var scope = placed.All.Values.First(o => o.Kind == PlacedKind.Telescope && !before.Contains(o.Id));
        var spot = scope.WorldTransform(placed.Origin).Origin;
        me.GlobalPosition = new Vector3(spot.X + 1.2f, me.GlobalPosition.Y, spot.Z);
        me.Velocity = Vector3.Zero;
        await Seconds(1.0);
        Expect(Telescopes.TryInteract(me), "E takes the telescope");
        bool looking = await Until(() => TelescopeView.Current != null, 6);
        Expect(looking, "the server let me look through it");
        return looking;
    }

    /// <summary>
    /// A in the far-marker list on the Moon where A said it stands (B's eye 384,000 km off: 0.01° is 67 m), then the
    /// telescope locked on A from the menu: A's label in the middle of the eyepiece, on the Moon's disc.
    /// </summary>
    private async Task ReadMarker(GlobalPos told)
    {
        FarMarkers.Marker? Mark() => FarMarkers.Current.FirstOrDefault(m => m.At.Body == BodyId.Moon) is { Peer: not 0 } m ? m : null;
        bool listed = await Until(() => Mark() != null, 10);
        Expect(listed, "the far-marker list has A on the Moon");
        if (Mark() is not { } mark) return;
        double off = mark.At.DistanceTo(told);
        GD.Print(FormattableString.Invariant($"{Log} far marker {mark.Peer} on the {mark.At.Body}, {off:F1} m from where A says it stands"));
        Expect(off < 67, FormattableString.Invariant($"the marker is within 0.01° of A's place ({off:F1} m of 67)"));

        var view = TelescopeView.Current!;
        view.Zoom(60);   // the long end: the Moon's disc fills the eyepiece
        Expect(view.SelectPlayer(mark.Peer), "the telescope's menu locks on A");
        await Seconds(0.5);
        var labelled = view.Labels.FirstOrDefault(l => l.Peer == mark.Peer);
        Expect(labelled.Text != null, "A's label is in the eyepiece");
        if (labelled.Text == null) return;
        var size = GetViewport().GetVisibleRect().Size;
        float fromMiddle = labelled.Screen.DistanceTo(size / 2);
        GD.Print(FormattableString.Invariant($"{Log} label \"{labelled.Text}\" {fromMiddle:F1} px from the eyepiece's middle"));
        Expect(fromMiddle < 4, FormattableString.Invariant($"the lock holds A in the middle ({fromMiddle:F1} px)"));
        Expect(labelled.Text.Contains("Moon"), "the label names the Moon");
        await Picture("telescope");
        if (DayNight.Instance?.Sky?.Moon is { } moonDir)
        {
            double deg = Mathf.RadToDeg(labelled.Direction.AngleTo(moonDir.Normalized()));
            GD.Print(FormattableString.Invariant($"{Log} A's marker {deg:F3}° from the Moon's centre in the sky (its disc 0.26°)"));
            Expect(deg < 0.3, FormattableString.Invariant($"A's marker is on the Moon's disc ({deg:F3}°)"));
        }
    }

    // ---- both ----------------------------------------------------------------------------------------

    /// <summary>
    /// The fly camera to the Apollo 15 site (<c>/goto</c>, an admin's online), down to 3 m over the generated ground
    /// <paramref name="east"/> metres east of the site's point, then T: standing on the Moon.
    /// </summary>
    private async Task<bool> ToTheMoon(FootPlayer me, float east)
    {
        if (World is not { } world) { Fail("no client world"); return false; }
        Chat!.Send("/goto " + Site);
        if (!await Until(() => Origin?.Body == BodyId.Moon, 15)) { Fail("the server's /goto never moved my camera to the Moon"); return false; }
        if (!await Until(() => BodyGround.Active != null && CameraNode() != null, 10)) { Fail("no Moon ground (--bodies)"); return false; }
        var ground = BodyGround.Active!;
        var cam = CameraNode()!;
        var axes = Origin!.Frame.Axes!;
        var p = axes.ToEcef(cam.GlobalPosition.X + east, cam.GlobalPosition.Y, cam.GlobalPosition.Z);
        var n = ground.Heights.NormalUnder(p);
        var g = ground.Heights.Ground(n);
        var (wx, wy, wz) = axes.FromEcef(new EcefPos(g.X + n.X * 3, g.Y + n.Y * 3, g.Z + n.Z * 3));
        cam.GlobalPosition = new Vector3((float)wx, (float)wy, (float)wz);
        if (!await Until(() => ground.Covers(cam.GlobalPosition), 30)) { Fail("no near ground under the camera after 30 s"); return false; }
        world.ToggleMode();
        if (!await Until(() => Me is { } m && m.Origin?.Body == BodyId.Moon && m.IsOnFloor(), 30)) { Fail("never stood on the Moon"); return false; }
        bool published = await Until(() => Me!.NetGlobal.Body == BodyId.Moon, 3);
        GD.Print($"{Log} on the Moon: anchor body {Origin!.Body}, published {Me!.NetGlobal.Body}");
        Expect(published, "my published place is on the Moon");
        return true;
    }

    private Node3D? CameraNode() => GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false) as Node3D;

    /// <summary>
    /// The other's copy on the Moon, drawn where its owner says it is; this one walks 15 m and jumps while the other
    /// watches, then watches the other do the same: the gait, the speed and the jump seen on the remote peer.
    /// </summary>
    private async Task SeeOther(FootPlayer me)
    {
        bool there = await Until(() => OtherCopy() is { } o && o.NetGlobal.Body == BodyId.Moon && o.GlobalPosition.DistanceTo(me.GlobalPosition) < 100, 30);
        Expect(there, $"{Other}'s copy is here on the Moon");
        if (!there) return;
        var other = OtherCopy()!;
        float drawn = other.GlobalPosition.DistanceTo(Origin!.ToWorld(other.NetGlobal));
        GD.Print(FormattableString.Invariant($"{Log} {Other} drawn {drawn:F2} m from its published place, {other.GlobalPosition.DistanceTo(me.GlobalPosition):F1} m from me"));
        Expect(drawn < 1, FormattableString.Invariant($"{Other}'s copy within 1 m of its place ({drawn:F2} m)"));
        await Picture("other", other);

        // A moves first, then B (each watches the other)
        if (_role == "A") await Move(me);
        await Watch(other);
        if (_role == "B") await Move(me);
    }

    /// <summary>This player walks 15 m north, then jumps; says so.</summary>
    private async Task Move(FootPlayer me)
    {
        Say("walking");
        var start = me.GlobalPosition;
        await WalkUntil(me, new Vector3(0, 0, -1), () => me.GlobalPosition.DistanceTo(start) > 15, 20);
        await Seconds(0.5);
        Say("jumping");
        // the own player reads its keys (FootActions is a bot's seam): the jump action pressed, then let go
        Input.ActionPress(PlayerInput.Jump);
        bool left = await Until(() => !me.IsOnFloor(), 2);
        Input.ActionRelease(PlayerInput.Jump);
        Expect(left, "my jump left the ground");
        await Until(() => me.IsOnFloor(), 15);
        Say("moved");
    }

    /// <summary>The other's copy walking (its pose's ground speed, along its path), then rising past 2 m in its jump.</summary>
    private async Task Watch(FootPlayer other)
    {
        if (!await Heard(Other, "walking", 30)) { Fail($"{Other} never walked"); return; }
        float maxSpeed = 0, worst = 0;
        bool walked = await Until(() =>
        {
            if (!IsInstanceValid(other)) return false;
            maxSpeed = Mathf.Max(maxSpeed, other.Anim.X);
            worst = Mathf.Max(worst, other.GlobalPosition.DistanceTo(Origin!.ToWorld(other.NetGlobal)));
            return maxSpeed > 1f;
        }, 20);
        GD.Print(FormattableString.Invariant($"{Log} {Other} walks: pose speed {maxSpeed:F2} m/s, drawn at most {worst:F2} m from its place"));
        Expect(walked, $"{Other}'s copy walks (its gait's ground speed)");
        Expect(worst < 1, FormattableString.Invariant($"{Other}'s walking copy within 1 m of its place ({worst:F2} m)"));
        if (!await Heard(Other, "jumping", 30)) { Fail($"{Other} never jumped"); return; }
        // the anchor's up: the Moon's local vertical at the site (the frame is an ENU one there)
        var up = Vector3.Up;
        float from = other.GlobalPosition.Dot(up), rise = 0;
        bool rose = await Until(() =>
        {
            if (!IsInstanceValid(other)) return false;
            rise = Mathf.Max(rise, other.GlobalPosition.Dot(up) - from);
            return rise > 2f;
        }, 10);
        GD.Print(FormattableString.Invariant($"{Log} {Other} jumps: its copy rose {rise:F2} m"));
        Expect(rose, FormattableString.Invariant($"{Other}'s copy rises in its jump on the Moon ({rise:F2} m)"));
        await Heard(Other, "moved", 30);
    }

    /// <summary>The server's bots on the Moon: their copies here, on the Moon, standing on its ground.</summary>
    private async Task SeeBots(FootPlayer me)
    {
        bool two = await Until(() => BotCopies().Count(b => b.NetGlobal.Body == BodyId.Moon) >= 2, 40);
        Expect(two, "two bots' copies on the Moon here");
        if (!two || BodyGround.Active is not { } ground) return;
        // stood: each a moment on its ground (the server's collision is the clients' near square)
        bool stood = await UntilHeld(() => BotCopies().Where(b => b.NetGlobal.Body == BodyId.Moon).All(b => Over(ground, b) is > -0.5 and < 1.0), 2, 40);
        foreach (var b in BotCopies().Where(b => b.NetGlobal.Body == BodyId.Moon))
            GD.Print(FormattableString.Invariant($"{Log} bot {b.Name} {Over(ground, b):F2} m over the Moon's ground, {b.GlobalPosition.DistanceTo(me.GlobalPosition):F0} m from me"));
        Expect(stood, "the bots stand on the Moon's ground");
        if (stood) await Picture("bots", BotCopies().Where(b => b.NetGlobal.Body == BodyId.Moon).OrderBy(b => b.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault());
    }

    /// <summary>A body's feet over the ground's heights under it, metres.</summary>
    private double Over(BodyGround ground, FootPlayer b)
    {
        var axes = Origin!.Frame.Axes!;
        var p = axes.ToEcef(b.GlobalPosition.X, b.GlobalPosition.Y, b.GlobalPosition.Z);
        var n = ground.Heights.NormalUnder(p);
        return ground.Body.Shape.ToGeodetic(p).H - ground.Heights.At(n);
    }
}
