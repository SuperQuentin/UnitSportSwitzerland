using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// The panel of a radio: the CDs to play (the server's shared ones, then this player's own), stop,
/// a volume slider, a box to burn a new CD from a link for everyone or for yourself only, and for
/// a radio lying in the world, pick it back up. Opened beside a radio in the world (E) or on the
/// one in your hand (Use). Modelled on <c>Loot.LootUi</c> — Esc closes it, and so does walking
/// away, the radio going away, or putting the held radio away.
///
/// <para>
/// While open it registers with <see cref="UiFocus"/>, so typing a link does not walk the player
/// around, and it gives the mouse back; closing re-captures it, exactly as chat does.
/// </para>
/// </summary>
public partial class RadioUi : CanvasLayer
{
    /// <summary>Farther than this from the radio and the panel closes itself.</summary>
    private const float WalkAway = 4f;

    /// <summary>The live panel, for <see cref="FootPlayer.TryInteract"/>.</summary>
    public static RadioUi? Instance { get; private set; }

    private Func<FootPlayer?> _local = () => null;
    private Inventory _inventory = new();
    private RadioBody? _radio;
    private int _heldSlot = -1;
    private PanelContainer _panel = null!;
    private Label _title = null!;
    private ItemList _list = null!;
    private Label _now = null!;
    private Label _status = null!;
    private LineEdit _link = null!;
    private CheckBox _mine = null!;
    private Button _pick = null!;
    private Button _remove = null!;
    private HSlider _volume = null!;
    private readonly List<int> _ids = new();

    public bool IsOpen => _panel != null && _panel.Visible;

    /// <summary>The panel is on the radio in the hand rather than one in the world.</summary>
    public bool Held => IsOpen && _heldSlot >= 0;

    /// <summary>Into the pack, or on the ground when it is full (<see cref="ItemController.Give"/>).</summary>
    public Func<ItemStack, int>? Give { get; set; }

    public static RadioUi Create(Func<FootPlayer?> local, Inventory inventory) =>
        new() { Name = "RadioUi", _local = local, _inventory = inventory };

