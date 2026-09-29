using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Loot;

/// <summary>
/// The panel over an open container: its stacks drawn exactly like inventory slots, click (or A)
/// to take one, "Take all". E, B or Esc closes it, and so does walking away
/// (<see cref="LootService._Process"/>).
/// </summary>
public partial class LootUi : CanvasLayer
{
    private const int SlotPx = 58;

    private readonly LootService _service;
    private PanelContainer _panel = null!;
    private Label _title = null!;
    private Label _status = null!;
    private HFlowContainer _grid = null!;
    private Button _takeAll = null!;
    private Label _itemName = null!;

    public LootUi(LootService service) => _service = service;
    public LootUi() : this(null!) { }

    public bool IsOpen => _panel.Visible;

    public override void _Ready()
    {
        // same layer as the inventory panel, so both read as one kind of screen
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
        _panel.OffsetLeft = -200;
        _panel.OffsetRight = 200;
        _panel.OffsetTop = -120;
        _panel.OffsetBottom = 120;
        AddChild(_panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(box);

        _title = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 20);
        box.AddChild(_title);

        _grid = new HFlowContainer { CustomMinimumSize = new Vector2(360, SlotPx) };
        _grid.AddThemeConstantOverride("h_separation", 6);
        _grid.AddThemeConstantOverride("v_separation", 6);
        box.AddChild(_grid);

        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(0.7f, 0.72f, 0.76f) };
        box.AddChild(_status);

        _itemName = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        box.AddChild(_itemName);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 12);
        box.AddChild(row);
        _takeAll = new Button { Text = "Take all" };
        _takeAll.Pressed += () => _service.TakeAll();
        row.AddChild(_takeAll);
        var close = new Button { Text = "Close" };
        close.Pressed += () => _service.Close();
        row.AddChild(close);
    }

    public void Open(string what)
    {
        _title.Text = char.ToUpperInvariant(what[0]) + what[1..];
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Refresh();
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        UiFocus.Set(this, false);
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public void Refresh()
    {
        if (!IsOpen) return;
        bool hadFocus = _grid.GetChildren().OfType<Control>().Any(c => c.HasFocus()) || _takeAll.HasFocus();
        foreach (var child in _grid.GetChildren()) child.QueueFree();

        var contents = _service.OpenContents().ToList();
        foreach (var (index, stack) in contents)
        {
            var slot = new SlotButton
            {
                Slot = index,
                CustomMinimumSize = new Vector2(SlotPx, SlotPx),
                TooltipText = ItemDefs.Get(stack.Id)?.Name ?? "",
            };
            int i = index;
            slot.Pressed += () => _service.Take(i);
            var def = ItemDefs.Get(stack.Id);
            slot.FocusEntered += () => _itemName.Text = Describe(stack, def);
            slot.MouseEntered += () => _itemName.Text = Describe(stack, def);
            _grid.AddChild(slot);
            slot.Display(stack, false, false);
        }

        _status.Text = _service.Waiting ? "Searching…" : contents.Count == 0 ? "Nothing useful here." : "";
        _takeAll.Disabled = contents.Count == 0 || _service.Waiting;
        _itemName.Text = "";
        if (hadFocus || contents.Count > 0) PlayerInput.FocusFirst(_panel);
    }

    private static string Describe(ItemStack stack, ItemDef? def) =>
        def == null ? "" : stack.Id == ItemId.Francs ? $"{stack.Count} CHF" : $"{def.Name} ×{stack.Count}";

    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsOpen || !e.IsPressed() || e.IsEcho()) return;
        if (e.IsActionPressed("ui_cancel") || e.IsActionPressed(PlayerInput.Menu)
            || e.IsActionPressed(PlayerInput.InteractMount) || e.IsActionPressed(PlayerInput.Inventory))
        {
            _service.Close();
            GetViewport().SetInputAsHandled();
        }
    }
}
