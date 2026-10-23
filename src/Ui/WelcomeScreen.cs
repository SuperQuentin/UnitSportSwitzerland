using System.Text.Json.Nodes;
using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Ui;

/// <summary>
/// The first-run welcome (#1131): three pages over the title screen on the very first launch, each one skippable.
/// <list type="number">
/// <item>Name: the Multiplayer name rules (2-20 letters, digits, - and _); empty keeps "no name yet".</item>
/// <item>Character: the figure editor (<see cref="CharacterPage"/>, #1127).</item>
/// <item>Account: offline (the default) or online (<see cref="AccountPage"/>, #1123).</item>
/// </list>
/// Shown when <see cref="GameSettings.FirstRunDone"/> is false (a fresh install: no settings file yet) and the game
/// was started with no command-line arguments, so checks and tools never meet it; <c>--welcome</c> forces it, and
/// Settings › Gameplay shows it again. Back / Next / Skip at the bottom; Esc or pad B goes a page back, and from the
/// first page closes it. <c>--welcome N</c> opens on page N (screenshots). Docs: <c>docs/notes/ui/welcome.md</c>.
/// </summary>
public partial class WelcomeScreen : Screen
{
    private static readonly string[] Titles = { "Your name", "Your figure", "Your account" };

    /// <summary>Where the pages save: the settings file, or a check's recorder (<see cref="WelcomeCheck"/>) that saves nothing.</summary>
    public static System.Action<string, JsonNode> Save = WelcomeCheck.Requested() ? WelcomeCheck.Record : GameSettings.SaveOnly;

    private VBoxContainer _page = null!;
    private Label _subtitle = null!;
    private Button _back = null!, _next = null!;
    private LineEdit? _name;
    private Label? _nameError;
    private bool _closing;

    public int Step { get; private set; }

    /// <summary>Shown at the title on a fresh install started without arguments, or with <c>--welcome</c>.</summary>
    public static bool Wanted() =>
        CmdArgs.Has("--welcome") || WelcomeCheck.Requested() || (!GameSettings.Current.FirstRunDone && OS.GetCmdlineUserArgs().Length == 0);

    public static WelcomeScreen Create() => new() { Name = "Welcome" };

    public override void _Ready()
    {
        // not done until it is: quitting half-way shows it again next time
        Save(nameof(GameSettings.FirstRunDone), false);
        var (body, header) = Framed("Welcome to UnitSport Switzerland", "", new Vector2(980, 640));
        _subtitle = (Label)header.GetChild(1).GetChild(1);
        _page = UiKit.VBox(10);
        _page.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(_page);

        var foot = UiKit.HBox(10);
        var skip = UiKit.Button("Skip the welcome");
        skip.AddThemeColorOverride("font_color", UiTheme.TextDim);
        skip.Pressed += Finish;
        foot.AddChild(skip);
        foot.AddChild(UiKit.Spacer(expand: true));
        _back = UiKit.Button("Back");
        _back.Pressed += () => Show(Step - 1);
        foot.AddChild(_back);
        _next = UiKit.Button("Next", primary: true, minWidth: 160);
        _next.Pressed += Next;
        foot.AddChild(_next);
        body.AddChild(foot);
        // "--welcome N": open on page N, for screenshotting it
        Show((CmdArgs.Int("--welcome") ?? 1) - 1);
    }

    /// <summary>Next, or Start playing on the last page; refused while the name typed breaks its rules.</summary>
    public void Next()
    {
        if (Step == 0 && !TakeName()) return;
        if (Step == Titles.Length - 1) Finish();
        else Show(Step + 1);
    }

    /// <summary>For checks: the name field's text.</summary>
    public void TypeName(string text) { if (_name != null) _name.Text = text; }

    public string? NameError => _nameError is { Visible: true } e ? e.Text : null;

    public override bool OnBack()
    {
        if (_closing) return true;
        if (Step > 0) { Show(Step - 1); return false; }
        Done();
        return true;
    }

    public override void OnShown() => _next.CallDeferred(Control.MethodName.GrabFocus);

    private void Show(int step)
    {
        Step = Mathf.Clamp(step, 0, Titles.Length - 1);
        foreach (var c in _page.GetChildren()) { _page.RemoveChild(c); c.QueueFree(); }
        _name = null;
        _nameError = null;
        _subtitle.Text = $"Step {Step + 1} of {Titles.Length}: {Titles[Step]}. Everything here can be changed later.";
        _back.Disabled = Step == 0;
        _next.Text = Step == Titles.Length - 1 ? "Start playing" : "Next";

        var (scroll, rows) = UiKit.ScrollPage(4);
        switch (Step)
        {
            case 0:
                rows.AddChild(UiKit.Text("How other players see you, and the name on your saves. Change it later on the Multiplayer screen.",
                    UiTheme.FontBody, UiTheme.TextDim, wrap: true));
                rows.AddChild(UiKit.Spacer(8));
                _name = UiKit.TextRow(rows, "Name", GameSettings.Current.PlayerName, "e.g. Heidi_92", "Letters, digits, - and _; 2 to 20");
                _name.MaxLength = 20;
                _name.TextSubmitted += _ => Next();
                _nameError = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber, wrap: true);
                _nameError.Visible = false;
                rows.AddChild(_nameError);
                break;
            case 1:
                rows.AddChild(UiKit.Text("Who you are in the world. Later: the Character menu (title screen, pause menu, P on foot).",
                    UiTheme.FontBody, UiTheme.TextDim, wrap: true));
                rows.AddChild(UiKit.Spacer(8));
                CharacterPage.Fill(rows);
                break;
            default:
                rows.AddChild(UiKit.Text("Offline needs nothing: solo and most servers work without an account. Online signs you in for the "
                    + "servers that require one, and keeps your name and scores across them. Later: Settings › Account.",
                    UiTheme.FontBody, UiTheme.TextDim, wrap: true));
                rows.AddChild(UiKit.Spacer(8));
                AccountPage.Fill(rows);
                break;
        }
        _page.AddChild(scroll);
        if (_name != null) _name.CallDeferred(Control.MethodName.GrabFocus);
        else _next.CallDeferred(Control.MethodName.GrabFocus);
    }

    /// <summary>Keeps a name that follows the rules (empty: none yet); false, with the reason shown, when it does not.</summary>
    private bool TakeName()
    {
        if (_name == null) return true;
        string t = _name.Text.Trim();
        string? why = t.Length == 0 ? null
            : t.Length < 2 ? "At least 2 characters."
            : PlayerRegistry.Sanitize(t) != t ? "Only letters, digits, - and _ (no spaces), up to 20."
            : null;
        if (_nameError != null) { _nameError.Text = why ?? ""; _nameError.Visible = why != null; }
        if (why != null) return false;
        if (t.Length > 0 && t != GameSettings.Current.PlayerName)
        {
            GameSettings.Current.PlayerName = t;
            Save(nameof(GameSettings.PlayerName), t);
        }
        return true;
    }

    private void Done()
    {
        GameSettings.Current.FirstRunDone = true;
        Save(nameof(GameSettings.FirstRunDone), true);
    }

    private void Finish()
    {
        Done();
        _closing = true;
        Shell.Back();
    }
}
