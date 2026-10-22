using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// The in-game Esc menu: Resume, Settings, Controls (Skip tutorial while it runs), Leave to the main menu, Quit. It does not
/// stop the world — online nothing can, and one rule is simpler than two — but it takes the
/// pointer and holds the movement keys while open. Esc / Start / B resumes.
/// </summary>
public partial class PauseScreen : Screen
{
    private Button _resume = null!;
    private Button? _accept;

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

        var paused = UiKit.Text("Paused", 48);
        UiKit.Display(paused);   // the skin's title face (#1135)
        column.AddChild(paused);
        column.AddChild(UiKit.Text(Shell.SessionLine(), UiTheme.FontSmall, UiTheme.Amber));
        column.AddChild(UiKit.Spacer(26));

        // an invite waiting (#840): answering it is the first thing here, so a pad does it with Start, A
        if (Net.InviteService.Client?.Newest is { } invite)
        {
            int id = invite.Id;
            _accept = Entry(column, $"Accept: {invite.Title}", () => { Net.InviteService.Client?.Accept(id); Shell.Back(); });
            _accept.ClipText = true;
            _accept.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        }
        _resume = Entry(column, "Resume", () => Shell.Back());
        // launch, configure and join (#840): what the console does, as a page
        if (Permissions.Online) Entry(column, "Activities", () => Shell.Push(ActivitiesScreen.Create(
            Net.InviteService.Client is { IncomingCount: > 0 } ? ActivitiesScreen.Tab.Invites : ActivitiesScreen.Tab.BattleRoyale)));
        // the movie studio (#638): what a pad or VR player has instead of F5, and the way in
        if (Shell.World is { } world && Movie.ReplayRecorder.Instance != null)
        {
            Entry(column, "Save clip", () => { Movie.MovieSession.SaveClip(); Shell.Back(); });
            // Record / Stop (#775): Shift+F5 on a keyboard, this on a pad or in VR
            var recorder = Movie.ReplayRecorder.Instance;
            Entry(column, recorder.Recording ? $"Stop recording ({Movie.MovieSession.Clock(recorder.SessionSeconds)})" : "Record",
                () => { Movie.MovieSession.ToggleRecording(); Shell.Back(); });
            Entry(column, "Movie studio", () =>
            {
                Movie.MovieSession.SaveClip(quiet: true);
                Shell.Push(Movie.MovieStudio.Create(world));
            });
        }
        Entry(column, "Settings", () => Shell.Push(SettingsScreen.Create()));
        Entry(column, "Controls", () => Shell.ShowControls());
        if (Tutorial.Current is { } tutorial)
            Entry(column, "Skip tutorial", () => { tutorial.Skip(); Shell.Back(); });
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

    private static Button Entry(Container into, string text, Action pressed, bool dim = false)
    {
        var b = UiKit.MenuButton(text, dim ? 17 : 21);
        if (dim) UiKit.DimFont(b);
        b.Pressed += pressed;
        into.AddChild(b);
        return b;
    }

    public override void OnShown() => (_accept ?? _resume).CallDeferred(Control.MethodName.GrabFocus);
}
