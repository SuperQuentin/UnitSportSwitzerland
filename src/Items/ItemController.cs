using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// What the item in hand <i>does</i>: the local player's inventory made to act on the world.
///
/// <para>
/// Owned by <see cref="ClientWorld"/>, not by the player node, for the same reason the ride
/// picker is: in multiplayer the player is spawned by the server and replaced on a reconnect,
/// while the inventory has to outlive that. The player is resolved every frame through
/// <see cref="ActivePlayer"/>, and everything this pushes onto it (FOV, look scale, the held item
/// id) is re-asserted each frame, so a fresh player node simply picks it up.
/// </para>
///
/// <para>
/// Items only work on foot. Mounted, the shoulder buttons are the trick and boost buttons and
/// the mouse buttons mean nothing to a bicycle; the hotbar is still shown, dimmed, so it is
/// obvious the items are there and why they do nothing.
/// </para>
/// </summary>
public partial class ItemController : Node
{
    internal const float PlaceReach = 6f;

    private readonly Inventory _inventory;
    private readonly WorldOrigin _origin;
    private InventoryUi _ui = null!;
    private SmartBinocularsHud _smart = null!;
    private FlagGhost _flagGhost = null!;
    private AudioStreamPlayer _sfx = null!;
    private bool _capturing;
    private bool _forceAim;
    private bool _wasKnockedOut;

    // camera zoom: 35 mm-equivalent focal length, kept across aims; wheel / D-pad change it while aiming
    private const float FocalMin = 24f, FocalMax = 200f, ZoomStep = 1.12f;
    private float _focalMm = 35f;
    private bool _aimingPhoto;

    // the Polaroid: the print coming out and developing (DevelopSeconds), then the Photo item
    public const float DevelopSeconds = 3f;
    private PhotoUi _photoUi = null!;
    private string? _developing;
    private float _developT;
    private ShaderMaterial? _develop3D;
    private MeshInstance3D? _ghost;

    /// <summary>Vertical FOV in degrees of a 35 mm-equivalent focal length (35 mm is about 38 degrees).</summary>
    public static float FovFromFocal(float mm) => Mathf.RadToDeg(2f * Mathf.Atan(12f / mm));

    /// <summary>Fires the held gun (a shell already taken); set by the bird hunt, <c>Birds.BirdLife</c>.</summary>
    public Action<FootPlayer>? Fire { get; set; }

    /// <summary>Resolved per frame, never captured: the local on-foot player, or null (fly camera, replay).</summary>
    public Func<FootPlayer?>? ActivePlayer { get; set; }

    public Inventory Inventory => _inventory;
    public InventoryUi Ui => _ui;
    public PhotoUi PhotoUi => _photoUi;

    /// <summary>Holds Aim down as if pressed ("--aim", and the photo probes).</summary>
    public bool ForceAim { get => _forceAim; set => _forceAim = value; }

    /// <summary>The photo id being developed right now (it becomes an item when done), or null.</summary>
    public string? Developing => _developing;

    /// <summary>Raised when a developed print goes into the pack (or would not fit): the photo id.</summary>
    public event Action<string>? Printed;

    public ItemController(Inventory inventory, WorldOrigin origin)
    {
        _inventory = inventory;
        _origin = origin;
    }

    public ItemController() : this(new Inventory(), WorldOrigin.SwissDefault()) { }

