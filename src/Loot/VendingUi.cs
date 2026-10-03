using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Loot;

/// <summary>
/// The face of a PAUSA vending machine (#273): its 4×6 spirals behind the glass (A1 to D6, what is
/// in each and the price), the keypad and its little LCD. Type a code (the keys, or A-D and 1-6
/// on the keyboard), OK, and it takes the cash: the spiral turns and the item drops into the flap.
/// Now and then it catches on the spiral: hit the machine (the button, or Space) to free it,
/// sometimes with one more. A machine "Hors service" this period takes no coins. Cash only.
/// </summary>
public partial class VendingUi : CanvasLayer
{
    private readonly ShopService _shop;
    private PanelContainer _panel = null!;
    private GridContainer _grid = null!;
    private Label _lcd = null!, _cash = null!;
    private Button _hit = null!;
    private Control _flap = null!;
    private readonly List<Button> _keys = new();
    private string _code = "";
    private string _message = "";
    private double _messageUntil;
    private int _shownDrop = -1;
    private Inventory? _inventory;

    public VendingUi(ShopService shop) => _shop = shop;
    public VendingUi() : this(null!) { }

    public bool IsOpen => _panel.Visible;

    /// <summary>What the LCD shows (probes read it).</summary>
    public string LcdText => _lcd.Text;

