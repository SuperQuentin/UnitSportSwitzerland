using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--placedcheck A|B|C</c> with <c>--connect</c> (driven by <c>tools/placedcheck.sh</c>): held-item
/// events and placed objects between real clients, coordinated through chat lines.
/// <list type="bullet">
/// <item>A plants a flag through the real item path (Use on the flag slot, view pitched at the
/// ground), sticks a photo card through the API, is refused a flag 40 m away, then — once B is
/// in — fires the shotgun through the real item path and sends a camera flash.</item>
/// <item>B joins AFTER the flag is planted: it must have A's flag and photo from the join snapshot,
/// hear A's Shot and PhotoFlash, and be refused removing A's photo (owner-only). It saves a
/// screenshot to <c>test_output/placedcheck_b.png</c>. A's real Polaroid is also put upright on a
/// "wall" (through the API): B must draw it poster-sized with the image
/// (<c>test_output/placedcheck_b_poster.png</c>). A then removes its photos itself.</item>
/// <item>C runs against the RESTARTED server: A's flag must still be there; C pulls it up (anyone
/// may), which also cleans up.</item>
/// </list>
/// Scratch inventories. The server's list is the real <c>user://placed/server.json</c>.
/// </summary>
public partial class PlacedProbe : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--placedcheck");
            return i >= 0 && i + 1 < args.Length ? args[i + 1].ToUpperInvariant() : null;
        }
    }

    private readonly ItemController _items;
    private readonly List<string> _heard = new();
    private readonly List<ItemEvent> _events = new();
    private string _role = "";
    private int _failures;

    public PlacedProbe(ItemController items) => _items = items;
    public PlacedProbe() : this(null!) { }

    private ChatManager? Chat => GetParent().GetNodeOrNull<ChatManager>(ChatManager.NodeName);
    private FootPlayer? Me => GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ItemEvents.Received += e => { if (!e.Local) _events.Add(e); };
        if (!await Until(() => Chat != null && Permissions.Online && Me != null && Me.IsOnFloor()
                               && PlacedObjects.Instance != null && ItemEvents.Instance != null, 150))
        { Fail("no player on the ground"); return; }
        Chat!.LineReceived += (line, _) => _heard.Add(line);
        var me = Me!;
        var placed = PlacedObjects.Instance!;
        await Seconds(2.0);   // the join snapshot, and the server's copy of our position

        if (_role == "A") await RunA(me, placed);
        else if (_role == "B") await RunB(me, placed);
        else await RunC(placed);

        GD.Print(_failures == 0 ? $"[placedcheck {_role}] RESULT: ok" : $"[placedcheck {_role}] RESULT: FAILED ({_failures})");
        await Seconds(1.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task RunA(FootPlayer me, PlacedObjects placed)
    {
        // the real path: flag in hand, Use, with the view pitched down at the ground in front
        int flagSlot = SlotOf(ItemId.SwissFlag);
        if (flagSlot < 0) { _items.Inventory.Add(ItemId.SwissFlag, 1); flagSlot = SlotOf(ItemId.SwissFlag); }
        int flagsBefore = CountOf(ItemId.SwissFlag);
        var cam = me.Camera;
        me.LookPitch = -0.75f;
        await Seconds(0.3);
        var mine = placed.All.Keys.ToHashSet();
        _items.UseSlot(me, flagSlot);
        Expect(await Until(() => placed.All.Values.Any(o => !mine.Contains(o.Id) && o.Kind == PlacedKind.Flag), 5),
            "the server granted the flag planted through the item");
        Expect(CountOf(ItemId.SwissFlag) == flagsBefore - 1, "the flag left the pack");
        var flag = placed.All.Values.FirstOrDefault(o => !mine.Contains(o.Id) && o.Kind == PlacedKind.Flag);
        if (flag == null) { Fail("no flag to go on with"); return; }
        Expect(placed.GetNodeOrNull($"P{flag.Id}") != null, "the flag is drawn here");
        GD.Print(FormattableString.Invariant($"[placedcheck A] flag #{flag.Id} at LV95 {flag.E:F1}/{flag.N:F1}"));

        // a photo card on a pole in front, through the API (owner-only removal)
        PlacedResult? photo = null;
        placed.RequestPlace(PlacedKind.Photo, new Transform3D(Basis.Identity, me.GlobalPosition + Vector3.Up * 1.5f + me.GlobalTransform.Basis.Z * -1.5f),
            "probe-photo", r => photo = r);
        Expect(await Until(() => photo != null, 5) && photo!.Value.Ok, "the photo card was placed");

        // the server checks reach
        PlacedResult? far = null;
        placed.RequestPlace(PlacedKind.Flag, new Transform3D(Basis.Identity, me.GlobalPosition + new Vector3(40, 0, 0)), "", r => far = r);
        Expect(await Until(() => far != null, 5) && far!.Value.Refused == "Too far away.", $"a flag 40 m away is refused ({far?.Refused})");

        // a real Polaroid: taken, developed into the pack, stuck on the ground through the item
        // path, which uploads its image; B joins later and must fetch it by hash
        string? shot = await TakeAndStickPhoto(me, placed);

        Say($"planted {flag.Id} {photo?.Object?.Id ?? 0} {shot}");
        if (!await Heard("B", "ready", 150)) { Fail("B never joined"); return; }

        // the real path again: the shotgun, a shell spent, the event sent
        int gun = SlotOf(ItemId.Shotgun);
        if (gun < 0) { Fail("no shotgun in the scratch pack"); return; }
        if (SlotOf(ItemId.Shells) < 0) _items.Inventory.Add(ItemId.Shells, 5);
        for (int i = 0; i < 3; i++)
        {
            _items.UseSlot(me, gun);
            await Seconds(0.9);   // longer than the shotgun pump
        }
        var look = -cam.GlobalTransform.Basis.Z;
        ItemEvents.Instance!.Send(ItemEventKind.PhotoFlash, ItemEvents.MuzzleOf(me, look, 0.1f), look, "probe");
        Say("fired");

        if (!await Heard("B", "done", 60)) { Fail("B never finished"); return; }
        if (photo?.Object is { } card)
        {
            PlacedResult? gone = null;
            placed.RequestRemove(card.Id, r => gone = r);
            Expect(await Until(() => gone != null, 5) && gone!.Value.Ok, "the owner removed its own photo");
        }
        foreach (var wallPhoto in placed.All.Values.Where(o => o.Payload == shot && PhotoVisuals.IsWall(o.WorldTransform(placed.Origin))).ToList())
        {
            PlacedResult? down = null;
            placed.RequestRemove(wallPhoto.Id, r => down = r);
            Expect(await Until(() => down != null, 5) && down!.Value.Ok, "the owner took its poster down");
        }
        if (shot != null && placed.All.Values.FirstOrDefault(o => o.Payload == shot) is { } stuck)
        {
            _items.PickUpPhoto(stuck.Id);
            Expect(await Until(() => !placed.All.ContainsKey(stuck.Id) && SlotOfPhoto(shot) >= 0, 5),
                "the owner took its Polaroid back into the pack");
        }
    }

    /// <summary>A: camera Use, wait for the print, then Aim + Use it onto the ground in front. The photo id.</summary>
    private async Task<string?> TakeAndStickPhoto(FootPlayer me, PlacedObjects placed)
    {
        int cam = SlotOf(ItemId.Camera);
        if (cam < 0) { Expect(false, "a camera in the scratch pack"); return null; }
        me.LookPitch = 0.05f;
        _items.Inventory.Select(cam);
        _items.ForceAim = true;   // the camera shoots through its viewfinder only
        await Seconds(1.2);
        string? shot = null;
        _items.Printed += id => shot = id;
        _items.UseSlot(me, cam);
        _items.ForceAim = false;
        Expect(await Until(() => shot != null && SlotOfPhoto(shot) >= 0, ItemController.DevelopSeconds + 5),
            $"a photo was taken and developed into the pack ({shot})");
        if (shot == null || SlotOfPhoto(shot) < 0) return null;

        _items.Inventory.Move(SlotOfPhoto(shot), _items.Inventory.Selected);
        me.LookPitch = -0.9f;
        _items.ForceAim = true;
        await Seconds(0.9);   // longer than the shotgun pump
        _items.UseSlot(me, _items.Inventory.Selected);
        _items.ForceAim = false;
        Expect(await Until(() => placed.All.Values.Any(o => o.Kind == PlacedKind.Photo && o.Payload == shot), 5),
            "the Polaroid is stuck on the ground (item path, image uploaded)");
        GD.Print($"[placedcheck A] photo {shot}: {PhotoStore.Bytes(shot)?.Length ?? 0} bytes");

        // the same print upright, as if on a wall, 2 m to the side at eye height: a poster for B
        var side = me.GlobalTransform.Basis.X with { Y = 0 };
        var wall = new Transform3D(Basis.Identity, me.GlobalPosition + side.Normalized() * 2f + Vector3.Up * 1.6f);
        PlacedResult? poster = null;
        placed.RequestPlace(PlacedKind.Photo, wall, shot, r => poster = r);
        Expect(await Until(() => poster != null, 5) && poster!.Value.Ok, "the Polaroid also went up on a wall");
        var card = poster?.Object is { } p ? placed.GetNodeOrNull<MeshInstance3D>($"P{p.Id}/Card") : null;
        Expect(card?.Mesh == PhotoVisuals.Poster, "drawn as a poster here");
        return shot;
    }

    private async Task RunB(FootPlayer me, PlacedObjects placed)
    {
        // A said "planted" before this client even started: the join snapshot must carry it
        var flag = placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.Flag && o.Owner == "PlacedA");
        var photo = placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.Photo && o.Owner == "PlacedA");
        Expect(flag != null, "A's flag arrived with the join snapshot");
        Expect(photo != null, "A's photo arrived with the join snapshot");
        Expect(flag != null && placed.GetNodeOrNull($"P{flag.Id}") is Node3D, "A's flag is drawn here");

        // stand by the flag, facing it, so it is in the screenshot and A is in sight
        if (flag != null)
        {
            var at = placed.GetNodeOrNull<Node3D>($"P{flag.Id}")?.GlobalPosition ?? me.GlobalPosition;
            me.GlobalPosition = at + new Vector3(3.5f, 1.5f, 3.5f);
            me.Velocity = Vector3.Zero;
            me.RequestReplacement();
            await Until(() => me.IsOnFloor(), 10);
            me.LookPitch = -0.2f;
        }
        await Seconds(2.0);   // interest: the server must see us near A before A fires
        for (int tries = 0; tries < 30 && !_heard.Any(l => l.Contains("PC A fired")); tries++)
        {
            Say("ready");
            await Heard("A", "fired", 3);
        }
        Expect(await Until(() => _events.Count(e => e.Kind == ItemEventKind.Shot) >= 3, 10),
            $"heard A's three shots ({_events.Count(e => e.Kind == ItemEventKind.Shot)})");
        Expect(await Until(() => _events.Any(e => e.Kind == ItemEventKind.PhotoFlash), 5), "saw A's camera flash");
        Expect(_events.All(e => e.Peer != Multiplayer.GetUniqueId()), "no event of our own came back");

        if (photo != null)
        {
            PlacedResult? r = null;
            placed.RequestRemove(photo.Id, x => r = x);
            Expect(await Until(() => r != null, 5) && r!.Value.Refused == "That is not yours.",
                $"removing A's photo is refused ({r?.Refused})");
        }

        await Seconds(0.5);
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, "placedcheck_b.png"));

        // A's Polaroid: in the snapshot by id only; the image must come from the server, by hash
        var polaroid = placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.Photo && o.Owner == "PlacedA"
                                                             && PhotoStore.IsValidId(o.Payload)
                                                             && !PhotoVisuals.IsWall(o.WorldTransform(placed.Origin)));
        Expect(polaroid != null, "A's Polaroid arrived with the join snapshot");
        if (polaroid != null)
        {
            string id = polaroid.Payload;
            Expect(await Until(() => PhotoStore.Has(id), 15), $"its image {id} was fetched from the server");
            var bytes = PhotoStore.Bytes(id);
            Expect(bytes != null && PhotoStore.IdOf(bytes) == id, $"the bytes hash to the id ({bytes?.Length ?? 0} bytes)");
            Expect(PhotoStore.PathOf(id)?.StartsWith(PhotoStore.CacheDir) == true, "kept in the photo cache, not taken here");
            await Seconds(0.3);
            var card = placed.GetNodeOrNull<MeshInstance3D>($"P{polaroid.Id}/Card");
            Expect(card?.MaterialOverride is StandardMaterial3D sm && sm.AlbedoTexture != null && sm.AlbedoTexture != PhotoVisuals.Blank,
                "the stuck card shows the image, not the blank");

            // stand a metre back from it, looking down at it
            var at = polaroid.WorldTransform(placed.Origin).Origin;
            var fwd = -me.Camera.GlobalTransform.Basis.Z with { Y = 0 };
            fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
            me.GlobalPosition = at - fwd * 0.75f + Vector3.Up * 0.5f;
            me.Velocity = Vector3.Zero;
            me.RequestReplacement();
            await Until(() => me.IsOnFloor(), 10);
            me.LookPitch = -1.1f;
            await Seconds(1.0);
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, "placedcheck_b_photo.png"));
        }

        // the same print on a wall: drawn here poster-sized, with the image
        var wall = placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.Photo && o.Owner == "PlacedA"
                                                         && PhotoStore.IsValidId(o.Payload) && PhotoVisuals.IsWall(o.WorldTransform(placed.Origin)));
        Expect(wall != null, "A's wall Polaroid arrived");
        if (wall != null)
        {
            var poster = placed.GetNodeOrNull<MeshInstance3D>($"P{wall.Id}/Card");
            var size = poster?.Mesh.GetAabb().Size ?? Vector3.Zero;
            Expect(poster?.Mesh == PhotoVisuals.Poster && size.X > 0.5f, FormattableString.Invariant($"drawn as a poster ({size.X:F2} x {size.Y:F2} m)"));
            Expect(await Until(() => poster?.MaterialOverride is StandardMaterial3D sm && sm.AlbedoTexture != null
                                     && sm.AlbedoTexture != PhotoVisuals.Blank, 10), "the poster shows the image");
            // 2.5 m in front of it (it faces +Z), level, looking at it
            var at = wall.WorldTransform(placed.Origin);
            var face = at.Basis.Z with { Y = 0 };
            face = face.LengthSquared() > 1e-4f ? face.Normalized() : Vector3.Back;
            me.GlobalPosition = at.Origin + face * 2.5f + Vector3.Up * 0.3f;
            me.Velocity = Vector3.Zero;
            me.RequestReplacement();
            await Until(() => me.IsOnFloor(), 10);
            await Seconds(0.5);
            var to = at.Origin - me.Camera.GlobalPosition;   // look straight at its centre
            me.LookYaw = Mathf.Atan2(-to.X, -to.Z);
            me.LookPitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
            await Seconds(1.0);
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, "placedcheck_b_poster.png"));
        }
        Say("done");
    }

    private async Task RunC(PlacedObjects placed)
    {
        var flag = placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.Flag && o.Owner == "PlacedA");
        Expect(flag != null, "A's flag survived the server restart");
        Expect(!placed.All.Values.Any(o => o.Kind == PlacedKind.Photo && o.Owner == "PlacedA"), "A's removed photo stayed removed");
        if (flag == null) return;
        // anyone may pull a flag up; C walks over first (the server checks reach)
        var me = Me!;
        me.GlobalPosition = flag.WorldTransform(placed.Origin).Origin + new Vector3(1.5f, 1.5f, 0);
        me.Velocity = Vector3.Zero;
        me.RequestReplacement();
        await Until(() => me.IsOnFloor(), 10);
        await Seconds(1.5);
        PlacedResult? r = null;
        placed.RequestRemove(flag.Id, x => r = x);
        Expect(await Until(() => r != null, 5) && r!.Value.Ok, $"C (not the planter) pulled the flag up ({r?.Refused})");
    }

    private int SlotOf(ItemId id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id && !_items.Inventory[i].IsEmpty) return i;
        return -1;
    }

    private int SlotOfPhoto(string? id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == ItemId.Photo && _items.Inventory[i].Data == id) return i;
        return -1;
    }

    private int CountOf(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private void Say(string what)
    {
        GD.Print($"[placedcheck {_role}] say {what}");
        Chat?.Send($"PC {_role} {what}");
    }

    private Task<bool> Heard(string role, string what, double seconds) =>
        Until(() => _heard.Any(l => l.Contains($"PC {role} {what}")), seconds);

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
        GD.Print($"[placedcheck {_role}] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private void Fail(string why)
    {
        GD.Print($"[placedcheck {_role}] RESULT: FAILED — {why}");
        GetTree().Quit(1);
    }
}
