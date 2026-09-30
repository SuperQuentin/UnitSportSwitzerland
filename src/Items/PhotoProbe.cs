using Godot;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--photocheck</c> (with <c>--ride foot,&lt;s&gt; --view first --photo-dir &lt;abs&gt;</c>): the Polaroid
/// offline, through the real item paths, with screenshots in <c>test_output/</c>. Takes a photo at
/// rest (the print slides out of the camera: <c>photocheck_develop_3d.png</c>) and one at the eye
/// (the card at the bottom of the screen: <c>photocheck_develop_ui.png</c>); checks both became
/// Photo items whose id is the hash of a stored JPEG with a sidecar; opens the album
/// (<c>photocheck_album.png</c>) and a print (<c>photocheck_inspect.png</c>); sticks one on the
/// ground with the ghost showing (<c>photocheck_ghost.png</c>, <c>photocheck_stuck.png</c>) and
/// takes it back with an empty hand. Scratch inventory; the offline placed list ends as it began.
/// </summary>
public partial class PhotoProbe : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--photocheck") >= 0;

    private readonly ItemController _items;
    private int _failures;

    public PhotoProbe(ItemController items) => _items = items;
    public PhotoProbe() : this(null!) { }

    private Inventory Inv => _items.Inventory;

    public override async void _Ready()
    {
        for (int i = 0; i < 900 && (_items.UsablePlayer is not { } p || !p.IsOnFloor()); i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_items.UsablePlayer is not { } me)
        {
            GD.Print("[photocheck] RESULT: FAILED — no player on foot");
            GetTree().Quit(1);
            return;
        }
        await Seconds(1.5);
        GD.Print($"[photocheck] photo dir {PhotoStore.LocalDir}");

        // 1: a shot at rest: the print comes out of the bottom of the camera, in first person
        int cam = SlotOf(ItemId.Camera);
        Inv.Select(cam);
        me.LookPitch = 0.05f;
        await Seconds(0.8);
        _items.UseSlot(me, cam);
        Expect(await Until(() => _items.Developing != null, 3), "the first print is developing");
        string first = _items.Developing ?? "";
        await Seconds(1.6);
        Shot("photocheck_develop_3d.png");
        Expect(await Until(() => Photos().Contains(first), ItemController.DevelopSeconds + 2), "it became a Photo item");

        // 2: at the eye: the camera is hidden, the card rises at the bottom of the screen
        me.LookPitch = -0.1f;
        _items.ForceAim = true;
        await Seconds(1.2);
        _items.UseSlot(me, cam);
        Expect(await Until(() => _items.Developing != null, 3), "the second print is developing");
        string second = _items.Developing ?? "";
        await Seconds(1.4);
        Shot("photocheck_develop_ui.png");
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
        Shot("photocheck_album.png");
        _items.PhotoUi.CloseAlbum();
        _items.Ui.Close();
        await Seconds(0.3);

        int slot = Enumerable.Range(0, Inventory.Size).First(i => Inv[i].Data == first);
        Inv.Move(slot, Inv.Selected);   // into the hand
        await Seconds(0.3);
        _items.UseSlot(me, Inv.Selected);   // Use without Aim: look at it
        await Seconds(0.8);
        Expect(_items.PhotoUi.Viewing == first, "Use shows the print");
        Shot("photocheck_inspect.png");
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
        Shot("photocheck_ghost.png");
        _items.UseSlot(me, Inv.Selected);
        _items.ForceAim = false;
        Expect(await Until(() => placed.All.Values.Any(o => !before.Contains(o.Id) && o.Kind == PlacedKind.Photo && o.Payload == first), 3),
            "stuck: a placed Photo carrying its id");
        Expect(!Photos().Contains(first), "the print left the pack");
        var stuck = placed.All.Values.FirstOrDefault(o => !before.Contains(o.Id) && o.Payload == first);
        await Seconds(0.8);
        me.LookPitch = -0.6f;
        await Seconds(0.5);
        Shot("photocheck_stuck.png");
        var card = stuck != null ? placed.GetNodeOrNull<MeshInstance3D>($"P{stuck.Id}/Card") : null;
        Expect(card?.MaterialOverride is StandardMaterial3D sm && sm.AlbedoTexture == PhotoStore.Texture(first),
            "the stuck card shows the print");

        // 5: an empty hand takes it back
        if (stuck != null)
        {
            _items.PickUpPhoto(stuck.Id);
            Expect(await Until(() => !placed.All.ContainsKey(stuck.Id) && Photos().Contains(first), 3), "taken back into the pack");
        }

        GD.Print(_failures == 0 ? "[photocheck] RESULT: ok" : $"[photocheck] RESULT: FAILED ({_failures})");
        await Seconds(0.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private List<string> Photos() =>
        Enumerable.Range(0, Inventory.Size).Where(i => Inv[i].Id == ItemId.Photo && Inv[i].Data != null)
            .Select(i => Inv[i].Data!).ToList();

    private int SlotOf(ItemId id) => Enumerable.Range(0, Inventory.Size).First(i => Inv[i].Id == id);

    private void Shot(string name)
    {
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, name));
        GD.Print($"[photocheck] screenshot test_output/{name}");
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
        GD.Print($"[photocheck] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