    public override void _Ready()
    {
        Layer = 12;
        var root = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _panel = new PanelContainer { Theme = UiTheme.Get(), Visible = false };
        // the cabinet: PAUSA red under the glass
        _panel.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.55f, 0.06f, 0.08f, 0.96f), 14, 16, 14, new Color(1, 1, 1, 0.12f), 1));
        root.AddChild(_panel);

        var box = UiKit.VBox(10);
        _panel.AddChild(box);
        // the white band and the wordmark
        var band = new PanelContainer();
        band.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.96f, 0.96f, 0.94f), 6, 12, 4));
        var word = UiKit.Text("PAUSA", 28, new Color(0.80f, 0.08f, 0.10f), bold: true, align: HorizontalAlignment.Center);
        band.AddChild(word);
        box.AddChild(band);

        var face = UiKit.HBox(14);
        box.AddChild(face);

        var glassCol = UiKit.VBox(8);
        face.AddChild(glassCol);
        var glass = new PanelContainer();
        glass.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.08f, 0.09f, 0.11f, 0.95f), 8, 10, 10, new Color(0.7f, 0.85f, 1f, 0.25f), 2));
        glassCol.AddChild(glass);
        _grid = new GridContainer { Columns = ShopTables.VendingCols };
        _grid.AddThemeConstantOverride("h_separation", 6);
        _grid.AddThemeConstantOverride("v_separation", 8);
        glass.AddChild(_grid);
        // the pickup flap: where a bought item lands
        _flap = new Panel { CustomMinimumSize = new Vector2(0, 44) };
        _flap.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.12f, 0.12f, 0.13f, 0.95f), 6, 8, 4, new Color(1, 1, 1, 0.08f), 1));
        glassCol.AddChild(_flap);

        var pad = UiKit.VBox(8);
        pad.CustomMinimumSize = new Vector2(190, 0);
        face.AddChild(pad);
        var lcdBox = new PanelContainer();
        lcdBox.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.42f, 0.62f, 0.44f), 4, 8, 6, new Color(0, 0, 0, 0.5f), 2));
        _lcd = UiKit.Text("", UiTheme.FontSmall, new Color(0.06f, 0.12f, 0.06f), bold: true, align: HorizontalAlignment.Center, wrap: true);
        _lcd.CustomMinimumSize = new Vector2(170, 44);
        lcdBox.AddChild(_lcd);
        pad.AddChild(lcdBox);

        var keys = new GridContainer { Columns = 3 };
        keys.AddThemeConstantOverride("h_separation", 6);
        keys.AddThemeConstantOverride("v_separation", 6);
        pad.AddChild(keys);
        foreach (string k in new[] { "A", "B", "C", "D", "1", "2", "3", "4", "5", "6", "CLR", "OK" })
        {
            var b = UiKit.Button(k, primary: k == "OK", minWidth: 54);
            string key = k;
            b.Pressed += () => Press(key);
            keys.AddChild(b);
            _keys.Add(b);
        }
        _cash = UiKit.Text("", UiTheme.FontSmall, UiTheme.Text, align: HorizontalAlignment.Center);
        pad.AddChild(_cash);
        pad.AddChild(UiKit.Text("Coins only", UiTheme.FontTiny, new Color(1, 1, 1, 0.6f), align: HorizontalAlignment.Center));
        _hit = UiKit.Button("Hit the machine", minWidth: 170);
        _hit.Pressed += () => _shop.Bump();
        pad.AddChild(_hit);
    }

    public override void _ExitTree() => Unhook();

    public void Open(InteriorNode node, FurniturePlan machine)
    {
        Unhook();
        _inventory = _shop.Items?.Inventory;
        _shop.Changed += Rebuild;
        if (_inventory != null) _inventory.Changed += Rebuild;
        _code = "";
        _message = "";
        _shownDrop = -1;
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Rebuild();
    }

    public void Close()
    {
        Unhook();
        if (!IsOpen) return;
        _panel.Visible = false;
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    private void Unhook()
    {
        if (_shop == null) return;
        _shop.Changed -= Rebuild;
        if (_inventory != null) _inventory.Changed -= Rebuild;
    }

    /// <summary>A line on the LCD for a few seconds (an answer, a refusal).</summary>
    public void Lcd(string text)
    {
        if (!IsOpen) return;
        _message = text.ToUpperInvariant();
        _messageUntil = Time.GetTicksMsec() / 1000.0 + 3.0;
        ShowLcd();
    }

    /// <summary>A key of the pad: a row letter, a column digit, CLR or OK.</summary>
    public void Press(string key)
    {
        if (!IsOpen || _shop.OutOfOrder) return;
        if (key == "CLR") _code = "";
        else if (key == "OK")
        {
            int slot = ShopTables.ParseSlot(_code);
            if (slot < 0) Lcd("Code?");
            else _shop.Buy(slot, 1, PayWith.Cash);
            _code = "";
        }
        else if (key.Length == 1 && char.IsLetter(key[0])) _code = key;
        else if (_code.Length == 1) _code += key;
        _message = "";
        ShowLcd();
    }

    private void ShowLcd()
    {
        if (_shop.OutOfOrder) { _lcd.Text = "HORS SERVICE"; return; }
        if (_message.Length > 0 && Time.GetTicksMsec() / 1000.0 < _messageUntil) { _lcd.Text = _message; return; }
        int slot = ShopTables.ParseSlot(_code);
        _lcd.Text = _shop.Waiting && _shop.OpenStock.Count == 0 ? "…"
            : _shop.Stuck ? "STUCK! HIT IT"
            : slot >= 0 && slot < _shop.OpenStock.Count ? $"{_code}  {_shop.OpenStock[slot].Price} CHF  OK?"
            : _code.Length > 0 ? _code : "CODE?";
    }

    private void Rebuild()
    {
        if (!IsOpen) return;
        foreach (var c in _grid.GetChildren()) c.QueueFree();
        int cash = _inventory?.Cash ?? 0;
        foreach (var line in _shop.OpenStock)
        {
            int left = _shop.Left(line.Slot);
            var card = new PanelContainer { CustomMinimumSize = new Vector2(70, 74) };
            card.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(1, 1, 1, line.Slot == _shownDrop ? 0.22f : 0.06f), 6, 4, 4));
            var v = UiKit.VBox(1);
            card.AddChild(v);
            v.AddChild(new TextureRect
            {
                Texture = ItemIcons.Get(line.Id), CustomMinimumSize = new Vector2(32, 32),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest, MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = left > 0 ? Colors.White : new Color(1, 1, 1, 0.2f),
            });
            v.AddChild(UiKit.Text($"{ShopTables.SlotName(line.Slot)}  {line.Price}.-", UiTheme.FontTiny, left > 0 ? UiTheme.Text : UiTheme.TextFaint, align: HorizontalAlignment.Center));
            v.AddChild(UiKit.Text(left > 0 ? $"×{left}" : "empty", UiTheme.FontTiny, left > 0 ? UiTheme.TextDim : UiTheme.Bad, align: HorizontalAlignment.Center));
            card.TooltipText = ItemDefs.Get(line.Id)?.Name ?? "";
            _grid.AddChild(card);
        }
        _cash.Text = $"Cash {ShopUi.Chf(cash)}";
        foreach (var k in _keys) k.Disabled = _shop.OutOfOrder || _shop.Waiting;
        _hit.Visible = _shop.Stuck;
        if (_shop.LastDropped >= 0 && _shop.LastDropped != _shownDrop && _shop.LastDropped < _shop.OpenStock.Count)
            Drop(_shop.OpenStock[_shop.LastDropped].Id);
        _shownDrop = _shop.LastDropped;
        ShowLcd();
    }

    /// <summary>The spiral turned: the item falls into the flap and sits there a moment.</summary>
    private void Drop(ItemId id)
    {
        var icon = new TextureRect
        {
            Texture = ItemIcons.Get(id), Size = new Vector2(32, 32), Position = new Vector2(20, -60),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _flap.AddChild(icon);
        var t = icon.CreateTween();
        t.TweenProperty(icon, "position:y", 6f, 0.45).SetTrans(Tween.TransitionType.Bounce).SetEase(Tween.EaseType.Out);
        t.TweenInterval(1.2);
        t.TweenProperty(icon, "modulate:a", 0f, 0.4);
        t.TweenCallback(Callable.From(icon.QueueFree));
    }

    public override void _Process(double delta)
    {
        if (IsOpen && _message.Length > 0 && Time.GetTicksMsec() / 1000.0 >= _messageUntil)
        {
            _message = "";
            ShowLcd();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)
            || e.IsActionPressed(PlayerInput.InteractMount) || e.IsActionPressed(PlayerInput.Inventory))
        {
            _shop.Close();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is not InputEventKey k) return;
        string? key = k.Keycode switch
        {
            >= Key.A and <= Key.D => ((char)('A' + (k.Keycode - Key.A))).ToString(),
            >= Key.Key1 and <= Key.Key6 => ((char)('1' + (k.Keycode - Key.Key1))).ToString(),
            >= Key.Kp1 and <= Key.Kp6 => ((char)('1' + (k.Keycode - Key.Kp1))).ToString(),
            Key.Enter or Key.KpEnter => "OK",
            Key.Backspace or Key.Delete => "CLR",
            Key.Space => "HIT",
            _ => null,
        };
        if (key == null) return;
        if (key == "HIT") { if (_shop.Stuck) _shop.Bump(); }
        else Press(key);
        GetViewport().SetInputAsHandled();
    }
}