    public override void _Ready()
    {
        Name = "Items";
        _sfx = new AudioStreamPlayer { Name = "Sfx", VolumeDb = -8f, Bus = SfxBus.Name };
        AddChild(_sfx);
        _ui = new InventoryUi(this) { Name = "InventoryUi" };
        AddChild(_ui);
        _photoUi = new PhotoUi(this) { Name = "PhotoUi" };
        AddChild(_photoUi);
        _smart = new SmartBinocularsHud();
        AddChild(_smart);
        _flagGhost = new FlagGhost { Name = "FlagGhost" };
        AddChild(_flagGhost);

        _inventory.Changed += () => _ui.Refresh();

        // "--hold <item>" puts that item in the hand, for screenshotting the viewmodel
        var args = OS.GetCmdlineUserArgs();
        _forceAim = Array.IndexOf(args, "--aim") >= 0;   // and "--aim" holds Aim down
        int gi = Array.IndexOf(args, "--give");   // "--give <item>": a dev flag, puts one in hotbar slot 1 (for screenshots)
        if (gi >= 0 && gi + 1 < args.Length && Enum.TryParse<ItemId>(args[gi + 1], true, out var give) && !_inventory.Contains(give))
            _inventory.Put(0, new ItemStack(give, 1));   // hotbar slot 1, so --hold finds it
        int at = Array.IndexOf(args, "--hold");
        if (at >= 0 && at + 1 < args.Length && Enum.TryParse<ItemId>(args[at + 1], true, out var hold))
            for (int i = 0; i < Inventory.HotbarSize; i++)
                if (_inventory[i].Id == hold) _inventory.Select(i);
        int zi = Array.IndexOf(args, "--zoom");   // "--zoom <mm>" starts the camera at that focal length
        if (zi >= 0 && zi + 1 < args.Length
            && float.TryParse(args[zi + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var zmm))
            _focalMm = Mathf.Clamp(zmm, FocalMin, FocalMax);
    }

    /// <summary>The player if items can be used right now: on foot, on screen, not in a menu.</summary>
    public FootPlayer? UsablePlayer
    {
        get
        {
            var p = CurrentPlayer();
            return p is { IsViewing: true } && p.Ride == RideKind.OnFoot ? p : null;
        }
    }

    /// <summary>
    /// The resolver's answer, else whichever player owns the camera on screen — only the local
    /// player ever has one, and that also covers bodies made outside <see cref="ClientWorld"/>
    /// (the probes).
    /// </summary>
    private FootPlayer? CurrentPlayer() =>
        ActivePlayer?.Invoke() ?? GetViewport().GetCamera3D()?.GetParent() as FootPlayer;

    public override void _Process(double delta)
    {
        var player = CurrentPlayer();
        _ui.PlayerPresent = player is { IsViewing: true };
        _ui.ItemsActive = UsablePlayer != null;
        UpdateDevelop((float)delta);
        if (player == null)
        {
            ShowGhost(null);
            return;
        }

        // cash you carry is lost when you go down; what you claimed to the account is not
        if (player.KnockedOut && !_wasKnockedOut && _inventory.Cash > 0)
        {
            int lost = _inventory.Cash;
            _inventory.TakeCash(lost);
            _ui.Toast($"You dropped {lost} CHF you had not claimed.");
        }
        _wasKnockedOut = player.KnockedOut;

        player.HeldItemId = (int)_inventory.HeldId;
        var visual = player.GetNodeOrNull<HeldItemVisual>("HeldItem");
        if (visual != null) visual.HeldData = _inventory.Held.Data;

        var def = ItemDefs.Get(_inventory.HeldId);
        bool usable = UsablePlayer != null;
        bool picking = _smart.PickerOpen && _inventory.HeldId == ItemId.SmartBinoculars;   // stays raised while a target is picked
        bool aiming = usable && (!UiFocus.TextEntryActive || picking)
                      && (PlayerInput.Held(PlayerInput.AimItem) || _forceAim || picking)
                      && def?.Use is ItemUse.Optic or ItemUse.Photo or ItemUse.Shoot;

        // everything pushed onto the player is re-asserted every frame, so letting go of Aim,
        // switching item or getting on a bike all fall back to normal without a special case
        _aimingPhoto = aiming && def!.Use == ItemUse.Photo;
        _ui.PhotoFocalMm = _focalMm;
        player.FovOverride = aiming ? def!.Use switch { ItemUse.Optic => 9f, ItemUse.Photo => FovFromFocal(_focalMm), _ => 50f } : null;
        player.ScopeView = aiming;
        player.ItemAction = _planting ? 2 : aiming ? 1 : 0;   // replicated: remote peers pose the arms from it
        player.LookScale = aiming ? def!.Use switch { ItemUse.Optic => 0.2f, ItemUse.Photo => Mathf.Clamp(FovFromFocal(_focalMm) / 76f, 0.04f, 1f), _ => 0.6f } : 1f;
        // held items stay visible while aiming: they are raised to a pose. Binoculars and the
        // camera hide once at the eye (you look through them: the overlay is the view).
        bool poseSettled = visual?.PoseSettled ?? true;
        // a photo in hand with Aim held: a ghost where it would stick (the print stays low, out of the way)
        bool sticking = usable && !UiFocus.TextEntryActive && def?.Use == ItemUse.Print
                        && (PlayerInput.Held(PlayerInput.AimItem) || _forceAim);
        ShowGhost(sticking ? StickTarget(player).At : null);
        _flagGhost.Step(player, usable && !_planting && _inventory.HeldId == ItemId.SwissFlag);
        if (visual != null)
        {
            visual.SetPose(_raiseFlag ? ViewPose.Raise : !aiming ? ViewPose.Rest : def!.Use switch
            {
                ItemUse.Shoot => ViewPose.Aim,
                _ => ViewPose.Eye,
            });
            visual.Suppressed = _capturing || (aiming && def!.Use is (ItemUse.Optic or ItemUse.Photo) && poseSettled);
        }

        // the viewfinder / binocular overlay appears once the item has been raised
        _ui.Scope = aiming && (def!.Use == ItemUse.Shoot || poseSettled) ? def.Use : null;
        _smart.Held = usable && _inventory.HeldId == ItemId.SmartBinoculars;
        _smart.Active = _smart.Held && _ui.Scope == ItemUse.Optic;
        _ui.Readout = usable && def?.Use == ItemUse.Readout ? GpsReadout(player) : null;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (UiFocus.TextEntryActive || !e.IsPressed() || e.IsEcho()) return;
        if (UsablePlayer is not { } player) return;

        // a click that is not aimed at the world (a released pointer over a menu) is not a use
        if (e is InputEventMouseButton && Input.MouseMode != Input.MouseModeEnum.Captured) return;

        if (e.IsActionPressed(PlayerInput.UseItem))
        {
            UseHeld(player);
            GetViewport().SetInputAsHandled();
        }
        else if (_aimingPhoto && (e.IsActionPressed(PlayerInput.NextItem) || e.IsActionPressed(PlayerInput.PrevItem)))
        {
            // aiming the camera, the wheel / D-pad zoom instead of cycling the hotbar: wheel up = in
            bool next = e.IsActionPressed(PlayerInput.NextItem);
            bool pad = e is InputEventJoypadButton;
            if (pad && next && _focalMm >= FocalMax - 0.5f) _focalMm = FocalMin;   // the pad has one key: wrap
            else _focalMm = Mathf.Clamp(_focalMm * (next == pad ? ZoomStep : 1f / ZoomStep), FocalMin, FocalMax);
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed(PlayerInput.NextItem))
        {
            _inventory.Cycle(+1);
            Click();
            GetViewport().SetInputAsHandled();
        }
        else if (e.IsActionPressed(PlayerInput.PrevItem))
        {
            _inventory.Cycle(-1);
            Click();
            GetViewport().SetInputAsHandled();
        }
        else if (e is InputEventKey key)
        {
            // physical 1..6, so the hotbar keys are the same keys on AZERTY
            int slot = (int)key.PhysicalKeycode - (int)Key.Key1;
            if (slot < 0 || slot >= Inventory.HotbarSize) return;
            _inventory.Select(slot);
            Click();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Click() => Play(SfxSynth.Tick, 1.4f);

    private void Play(AudioStream stream, float pitch = 1f)
    {
        _sfx.Stream = stream;
        _sfx.PitchScale = pitch;
        _sfx.Play();
    }

    // ------------------------------------------------------------------------------------
    // using things
    // ------------------------------------------------------------------------------------

    private void UseHeld(FootPlayer player)
    {
        // an empty hand takes back a photo of yours you are looking at
        if (_inventory.Held.IsEmpty)
        {
            if (StickTarget(player).PhotoId is long id) PickUpPhoto(id);
            return;
        }
        UseSlot(player, _inventory.Selected);
    }

    /// <summary>Uses whatever is in <paramref name="slot"/>; the inventory panel calls this for "Use" on any slot.</summary>
    public void UseSlot(FootPlayer? player, int slot)
    {
        player ??= UsablePlayer;
        var stack = _inventory[slot];
        if (player == null || stack.IsEmpty || ItemDefs.Get(stack.Id) is not { } def) return;

        switch (def.Use)
        {
            case ItemUse.Consume:
                if (player.Heal(def.Heal))
                {
                    _inventory.TakeOne(slot);
                    Kick(player);
                    Play(SfxSynth.Chime, 1.2f);
                    _ui.Toast($"{def.Name}: +{def.Heal:F0} health");
                }
                else _ui.Toast("Already at full health.");
                break;

            case ItemUse.Photo:
                if (!_capturing) TakePhoto(player);
                break;

            case ItemUse.Place:
                PlaceOrPickUpFlag(player, slot);
                break;

            case ItemUse.Optic when stack.Id == ItemId.SmartBinoculars:
                if (_smart.Active) _smart.OpenPicker();
                else _ui.Toast(InputHints.Format("Hold Aim ({aim_item}), then {use_item} picks the target item."));
                break;

            case ItemUse.Optic:
                _ui.Toast(InputHints.Format("Hold Aim ({aim_item}) to look through them."));
                break;

            case ItemUse.Readout:
                break;

            case ItemUse.Shoot:
            {
                // the action has to be pumped before the next shell: no firing until it has cycled
                if (Time.GetTicksMsec() < _nextShotMs) break;
                int shells = -1;
                for (int i = 0; i < Inventory.Size && shells < 0; i++)
                    if (_inventory[i].Id == ItemId.Shells && !_inventory[i].IsEmpty) shells = i;
                if (shells < 0)
                {
                    Play(SfxSynth.Tick, 0.5f);
                    _ui.Toast("Out of shells.");
                    break;
                }
                _inventory.TakeOne(shells);
                _nextShotMs = Time.GetTicksMsec() + (ulong)((HeldItemVisual.PumpDelay + HeldItemVisual.PumpTime + 0.1f) * 1000f);
                Recoil(player);
                Fire?.Invoke(player);
                break;
            }

            case ItemUse.Wear:
                bool on = _inventory.Worn != stack.Id;
                _inventory.SetWorn(on ? stack.Id : ItemId.None);
                Play(SfxSynth.Tick, on ? 1.2f : 0.9f);
                _ui.Toast(on ? $"You put on the {def.Name.ToLowerInvariant()}." : $"You take off the {def.Name.ToLowerInvariant()}.");
                break;

            case ItemUse.Material:
                _ui.Toast($"{def.Name}: keep it for trading or building.");
                break;

            case ItemUse.Print:
                // Aim + Use sticks it (or takes back a stuck one); Use alone looks at it
                if (slot == _inventory.Selected && !_ui.IsOpen
                    && (PlayerInput.Held(PlayerInput.AimItem) || _forceAim))
                    StickOrPickUpPhoto(player, slot);
                else
                {
                    if (slot == _inventory.Selected && player.GetNodeOrNull<HeldItemVisual>("HeldItem") is { } hv)
                        hv.PlayOneShot(ViewPose.Inspect, 0.25f, 0.6f, 0.3f);
                    _photoUi.Inspect(stack.Data);
                }
                break;
        }
    }

    private ulong _nextShotMs;

    /// <summary>A shotgun's kick: the viewmodel jolts, the view punches up a few degrees, the action cycles.</summary>
    private static void Recoil(FootPlayer player)
    {
        if (player.GetNodeOrNull<HeldItemVisual>("HeldItem") is { } v)
        {
            v.Kick = 1f;
            v.Recoil = 1f;
            v.Pump();
        }
        player.Punch(Mathf.DegToRad(4.5f));
    }

    private static void Kick(FootPlayer player)
    {
        if (player.GetNodeOrNull<HeldItemVisual>("HeldItem") is { } v) v.Kick = 1f;
    }

    /// <summary>
    /// Saves the frame as it is on screen, minus the inventory UI and the item itself, to
    /// <c>user://photos/</c>. Waits for <see cref="RenderingServer.FramePostDraw"/>: reading the
    /// viewport from <c>_Process</c> returns whatever the render thread last left there, which is
    /// the frame <i>before</i> the UI was hidden (the exporter learned this the hard way).
    /// </summary>
    private async void TakePhoto(FootPlayer player)
    {
        _capturing = true;
        _ui.Visible = false;
        if (player.GetNodeOrNull<HeldItemVisual>("HeldItem") is { } v) v.Suppressed = true;

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        string path = "";
        string? photo = null;
        try
        {
            var image = GetViewport().GetTexture().GetImage();
            DirAccess.MakeDirRecursiveAbsolute("user://photos");
            path = $"user://photos/photo_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            image.SavePng(path);
            // the Polaroid print: square crop on the white card, named by its hash, with where and when
            if (IsInstanceValid(player))
            {
                var (e, n) = _origin.ToLv95(player.GlobalPosition);
                photo = PhotoStore.Save(image, e, n, player.GlobalPosition.Y, _focalMm, path);
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"[items] photo failed: {e.Message}");
        }

        _ui.Visible = true;
        _capturing = false;
        if (!IsInstanceValid(player)) return;
        // the flash others see (and a light pulse here) — after the capture, not in it
        var look = -player.Camera.GlobalTransform.Basis.Z;
        ItemEvents.Instance?.Send(ItemEventKind.PhotoFlash, ItemEvents.MuzzleOf(player, look, 0.1f), look, path);
        Kick(player);
        Play(SfxSynth.Tick, 0.6f);
        _ui.Flash();
        if (photo == null)
        {
            _ui.Toast("Photo failed.");
            return;
        }
        // two frames for the camera hidden during the capture to be drawn again, so the print
        // knows whether it can come out of it
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (IsInstanceValid(player)) StartDevelop(player, photo);
    }

    // ------------------------------------------------------------------------------------
    // the Polaroid: printing, sticking, taking back
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The print comes out: from the bottom of the camera when it is on screen in first person,
    /// else as a card rising at the bottom of the screen (the camera is at the eye then). It
    /// develops over <see cref="DevelopSeconds"/>, then goes in the pack. Film is unlimited.
    /// </summary>
    private void StartDevelop(FootPlayer player, string photo)
    {
        if (_developing != null) FinishDevelop();   // a quick second shot: the first is done
        var tex = PhotoStore.Texture(photo) ?? PhotoVisuals.Blank;
        _developing = photo;
        _developT = 0f;
        _develop3D = PhotoVisuals.Developing3D(tex);
        var visual = player.GetNodeOrNull<HeldItemVisual>("HeldItem");
        if (visual == null || !visual.ShowPrint(_develop3D)) _photoUi.StartDevelop(tex);
        Play(SfxSynth.Whoosh, 2.2f);   // the motor pushing the print out
    }

    private void UpdateDevelop(float dt)
    {
        if (_developing == null) return;
        _developT += dt;
        float t = Mathf.Clamp(_developT / DevelopSeconds, 0f, 1f);
        _develop3D?.SetShaderParameter("develop", t);
        _photoUi.SetDevelop(t);
        if (_developT >= DevelopSeconds + 0.3f) FinishDevelop();
    }

    private void FinishDevelop()
    {
        if (_developing is not { } photo) return;
        _developing = null;
        _photoUi.EndDevelop();
        if (CurrentPlayer()?.GetNodeOrNull<HeldItemVisual>("HeldItem") is { } v) v.HidePrint();
        if (_inventory.Add(new ItemStack(ItemId.Photo, 1, photo)) > 0)
            _ui.Toast("Pack full: the photo is only in your album.");
        else
        {
            Play(SfxSynth.Chime, 1.5f);
            _ui.Toast("Photo developed: in your pack.");
        }
        Printed?.Invoke(photo);
    }

    /// <summary>What Aim + Use would do with a photo right now: stick it at <c>At</c>, or take back placed photo <c>PhotoId</c>.</summary>
    private (Transform3D? At, long? PhotoId, string? Why) StickTarget(FootPlayer player)
    {
        var camera = player.Camera;
        var from = camera.GlobalPosition;
        var forward = -camera.GlobalTransform.Basis.Z;
        float reach = PlaceReach + from.DistanceTo(player.GlobalPosition + Vector3.Up * 1.6f);
        var query = PhysicsRayQueryParameters3D.Create(from, from + forward * reach,
            uint.MaxValue, new Godot.Collections.Array<Rid> { player.GetRid() });
        var hit = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0) return (null, null, "Nothing in reach to stick it on.");
        if (PlacedObjects.IdOf(hit["collider"].AsGodotObject() as Node) is long id
            && PlacedObjects.Instance?.All.TryGetValue(id, out var o) == true && o.Kind == PlacedKind.Photo)
            return (null, id, null);
        var point = hit["position"].AsVector3();
        if (point.DistanceTo(player.GlobalPosition) > PlaceReach) return (null, null, "Too far away.");
        return (PhotoVisuals.StickTransform(point, hit["normal"].AsVector3(), forward), null, null);
    }

    /// <summary>Aim + Use with a photo: sticks it where you look, or takes back the stuck photo you look at.</summary>
    public void StickOrPickUpPhoto(FootPlayer player, int slot)
    {
        if (PlacedObjects.Instance is not { } placed) return;
        var target = StickTarget(player);
        if (target.PhotoId is long id)
        {
            PickUpPhoto(id);
            return;
        }
        if (target.At is not { } at)
        {
            _ui.Toast(target.Why ?? "Cannot stick it there.");
            return;
        }
        var stack = _inventory[slot];
        if (stack.Id != ItemId.Photo || !PhotoStore.IsValidId(stack.Data) || !PhotoStore.Has(stack.Data!))
        {
            _ui.Toast("This print has no image to stick.");
            return;
        }
        // the image goes to the server first, so the others can fetch it once the photo is up
        PhotoTransfer.Instance?.Upload(stack.Data!);
        _inventory.TakeOne(slot);
        Kick(player);
        placed.RequestPlace(PlacedKind.Photo, at, stack.Data!, r =>
        {
            if (!r.Ok)
            {
                _inventory.Add(stack with { Count = 1 });   // refused: the print comes back
                _ui.Toast($"Cannot stick it here: {r.Refused}");
                return;
            }
            Play(SfxSynth.Tick, 1.8f);
            _ui.Toast("Photo stuck. Use on it with an empty hand to take it back.");
        });
    }

    /// <summary>Takes a stuck photo back into the pack (the server only lets its owner).</summary>
    public void PickUpPhoto(long id)
    {
        if (PlacedObjects.Instance is not { } placed || !placed.All.TryGetValue(id, out var o)) return;
        if (_inventory.Room(ItemId.Photo, o.Payload) < 1)
        {
            _ui.Toast("No room in your pack.");
            return;
        }
        placed.RequestRemove(id, r =>
        {
            if (!r.Ok)
            {
                _ui.Toast($"Cannot take it: {r.Refused}");
                return;
            }
            if (_inventory.Add(new ItemStack(ItemId.Photo, 1, o.Payload)) > 0) _ui.Toast("No room in your pack: the photo is lost.");
            else _ui.Toast("Photo taken back.");
            Play(SfxSynth.Whoosh, 1.6f);
        });
    }

    /// <summary>A see-through card where the held photo would stick, while Aim is held; null hides it.</summary>
    private void ShowGhost(Transform3D? at)
    {
        if (at == null)
        {
            if (_ghost != null && IsInstanceValid(_ghost)) _ghost.Visible = false;
            return;
        }
        if (_ghost == null || !IsInstanceValid(_ghost))
        {
            _ghost = new MeshInstance3D
            {
                Name = "PhotoGhost", Mesh = PhotoVisuals.Card, TopLevel = true,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_ghost);
        }
        var tex = PhotoStore.Texture(_inventory.Held.Data) ?? PhotoVisuals.Blank;
        if (_ghost.MaterialOverride is not StandardMaterial3D m || m.AlbedoTexture != tex)
            _ghost.MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = tex, AlbedoColor = new Color(1, 1, 1, 0.55f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
        _ghost.GlobalTransform = at.Value;
        _ghost.Visible = true;
    }

    /// <summary>
    /// Plants a flag where the view meets the ground (the ghost shows the spot), or — if the view
    /// meets a planted flag — takes it back. Placing only on ground flat enough to stand on. Both are
    /// a short stroke: the flag is raised and stabbed down (the request goes out at the stab), or
    /// reached down for and pulled up. Planted flags are <see cref="PlacedObjects"/>: the server
    /// keeps and saves them, so the flag leaves the pack at the stab and comes back if refused.
    /// </summary>
    private async void PlaceOrPickUpFlag(FootPlayer player, int slot)
    {
        if (_planting || PlacedObjects.Instance is not { } placed) return;
        var aim = FlagGhost.Aim(player);
        if (aim.Kind == FlagAimKind.None)
        {
            _ui.Toast("Nothing in reach to plant it in.");
            return;
        }

        if (aim.Kind == FlagAimKind.PickUp)
        {
            if (_inventory.Room(ItemId.SwissFlag) < 1)
            {
                _ui.Toast("No room in your pack.");
                return;
            }
            await Stroke(player, raise: false, () => placed.RequestRemove(aim.Id, r =>
            {
                if (!r.Ok)
                {
                    _ui.Toast($"Cannot pick it up: {r.Refused}");
                    return;
                }
                if (_inventory.Add(ItemId.SwissFlag, 1) > 0) _ui.Toast("No room in your pack: the flag is lost.");
                else _ui.Toast("Flag picked up.");
                Play(SfxSynth.Whoosh, 1.3f);
            }));
            return;
        }

        if (!aim.Valid)
        {
            _ui.Toast(aim.Reason);
            return;
        }

        var at = new Transform3D(new Basis(Vector3.Up, aim.Yaw), aim.Point);
        await Stroke(player, raise: true, () =>
        {
            if (_inventory[slot].Id != ItemId.SwissFlag) return;   // swapped away during the raise
            _inventory.TakeOne(slot);
            Kick(player);
            placed.RequestPlace(PlacedKind.Flag, at, "", r =>
            {
                if (!r.Ok)
                {
                    _inventory.Add(ItemId.SwissFlag, 1);   // the server said no: the flag comes back
                    _ui.Toast($"Cannot plant it here: {r.Refused}");
                    return;
                }
                _ui.Toast("Flag planted.");
            });
        });
    }

    private bool _planting, _raiseFlag;

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    /// <summary>
    /// The plant / pull-up stroke, timed here rather than in the viewmodel (which does not run in third
    /// person): <c>ItemAction = 2</c> for its whole length makes every peer pose the Plant arms.
    /// <paramref name="raise"/>: lift the flag, then stab (<paramref name="onPeak"/> at the stab);
    /// otherwise reach down and pull up (<paramref name="onPeak"/> when the hands close on the pole).
    /// </summary>
    private async Task Stroke(FootPlayer player, bool raise, Action onPeak)
    {
        _planting = true;
        try
        {
            var visual = player.GetNodeOrNull<HeldItemVisual>("HeldItem");
            if (raise)
            {
                _raiseFlag = true;
                await Wait(0.45);   // long enough for remote peers to ease into the Plant arms before the stab
                if (!IsInstanceValid(player)) return;
                visual?.PlayOneShot(ViewPose.Plant, 0.12f, 0.1f, 0.3f);
                await Wait(0.12);
                _raiseFlag = false;
            }
            else
            {
                visual?.PlayOneShot(ViewPose.Plant, 0.25f, 0.05f, 0.3f);
                await Wait(0.25);
            }
            onPeak();
            await Wait(raise ? 0.4 : 0.35);
        }
        finally
        {
            _raiseFlag = false;
            _planting = false;
        }
    }

    /// <summary>LV95 position, altitude and compass heading — what a hiking GPS shows.</summary>
    private string GpsReadout(FootPlayer player)
    {
        var p = player.GlobalPosition;
        var (e, n) = _origin.ToLv95(p);
        var f = -player.Camera.GlobalTransform.Basis.Z;
        // world −Z is north (LV95 N grows as Z falls), +X is east
        // whole degrees as an int: formatting a float -0.0 prints "-000"
        int bearing = Mathf.PosMod(Mathf.RoundToInt(Mathf.RadToDeg(Mathf.Atan2(f.X, -f.Z))), 360);
        string[] points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        string point = points[(int)Mathf.Round(bearing / 45f) % 8];
        return $"E {e:# ### ###}   N {n:# ### ###}\n{p.Y:F0} m   {bearing:000}° {point}";
    }
}
