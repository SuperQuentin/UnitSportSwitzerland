using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// <c>godot --headless --path . -- --welcomecheck</c> (#1131): the first-run welcome opens over the title, refuses a name
/// that breaks the rules and keeps one that follows them, walks Character and Account, goes a page back as Esc does,
/// and leaves to the title as done. Nothing reaches the settings file: the pages save into <see cref="Record"/>.
/// </summary>
public partial class WelcomeCheck : Node
{
    private static readonly Dictionary<string, JsonNode> Saved = new();
    private readonly GameShell _shell;
    private int _step;
    private double _wait;
    private bool _acted;

    public WelcomeCheck(GameShell shell)
    {
        _shell = shell;
        Name = "WelcomeCheck";
    }

    public WelcomeCheck() : this(null!) { }

    public static bool Requested() => CmdArgs.Has("--welcomecheck");

    /// <summary>The welcome's saves during the check, kept here instead of the settings file.</summary>
    public static void Record(string key, JsonNode value) => Saved[key] = value.DeepClone();

    private WelcomeScreen? Welcome => _shell.Top as WelcomeScreen;
    private static string? SavedText(string key) => Saved.TryGetValue(key, out var v) ? v.ToJsonString() : null;

    private (System.Action Act, System.Func<bool> Ok, string Name)[] Steps => new (System.Action, System.Func<bool>, string)[]
    {
        (() => { }, () => Welcome is { Step: 0 } && _shell.Top!.IsInsideTree(), "the welcome opens over the title, on its first page"),
        (() => { }, () => SavedText(nameof(GameSettings.FirstRunDone)) == "false", "it saves itself as not done yet"),
        (() => { Welcome!.TypeName("a b"); Welcome.Next(); }, () => Welcome is { Step: 0, NameError: not null }, "a name with a space is refused, with the reason"),
        (() => { Welcome!.TypeName("Heidi_92"); Welcome.Next(); }, () => Welcome is { Step: 1 }, "a good name goes on to the figure"),
        (() => { }, () => SavedText(nameof(GameSettings.PlayerName)) == "\"Heidi_92\"", "and the name is kept"),
        (() => Welcome!.Next(), () => Welcome is { Step: 2 } && Welcome.FindChild("*", true, false) != null, "then the account page"),
        (() => _shell.Back(), () => Welcome is { Step: 1 }, "Esc goes a page back"),
        (() => { Welcome!.Next(); Welcome.Next(); }, () => _shell.Top is TitleScreen, "Start playing leaves to the title"),
        (() => { }, () => SavedText(nameof(GameSettings.FirstRunDone)) == "true" && GameSettings.Current.FirstRunDone, "and the welcome is done"),
    };

    public override void _Process(double delta)
    {
        var steps = Steps;
        if (_step >= steps.Length) return;
        var (act, ok, name) = steps[_step];
        if (!_acted) { act(); _acted = true; _wait = 0; return; }
        _wait += delta;
        if (ok())
        {
            GD.Print($"[welcomecheck] ok   {name}");
            _acted = false;
            if (++_step == steps.Length) { GD.Print("[welcomecheck] RESULT: ok"); GetTree().Quit(0); }
        }
        else if (_wait > 3)
        {
            GD.Print($"[welcomecheck] FAIL {name} (top={_shell.Top?.Name ?? "none"}, step={Welcome?.Step})");
            GD.Print("[welcomecheck] RESULT: FAILED");
            GetTree().Quit(1);
            _step = steps.Length;
        }
    }
}
