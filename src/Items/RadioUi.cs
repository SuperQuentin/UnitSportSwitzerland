using Godot;
using UnitSport.Audio.Cd;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// The panel over a radio you stand beside: the CDs in the shared library to play, stop, pick the
/// radio back up, and a box to burn a new CD from a YouTube link. Modelled on
/// <c>Loot.LootUi</c> — Esc closes it, and so does walking away or the radio going away.
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
    private PanelContainer _panel = null!;
    private ItemList _list = null!;
    private Label _now = null!;
    private Label _status = null!;
    private LineEdit _link = null!;
    private readonly List<int> _ids = new();

    public bool IsOpen => _panel != null && _panel.Visible;

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
        _panel.OffsetLeft = -230;
        _panel.OffsetRight = 230;
        _panel.OffsetTop = -190;
        _panel.OffsetBottom = 190;
        AddChild(_panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(box);

        var title = new Label { Text = "Radio", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(title);

        _list = new ItemList
        {
            CustomMinimumSize = new Vector2(430, 150),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SelectMode = ItemList.SelectModeEnum.Single,
        };
        _list.ItemActivated += _ => PlaySelected();
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
        stop.Pressed += () =>
        {
            if (Live() is { } r) RadioManager.Instance?.Stop(r);
        };
        row.AddChild(stop);
        var pick = new Button { Text = "Pick up" };
        pick.Pressed += PickUp;
        row.AddChild(pick);
        var close = new Button { Text = "Close" };
        close.Pressed += Close;
        row.AddChild(close);

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

    public void Open(RadioBody radio)
    {
        _radio = radio;
        _status.Text = "";
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
        _link.ReleaseFocus();
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    /// <summary>The radio being looked at, or null once it is gone.</summary>
    private RadioBody? Live() => _radio != null && IsInstanceValid(_radio) && _radio.IsInsideTree() ? _radio : null;

    private void Refresh()
    {
        if (!IsOpen) return;
        var before = _list.GetSelectedItems();
        int keepId = before.Length > 0 && before[0] < _ids.Count ? _ids[before[0]] : -1;
        _list.Clear();
        _ids.Clear();
        var all = CdLibrary.Instance?.All;
        if (all != null)
            foreach (var (id, cd) in all.OrderBy(kv => kv.Key))
            {
                _ids.Add(id);
                _list.AddItem(cd.Describe());
                if (id == keepId) _list.Select(_ids.Count - 1);
            }
        if (_ids.Count == 0) _status.Text = "No CDs yet: paste a link below to burn one.";
    }

    private void PlaySelected()
    {
        if (Live() is not { } radio || RadioManager.Instance is not { } manager) return;
        var selected = _list.GetSelectedItems();
        if (selected.Length == 0) { _status.Text = "Pick a CD first."; return; }
        manager.Play(radio, _ids[selected[0]]);
    }

    private void PickUp()
    {
        if (Live() is not { } radio || RadioManager.Instance is not { } manager) return;
        manager.PickUp(radio, () =>
        {
            _inventory.Add(ItemId.Radio, 1);
            Close();
        });
    }

    private void Burn()
    {
        string text = _link.Text.Trim();
        if (text.Length == 0 || CdLibrary.Instance is not { } library) return;
        _link.Text = "";
        library.RequestBurn(text);
    }

    private void OnBurnStatus(string line)
    {
        // the last line only: a burn reports several as it goes; deferred as it may come from a worker
        Callable.From(() => { if (IsInstanceValid(this)) _status.Text = line; }).CallDeferred();
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        if (Live() is not { } radio) { Close(); return; }
        if (_local() is { } player && IsInstanceValid(player)
            && player.GlobalPosition.DistanceTo(radio.GlobalPosition) > WalkAway)
        {
            Close();
            return;
        }
        _now.Text = radio.Playing && radio.Cd is { } cd ? $"Playing: {cd.Title}" : "Nothing playing.";
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
