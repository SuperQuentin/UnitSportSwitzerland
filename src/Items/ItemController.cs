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
    public SmartBinocularsHud SmartHud => _smart;
    private FlagGhost _flagGhost = null!;
    private ThrowAim _throw = null!;

    /// <summary>The live controller (the local player's), for the E key's pick-up; null when none.</summary>
    public static ItemController? Instance { get; private set; }

    /// <summary>The throw in progress, for probes and the prompt bar.</summary>
    public ThrowAim Throw => _throw;
    private AudioStreamPlayer _sfx = null!;
    private bool _capturing;
    private bool _forceAim;
    private bool _wasKnockedOut;

    // a use that plays an animation first (eat, drink, put on a hat): the effect lands at the peak
    private bool _useBusy, _usePeaked;
    private ItemId _useItem;
    private static readonly Random SfxRng = new();

    // camera zoom: 35 mm-equivalent focal length, kept across aims; wheel / D-pad change it while aiming
    private const float FocalMin = 24f, FocalMax = 200f, ZoomStep = 1.12f;
    private float _focalMm = 35f;
    private bool _aimingPhoto;
    private bool _aimingScope;   // Aim held on a scoped item (optic / camera / gun): the wheel belongs to it

    // the Polaroid: the print coming out and developing (DevelopSeconds), then the Photo item
    public const float DevelopSeconds = 3f;
    private PhotoUi _photoUi = null!;
    private string? _developing;
    private float _developT;
    private ShaderMaterial? _develop3D;
    private MeshInstance3D? _ghost;

    /// <summary>Vertical FOV in degrees of a 35 mm-equivalent focal length (35 mm is about 38 degrees).</summary>
    public static float FovFromFocal(float mm) => Mathf.RadToDeg(2f * Mathf.Atan(12f / mm));

    /// <summary>
    /// A shotgun shot left the eye along the aim (both given), the shell already spent and the
    /// blast already sent; set by the bird hunt, <c>Birds.BirdLife</c>.
    /// </summary>
    public Action<FootPlayer, Vector3, Vector3>? Fire { get; set; }

    /// <summary>Resolved per frame, never captured: the local on-foot player, or null (fly camera, replay).</summary>
    public Func<FootPlayer?>? ActivePlayer { get; set; }

    public Inventory Inventory => _inventory;
    public InventoryUi Ui => _ui;

    /// <summary>Every item, for the offline player or an admin (#262).</summary>
    public CatalogueUi Catalogue => _catalogue;
    private CatalogueUi _catalogue = null!;

    /// <summary>Sends a chat command (<c>ChatManager.Send</c>): how the catalogue asks for items.</summary>
    public Action<string>? RunCommand { get; set; }
    public PhotoUi PhotoUi => _photoUi;

    /// <summary>Holds Use down as if pressed, for the throw's wind-up (<c>--dropcheck</c>).</summary>
    public bool ForceUse { get; set; }

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
        _catalogue = new CatalogueUi(this) { Name = "CatalogueUi" };
        AddChild(_catalogue);
        _smart = new SmartBinocularsHud();
        AddChild(_smart);
        _flagGhost = new FlagGhost { Name = "FlagGhost" };
        AddChild(_flagGhost);
        _throw = new ThrowAim { Name = "ThrowAim" };
        AddChild(_throw);
        Instance = this;
        DroppedItems.Refused += OnDropRefused;

        _inventory.Changed += () => _ui.Refresh();

        // a throw the server turned down (too many radios, say): the radio goes back in the pack
        RadioManager.Refused += OnRadioRefused;

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

    private void OnRadioRefused(string text)
    {
        _ui.Toast(text);
        if (text.Contains("throw", StringComparison.OrdinalIgnoreCase)) Give(new ItemStack(ItemId.Radio, 1));
    }

    private void OnDropRefused(string text, ItemStack back)
    {
        _ui.Toast(text);
        if (!back.IsEmpty) _inventory.Add(back);
    }

    public override void _ExitTree()
    {
        RadioManager.Refused -= OnRadioRefused;
        DroppedItems.Refused -= OnDropRefused;
        if (Instance == this) Instance = null;
        Highlight.Point(null);
    }

    /// <summary>The player if items can be used right now: on foot (not in a passenger seat), on screen, not in a menu.</summary>
    public FootPlayer? UsablePlayer
    {
        get
        {
            var p = CurrentPlayer();
            return p is { IsViewing: true, RidingAlong: false } && p.Ride == RideKind.OnFoot ? p : null;
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
            Highlight.Point(null);
            Vehicles.VehicleReach.Point(null);
            _throw.Step(null, false, false, (float)delta);
            return;
        }

        // cash you carry is lost when you go down; what you claimed to the account is not
        if (player.KnockedOut && !_wasKnockedOut && _inventory.Cash > 0 && !_inventory.InMatch)
        {
            int lost = _inventory.Cash;
            _inventory.TakeCash(lost);
            _ui.Toast($"You dropped {lost} CHF you had not banked.");
        }
        _wasKnockedOut = player.KnockedOut;

        player.HeldItemId = (int)_inventory.HeldId;
        // a radio plays in the hand too, and on the back once put away: everyone near hears what
        // the stack's data says (#168, #261); one not in the hand is drawn on the back
        int radio = _inventory.RadioSlot();
        player.HeldRadio = radio >= 0 ? _inventory[radio].Data ?? "" : "";
        player.BackItemId = radio >= 0 && _inventory.HeldId != ItemId.Radio ? (int)ItemId.Radio : 0;
        var visual = player.GetNodeOrNull<HeldItemVisual>("HeldItem");
        if (visual != null) visual.HeldData = _inventory.Held.Data;

        // switching item mid-animation cancels it (once the effect has landed, the item may be gone: let it finish)
        if (_useBusy && (visual == null || !visual.OneShotActive || (!_usePeaked && _inventory.HeldId != _useItem)))
        {
            if (visual?.OneShotActive == true) visual.CancelOneShot();
            _useBusy = false;
        }

        var def = ItemDefs.Get(_inventory.HeldId);
        bool usable = UsablePlayer != null;
        bool aiming = usable && !UiFocus.TextEntryActive
                      && (PlayerInput.Held(PlayerInput.AimItem) || _forceAim)
                      && def?.Use is ItemUse.Optic or ItemUse.Photo or ItemUse.Shoot;

        // everything pushed onto the player is re-asserted every frame, so letting go of Aim,
        // switching item or getting on a bike all fall back to normal without a special case
        _aimingPhoto = aiming && def!.Use == ItemUse.Photo;
        _aimingScope = aiming;
        _ui.PhotoFocalMm = _focalMm;
        // binoculars breathe: a slow tiny zoom drift, and the overlay drifts with it
        float breath = (float)Time.GetTicksMsec() / 1000f;
        float breathFov = 1f + 0.012f * Mathf.Sin(breath * 1.3f);
        _ui.OpticSway = aiming && def!.Use == ItemUse.Optic
            ? new Vector2(0.0035f * Mathf.Sin(breath * 0.9f + 1f), 0.005f * Mathf.Sin(breath * 1.3f)) : Vector2.Zero;
        var weapon = Weapons.Get(_inventory.HeldId);
        // a scoped gun is held to the eye like the binoculars, and drawn as their overlay
        bool scoped = aiming && weapon is { AimFov: < 20f };
        player.FovOverride = aiming ? def!.Use switch { ItemUse.Optic => 9f * breathFov, ItemUse.Photo => FovFromFocal(_focalMm), _ => weapon?.AimFov ?? 50f } : null;
        player.ScopeView = aiming;
        player.ItemAction = _planting || _useBusy ? 2 : aiming ? 1 : 0;   // replicated: remote peers pose the arms from it
        player.LookScale = aiming ? def!.Use switch { ItemUse.Optic => 0.2f, ItemUse.Photo => Mathf.Clamp(FovFromFocal(_focalMm) / 76f, 0.04f, 1f), _ => scoped ? 0.15f : 0.6f } : 1f;
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
            if (!aiming && def?.Use == ItemUse.Readout) visual.SetPose(ViewPose.Read);   // the GPS is held up to read
            visual.ScreenText = _inventory.HeldId == ItemId.Gps && usable ? GpsScreen(player) : null;
            visual.Suppressed = (aiming && (def!.Use is (ItemUse.Optic or ItemUse.Photo) || scoped) && poseSettled);
        }

        // the viewfinder / binocular overlay appears once the item has been raised
        _ui.Scope = scoped ? (poseSettled ? ItemUse.Optic : null)
            : aiming && (def!.Use == ItemUse.Shoot || poseSettled) ? def!.Use : null;
        StepThrow(player, def, usable, aiming, (float)delta);

        // the smart binoculars read out the building at hand while held (#165): no aiming
        _smart.Held = usable && _inventory.HeldId == ItemId.SmartBinoculars;
        _smart.Player = UsablePlayer;
        // in first person the readout is on the device's own screen; the HUD panel is for third person
        _ui.Readout = usable && _inventory.HeldId == ItemId.Gps && !player.IsFirstPerson ? GpsReadout(player) : null;
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
        else if (e.IsActionPressed(PlayerInput.DropItem))
        {
            DropHeld(player, all: e is InputEventKey { CtrlPressed: true });
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
        else if (_aimingScope && e is InputEventMouseButton && (e.IsActionPressed(PlayerInput.NextItem) || e.IsActionPressed(PlayerInput.PrevItem)))
        {
            // aiming an optic or gun: the wheel is not a hotbar scroll, but it is still not left to leak elsewhere
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
        // aiming a throw: Use winds it up, letting go throws (StepThrow)
        if (_throw.Active)
        {
            _throw.BeginCharge();
            return;
        }
        // a click on the radio you point at takes it in the hand (#261); on anything else lying
        // there, with nothing in the hand, picks it up (a held tool still does its own thing)
        if (Highlight.Pointed is RadioBody radio && IsInstanceValid(radio))
        {
            TakeRadio(player, radio);
            return;
        }
        if (_inventory.Held.IsEmpty && Highlight.Pointed is DroppedItem dropped && IsInstanceValid(dropped))
        {
            PickUp(dropped);
            return;
        }
        // an empty hand takes back a photo of yours you are looking at
        if (_inventory.Held.IsEmpty)
        {
            if (StickTarget(player).PhotoId is long id) PickUpPhoto(id);
            return;
        }
        UseSlot(player, _inventory.Selected);
    }

    /// <summary>
    /// Puts something new in the inventory (a developed photo, a flag picked up, a spawned item):
    /// what does not fit is dropped on the ground in front of the player rather than lost. Returns
    /// how many could go nowhere at all (no player, nowhere to drop).
    /// </summary>
    public int Give(ItemStack stack)
    {
        int left = _inventory.Add(stack);
        if (left <= 0) return 0;
        var rest = stack with { Count = left };
        if (!DropStack(null, rest)) return left;
        _ui.Toast($"No room in your pack: {ItemDefs.Get(stack.Id)?.Name ?? "it"} dropped at your feet.");
        return 0;
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
                if (_useBusy) break;
                if (player.Health >= FootPlayer.MaxHealth - 0.01f)
                {
                    _ui.Toast("Already at full health.");
                    break;
                }
                bool drink = def.Category == ItemCategory.Water;
                StartUse(player, slot, def, ViewPose.Mouth, 0.35f, 0.6f, 0.3f, () =>
                {
                    if (!player.Heal(def.Heal)) return;
                    _inventory.TakeOne(slot);
                    Kick(player);
                    var bank = drink ? SfxSynth.GulpBank : SfxSynth.CrunchBank;
                    Play(bank.Variants[SfxRng.Next(bank.Variants.Length)]);
                    _ui.Toast($"{def.Name}: +{def.Heal:F0} health");
                });
                break;

            case ItemUse.Photo:
                // the picture is what the viewfinder frames: the camera shoots from the eye only
                if (_ui.Scope != ItemUse.Photo)
                    _ui.Toast(InputHints.Format("Hold Aim ({aim_item}) to look through the viewfinder, then {use_item} takes the picture."));
                else if (!_capturing) TakePhoto(player);
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
                if (Weapons.Get(stack.Id) is not { } weapon) break;
                // the action has to cycle before the next round (the shotgun's pump, a rifle's bolt)
                if (Time.GetTicksMsec() < _nextShotMs) break;
                int ammo = -1;
                for (int i = 0; i < Inventory.Size && ammo < 0; i++)
                    if (_inventory[i].Id == weapon.Ammo && !_inventory[i].IsEmpty) ammo = i;
                if (ammo < 0)
                {
                    Play(SfxSynth.Tick, 0.5f);
                    _ui.Toast(weapon.Ammo == ItemId.Shells ? "Out of shells." : $"Out of {ItemDefs.Get(weapon.Ammo)?.Name ?? "ammunition"}.");
                    break;
                }
                _inventory.TakeOne(ammo);
                _nextShotMs = Time.GetTicksMsec() + (ulong)(weapon.Interval * 1000f);
                Recoil(player, weapon);
                Shoot(player, weapon);
                break;
            }

            case ItemUse.Melee:
            {
                if (Weapons.Get(stack.Id) is not { } blade || Time.GetTicksMsec() < _nextShotMs) break;
                _nextShotMs = Time.GetTicksMsec() + (ulong)(blade.Interval * 1000f);
                Kick(player);
                Play(SfxSynth.WhooshBank.Variants[SfxRng.Next(SfxSynth.WhooshBank.Variants.Length)], 1.3f);
                var (eye, aim) = AimFrom(player, blade.Range);
                if (!PlayerHits.Stab(player, eye, aim, blade)) BattleRoyale.BrCrates.Instance?.TryBreak(eye, aim, blade.Range);
                break;
            }

            case ItemUse.Signal:
            {
                // a flare calls a supply drop (#198): only where there is a match to drop into
                if (BattleRoyale.BrManager.Instance?.CallDrop() != true)
                {
                    _ui.Toast("The flare would only call a supply drop in a Battle Royale.");
                    break;
                }
                _inventory.TakeOne(slot);
                Kick(player);
                var up = (Vector3.Up * 3f - player.Camera.GlobalTransform.Basis.Z).Normalized();
                ItemEvents.Instance?.Send(ItemEventKind.Flare, ItemEvents.MuzzleOf(player, up), up);
                _ui.Toast("Flare up: a supply drop is on its way.");
                break;
            }

            case ItemUse.Armor:
                if (_useBusy) break;
                if (player.Armor >= FootPlayer.MaxArmor - 0.01f)
                {
                    _ui.Toast("Your vest is already whole.");
                    break;
                }
                StartUse(player, slot, def, ViewPose.Head, 0.4f, 0.5f, 0.3f, () =>
                {
                    if (!player.AddArmor(FootPlayer.MaxArmor)) return;
                    _inventory.TakeOne(slot);
                    Play(SfxSynth.Tick, 0.8f);
                    _ui.Toast($"{def.Name} on: {FootPlayer.MaxArmor:F0} armour.");
                });
                break;

            case ItemUse.Wear:
                // worn: off into the pack; carried: on in its body slot, swapping with what was there
                if (Inventory.IsWearSlot(slot))
                {
                    _inventory.QuickMove(slot);
                    Play(SfxSynth.Tick, 0.9f);
                    break;
                }
                if (_useBusy) break;
                var worn = stack.Id;
                StartUse(player, slot, def, ViewPose.Head, 0.3f, 0.2f, 0.3f, () =>
                {
                    // the stack may have moved during the wind-up: wear it from wherever it is now
                    int at = _inventory[slot].Id == worn ? slot : -1;
                    for (int i = 0; at < 0 && i < _inventory.Capacity; i++)
                        if (_inventory[i].Id == worn) at = i;
                    if (at < 0 || !_inventory.Wear(at)) return;
                    Play(SfxSynth.Tick, 1.2f);
                    _ui.Toast($"You put on the {def.Name.ToLowerInvariant()}.");
                });
                break;

            case ItemUse.Throw:
            {
                // Use alone opens the radio's panel in the hand; Aim + Use throws it (#168)
                if (!PlayerInput.Held(PlayerInput.AimItem) && !_forceAim && slot == _inventory.Selected)
                {
                    RadioUi.Instance?.OpenHeld(slot);
                    break;
                }
                // Aim + Use from the pack panel (no wind-up there): a medium throw
                ThrowSlot(player, slot, 0.45f);
                break;
            }

            case ItemUse.Material:
                _ui.Toast($"{def.Name}: keep it for trading or building.");
                break;

            case ItemUse.Bag:
                // worn: off into the pack; carried: on, swapping with the one worn
                if (slot == Inventory.BagSlot) _inventory.QuickMove(slot);
                else if (_inventory.WearBag(slot)) _ui.Toast($"You put on the {def.Name.ToLowerInvariant()}: {_inventory.PackSize} pack slots.");
                Play(SfxSynth.Tick, 1.1f);
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

    /// <summary>
    /// Plays <paramref name="pose"/> as a one-shot, then runs <paramref name="effect"/> at its peak. Replicated
    /// as <c>ItemAction</c> = 2 for the animation's length. A use of a slot that is not the held one (from the
    /// pack panel) has nothing in hand to animate and applies at once.
    /// </summary>
    private void StartUse(FootPlayer player, int slot, ItemDef def, ViewPose pose, float inTime, float hold, float outTime, Action effect)
    {
        var visual = player.GetNodeOrNull<HeldItemVisual>("HeldItem");
        if (visual == null || slot != _inventory.Selected) { effect(); return; }
        _useBusy = true;
        _usePeaked = false;
        _useItem = def.Id;
        visual.PlayOneShot(pose, inTime, hold, outTime, () => { _usePeaked = true; effect(); });
    }

    private ulong _nextShotMs;

    // ------------------------------------------------------------------------------------
    // throwing, dropping, picking up (#206)
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The throw, every frame: Aim held with a throwable item brings out <see cref="ThrowAim"/>, which
    /// in turn pushes the shoulder camera, the FOV, the shake and the wind-up arm pose onto the player
    /// (re-asserted each frame, like everything else here). Also moves the pointing border.
    /// </summary>
    private void StepThrow(FootPlayer player, ItemDef? def, bool usable, bool aiming, float dt)
    {
        bool throwing = usable && !aiming && !UiFocus.TextEntryActive && !_ui.IsOpen && !_useBusy && !_planting
                        && ItemDefs.Throwable(def) && (PlayerInput.Held(PlayerInput.AimItem) || _forceAim);
        if (_throw.Step(player, throwing, PlayerInput.Held(PlayerInput.UseItem) || ForceUse, dt))
            ThrowSlot(player, _inventory.Selected, _throw.ReleasePower);

        player.ThrowAim = _throw.Active ? 1f : 0f;
        player.CameraShake = _throw.Shake;
        if (!aiming && _throw.Fov is { } fov) player.FovOverride = fov;
        if (_throw.Active)
        {
            player.LookScale = 0.75f;
            player.ItemAction = 3;
        }
        else if (_throw.SinceRelease < 0.4f) player.ItemAction = 4;

        Highlight.Point(usable && !_throw.Active && !_ui.IsOpen ? Highlight.Find(player) : null);
        // a world item pointed at comes first; else the car door (or machine) the player is at (#261)
        Vehicles.VehicleReach.Point(usable && !_throw.Active && !_ui.IsOpen && Highlight.Pointed == null
            ? Vehicles.VehicleReach.Find(player) : null);
    }

    /// <summary>Throws one from <paramref name="slot"/> out of the hand along the view, at <paramref name="power"/> 0..1.</summary>
    private void ThrowSlot(FootPlayer player, int slot, float power)
    {
        if (_inventory[slot].IsEmpty) return;
        if (!Release(player, slot, 1, ThrowAim.Origin(player), ThrowAim.Launch(player, power), power)) return;
        Kick(player);
        player.Punch(Mathf.DegToRad(1.2f + 2.5f * power));
        var bank = SfxSynth.WhooshBank;
        Play(bank.Variants[SfxRng.Next(bank.Variants.Length)], Mathf.Lerp(0.75f, 1.35f, power));
    }

    /// <summary>Drops one of the item in hand at the player's feet (<paramref name="all"/>: the whole stack).</summary>
    private void DropHeld(FootPlayer player, bool all)
    {
        if (_throw.Charging || _useBusy || _planting) return;
        DropSlot(player, _inventory.Selected, all);
    }

    /// <summary>Drops from any slot, a little ahead of the player: the hand's Q, and the pack panel's Drop.</summary>
    public void DropSlot(FootPlayer? player, int slot, bool all)
    {
        player ??= UsablePlayer;
        var stack = _inventory[slot];
        if (player == null || stack.IsEmpty || !CanRelease(stack)) return;
        DropStack(player, _inventory.TakeFrom(slot, all ? stack.Count : 1));
    }

    /// <summary>
    /// Drops a stack that is in no slot (the cursor's, a print with no room in the pack) a little
    /// ahead of <paramref name="player"/> (null: the local one), the same toss as Q. Keeps
    /// <see cref="ItemStack.Data"/>. False when there is nobody or nowhere to put it: the caller
    /// still holds the stack then.
    /// </summary>
    public bool DropStack(FootPlayer? player, ItemStack stack)
    {
        player ??= CurrentPlayer();
        if (player == null || stack.IsEmpty || !CanRelease(stack)) return false;
        var view = player.Camera.GlobalTransform.Basis;
        var ahead = new Vector3(-view.Z.X, 0, -view.Z.Z).Normalized();
        var origin = player.GlobalPosition + Vector3.Up * 1.15f + ahead * 0.45f;
        var velocity = ahead * 1.8f + Vector3.Up * 1.4f + player.Velocity;
        Launch(player, stack, origin, velocity, 0.1f);
        Kick(player);
        Play(SfxSynth.Whoosh, 1.5f);
        return true;
    }

    /// <summary>Whether the world can take <paramref name="stack"/> now (its manager exists); toasts when not.</summary>
    private bool CanRelease(ItemStack stack)
    {
        if (stack.Id == ItemId.Radio ? RadioManager.Instance != null : DroppedItems.Instance != null) return true;
        _ui.Toast("Nowhere to put it.");
        return false;
    }

    /// <summary>Takes <paramref name="count"/> out of <paramref name="slot"/> and launches it (<see cref="Launch"/>). False when there is nowhere to put it.</summary>
    private bool Release(FootPlayer player, int slot, int count, Vector3 origin, Vector3 velocity, float power)
    {
        var stack = _inventory[slot];
        if (stack.IsEmpty || !CanRelease(stack)) return false;
        Launch(player, _inventory.TakeFrom(slot, count), origin, velocity, power);
        return true;
    }

    /// <summary>
    /// Puts <paramref name="stack"/> in the world at <paramref name="origin"/>, moving at
    /// <paramref name="velocity"/>: radios as <see cref="RadioBody"/> (each its own body, playing on
    /// where it lands), anything else as one <see cref="DroppedItem"/> tumbling end over end.
    /// <see cref="CanRelease"/> first.
    /// </summary>
    private void Launch(FootPlayer player, ItemStack stack, Vector3 origin, Vector3 velocity, float power)
    {
        var flat = new Vector3(velocity.X, 0, velocity.Z);
        var ahead = flat.LengthSquared() > 1e-4f ? flat.Normalized() : -player.GlobalTransform.Basis.Z;
        float yaw = Mathf.Atan2(-ahead.X, -ahead.Z);
        if (stack.Id == ItemId.Radio)
        {
            var play = RadioPlay.Decode(stack.Data);
            for (int i = 0; i < stack.Count; i++)
                RadioManager.Instance!.Throw(new RadioState("", 0, _origin.ToGlobal(origin + Vector3.Up * (0.25f * i)), yaw, velocity,
                    play?.CdId ?? 0, play?.StartedAt ?? 0, play != null, false, play?.Length ?? 0));
            return;
        }
        var right = ahead.Cross(Vector3.Up);
        var spin = right * -(3f + 14f * power)
                   + new Vector3(SfxRng.NextSingle() - 0.5f, SfxRng.NextSingle() - 0.5f, SfxRng.NextSingle() - 0.5f) * 3f;
        DroppedItems.Instance!.Drop(stack, origin, velocity, new Vector3(0, yaw, 0), spin);
    }

    /// <summary>
    /// E on a dropped item: room checked first, then the server asked (one winner). Here it flies
    /// into the hand at once; it goes in the pack when the server says so.
    /// </summary>
    public void PickUp(DroppedItem item)
    {
        if (DroppedItems.Instance is not { } dropped || UsablePlayer is not { } player) return;
        var stack = item.Stack;
        if (_inventory.Room(stack.Id, stack.Data) < stack.Count)
        {
            _ui.Toast("No room in your pack.");
            Play(SfxSynth.Tick, 0.5f);
            return;
        }
        if (item.Visual is { } visual)
        {
            FlyToHand(player, visual);
            visual.Visible = false;
        }
        Highlight.Point(null);
        Play(SfxSynth.Tick, 1.9f);
        dropped.PickUp(item, got =>
        {
            int left = _inventory.Add(got);
            if (left > 0)
            {
                // filled up in the meantime: what does not fit falls back down
                dropped.Drop(got with { Count = left }, player.GlobalPosition + Vector3.Up, Vector3.Up, Vector3.Zero, Vector3.Zero);
                _ui.Toast("Your pack is full: some of it fell back down.");
            }
            else _ui.Toast($"+ {(ItemDefs.Get(got.Id)?.Name ?? "item")}{(got.Count > 1 ? $" ×{got.Count}" : "")}");
            Play(SfxSynth.Chime, 1.8f);
            Kick(player);
        });
    }

    /// <summary>
    /// Takes a radio lying in the world straight into the hand (#261): it flies there at once,
    /// keeps playing what it played (the stack carries the CD, its start and its mode), and is
    /// selected, the item that was in the hand going to the pack if the hotbar is full.
    /// </summary>
    public void TakeRadio(FootPlayer player, RadioBody radio)
    {
        if (RadioManager.Instance is not { } manager) return;
        string? playing = radio.NowPlaying is { } p ? (p with { Mode = RadioQueue.Clamp(radio.Mode) }).Encode() : null;
        if (_inventory.Room(ItemId.Radio, playing) < 1)
        {
            _ui.Toast("No room in your pack.");
            Play(SfxSynth.Tick, 0.5f);
            return;
        }
        if (radio.GetNodeOrNull<MeshInstance3D>("Visual") is { } visual)
        {
            FlyToHand(player, visual);
            visual.Visible = false;
        }
        Highlight.Point(null);
        Play(SfxSynth.Tick, 1.9f);
        manager.PickUp(radio, () =>
        {
            Give(new ItemStack(ItemId.Radio, 1, playing));
            InHand(ItemId.Radio, playing);
            Play(SfxSynth.Chime, 1.8f);
            Kick(player);
        });
    }

    /// <summary>Selects the slot holding <paramref name="id"/> with <paramref name="data"/>: the hotbar's, else swapped in from the pack.</summary>
    private void InHand(ItemId id, string? data)
    {
        for (int i = 0; i < _inventory.Capacity; i++)
        {
            var s = _inventory[i];
            if (s.IsEmpty || s.Id != id || s.Data != data) continue;
            if (Inventory.IsHotbar(i)) _inventory.Select(i);
            else _inventory.SwapWithHotbar(i, _inventory.Selected);
            return;
        }
    }

    /// <summary>A copy of the picked-up item sucked into the player's hand, spinning and shrinking.</summary>
    private void FlyToHand(FootPlayer player, MeshInstance3D visual)
    {
        var ghost = new MeshInstance3D
        {
            Name = "PickedUp", Mesh = visual.Mesh, MaterialOverride = visual.MaterialOverride, TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(ghost);
        var start = visual.GlobalTransform;
        var turn = start.Basis.GetRotationQuaternion();
        ghost.GlobalTransform = start;
        var tween = ghost.CreateTween();
        tween.TweenMethod(Callable.From<float>(t =>
        {
            if (!IsInstanceValid(player)) return;
            var hand = player.GlobalPosition + Vector3.Up * 1.15f;
            float e = t * t;
            var at = start.Origin.Lerp(hand, e) + Vector3.Up * (Mathf.Sin(t * Mathf.Pi) * 0.35f);
            var basis = new Basis(turn) * new Basis(Vector3.Up, t * 5f);
            ghost.GlobalTransform = new Transform3D(basis.Scaled(start.Basis.Scale * Mathf.Lerp(1f, 0.2f, e)), at);
        }), 0f, 1f, 0.26f);
        tween.TweenCallback(Callable.From(ghost.QueueFree));
    }

    /// <summary>A gun's kick: the viewmodel jolts, the view punches up a few degrees, a shotgun's action cycles.</summary>
    private static void Recoil(FootPlayer player, WeaponDef weapon)
    {
        bool shotgun = weapon.Id == ItemId.Shotgun;
        if (player.GetNodeOrNull<HeldItemVisual>("HeldItem") is { } v)
        {
            v.Kick = 1f;
            v.Recoil = shotgun || weapon.Id == ItemId.HuntingRifle ? 1f : 0.45f;
            if (shotgun) v.Pump();
        }
        player.Punch(Mathf.DegToRad(weapon.Id switch { ItemId.Shotgun => 4.5f, ItemId.HuntingRifle => 5f, ItemId.Pistol => 2.5f, _ => 1.4f }));
    }

    /// <summary>
    /// Where a shot from <paramref name="player"/> starts and goes. It leaves the EYE: in third
    /// person the camera is ~3 m behind and to the side, so the camera's ray finds what the
    /// crosshair is on and the barrel aims from the eye at that point.
    /// </summary>
    public static (Vector3 Eye, Vector3 Aim) AimFrom(FootPlayer player, float range)
    {
        var cam = player.Camera;
        var eye = player.EyePosition;
        var look = -cam.GlobalTransform.Basis.Z;
        var aim = look;
        if (!player.IsFirstPerson && !player.ScopeView)
        {
            var start = cam.GlobalPosition + look * Mathf.Max(0f, (eye - cam.GlobalPosition).Dot(look));
            var end = start + look * (range + 10f);
            var ray = player.GetWorld3D().DirectSpaceState.IntersectRay(
                PhysicsRayQueryParameters3D.Create(start, end, uint.MaxValue, new Godot.Collections.Array<Rid> { player.GetRid() }));
            var point = ray.Count > 0 ? ray["position"].AsVector3() : end;
            if (point.DistanceTo(eye) > 1f) aim = (point - eye).Normalized();
        }
        return (eye, aim);
    }

    /// <summary>
    /// A shot: heard and seen by everyone near (an item event, this player included), traced
    /// against the other players, and for the shotgun against the birds.
    /// </summary>
    private void Shoot(FootPlayer player, WeaponDef weapon)
    {
        var (eye, aim) = AimFrom(player, weapon.Range);
        if (ItemEvents.Instance is { } events)
            events.Send(ItemEventKind.Shot, ItemEvents.MuzzleOf(player, aim), aim, ((int)weapon.Id).ToString(System.Globalization.CultureInfo.InvariantCulture));
        else
        {
            // a probe world without the event node: a plain sound
            var (stream, pitch, db) = SfxSynth.Shotgun.Pick(SfxRng);
            Play(stream, pitch * weapon.Pitch);
        }
        PlayerHits.Shoot(player, eye, aim, weapon);
        // a shot through a supply crate breaks it open (#198)
        BattleRoyale.BrCrates.Instance?.TryBreak(eye, aim, weapon.Range);
        if (weapon.Id == ItemId.Shotgun) Fire?.Invoke(player, eye, aim);
    }

    private static void Kick(FootPlayer player)
    {
        if (player.GetNodeOrNull<HeldItemVisual>("HeldItem") is { } v) v.Kick = 1f;
    }

    /// <summary>
    /// Takes the picture the viewfinder frames (<see cref="PhotoCapture"/>: rendered from the eye
    /// at the focal length, no HUD, no held item), saves the full frame to <c>user://photos/</c>
    /// and prints the Polaroid. Only called with the camera at the eye (see <see cref="UseSlot"/>).
    /// </summary>
    private async void TakePhoto(FootPlayer player)
    {
        _capturing = true;
        string path = "";
        string? photo = null;
        try
        {
            var image = await PhotoCapture.Render(this, player.Camera, FovFromFocal(_focalMm));
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
        // two frames for a camera lowered right after the shot to be drawn again, so the print
        // comes out of it
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
        if (_inventory.Room(ItemId.Photo, photo) < 1)
        {
            if (Give(new ItemStack(ItemId.Photo, 1, photo)) > 0) _ui.Toast("Pack full: the photo is only in your album.");
        }
        else
        {
            Give(new ItemStack(ItemId.Photo, 1, photo));
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
                Give(stack with { Count = 1 });   // refused: the print comes back
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
        placed.RequestRemove(id, r =>
        {
            if (!r.Ok)
            {
                _ui.Toast($"Cannot take it: {r.Refused}");
                return;
            }
            if (_inventory.Room(ItemId.Photo, o.Payload) >= 1) _ui.Toast("Photo taken back.");
            Give(new ItemStack(ItemId.Photo, 1, o.Payload));
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
                Name = "PhotoGhost", TopLevel = true,
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
        _ghost.Mesh = PhotoVisuals.MeshFor(at.Value);   // a poster on a wall, a Polaroid on the ground
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
            await Stroke(player, raise: false, () => placed.RequestRemove(aim.Id, r =>
            {
                if (!r.Ok)
                {
                    _ui.Toast($"Cannot pick it up: {r.Refused}");
                    return;
                }
                if (_inventory.Room(ItemId.SwissFlag) >= 1) _ui.Toast("Flag picked up.");
                Give(new ItemStack(ItemId.SwissFlag, 1));
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
                    Give(new ItemStack(ItemId.SwissFlag, 1));   // the server said no: the flag comes back
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

    /// <summary>The same numbers in the short lines that fit the device's own screen.</summary>
    private string GpsScreen(FootPlayer player)
    {
        var p = player.GlobalPosition;
        var (e, n) = _origin.ToLv95(p);
        var f = -player.Camera.GlobalTransform.Basis.Z;
        int bearing = Mathf.PosMod(Mathf.RoundToInt(Mathf.RadToDeg(Mathf.Atan2(f.X, -f.Z))), 360);
        string[] points = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        string point = points[(int)Mathf.Round(bearing / 45f) % 8];
        return $"E {e:# ### ###}\nN {n:# ### ###}\n{p.Y:F0} m\n{bearing:000}° {point}";
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
