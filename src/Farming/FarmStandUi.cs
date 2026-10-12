using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Loot;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Farming;

/// <summary>
/// A farm stand's panel (#494), opened with E / Y / a VR hand at the stand (<see cref="FarmStands.TryOpen"/>).
/// The owner sees the crates with "Take back", the pack's produce with "Stock" (shift: all) and the
/// honesty box with "Collect"; anyone else sees the crates with their price and "Buy" (shift: 5),
/// paid in cash into the owner's box. Rebuilt only when the stand, the pack or an answer changes.
/// E, Tab or Esc closes it; walking away does too.
/// </summary>
public partial class FarmStandUi : CanvasLayer
{
    private readonly FarmStands _stands;
    private PanelContainer _panel = null!;
    private Label _title = null!, _money = null!, _status = null!, _box = null!, _rate = null!;
    private VBoxContainer _crates = null!, _pack = null!, _right = null!;
    private Button _collect = null!;
    private FootPlayer? _user;
    private Vector3 _at;
    private Inventory? _inventory;

    public FarmStandUi(FarmStands stands) => _stands = stands;
    public FarmStandUi() : this(null!) { }

    public bool IsOpen => _panel.Visible;
    /// <summary>The stand open in the panel, 0 none.</summary>
    public long OpenId { get; private set; }

