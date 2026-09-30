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
    private const float PlaceReach = 6f;

    private readonly Inventory _inventory;
    private readonly WorldOrigin _origin;
    private InventoryUi _ui = null!;
    private Node3D _placed = null!;
    private AudioStreamPlayer _sfx = null!;
    private bool _capturing;
    private bool _forceAim;
    private bool _wasKnockedOut;

    // camera zoom: 35 mm-equivalent focal length, kept across aims; wheel / D-pad change it while aiming
    private const float FocalMin = 24f, FocalMax = 200f, ZoomStep = 1.12f;
    private float _focalMm = 35f;
    private bool _aimingPhoto;

    /// <summary>Vertical FOV in degrees of a 35 mm-equivalent focal length (35 mm is about 38 degrees).</summary>
    public static float FovFromFocal(float mm) => Mathf.RadToDeg(2f * Mathf.Atan(12f / mm));

    /// <summary>Fires the held gun (a shell already taken); set by the bird hunt, <c>Birds.BirdLife</c>.</summary>
    public Action<FootPlayer>? Fire { get; set; }

    /// <summary>Resolved per frame, never captured: the local on-foot player, or null (fly camera, replay).</summary>
    public Func<FootPlayer?>? ActivePlayer { get; set; }

    public Inventory Inventory => _inventory;
    public InventoryUi Ui => _ui;

    public ItemController(Inventory inventory, WorldOrigin origin)
    {
        _inventory = inventory;
        _origin = origin;
    }

    public ItemController() : this(new Inventory(), WorldOrigin.SwissDefault()) { }

    public override void _Ready()
    {
        Name = "Items";
        _placed = new Node3D { Name = "PlacedFlags" };
        AddChild(_placed);
        _sfx = new AudioStreamPlayer { Name = "Sfx", VolumeDb = -8f, Bus = SfxBus.Name };
        AddChild(_sfx);
        _ui = new InventoryUi(this) { Name = "InventoryUi" };
        AddChild(_ui);

        _inventory.Changed += () => _ui.Refresh();

        // "--hold <item>" puts that item in the hand, for screenshotting the viewmodel
        var args = OS.GetCmdlineUserArgs();
        _forceAim = Array.IndexOf(args, "--aim") >= 0;   // and "--aim" holds Aim down
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
        if (player == null) return;

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

        var def = ItemDefs.Get(_inventory.HeldId);
        bool usable = UsablePlayer != null;
        bool aiming = usable && !UiFocus.TextEntryActive
                      && (PlayerInput.Held(PlayerInput.AimItem) || _forceAim)
                      && def?.Use is ItemUse.Optic or ItemUse.Photo or ItemUse.Shoot;

        // everything pushed onto the player is re-asserted every frame, so letting go of Aim,
        // switching item or getting on a bike all fall back to normal without a special case
        _aimingPhoto = aiming && def!.Use == ItemUse.Photo;
        _ui.PhotoFocalMm = _focalMm;
        player.FovOverride = aiming ? def!.Use switch { ItemUse.Optic => 9f, ItemUse.Photo => FovFromFocal(_focalMm), _ => 50f } : null;
        player.ScopeView = aiming;
        player.ItemAction = aiming ? 1 : 0;   // replicated: remote peers pose the arms from it
        player.LookScale = aiming ? def!.Use switch { ItemUse.Optic => 0.2f, ItemUse.Photo => Mathf.Clamp(FovFromFocal(_focalMm) / 76f, 0.04f, 1f), _ => 0.6f } : 1f;
        // held items stay visible while aiming: they are raised to a pose. Binoculars and the
        // camera hide once at the eye (you look through them: the overlay is the view).
        bool poseSettled = visual?.PoseSettled ?? true;
        if (visual != null)
        {
            visual.SetPose(!aiming ? ViewPose.Rest : def!.Use switch
            {
                ItemUse.Shoot => ViewPose.Aim,
                _ => ViewPose.Eye,
            });
            visual.Suppressed = _capturing || (aiming && def!.Use is (ItemUse.Optic or ItemUse.Photo) && poseSettled);
        }

        // the viewfinder / binocular overlay appears once the item has been raised
        _ui.Scope = aiming && (def!.Use == ItemUse.Shoot || poseSettled) ? def.Use : null;
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

    private void UseHeld(FootPlayer player) => UseSlot(player, _inventory.Selected);

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

            case ItemUse.Optic:
                _ui.Toast(InputHints.Format("Hold Aim ({aim_item}) to look through them."));
                break;

            case ItemUse.Readout:
                break;

            case ItemUse.Shoot:
            {
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
                Kick(player);
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
        }
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
        try
        {
            var image = GetViewport().GetTexture().GetImage();
            DirAccess.MakeDirRecursiveAbsolute("user://photos");
            path = $"user://photos/photo_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            image.SavePng(path);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[items] photo failed: {e.Message}");
        }

        _ui.Visible = true;
        _capturing = false;
        if (!IsInstanceValid(player)) return;
        Kick(player);
        Play(SfxSynth.Tick, 0.6f);
        _ui.Flash();
        _ui.Toast(path.Length > 0 ? $"Photo saved: {ProjectSettings.GlobalizePath(path)}" : "Photo failed.");
    }

    /// <summary>
    /// Plants a flag where the view meets the ground, or — if the view meets a planted flag —
    /// takes it back. Placing only on ground flat enough to stand on: a flag is planted, not
    /// stuck to a cliff.
    /// </summary>
    private void PlaceOrPickUpFlag(FootPlayer player, int slot)
    {
        var camera = player.Camera;
        var from = camera.GlobalPosition;
        var forward = -camera.GlobalTransform.Basis.Z;
        // third person looks from behind the shoulder, so reach is measured from the body
        float reach = PlaceReach + from.DistanceTo(player.GlobalPosition + Vector3.Up * 1.6f);

        var query = PhysicsRayQueryParameters3D.Create(from, from + forward * reach,
            uint.MaxValue, new Godot.Collections.Array<Rid> { player.GetRid() });
        var hit = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0)
        {
            _ui.Toast("Nothing in reach to plant it in.");
            return;
        }

        if (hit["collider"].AsGodotObject() is Node node && node.IsInGroup(FlagGroup))
        {
            if (_inventory.Add(ItemId.SwissFlag, 1) > 0)
            {
                _ui.Toast("No room in your pack.");
                return;
            }
            node.QueueFree();
            Play(SfxSynth.Whoosh, 1.3f);
            _ui.Toast("Flag picked up.");
            return;
        }

        var point = hit["position"].AsVector3();
        var normal = hit["normal"].AsVector3();
        if (normal.Y < 0.6f)
        {
            _ui.Toast("Too steep to plant a flag.");
            return;
        }
        if (point.DistanceTo(player.GlobalPosition) > PlaceReach)
        {
            _ui.Toast("Too far away.");
            return;
        }

        // the cloth faces whoever planted it
        var toPlayer = (player.GlobalPosition - point) with { Y = 0 };
        float yaw = toPlayer.LengthSquared() > 1e-4f ? Mathf.Atan2(toPlayer.X, toPlayer.Z) : 0f;
        _placed.AddChild(CreatePlantedFlag(new Transform3D(new Basis(Vector3.Up, yaw), point)));

        _inventory.TakeOne(slot);
        Kick(player);
        Play(SfxSynth.Landing, 1.5f);
        _ui.Toast("Flag planted.");
    }

    public const string FlagGroup = "planted_flag";

    private static StaticBody3D CreatePlantedFlag(Transform3D at)
    {
        var body = new StaticBody3D { Name = "Flag", Transform = at };
        body.AddToGroup(FlagGroup);
        body.AddChild(new MeshInstance3D
        {
            Mesh = ItemDefs.PlantedFlagMesh(),
            MaterialOverride = ItemDefs.Material,
        });
        // pole-thin, so it is something to aim at for picking up rather than a wall to walk into
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(0.12f, 1.9f, 0.12f) },
            Position = new Vector3(0, 0.95f, 0),
        });
        return body;
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
