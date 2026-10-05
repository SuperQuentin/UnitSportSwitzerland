using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Ui;

namespace UnitSport.Loot;

/// <summary>
/// A shop's counter (#273): the catalogue next to your pack. Each line shows what is left this
/// restock period, the price, and how it may be paid: cash, or the card too from 20 CHF in the
/// shops that take it (the bank account, charged by the server), the shotgun on the card only.
/// Drag a pack line onto the catalogue, or press Sell, to sell it for 35 % of its value, if the
/// shop buys that kind of thing. E, Tab or Esc closes it; walking away does too (<see cref="ShopService"/>).
/// </summary>
public partial class ShopUi : CanvasLayer
{
    private readonly ShopService _shop;
    private PanelContainer _panel = null!;
    private Label _title = null!, _money = null!, _status = null!, _buys = null!;
    private VBoxContainer _catalogue = null!, _pack = null!, _orders = null!;
    /// <summary>The open shop's type and building (a farm co-op shows its market and orders, #494).</summary>
    private ShopType _type;
    private string _key = "";
    private Inventory? _inventory;

    public ShopUi(ShopService shop) => _shop = shop;
    public ShopUi() : this(null!) { }

    public bool IsOpen => _panel.Visible;

    public override void _Ready()
    {
        Layer = 12;
        var root = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _panel = new PanelContainer { Theme = UiTheme.Get(), Visible = false, CustomMinimumSize = new Vector2(780, 0) };
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

        var columns = UiKit.HBox(14);
        box.AddChild(columns);

        var drop = new ShopDrop { Dropped = id => _shop.Sell(id, _inventory?.CountPlain(id) ?? 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        drop.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.10f, 0.115f, 0.14f, 0.55f), 10, 10, 10, new Color(1, 1, 1, 0.07f), 1));
        columns.AddChild(drop);
        var left = UiKit.VBox(6);
        drop.AddChild(left);
        left.AddChild(UiKit.Section("Catalogue"));
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(450, 400), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        left.AddChild(scroll);
        _catalogue = UiKit.VBox(4);
        _catalogue.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_catalogue);

        var right = UiKit.VBox(6);
        right.CustomMinimumSize = new Vector2(280, 0);
        columns.AddChild(right);
        right.AddChild(UiKit.Section("Your pack"));
        _buys = UiKit.Text("", UiTheme.FontTiny, UiTheme.TextFaint, wrap: true);
        right.AddChild(_buys);
        var packScroll = new ScrollContainer { CustomMinimumSize = new Vector2(280, 370), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        right.AddChild(packScroll);
        _pack = UiKit.VBox(4);
        _pack.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        packScroll.AddChild(_pack);

        // a farm co-op's orders and the player's contracts (#494, Farming.CoopPanel)
        _orders = UiKit.VBox(4);
        box.AddChild(_orders);

        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim, align: HorizontalAlignment.Center, wrap: true);
        box.AddChild(_status);
    }

    public override void _ExitTree() => Unhook();

    public void Open(InteriorNode node, FurniturePlan counter, ShopType type)
    {
        Unhook();
        _inventory = _shop.Items?.Inventory;
        _shop.Changed += Rebuild;
        if (_inventory != null) _inventory.Changed += Rebuild;
        if (Bank.Instance is { } bank) bank.BalanceChanged += OnBalance;
        _type = type;
        _key = _shop.Open?.Key ?? "";
        if (Farming.FarmSales.Instance is { } sales && type == ShopType.FarmCoop)
        {
            sales.ContractsChanged += Rebuild;
            sales.Refresh();
        }
        _title.Text = ShopTables.Name(type);
        _buys.Text = type == ShopType.FarmCoop ? BuysText(type) + " " + Farming.CoopPanel.MarketLine(_key) : BuysText(type);
        _status.Text = "Cash under 20 CHF; from 20 CHF most shops take the card too (your bank account).";
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Bank.Instance?.Refresh();
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
        if (Bank.Instance is { } bank) bank.BalanceChanged -= OnBalance;
        if (Farming.FarmSales.Instance is { } sales) sales.ContractsChanged -= Rebuild;
    }

    private void OnBalance(long _) => Rebuild();

    public void Status(string text)
    {
        if (IsOpen) _status.Text = text;
    }

    private static string BuysText(ShopType type)
    {
        var cats = Enum.GetValues<ItemCategory>().Where(c => ShopTables.Buys(type, c)).Select(c => c.ToString().ToLowerInvariant()).ToList();
        return cats.Count == 0 ? "This shop buys nothing." : $"Buys {string.Join(", ", cats)} for 35 % of its value: drag a line onto the catalogue, or Sell."
            + (type == ShopType.FarmCoop ? " Harvest prices follow the season and the week's wishes." : "");
    }

    private void Rebuild()
    {
        if (!IsOpen) return;
        int cash = _inventory?.Cash ?? 0;
        long account = Bank.Instance?.Balance ?? 0;
        _money.Text = $"Cash {Chf(cash)}   ·   Card {Chf(account)}";
        foreach (var c in _catalogue.GetChildren()) c.QueueFree();
        foreach (var c in _pack.GetChildren()) c.QueueFree();

        if (_shop.OpenStock.Count == 0)
        {
            _catalogue.AddChild(UiKit.Text(_shop.Waiting ? "…" : "Nothing for sale.", UiTheme.FontSmall, UiTheme.TextDim));
        }
        foreach (var line in _shop.OpenStock)
        {
            int left = _shop.Left(line.Slot);
            if (line.Stock <= 0 && line.Id != ItemId.Shotgun) continue;   // not carried this period
            var row = UiKit.HBox(8);
            row.AddChild(Icon(line.Id));
            var name = UiKit.Text(ItemDefs.Get(line.Id)?.Name ?? line.Id.ToString(), UiTheme.FontSmall, left > 0 ? UiTheme.Text : UiTheme.TextFaint);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(name);
            row.AddChild(UiKit.Text(line.Stock == 0 ? "out of season" : left > 0 ? $"×{left}" : "sold out", UiTheme.FontTiny, left > 0 ? UiTheme.TextDim : UiTheme.Bad));
            row.AddChild(UiKit.Text($"{line.Price} CHF", UiTheme.FontSmall, UiTheme.Amber));
            int slot = line.Slot;
            if (line.Pay != Payment.Card)
            {
                var buy = UiKit.Button("Cash", minWidth: 64);
                buy.Disabled = left <= 0 || _shop.Waiting || cash < line.Price;
                buy.TooltipText = "Pay cash; shift-click buys 5";
                buy.Pressed += () => _shop.Buy(slot, Input.IsKeyPressed(Key.Shift) ? 5 : 1, PayWith.Cash);
                row.AddChild(buy);
            }
            if (line.Pay != Payment.Cash)
            {
                var card = UiKit.Button("Card", primary: line.Pay == Payment.Card, minWidth: 64);
                card.Disabled = left <= 0 || _shop.Waiting || account < line.Price;
                card.TooltipText = line.Pay == Payment.Card ? "Card only: charged to your bank account" : "Charged to your bank account";
                card.Pressed += () => _shop.Buy(slot, 1, PayWith.Card);
                row.AddChild(card);
            }
            _catalogue.AddChild(row);
        }

        if (_inventory == null) return;
        int shown = 0;
        foreach (var def in ItemDefs.All)
        {
            int each = _shop.SellPriceOf(def.Id);
            int have = each > 0 ? _inventory.CountPlain(def.Id) : 0;
            if (have <= 0) continue;
            shown++;
            var row = new SellRow { Id = def.Id };
            row.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(1, 1, 1, 0.03f), 6, 6, 3));
            var h = UiKit.HBox(6);
            row.AddChild(h);
            h.AddChild(Icon(def.Id));
            var name = UiKit.Text($"{def.Name} ×{have}", UiTheme.FontSmall);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            h.AddChild(name);
            // the co-op wants it this week (#494): say so beside the price
            if (_type == ShopType.FarmCoop && Farming.FarmSales.WantedTag(_key, def.Id) is { Length: > 0 } wanted)
                h.AddChild(UiKit.Text(wanted, UiTheme.FontTiny, UiTheme.Amber));
            h.AddChild(UiKit.Text($"{each} CHF", UiTheme.FontTiny, UiTheme.Good));
            var id = def.Id;
            var sell = UiKit.Button("Sell", minWidth: 52);
            sell.Disabled = _shop.Waiting;
            sell.TooltipText = "Sell one; shift-click sells them all";
            sell.Pressed += () => _shop.Sell(id, Input.IsKeyPressed(Key.Shift) ? have : 1);
            h.AddChild(sell);
            _pack.AddChild(row);
        }
        if (shown == 0) _pack.AddChild(UiKit.Text("Nothing this shop buys.", UiTheme.FontSmall, UiTheme.TextFaint));
        if (_type == ShopType.FarmCoop) Farming.CoopPanel.Fill(_orders, _key);
        else foreach (var c in _orders.GetChildren()) c.QueueFree();
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
            _shop.Close();
            GetViewport().SetInputAsHandled();
        }
    }

    public static string Chf(long amount) =>
        amount.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", "'") + " CHF";
}

/// <summary>A pack line in the shop's panel: dragged onto the catalogue, it is sold.</summary>
public partial class SellRow : PanelContainer
{
    public ItemId Id { get; set; }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        SetDragPreview(new TextureRect
        {
            Texture = ItemIcons.Get(Id),
            Size = new Vector2(40, 40),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            TextureFilter = CanvasItem.TextureFilterEnum.Nearest,
        });
        return (int)Id;
    }
}

/// <summary>The catalogue as a drop target: a pack line let go on it is sold.</summary>
public partial class ShopDrop : PanelContainer
{
    public Action<ItemId>? Dropped { get; set; }

    public override bool _CanDropData(Vector2 atPosition, Variant data) => data.VariantType == Variant.Type.Int;

    public override void _DropData(Vector2 atPosition, Variant data) => Dropped?.Invoke((ItemId)(int)data);
}
