using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// The teller desk of a bank (#213): the one place cash moves between the pocket and the account.
/// Opened with E at a <see cref="FurnitureType.TellerDesk"/> (<c>LootService.TrySearch</c>):
/// deposit all the cash you carry, or draw some back out. Every move is the server's to grant
/// (<see cref="Bank"/>), which also checks you stand in a bank. Walking away, E or Esc closes it.
/// </summary>
public partial class BankCounterUi : CanvasLayer
{
    private const float CloseReach = 3.2f;

    private PanelContainer _panel = null!;
    private Label _cash = null!, _account = null!, _status = null!;
    private Button _deposit = null!;
    private readonly List<(Button Button, int Amount)> _withdraw = new();
    private FootPlayer? _player;
    private InteriorNode? _node;
    private FurniturePlan? _desk;

    public bool IsOpen => _panel.Visible;

    public override void _Ready()
    {
        Layer = 12;
        var root = new CenterContainer();
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(root);

        _panel = new PanelContainer { Visible = false, CustomMinimumSize = new Vector2(420, 0) };
        _panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.07f, 0.09f, 0.94f), BorderColor = new Color(0.78f, 0.66f, 0.32f),
            BorderWidthBottom = 2, BorderWidthTop = 2, BorderWidthLeft = 2, BorderWidthRight = 2,
            ContentMarginLeft = 22, ContentMarginRight = 22, ContentMarginTop = 18, ContentMarginBottom = 18,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6, CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
        });
        root.AddChild(_panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        _panel.AddChild(box);

        var title = new Label { Text = "BANK — counter", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.5f));
        box.AddChild(title);

        _cash = new Label();
        _account = new Label();
        box.AddChild(_cash);
        box.AddChild(_account);

        _deposit = new Button();
        _deposit.Pressed += () => Bank.Instance?.ClaimAll();
        box.AddChild(_deposit);

        box.AddChild(new Label { Text = "Withdraw" });
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        box.AddChild(row);
        foreach (int amount in new[] { 20, 50, 100, 500, 0 })
        {
            var b = new Button { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            int a = amount;
            b.Pressed += () => Bank.Instance?.Withdraw(a > 0 ? a : (int)Math.Min(int.MaxValue, Bank.Instance.Balance));
            row.AddChild(b);
            _withdraw.Add((b, amount));
        }

        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeColorOverride("font_color", new Color(0.65f, 0.67f, 0.7f));
        box.AddChild(_status);

        if (Bank.Instance is { } bank) bank.BalanceChanged += OnBalanceChanged;
    }

    public override void _ExitTree()
    {
        if (Bank.Instance is { } bank) bank.BalanceChanged -= OnBalanceChanged;
        UiFocus.Set(this, false);
    }

    public void Open(FootPlayer player, InteriorNode node, FurniturePlan desk)
    {
        _player = player;
        _node = node;
        _desk = desk;
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
        Bank.Instance?.Refresh();
        Refresh();
    }

    public void Close()
    {
        if (!IsOpen) return;
        _panel.Visible = false;
        _player = null;
        UiFocus.Set(this, false);
        MouseCapture.Capture();
    }

    private void OnBalanceChanged(long deposited)
    {
        if (IsOpen && deposited > 0) _status.Text = $"Deposited {Chf(deposited)}. Your money is safe here.";
        Refresh();
    }

    private void Refresh()
    {
        if (!IsOpen) return;
        var bank = Bank.Instance;
        int cash = bank?.Pocket?.Cash ?? 0;
        long balance = bank?.Balance ?? 0;
        bool pending = bank?.Pending == true;
        _cash.Text = $"Cash on you:  {Chf(cash)}";
        _account.Text = $"Account:  {Chf(balance)}";
        _deposit.Text = pending ? "…" : cash > 0 ? $"Deposit {Chf(cash)}" : "No cash to deposit";
        _deposit.Disabled = pending || cash <= 0 || bank == null;
        foreach (var (b, amount) in _withdraw)
        {
            b.Text = amount > 0 ? amount.ToString() : "All";
            b.Disabled = pending || bank == null || balance <= 0 || amount > balance;
        }
        if (string.IsNullOrEmpty(_status.Text) || pending)
            _status.Text = pending ? "The teller is counting…" : "Cash you carry is lost if you are knocked out.";
    }

    public override void _Process(double delta)
    {
        if (!IsOpen) return;
        if (_player is not { } p || !IsInstanceValid(p) || _node is not { } node || !IsInstanceValid(node) || _desk is not { } d)
        {
            Close();
            return;
        }
        var local = node.ToLocal(p.GlobalPosition);
        if (new Vector2(local.X - d.X, local.Z - d.Z).Length() > CloseReach + Mathf.Max(d.W, d.D) / 2) Close();
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

    private static string Chf(long amount) =>
        amount.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", "'") + " CHF";
}
