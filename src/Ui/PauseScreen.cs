using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The in-game Esc menu: Resume, Settings, Controls, Leave to the main menu, Quit. It does not
/// stop the world — online nothing can, and one rule is simpler than two — but it takes the
/// pointer and holds the movement keys while open. Esc / Start / B resumes.
/// </summary>
public partial class PauseScreen : Screen
{
    private Button _resume = null!;

    public static PauseScreen Create() => new() { Name = "Pause" };

    public override void _Ready()
    {
        var dim = new ColorRect { Color = new Color(0.01f, 0.012f, 0.02f, 0.45f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        AddChild(TitleScreen.SideShade(0.9f, 0.55f));

        var column = UiKit.VBox(6);
        column.SetAnchorsPreset(LayoutPreset.LeftWide);
        column.OffsetLeft = 72; column.OffsetRight = 72 + 360;
        column.Alignment = BoxContainer.AlignmentMode.Center;
        AddChild(column);

        column.AddChild(UiKit.Text("Paused", 44, Colors.White, bold: true));
        column.AddChild(UiKit.Text(Shell.SessionLine(), UiTheme.FontSmall, UiTheme.Amber));
        column.AddChild(UiKit.Spacer(26));

        _resume = Entry(column, "Resume", () => Shell.Back());
        Entry(column, "Settings", () => Shell.Push(SettingsScreen.Create()));
        Entry(column, "Controls", () => Shell.ShowControls());
        // the medic armband (#218): online, outside a Battle Royale match
        if (Shell.World is { } world && Net.NetLink.Online(world) && BattleRoyale.BrManager.Instance?.InMatch != true
            && LocalPlayer(world) is { } me && world.GetNodeOrNull<Net.ChatManager>(Net.ChatManager.NodeName) is { } chat)
        {
            _me = me;
            _medic = Entry(column, Combat.Medic.MenuText(me), () => chat.Send(me.Medic ? "/medic off" : "/medic on"));
        }
        column.AddChild(UiKit.Spacer(8));
        Entry(column, "Leave to main menu", () =>
        {
            if (Shell.Hosting)
                Modal.Confirm(this, "Stop your server?", "You are hosting this game. Leaving stops the server for everyone on it.",
                    "Leave and stop", () => Shell.LeaveWorld(null), danger: true);
            else Shell.LeaveWorld(null);
        });
        Entry(column, "Quit to desktop", () => Shell.Quit(), dim: true);

        var foot = UiKit.Text(InputHints.Format("{menu} resumes  ·  {help} all controls"), UiTheme.FontTiny, UiTheme.TextFaint);
        foot.SetAnchorsPreset(LayoutPreset.BottomLeft);
        foot.OffsetLeft = 72; foot.OffsetTop = -50; foot.OffsetBottom = -26;
        AddChild(foot);
    }

    private Player.FootPlayer? _me;
    private Button? _medic;
    private double _medicTick;

    private static Player.FootPlayer? LocalPlayer(Node world)
    {
        foreach (var n in world.GetTree().GetNodesInGroup(Player.FootPlayer.Group))
            if (n is Player.FootPlayer p && !p.Npc && p.IsMultiplayerAuthority()) return p;
        return null;
    }

    public override void _Process(double delta)
    {
        if (_medic == null || _me == null || !IsInstanceValid(_me) || (_medicTick -= delta) > 0) return;
        _medicTick = 0.25;   // the timer shows seconds: no string built every frame
        string text = Combat.Medic.MenuText(_me);
        if (_medic.Text != text) _medic.Text = text;
        _medic.Disabled = !_me.Medic && (Combat.Medic.CooldownEnds > Combat.Medic.Now || Combat.Medic.PendingEnds > Combat.Medic.Now);
    }

    private static Button Entry(Container into, string text, Action pressed, bool dim = false)
    {
        var b = UiKit.MenuButton(text, dim ? 17 : 21);
        if (dim) b.AddThemeColorOverride("font_color", UiTheme.TextDim);
        b.Pressed += pressed;
        into.AddChild(b);
        return b;
    }

    public override void OnShown() => _resume.CallDeferred(Control.MethodName.GrabFocus);
}
