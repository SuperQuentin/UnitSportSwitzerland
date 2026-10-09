using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Playtest;

/// <summary>
/// The playtest panel (#751): the scenario list with each one's standing, the current scenario's
/// instructions and checklist, the conversation with Claude, and the verdict buttons.
///
/// <para>
/// Open (F10, a pad's two stick clicks together) it frees the mouse and takes the right of the
/// screen; closed, a small card top right keeps the scenario, its checklist and Claude's last line
/// in view while you play. Its contents are rebuilt only when the director says something changed.
/// The text box registers with <see cref="UiFocus"/>, as the chat does, so typing never walks the
/// player. In VR it shows on the XrUi panel with the rest of the screen UI (no keyboard there yet:
/// the buttons work, typing does not).
/// </para>
/// </summary>
public partial class PlaytestPanel : CanvasLayer
{
    public const string NodeName = "PlaytestPanel";

    private const int Width = 470, Margin = 14;

    private PlaytestDirector _director = null!;
    private PanelContainer _panel = null!, _card = null!;
    private VBoxContainer _list = null!, _current = null!, _talk = null!, _cardBody = null!;
    private ScrollContainer _talkScroll = null!;
    private LineEdit _input = null!;
    private Label _claude = null!, _cardClaude = null!;
    private TextureRect _dot = null!;
    private Button _validate = null!, _fail = null!;
    private bool _dirty = true, _failing, _listOpen = true, _stickToEnd = true;
    private Button _listToggle = null!;
    private ScrollContainer _listScroll = null!;
    private HBoxContainer _listButtons = null!;
    private PlaytestEntry? _foldedFor;
    private string _shownClaude = "";

    public static PlaytestPanel Create(PlaytestDirector director) => new() { Name = NodeName, _director = director };

    public bool IsOpen => _panel.Visible;

    public override void _Ready()
    {
        Layer = 16;   // over the chat, under the pause menu
        BuildPanel();
        BuildCard();
        _director.Changed += () => _dirty = true;
        PlayerInput.DeviceChanged += () => _dirty = true;
        Open(true);
    }

    public override void _Process(double delta)
    {
        if (_dirty) Refresh();
        // the "listening" dot follows time, not events: checked twice a second, drawn only on change
        if (Engine.GetProcessFrames() % 30 == 0) UpdateClaude();
    }

    public override void _Input(InputEvent e)
    {
        bool chord = e is InputEventJoypadButton { Pressed: true } b
            && (b.ButtonIndex == JoyButton.LeftStick && Input.IsJoyButtonPressed(b.Device, JoyButton.RightStick)
                || b.ButtonIndex == JoyButton.RightStick && Input.IsJoyButtonPressed(b.Device, JoyButton.LeftStick));
        if (chord || e.IsActionPressed(PlayerInput.PlaytestPanel) && !e.IsEcho())
        {
            Open(!IsOpen, focusButtons: chord);
            GetViewport().SetInputAsHandled();
        }
        else if (IsOpen && e is InputEventKey { Pressed: true, Keycode: Key.Escape } && !_input.HasFocus())
        {
            Open(false);
            GetViewport().SetInputAsHandled();
        }
    }

