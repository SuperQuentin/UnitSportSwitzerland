using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// The "what am I travelling as" picker, opened with E.
///
/// <para>
/// A menu rather than a cycle key, for two reasons: the list is meant to grow, and a refusal
/// needs somewhere to be explained. You cannot get on a bike while airborne or step off skis at
/// 70 km/h, and a key that silently does nothing in those moments reads as a broken key — so the
/// panel says why and stays open.
/// </para>
///
/// <para>
/// It registers with <see cref="UiFocus"/> while open. That is not about text: <see cref="FootPlayer"/>
/// reads physical keys every frame, so without it the 1/2/3 shortcuts would arrive at the same
/// time as W and you would ride away while choosing.
/// </para>
/// </summary>
public partial class RideUi : CanvasLayer
{
    private PanelContainer _panel = null!;
    private Label _status = null!;
    private readonly List<(RideKind Kind, Button Button)> _entries = new();

    /// <summary>Resolved per press, never captured: in multiplayer the player node is respawned.</summary>
    public Func<FootPlayer?>? ActivePlayer { get; set; }

    public bool IsOpen => _panel.Visible;

    public static RideUi Create() => new() { Name = "RideUi" };

    public override void _Ready()
    {
        Layer = 30;   // under the main menu, over the world

        var centre = new CenterContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(centre);

        _panel = new PanelContainer { CustomMinimumSize = new Vector2(440, 0), Visible = false };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.94f),
            ContentMarginLeft = 22, ContentMarginRight = 22,
            ContentMarginTop = 18, ContentMarginBottom = 18,
        };
        style.SetCornerRadiusAll(6);
        _panel.AddThemeStyleboxOverride("panel", style);
        centre.AddChild(_panel);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(rows);

        var title = new Label { Text = "Travel as" };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
        rows.AddChild(title);

        rows.AddChild(new HSeparator());

        Entry(rows, 1, RideKind.OnFoot, "On foot",
            "WASD, Shift run, Space jump, Ctrl slide, Space at a wall to kick off");

        int number = 2;
        foreach (var ride in Rideable.All)
            Entry(rows, number++, ride.Kind, ride.Label, ride.Blurb);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeColorOverride("font_color", new Color(0.92f, 0.55f, 0.35f));
        rows.AddChild(_status);

        var hint = new Label { Text = "E closes — you have to be stopped and on the ground" };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        rows.AddChild(hint);
    }

    private void Entry(Container into, int number, RideKind kind, string label, string blurb)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 0);

        var button = new Button { Text = $"{number}.  {label}", CustomMinimumSize = new Vector2(0, 32) };
        button.Alignment = HorizontalAlignment.Left;
        button.Pressed += () => Choose(kind);
        box.AddChild(button);

        var line = new Label { Text = blurb, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        line.AddThemeFontSizeOverride("font_size", 12);
        line.AddThemeColorOverride("font_color", new Color(0.55f, 0.59f, 0.65f));
        box.AddChild(line);

        into.AddChild(box);
        _entries.Add((kind, button));
    }

    private void Choose(RideKind kind)
    {
        var player = ActivePlayer?.Invoke();
        if (player == null)
        {
            _status.Text = "Nothing to mount — press T to drop out of the fly camera first.";
            return;
        }

        if (player.SetRide(kind))
        {
            Close();
            return;
        }

        // The refusal is the interesting case, so name the actual reason rather than "no".
        _status.Text = player.IsSliding
            ? "Not mid-slide."
            : player.IsOnFloor()
                ? "Too fast — slow down first."
                : "Not in the air.";
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        var player = ActivePlayer?.Invoke();
        var current = player?.Ride ?? RideKind.OnFoot;

        // mark what you are already on, so the panel answers "what am I riding" too
        foreach (var (kind, button) in _entries)
            button.Disabled = kind == current;

        _status.Text = "";
        _panel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
    }

    public void Close()
    {
        _panel.Visible = false;
        UiFocus.Set(this, false);
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (!IsOpen || @event is not InputEventKey { Pressed: true, Echo: false } key) return;

        if (key.PhysicalKeycode is Key.E or Key.Escape)
        {
            Close();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Key.Key1 is the physical "1", so the shortcuts land in the same place on an AZERTY
        // keyboard as on a QWERTY one — the same reason the movement keys are read physically.
        int index = (int)key.PhysicalKeycode - (int)Key.Key1;
        if (index < 0 || index >= _entries.Count) return;

        Choose(_entries[index].Kind);
        GetViewport().SetInputAsHandled();
    }
}
