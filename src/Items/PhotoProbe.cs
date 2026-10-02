using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--photocheck</c> (with <c>--ride foot,&lt;s&gt; --view first --photo-dir &lt;abs&gt;</c>): the Polaroid
/// offline, through the real item paths, with screenshots in <c>test_output/</c>. Checks the camera
/// does not shoot at rest (only through the viewfinder); takes a photo at the eye and lowers the
/// camera at once (the print slides out of it: <c>photocheck_develop_3d.png</c>) and one kept at
/// the eye (the card at the bottom of the screen: <c>photocheck_develop_ui.png</c>); checks both became
/// Photo items whose id is the hash of a stored JPEG with a sidecar; opens the album
/// (<c>photocheck_album.png</c>) and a print (<c>photocheck_inspect.png</c>); sticks one on the
/// ground with the ghost showing (<c>photocheck_ghost.png</c>, <c>photocheck_stuck.png</c>) and
/// takes it back with an empty hand; puts one on a wall in front through the API: a poster
/// (<c>photocheck_poster.png</c>). Scratch inventory; the offline placed list ends as it began.
/// </summary>
public partial class PhotoProbe : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--photocheck");

    public PhotoProbe(ItemController items) : base(items, "photocheck", shots: "photocheck_") { }
    public PhotoProbe() : this(null!) { }

    private Inventory Inv => _items.Inventory;

    public override async void _Ready()
    {
        for (int i = 0; i < 900 && (_items.UsablePlayer is not { } p || !p.IsOnFloor()); i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_items.UsablePlayer is not { } me)
        {
            Fail("no player on foot");
            return;
        }
        await Seconds(1.5);
        GD.Print($"[photocheck] photo dir {PhotoStore.LocalDir}");

        // 0: at rest the camera does not shoot: the picture is what the viewfinder frames
        int cam = SlotOf(ItemId.Camera);
        Inv.Select(cam);
        me.LookPitch = 0.05f;
        await Seconds(0.8);
        _items.UseSlot(me, cam);
        Expect(!await Until(() => _items.Developing != null, 1.0), "no shot without the viewfinder up");

        // 1: at the eye, lowered right after: the print comes out of the bottom of the camera
        _items.ForceAim = true;
        await Seconds(1.2);
        _items.UseSlot(me, cam);
        _items.ForceAim = false;
        Expect(await Until(() => _items.Developing != null, 3), "the first print is developing");
        string first = _items.Developing ?? "";
        await Seconds(1.6);
        Shot("develop_3d");
        Expect(await Until(() => Photos().Contains(first), ItemController.DevelopSeconds + 2), "it became a Photo item");

        // 2: at the eye: the camera is hidden, the card rises at the bottom of the screen
        me.LookPitch = -0.1f;
        _items.ForceAim = true;
        await Seconds(1.2);
        _items.UseSlot(me, cam);
        Expect(await Until(() => _items.Developing != null, 3), "the second print is developing");
        string second = _items.Developing ?? "";
        await Seconds(1.4);
        Shot("develop_ui");
        _items.ForceAim = false;
        Expect(await Until(() => Photos().Contains(second), ItemController.DevelopSeconds + 2), "it became a Photo item too");
        Expect(first != second, "two different prints");

        foreach (var id in new[] { first, second })
        {
            var bytes = PhotoStore.Bytes(id);
            Expect(bytes != null && bytes.Length <= PhotoTransfer.MaxBytes && PhotoStore.IdOf(bytes) == id,
                $"{id}: stored, {bytes?.Length ?? 0} bytes, hash = id");
            Expect(PhotoStore.Meta(id) is { FocalMm: > 0 } m && m.Taken.Length > 0, $"{id}: sidecar ({PhotoStore.Caption(id)})");
        }

        // 3: the album, then one print large
        _items.Ui.Open();
        await Seconds(0.3);
        _items.PhotoUi.OpenAlbum();
        await Seconds(0.6);
        Expect(_items.PhotoUi.AlbumIds().Take(2).ToHashSet().SetEquals(new[] { first, second }), "the album lists both, pack first");
        Shot("album");
        _items.PhotoUi.CloseAlbum();
        _items.Ui.Close();
        await Seconds(0.3);

        int slot = Enumerable.Range(0, Inventory.Size).First(i => Inv[i].Data == first);
        Inv.Move(slot, Inv.Selected);   // into the hand
        await Seconds(0.3);
        _items.UseSlot(me, Inv.Selected);   // Use without Aim: look at it
        await Seconds(0.8);
        Expect(_items.PhotoUi.Viewing == first, "Use shows the print");
        Shot("inspect");
        var esc = new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true };
        Input.ParseInputEvent(esc);
        await Seconds(0.3);
        Expect(_items.PhotoUi.Viewing == null, "a key closes it");

        // 4: Aim: the ghost; Aim + Use: stuck on the ground in front
        var placed = PlacedObjects.Instance!;
        var before = placed.All.Keys.ToHashSet();
        me.LookPitch = -0.9f;
        _items.ForceAim = true;
        await Seconds(0.8);
        Expect(GetParent().FindChild("PhotoGhost", true, false) is MeshInstance3D { Visible: true }, "the ghost shows where it goes");
        Shot("ghost");
        _items.UseSlot(me, Inv.Selected);
        _items.ForceAim = false;
        Expect(await Until(() => placed.All.Values.Any(o => !before.Contains(o.Id) && o.Kind == PlacedKind.Photo && o.Payload == first), 3),
            "stuck: a placed Photo carrying its id");
        Expect(!Photos().Contains(first), "the print left the pack");
        var stuck = placed.All.Values.FirstOrDefault(o => !before.Contains(o.Id) && o.Payload == first);
        await Seconds(0.8);
        me.LookPitch = -0.6f;
        await Seconds(0.5);
        Shot("stuck");
        var card = stuck != null ? placed.GetNodeOrNull<MeshInstance3D>($"P{stuck.Id}/Card") : null;
        Expect(card?.MaterialOverride is StandardMaterial3D sm && sm.AlbedoTexture == PhotoStore.Texture(first),
            "the stuck card shows the print");

        // 5: an empty hand takes it back
        if (stuck != null)
        {
            _items.PickUpPhoto(stuck.Id);
            Expect(await Until(() => !placed.All.ContainsKey(stuck.Id) && Photos().Contains(first), 3), "taken back into the pack");
        }

        // 6: on a wall it is a poster: the second print, upright 1.8 m in front, facing us
        me.LookPitch = 0f;
        await Seconds(0.5);
        var fwd = -me.Camera.GlobalTransform.Basis.Z with { Y = 0 };
        fwd = fwd.LengthSquared() > 1e-4f ? fwd.Normalized() : Vector3.Forward;
        var wall = new Transform3D(Basis.LookingAt(fwd, Vector3.Up), me.Camera.GlobalPosition + fwd * 1.8f);
        PlacedResult? poster = null;
        placed.RequestPlace(PlacedKind.Photo, wall, second, r => poster = r);
        Expect(await Until(() => poster != null, 3) && poster!.Value.Ok, "the second print went on the wall");
        if (poster?.Object is { } onWall)
        {
            await Seconds(0.6);
            var mesh = placed.GetNodeOrNull<MeshInstance3D>($"P{onWall.Id}/Card");
            var size = mesh?.Mesh.GetAabb().Size ?? Vector3.Zero;
            Expect(mesh?.Mesh == PhotoVisuals.Poster && size.X > 0.5f && Mathf.Abs(size.Y / size.X - PhotoStore.CardSize.Y / PhotoStore.CardSize.X) < 0.01f,
                FormattableString.Invariant($"drawn as a poster, card aspect ({size.X:F2} x {size.Y:F2} m)"));
            Expect(mesh?.MaterialOverride is StandardMaterial3D pm && pm.AlbedoTexture == PhotoStore.Texture(second), "the poster shows the print");
            Shot("poster");
            PlacedResult? gone = null;
            placed.RequestRemove(onWall.Id, r => gone = r);
            Expect(await Until(() => gone != null, 3) && gone!.Value.Ok, "the poster came down");
        }

        await Finish(0.5);
    }

    private List<string> Photos() =>
        Enumerable.Range(0, Inventory.Size).Where(i => Inv[i].Id == ItemId.Photo && Inv[i].Data != null)
            .Select(i => Inv[i].Data!).ToList();

    protected override string Shot(string name)
    {
        string path = base.Shot(name);
        GD.Print($"{Log} screenshot test_output/photocheck_{name}.png");
        return path;
    }
}