    private void Open(bool open, bool focusButtons = false)
    {
        _panel.Visible = open;
        _card.Visible = !open;
        if (open)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
            if (focusButtons) _validate.GrabFocus();
        }
        else
        {
            _input.ReleaseFocus();
            UiFocus.Set(this, false);
            MouseCapture.Capture();
        }
        _dirty = true;
    }

    // ---- building ------------------------------------------------------------------------

    private void BuildPanel()
    {
        _panel = new PanelContainer
        {
            Theme = UiTheme.Get(),
            AnchorLeft = 1, AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = -Width - Margin, OffsetRight = -Margin, OffsetTop = Margin, OffsetBottom = -Margin,
        };
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.86f, 12, 16));
        AddChild(_panel);
        var col = UiKit.VBox(10);
        _panel.AddChild(col);

        var head = UiKit.HBox(8);
        head.AddChild(UiTheme.Title("Playtest", UiTheme.FontHeading));
        head.AddChild(UiKit.Spacer(expand: true));
        head.AddChild(_dot = UiKit.StatusDot(UiTheme.TextFaint));
        head.AddChild(_claude = UiKit.Text("Claude not connected", UiTheme.FontSmall, UiTheme.TextDim));
        col.AddChild(head);

        // the scenarios: a short scrolling list, folded away while one runs so the conversation keeps its room
        _listToggle = new Button { Text = "SCENARIOS", Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = Control.FocusModeEnum.All };
        _listToggle.AddThemeFontSizeOverride("font_size", UiTheme.FontTiny);
        _listToggle.AddThemeColorOverride("font_color", UiTheme.TextDim);
        _listToggle.Pressed += () => { _listOpen = !_listOpen; _dirty = true; };
        col.AddChild(_listToggle);
        var (listScroll, list) = UiKit.ScrollPage(0);
        _listScroll = listScroll;
        listScroll.CustomMinimumSize = new Vector2(0, 112);
        listScroll.SizeFlagsVertical = Control.SizeFlags.Fill;
        _list = list;
        col.AddChild(listScroll);
        var next = UiKit.Button("Next due ▶");
        next.Pressed += () => { if (_director.NextDue() is { } e) _ = _director.Start(e); };
        var refresh = UiKit.Button("Re-check");
        refresh.TooltipText = "Hash the covered files again (after pulling or editing)";
        refresh.Pressed += _director.RefreshStatus;
        var listButtons = _listButtons = UiKit.HBox(8);
        listButtons.AddChild(next);
        listButtons.AddChild(refresh);
        col.AddChild(listButtons);

        col.AddChild(UiKit.Line());
        _current = UiKit.VBox(4);
        col.AddChild(_current);

        var verdicts = UiKit.HBox(8);
        _validate = UiKit.Button("✔ Validate", primary: true);
        _validate.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _validate.Pressed += () => { _director.Verdict(true, TakeInput()); };
        _fail = UiKit.Button("✖ Fail…");
        _fail.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _fail.Pressed += Fail;
        var reset = UiKit.Button("↺ Reset");
        reset.Pressed += () => { if (_director.Current is { } c) _ = _director.Start(c); };
        verdicts.AddChild(_validate);
        verdicts.AddChild(_fail);
        verdicts.AddChild(reset);
        col.AddChild(verdicts);

        col.AddChild(UiKit.Line());
        col.AddChild(UiKit.Section("Claude"));
        (_talkScroll, _talk) = UiKit.ScrollPage(4);
        _talkScroll.CustomMinimumSize = new Vector2(0, 90);
        // to the newest line once the new rows have their height: the bar's range changes a frame or two later
        _talkScroll.GetVScrollBar().Changed += () =>
        {
            if (!_stickToEnd) return;
            var bar = _talkScroll.GetVScrollBar();
            _talkScroll.ScrollVertical = (int)(bar.MaxValue - bar.Page);
        };
        _talkScroll.GetVScrollBar().Scrolling += () => _stickToEnd = false;
        col.AddChild(_talkScroll);

        var inputRow = UiKit.HBox(8);
        _input = new LineEdit
        {
            PlaceholderText = "Tell Claude what you see…",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 38),
        };
        _input.FocusEntered += () => UiFocus.Set(this, true);
        _input.FocusExited += () => UiFocus.Set(this, false);
        _input.TextSubmitted += _ => Send();
        var send = UiKit.Button("Send");
        send.Pressed += Send;
        inputRow.AddChild(_input);
        inputRow.AddChild(send);
        col.AddChild(inputRow);
    }

    /// <summary>The small card shown while the panel is closed.</summary>
    private void BuildCard()
    {
        _card = new PanelContainer
        {
            Theme = UiTheme.Get(),
            AnchorLeft = 1, AnchorRight = 1,
            OffsetLeft = -340 - Margin, OffsetRight = -Margin, OffsetTop = Margin,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _card.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.55f, 10, 12));
        AddChild(_card);
        var col = UiKit.VBox(4);
        col.MouseFilter = Control.MouseFilterEnum.Ignore;
        _card.AddChild(col);
        col.AddChild(_cardBody = UiKit.VBox(2));
        col.AddChild(_cardClaude = UiKit.Text("", UiTheme.FontSmall, UiTheme.Amber, wrap: true));
    }

    // ---- refreshing ----------------------------------------------------------------------

    private void Refresh()
    {
        _dirty = false;
        RefreshList();
        RefreshCurrent();
        RefreshTalk();
        RefreshCard();
        UpdateClaude();
    }

    private static (string Chip, Color Color) Chip(PlaytestStatus s) => s switch
    {
        PlaytestStatus.Validated => ("✔", UiTheme.Good),
        PlaytestStatus.Failed => ("✖", UiTheme.Bad),
        PlaytestStatus.Stale => ("↻", UiTheme.Warn),
        _ => ("●", UiTheme.TextDim),
    };

    private void RefreshList()
    {
        // a scenario just started: fold the list (the toggle opens it again)
        if (_director.Current != _foldedFor) { _foldedFor = _director.Current; _listOpen = _director.Current == null; }
        int due = _director.Status.Values.Count(v => v != PlaytestStatus.Validated);
        _listToggle.Text = $"{(_listOpen ? "▾" : "▸")}  SCENARIOS  ·  {due} of {_director.Entries.Count} due";
        _listScroll.Visible = _listButtons.Visible = _listOpen;
        foreach (var c in _list.GetChildren()) c.QueueFree();
        string? category = null;
        foreach (var e in _director.Entries)
        {
            if (e.Category != category)
            {
                category = e.Category;
                _list.AddChild(UiKit.Text(category, UiTheme.FontTiny, UiTheme.TextFaint, bold: true));
            }
            var (chip, color) = Chip(_director.Status.GetValueOrDefault(e.Id));
            var b = new Button
            {
                Text = $"{chip}  {e.Title}{(_director.Playable(e) ? "" : $"   ({e.Scenario.Course})")}",
                Alignment = HorizontalAlignment.Left,
                Flat = e != _director.Current,
                FocusMode = Control.FocusModeEnum.All,
                CustomMinimumSize = new Vector2(0, 26),
                TooltipText = $"{e.Id}: {_director.Status.GetValueOrDefault(e.Id)}",
            };
            b.AddThemeColorOverride("font_color", e == _director.Current ? UiTheme.Amber : color);
            b.AddThemeFontSizeOverride("font_size", UiTheme.FontSmall);
            var entry = e;
            b.Pressed += () => _ = _director.Start(entry);
            _list.AddChild(b);
        }
    }

    private void RefreshCurrent()
    {
        foreach (var c in _current.GetChildren()) c.QueueFree();
        var cur = _director.Current;
        _validate.Disabled = _fail.Disabled = cur == null;
        if (cur == null)
        {
            _current.AddChild(UiKit.Text("Pick a scenario above, or Next due.", UiTheme.FontBody, UiTheme.TextDim, wrap: true));
            return;
        }
        var (chip, color) = Chip(_director.Status.GetValueOrDefault(cur.Id));
        _current.AddChild(UiKit.Text($"{cur.Title}  {chip}", UiTheme.FontBody + 2, color == UiTheme.TextDim ? UiTheme.Text : color, bold: true, wrap: true));
        _current.AddChild(UiKit.Text(cur.Scenario.Instructions, UiTheme.FontSmall, UiTheme.Text, wrap: true));
        foreach (string item in cur.Scenario.Checklist)
            _current.AddChild(UiKit.Text("☐ " + item, UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        if (_director.SetupError is { } err)
            _current.AddChild(UiKit.Text("Setup failed: " + err, UiTheme.FontSmall, UiTheme.Bad, wrap: true));
        _current.AddChild(UiKit.Text(InputHints.Prompt(PlayerInput.PlaytestPanel, "panel") + (InputHints.Pad ? " (L3+R3)" : ""),
            UiTheme.FontTiny, UiTheme.TextFaint));
    }

    private void RefreshTalk()
    {
        foreach (var c in _talk.GetChildren()) c.QueueFree();
        foreach (var line in _director.Conversation.TakeLast(40))
        {
            var (who, color) = line.Who switch
            {
                "claude" => ("Claude", UiTheme.Amber),
                "you" => ("You", UiTheme.Text),
                _ => ("", UiTheme.TextDim),
            };
            _talk.AddChild(UiKit.Text(who.Length > 0 ? $"{who}: {line.Text}" : line.Text, UiTheme.FontSmall, color, wrap: true));
        }
        _stickToEnd = true;
    }

    private void RefreshCard()
    {
        foreach (var c in _cardBody.GetChildren()) c.QueueFree();
        var cur = _director.Current;
        _cardBody.AddChild(UiKit.Text(cur == null ? "Playtest" : cur.Title, UiTheme.FontSmall, UiTheme.Text, bold: true, wrap: true));
        if (cur != null)
            foreach (string item in cur.Scenario.Checklist.Take(4))
                _cardBody.AddChild(UiKit.Text("☐ " + item, UiTheme.FontTiny, UiTheme.TextDim, wrap: true));
        _cardBody.AddChild(UiKit.Text(InputHints.Prompt(PlayerInput.PlaytestPanel, "verdict, talk to Claude") + (InputHints.Pad ? " (L3+R3)" : ""),
            UiTheme.FontTiny, UiTheme.TextFaint));
        string last = _director.Conversation.LastOrDefault(l => l.Who == "claude")?.Text ?? "";
        _cardClaude.Text = last.Length > 0 ? "Claude: " + last : "";
        _cardClaude.Visible = last.Length > 0;
    }

    private void UpdateClaude()
    {
        string text = _director.Client == null ? "Claude not connected" : _director.Listening ? "Claude is listening" : "Claude is working…";
        if (text == _shownClaude) return;
        _shownClaude = text;
        _claude.Text = text;
        _dot.Texture = UiTheme.Dot(_director.Client == null ? UiTheme.TextFaint : _director.Listening ? UiTheme.Good : UiTheme.Warn, 10);
    }

    // ---- actions -------------------------------------------------------------------------

    private string TakeInput()
    {
        string text = _input.Text.Trim();
        _input.Text = "";
        return text;
    }

    private void Send()
    {
        string text = TakeInput();
        if (_failing)
        {
            _failing = false;
            _input.PlaceholderText = "Tell Claude what you see…";
            _director.Verdict(false, text.Length > 0 ? text : "no reason given");
            return;
        }
        _director.PlayerSays(text);
    }

    /// <summary>Fail asks why first: the text in the box is the reason, or the next one sent.</summary>
    private void Fail()
    {
        string text = TakeInput();
        if (text.Length > 0 || InputHints.Pad) { _director.Verdict(false, text.Length > 0 ? text : "no reason given (pad)"); return; }
        _failing = true;
        _input.PlaceholderText = "What is wrong? (Enter to record the failure)";
        _input.GrabFocus();
    }
}
