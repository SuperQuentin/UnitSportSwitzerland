using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Ui;

namespace UnitSport.Interiors.Games;

/// <summary>
/// The prize counter's shelves (#1326): what each prize costs in tickets, the tickets held at the top, a Swap button
/// per prize (greyed with the tickets still missing when there are too few). Mouse, keys, pad and a VR pointer through
/// the buttons; Esc / B / E closes it, as the vending machine's panel.
/// </summary>
public partial class PrizeCounterUi : CanvasLayer
{
    private PanelContainer _panel = null!;
    private VBoxContainer _rows = null!;
    private Label _held = null!;
    private Inventory? _inv;
    private Action<string> _say = _ => { };

    public bool IsOpen => _panel.Visible;

    /// <summary>The Swap buttons, a prize's row each (the check presses them).</summary>
    public IEnumerable<Button> Swaps => _rows.GetChildren().OfType<HBoxContainer>().Where(r => !r.IsQueuedForDeletion()).Select(r => (Button)r.GetChild(r.GetChildCount() - 1));

    public override void _Ready()
    {
        Layer = 12;
        var root = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);
        _panel = new PanelContainer { Theme = UiTheme.Get(), Visible = false };
        // the counter's glass case under its neon
        _panel.AddThemeStyleboxOverride("panel", UiTheme.Flat(new Color(0.10f, 0.06f, 0.18f, 0.96f), 14, 18, 14, new Color(0.98f, 0.30f, 0.75f, 0.6f), 2));
        root.AddChild(_panel);
        var box = UiKit.VBox(10);
        box.CustomMinimumSize = new Vector2(420, 0);
        _panel.AddChild(box);
        box.AddChild(UiKit.Text("PRIZES", 28, new Color(0.98f, 0.40f, 0.80f), bold: true, align: HorizontalAlignment.Center));
        _held = UiKit.Text("", UiTheme.FontBody, UiTheme.Text, align: HorizontalAlignment.Center);
        box.AddChild(_held);
        box.AddChild(UiKit.Line());
        _rows = UiKit.VBox(6);
        box.AddChild(_rows);
        var close = UiKit.Button("Close");
        close.Pressed += Close;
        box.AddChild(close);
    }

    public void Open(Inventory inv, Action<string> say)
    {
        Unhook();
        _inv = inv;
        _say = say;
        _inv.Changed += Rebuild;
        _panel.Visible = true;
        MouseCapture.Mode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Rebuild();
        // the first prize the player can have, else the first, takes the focus (pad and keys)
        foreach (var c in _rows.GetChildren())
            if (c is HBoxContainer row && row.GetChild(row.GetChildCount() - 1) is Button { Disabled: false } b) { b.GrabFocus(); return; }
    }

    public void Close()
    {
        Unhook();
        if (!IsOpen) return;
        _panel.Visible = false;
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    public override void _ExitTree() => Unhook();

    private void Unhook()
    {
        if (_inv != null) _inv.Changed -= Rebuild;
    }

    private void Rebuild()
    {
        if (!IsOpen || _inv == null) return;
        int tickets = _inv.CountPlain(ItemId.PrizeTicket);
        _held.Text = $"You hold {tickets} tickets   ·   {_inv.CountPlain(ItemId.ArcadeCoin)} Batzen";
        var focused = GetViewport()?.GuiGetFocusOwner();
        int focusAt = -1;
        int i = 0;
        foreach (var c in _rows.GetChildren())
        {
            if (c is HBoxContainer row && focused != null && row.IsAncestorOf(focused)) focusAt = i;
            i++;
            c.QueueFree();
        }
        var rows = new List<Button>();
        foreach (var (item, price) in ArcadeCounters.Prizes)
        {
            var row = UiKit.HBox(10);
            row.AddChild(new TextureRect
            {
                Texture = ItemIcons.Get(item), CustomMinimumSize = new Vector2(40, 40),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest, MouseFilter = Control.MouseFilterEnum.Ignore,
            });
            var name = UiKit.Text(ItemDefs.Get(item)?.Name ?? item.ToString(), UiTheme.FontBody, UiTheme.Text);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(name);
            row.AddChild(UiKit.Text($"{price} tickets", UiTheme.FontSmall, UiTheme.TextDim));
            bool can = tickets >= price;
            var swap = UiKit.Button(can ? "Swap" : $"{price - tickets} more", primary: can, minWidth: 110);
            swap.Disabled = !can;
            swap.TooltipText = can ? $"{price} tickets for the {ItemDefs.Get(item)?.Name}" : $"You need {price - tickets} more tickets";
            var prize = item;
            swap.Pressed += () => ArcadeCounters.Buy(_inv, prize, _say);
            row.AddChild(swap);
            _rows.AddChild(row);
            rows.Add(swap);
        }
        if (focusAt >= 0 && focusAt < rows.Count) rows[focusAt].CallDeferred(Control.MethodName.GrabFocus);
    }

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
