using Godot;
using UnitSport.Net.Accounts;

namespace UnitSport.Ui;

/// <summary>
/// The Account page (#1209), opened from the title screen's account button: the profile picture and name, a way to the
/// figure (the Character menu), and the account page (<see cref="AccountPage"/>: offline or online, sign in, create an
/// account, sign out). It replaced Settings' Account tab.
/// </summary>
public partial class AccountScreen : Screen
{
    private AvatarBadge _badge = null!;
    private Label _name = null!, _status = null!;

    public static AccountScreen Create() => new() { Name = "Account" };

    public override void _Ready()
    {
        var (body, _) = Framed("Account", "The same account on the website and in the game", new Vector2(940, 660));

        var top = UiKit.HBox(18);
        _badge = new AvatarBadge(92);
        top.AddChild(_badge);
        var who = UiKit.VBox(4);
        who.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        who.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _name = UiKit.Text("", 26);
        UiKit.Display(_name);
        who.AddChild(_name);
        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        who.AddChild(_status);
        top.AddChild(who);
        var figure = UiKit.Button("Change your figure");
        figure.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        figure.Pressed += () => Shell.Push(CharacterScreen.Create());
        top.AddChild(figure);
        body.AddChild(top);
        body.AddChild(UiKit.Line());

        var (scroll, rows) = UiKit.ScrollPage(4);
        body.AddChild(scroll);
        AccountPage.Fill(rows);

        Show();
        Account.Changed += Show;
    }

    public override void _ExitTree() => Account.Changed -= Show;

    public override void OnShown()
    {
        base.OnShown();
        _badge.Refresh();   // back from the Character menu: the new face
    }

    private void Show()
    {
        if (!IsInstanceValid(_name)) return;
        _badge.Portrait = Account.SignedIn;
        string player = Core.GameSettings.Current.PlayerName;
        _name.Text = Account.SignedIn ? Account.Username : player.Length > 0 ? player : "Not signed in";
        _status.Text = Account.Mode == AccountMode.Offline ? "Playing offline: no account"
            : Account.SignedIn ? $"Signed in · {Account.Email}" : "Online, not signed in";
    }
}