    public override void _Ready()
    {
        Layer = 12;
        var root = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        _panel = new PanelContainer { Theme = UiTheme.Get(), Visible = false, CustomMinimumSize = new Vector2(760, 0) };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.92f, 12, 18));
        root.AddChild(_panel);

        var box = UiKit.VBox(10);
        _panel.AddChild(box);
        var head = UiKit.HBox(12);
        box.AddChild(head);
        _title = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true);
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(_title);
        _money = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, align: HorizontalAlignment.Right);
        head.AddChild(_money);
        _rate = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint, wrap: true);
        box.AddChild(_rate);

        var columns = UiKit.HBox(14);
        box.AddChild(columns);
        var left = UiKit.VBox(6);
        left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        columns.AddChild(left);
        left.AddChild(UiKit.Section("On the stand"));
        _crates = UiKit.VBox(4);
        left.AddChild(_crates);
        var boxRow = UiKit.HBox(8);
        left.AddChild(boxRow);
        _box = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber);
        _box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        boxRow.AddChild(_box);
        _collect = UiKit.Button("Collect", primary: true, minWidth: 80);
        _collect.Pressed += () => _stands.Collect(OpenId);
        boxRow.AddChild(_collect);

        _right = UiKit.VBox(6);
        _right.CustomMinimumSize = new Vector2(280, 0);
        columns.AddChild(_right);
        _right.AddChild(UiKit.Section("Your pack"));
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(280, 300), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _right.AddChild(scroll);
        _pack = UiKit.VBox(4);
        _pack.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_pack);

        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, align: HorizontalAlignment.Center, wrap: true);
        box.AddChild(_status);
    }

    public override void _ExitTree() => Unhook();

    public void Open(FootPlayer p, PlacedObject o)
    {
        Unhook();
        _user = p;
        OpenId = o.Id;
        _at = o.WorldTransform(PlacedObjects.Instance?.Origin ?? WorldOrigin.SwissDefault()).Origin;
        _inventory = _stands.Items?.Inventory;
        _stands.Changed += OnChanged;
        if (_inventory != null) _inventory.Changed += Rebuild;
        _title.Text = _stands.IsMine(o.Id) ? "Your farm stand" : $"{o.Owner}'s farm stand";
        _status.Text = _stands.IsMine(o.Id)
            ? "Passers-by buy over time and pay into the honesty box. Shift-click stocks the whole stack."
            : "Self-service: take what you want and pay into the box. Shift-click buys 5.";
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Rebuild();
    }

    public void Close()
    {
        Unhook();
        OpenId = 0;
        _user = null;
        if (!IsOpen) return;
        _panel.Visible = false;
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    private void Unhook()
    {
        if (_stands == null) return;
        _stands.Changed -= OnChanged;
        if (_inventory != null) _inventory.Changed -= Rebuild;
    }

    private void OnChanged(long id)
    {
        if (id == OpenId) Rebuild();
    }

    public void Status(string text)
    {
        if (IsOpen) _status.Text = text;
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        if (_user is not { } p || !IsInstanceValid(p) || !p.IsViewing || p.GlobalPosition.DistanceTo(_at) > FarmStands.Reach + 2f
            || PlacedObjects.Instance?.All.ContainsKey(OpenId) != true)
            Close();
    }

    private void Rebuild()
    {
        if (!IsOpen) return;
        bool mine = _stands.IsMine(OpenId);
        _stands.All.TryGetValue(OpenId, out var s);
        int cash = _inventory?.Cash ?? 0;
        _money.Text = $"Cash {ShopUi.Chf(cash)}";
        foreach (var c in _crates.GetChildren()) c.QueueFree();
        foreach (var c in _pack.GetChildren()) c.QueueFree();
        _rate.Text = s == null ? "" : $"By a road {(float.IsNaN(s.RoadM) || s.RoadM < 0 ? "?" : $"{s.RoadM:F0} m")} away, {s.Houses} buildings round it: "
            + $"about {FarmStandRules.PerDay(s, ItemId.Potato):F0} sales a day a crate (cooked dishes twice that).";

        int shown = 0;
        if (s != null)
            for (int i = 0; i < s.Slots.Count; i++)
            {
                var slot = s.Slots[i];
                if (slot.Count <= 0) continue;
                shown++;
                int price = FarmStands.PriceOf(slot.Item), index = i;
                var row = UiKit.HBox(8);
                row.AddChild(Icon(slot.Item));
                var name = UiKit.Text(FarmSales.NameOfItem(slot.Item), UiTheme.FontSmall);
                name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(name);
                row.AddChild(UiKit.Text($"×{slot.Count}", UiTheme.FontTiny, UiTheme.TextDim));
                row.AddChild(UiKit.Text($"{price} CHF", UiTheme.FontSmall, UiTheme.Amber));
                var b = UiKit.Button(mine ? "Take back" : "Buy", minWidth: 90);
                b.Disabled = _stands.Waiting || !mine && cash < price;
                b.TooltipText = mine ? "Back into your pack; shift-click: the whole crate" : "Pay cash into the honesty box; shift-click buys 5";
                b.Pressed += () =>
                {
                    bool shift = Input.IsKeyPressed(Key.Shift);
                    if (mine) _stands.Take(OpenId, index, shift ? FarmStandRules.PerCrate : 1);
                    else _stands.Buy(OpenId, index, shift ? 5 : 1);
                };
                row.AddChild(b);
                _crates.AddChild(row);
            }
        if (shown == 0) _crates.AddChild(UiKit.Text(mine ? "Empty: stock it from your pack." : "Nothing for sale right now.", UiTheme.FontSmall, UiTheme.TextFaint));

        _box.Visible = _collect.Visible = mine;
        _right.Visible = mine;
        if (!mine) return;
        _box.Text = $"Honesty box: {s?.Cash ?? 0} CHF   (taken in all: {s?.Takings ?? 0} CHF)";
        _collect.Disabled = _stands.Waiting || (s?.Cash ?? 0) <= 0;
        if (_inventory == null) return;
        int listed = 0;
        foreach (var def in ItemDefs.All)
        {
            if (!FarmStandRules.Stockable(def.Id)) continue;
            int have = _inventory.CountPlain(def.Id);
            if (have <= 0) continue;
            listed++;
            var row = UiKit.HBox(6);
            row.AddChild(Icon(def.Id));
            var name = UiKit.Text($"{def.Name} ×{have}", UiTheme.FontSmall);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(name);
            row.AddChild(UiKit.Text($"{FarmStands.PriceOf(def.Id)} CHF", UiTheme.FontTiny, UiTheme.Good));
            var id = def.Id;
            var stock = UiKit.Button("Stock", minWidth: 60);
            stock.Disabled = _stands.Waiting;
            stock.TooltipText = "Put one on the stand; shift-click: the whole stack";
            stock.Pressed += () => _stands.Stock(OpenId, id, Input.IsKeyPressed(Key.Shift) ? have : 1);
            row.AddChild(stock);
            _pack.AddChild(row);
        }
        if (listed == 0) _pack.AddChild(UiKit.Text("No produce in your pack: harvests and what is made of them go on a stand.", UiTheme.FontSmall, UiTheme.TextFaint, wrap: true));
    }

    private static TextureRect Icon(ItemId id) => new()
    {
        Texture = ItemIcons.Get(id),
        CustomMinimumSize = new Vector2(28, 28),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)
            || e.IsActionPressed(PlayerInput.InteractMount) || e.IsActionPressed(PlayerInput.Inventory))
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