    public override void _Ready()
    {
        Instance = this;
        Layer = 12;
        _panel = new PanelContainer { Visible = false };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 0.92f),
            BorderColor = new Color(0.35f, 0.38f, 0.42f),
            BorderWidthLeft = 2, BorderWidthRight = 2, BorderWidthTop = 2, BorderWidthBottom = 2,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 12,
        });
        _panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        _panel.OffsetLeft = -240;
        _panel.OffsetRight = 240;
        _panel.OffsetTop = -220;
        _panel.OffsetBottom = 220;
        AddChild(_panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(box);

        _title = new Label { Text = "Radio", HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(_title);

        _list = new ItemList
        {
            CustomMinimumSize = new Vector2(450, 150),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SelectMode = ItemList.SelectModeEnum.Single,
        };
        _list.ItemActivated += _ => PlaySelected();
        _list.ItemSelected += _ => UpdateButtons();
        box.AddChild(_list);

        _now = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(0.7f, 0.72f, 0.76f) };
        box.AddChild(_now);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 12);
        box.AddChild(row);
        var play = new Button { Text = "Play" };
        play.Pressed += PlaySelected;
        row.AddChild(play);
        var stop = new Button { Text = "Stop" };
        stop.Pressed += StopRadio;
        row.AddChild(stop);
        _remove = new Button { Text = "Remove", TooltipText = "Delete one of your own CDs" };
        _remove.Pressed += RemoveSelected;
        row.AddChild(_remove);
        _pick = new Button { Text = "Pick up" };
        _pick.Pressed += PickUp;
        row.AddChild(_pick);
        var close = new Button { Text = "Close" };
        close.Pressed += Close;
        row.AddChild(close);

        var volume = new HBoxContainer();
        volume.AddThemeConstantOverride("separation", 8);
        box.AddChild(volume);
        volume.AddChild(new Label { Text = "Volume" });
        _volume = new HSlider
        {
            MinValue = 0, MaxValue = 1, Step = 0.05, Value = RadioSpeaker.UserVolume,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        _volume.ValueChanged += v => RadioSpeaker.UserVolume = (float)v;
        _volume.DragEnded += _ => RadioSpeaker.SaveVolume();
        volume.AddChild(_volume);

        var burn = new HBoxContainer();
        burn.AddThemeConstantOverride("separation", 8);
        box.AddChild(burn);
        _link = new LineEdit
        {
            PlaceholderText = "Paste a YouTube link to burn a CD",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MaxLength = 300,
        };
        _link.TextSubmitted += _ => Burn();
        burn.AddChild(_link);
        _mine = new CheckBox { Text = "Just for me", TooltipText = "Burn it on this computer, into your own list: nobody else hears it" };
        burn.AddChild(_mine);
        var burnButton = new Button { Text = "Burn" };
        burnButton.Pressed += Burn;
        burn.AddChild(burnButton);

        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(0.7f, 0.72f, 0.76f) };
        box.AddChild(_status);

        if (CdLibrary.Instance is { } library)
        {
            library.Changed += Refresh;
            library.BurnStatus += OnBurnStatus;
        }
    }

    public override void _ExitTree()
    {
        if (CdLibrary.Instance is { } library)
        {
            library.Changed -= Refresh;
            library.BurnStatus -= OnBurnStatus;
        }
        if (Instance == this) Instance = null;
        if (IsOpen) UiFocus.Set(this, false);
    }

    /// <summary>Opens the panel on a radio lying in the world.</summary>
    public void Open(RadioBody radio)
    {
        _radio = radio;
        _heldSlot = -1;
        OpenPanel("Radio");
    }

    /// <summary>Opens (or, open already, closes) the panel on the radio in hotbar <paramref name="slot"/>, the one in the hand.</summary>
    public void OpenHeld(int slot)
    {
        if (IsOpen) { Close(); return; }
        _radio = null;
        _heldSlot = slot;
        OpenPanel("Radio (in your hand)");
    }

    private void OpenPanel(string title)
    {
        _title.Text = title;
        _status.Text = "";
        _pick.Visible = _heldSlot < 0;
        _volume.SetValueNoSignal(RadioSpeaker.UserVolume);
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Refresh();
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        _radio = null;
        _heldSlot = -1;
        _link.ReleaseFocus();
        RadioSpeaker.SaveVolume();
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    /// <summary>The world radio being looked at, or null once it is gone.</summary>
    private RadioBody? Live() => _radio != null && IsInstanceValid(_radio) && _radio.IsInsideTree() ? _radio : null;

    /// <summary>The held radio is still in the hand.</summary>
    private bool HeldLive() => _heldSlot >= 0 && _inventory.Selected == _heldSlot && _inventory[_heldSlot].Id == ItemId.Radio;

    /// <summary>What the radio the panel is on plays: CD and since when, or null when silent.</summary>
    private RadioPlay? Current()
    {
        if (_heldSlot >= 0) return HeldLive() ? RadioPlay.Decode(_inventory[_heldSlot].Data) : null;
        return Live()?.NowPlaying;
    }

    private void Refresh()
    {
        if (!IsOpen) return;
        int keepId = SelectedId();
        _list.Clear();
        _ids.Clear();
        if (CdLibrary.Instance is { } library)
        {
            foreach (var (id, cd) in library.All.OrderBy(kv => kv.Key)) Add(id, cd.Describe());
            foreach (var (id, cd) in library.Personal.OrderBy(kv => kv.Value.Title)) Add(id, "(mine) " + cd.Describe());
        }
        if (_ids.Count == 0) _status.Text = "No CDs yet: paste a link below to burn one.";
        UpdateButtons();

        void Add(int id, string text)
        {
            _ids.Add(id);
            _list.AddItem(text);
            if (id == keepId) _list.Select(_ids.Count - 1);
        }
    }

    private int SelectedId()
    {
        var selected = _list.GetSelectedItems();
        return selected.Length > 0 && selected[0] < _ids.Count ? _ids[selected[0]] : 0;
    }

    private void UpdateButtons() => _remove.Disabled = SelectedId() >= 0;

    private void PlaySelected()
    {
        int id = SelectedId();
        if (id == 0) { _status.Text = "Pick a CD first."; return; }
        if (CdLibrary.Instance?.Find(id) is not { } cd) return;
        if (_heldSlot >= 0)
        {
            if (HeldLive()) _inventory.SetData(_heldSlot, new RadioPlay(id, ClockSync.ServerNow, cd.Duration).Encode());
            return;
        }
        if (Live() is not { } radio || RadioManager.Instance is not { } manager) return;
        manager.Play(radio, id, cd.Duration);
    }

    private void StopRadio()
    {
        if (_heldSlot >= 0)
        {
            if (HeldLive()) _inventory.SetData(_heldSlot, null);
            return;
        }
        if (Live() is { } r) RadioManager.Instance?.Stop(r);
    }

    private void RemoveSelected()
    {
        int id = SelectedId();
        if (id >= 0 || CdLibrary.Instance is not { } library) return;
        if (Current() is { } now && now.CdId == id) StopRadio();
        library.RemovePersonal(id);
    }

    private void PickUp()
    {
        if (Live() is not { } radio || RadioManager.Instance is not { } manager) return;
        // what it plays carries on in the hand: the stack keeps the CD and its start (#168)
        string? playing = radio.NowPlaying?.Encode();
        manager.PickUp(radio, () =>
        {
            if (Give != null) Give(new ItemStack(ItemId.Radio, 1, playing));
            else _inventory.Add(new ItemStack(ItemId.Radio, 1, playing));
            Close();
        });
    }

    private void Burn()
    {
        string text = _link.Text.Trim();
        if (text.Length == 0 || CdLibrary.Instance is not { } library) return;
        _link.Text = "";
        library.RequestBurn(text, _mine.ButtonPressed);
    }

    private void OnBurnStatus(string line)
    {
        // the last line only: a burn reports several as it goes; deferred as it may come from a worker
        Callable.From(() => { if (IsInstanceValid(this)) _status.Text = line; }).CallDeferred();
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        if (_heldSlot >= 0)
        {
            if (!HeldLive()) { Close(); return; }
        }
        else
        {
            if (Live() is not { } radio) { Close(); return; }
            if (_local() is { } player && IsInstanceValid(player)
                && player.GlobalPosition.DistanceTo(radio.GlobalPosition) > WalkAway)
            {
                Close();
                return;
            }
        }
        _now.Text = Current() is { } now && ClockSync.ServerNow - now.StartedAt < now.Length
            ? $"Playing: {CdLibrary.Instance?.Find(now.CdId)?.Title ?? "someone's own CD"}"
            : "Nothing playing.";
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        // not E: it is a letter in the link box
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
