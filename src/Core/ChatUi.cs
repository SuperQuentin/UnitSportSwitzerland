using Godot;
using UnitSport.Net;

namespace UnitSport.Core;

/// <summary>
/// Chat log and input box.
///
/// The log fades out when nothing has been said for a while so it stays out of the way of a
/// ride, and comes back the moment a line arrives or the box is opened. Enter opens the box,
/// Enter again sends, Esc cancels.
///
/// While the box has focus it registers with <see cref="UiFocus"/>, which is what stops the
/// movement controllers — they read physical keys directly — from walking the player around
/// as you type.
/// </summary>
public partial class ChatUi : CanvasLayer
{
    /// <summary>Lines kept in the scrollback.</summary>
    private const int MaxLines = 80;

    /// <summary>How long the log stays fully visible after the last line.</summary>
    private const double VisibleSeconds = 12.0;

    private ChatManager _chat = null!;
    private ChatCompleter _completer = null!;
    private VBoxContainer _log = null!;
    private ScrollContainer _scroll = null!;
    private PanelContainer _logPanel = null!;
    private LineEdit _input = null!;
    private Label _hint = null!;

    private double _sinceLastLine = double.MaxValue;
    private readonly List<string> _history = [];
    private int _historyCursor = -1;

    /// <summary>What Tab would offer for the text as it was last typed, and which one is in the box.</summary>
    private IReadOnlyList<Suggestion> _suggestions = [];
    private int _suggestionCursor = -1;

    public static ChatUi Create(ChatManager chat, ChatCompleter completer) =>
        new() { Name = "ChatUi", _chat = chat, _completer = completer };

    /// <summary>True while the input box is taking keystrokes.</summary>
    public bool IsTyping => _input.Visible;

    public override void _Ready()
    {
        Layer = 15;   // above the GPX HUD, below the teleport search and the mode menu

        _logPanel = new PanelContainer
        {
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = 12, OffsetRight = 520,
            OffsetTop = -284, OffsetBottom = -80,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _logPanel.AddThemeStyleboxOverride("panel", Panel());
        AddChild(_logPanel);

        _scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _logPanel.AddChild(_scroll);

        _log = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _log.AddThemeConstantOverride("separation", 1);
        _scroll.AddChild(_log);

        _input = new LineEdit
        {
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = 12, OffsetRight = 520,
            OffsetTop = -48, OffsetBottom = -18,
            PlaceholderText = "say something, or /help",
            Visible = false,
            MaxLength = 240,
        };
        _input.TextSubmitted += OnSubmitted;
        _input.TextChanged += _ => RefreshSuggestions();
        _input.GuiInput += OnInputGui;
        AddChild(_input);

        // one line above the box: what Tab would put in it
        _hint = new Label
        {
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = 16, OffsetRight = 520,
            OffsetTop = -76, OffsetBottom = -52,
            Visible = false,
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _hint.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
        _hint.AddThemeFontSizeOverride("font_size", 13);
        AddChild(_hint);

        _chat.LineReceived += (line, kind) => Callable.From(() => Append(line, kind)).CallDeferred();
        _chat.Kicked += reason => Callable.From(
            () => Append($"You were kicked: {reason}", ChatKind.Error)).CallDeferred();

        Append("Press Enter to chat, /help for commands.", ChatKind.System);
    }

    private static StyleBoxFlat Panel()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.72f),
            ContentMarginLeft = 10, ContentMarginRight = 10,
            ContentMarginTop = 6, ContentMarginBottom = 6,
        };
        style.SetCornerRadiusAll(4);
        return style;
    }

    /// <summary>Adds a line and wakes the log up.</summary>
    public void Append(string line, ChatKind kind)
    {
        var label = new Label
        {
            Text = line,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeColorOverride("font_color", ColorFor(kind));
        label.AddThemeFontSizeOverride("font_size", 13);
        _log.AddChild(label);

        while (_log.GetChildCount() > MaxLines) _log.GetChild(0).QueueFree();

        _sinceLastLine = 0;
        _logPanel.Modulate = Colors.White;

        // Scroll after the container has laid the new label out.
        Callable.From(() => _scroll.ScrollVertical = (int)_scroll.GetVScrollBar().MaxValue)
            .CallDeferred();
    }

    private static Color ColorFor(ChatKind kind) => kind switch
    {
        ChatKind.System => new Color(0.62f, 0.70f, 0.78f),
        ChatKind.Admin => new Color(0.98f, 0.72f, 0.10f),
        ChatKind.Private => new Color(0.55f, 0.85f, 0.70f),
        ChatKind.Error => new Color(0.94f, 0.45f, 0.40f),
        _ => new Color(0.92f, 0.93f, 0.95f),
    };

    /// <summary>Opens the input box and takes the keyboard.</summary>
    public void OpenInput(string prefill = "")
    {
        _input.Visible = true;
        _input.Text = prefill;
        _input.CaretColumn = prefill.Length;
        _input.GrabFocus();
        _logPanel.Modulate = Colors.White;
        _sinceLastLine = 0;
        _historyCursor = -1;
        RefreshSuggestions();

        // The fly camera holds the pointer captured; typing needs it back.
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
    }

    /// <summary>Recomputes what Tab offers for the text in the box, and shows it.</summary>
    private void RefreshSuggestions()
    {
        _suggestions = _completer.Complete(_input.Text);
        _suggestionCursor = -1;
        ShowSuggestions();
    }

    private void ShowSuggestions()
    {
        _hint.Visible = _input.Visible && _suggestions.Count > 0;
        if (!_hint.Visible) return;

        _hint.Text = "Tab: " + string.Join("   ", _suggestions.Select(
            (s, i) => i == _suggestionCursor ? $"[{s.Label}]" : s.Label));
    }

    /// <summary>
    /// Tab fills in the first suggestion and walks through the rest (Shift+Tab walks back). Handled
    /// on the box itself because a focused LineEdit would otherwise hand Tab to focus navigation.
    /// </summary>
    private void OnInputGui(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, PhysicalKeycode: Key.Tab } key) return;
        _input.AcceptEvent();

        if (_suggestions.Count == 0) return;
        int step = key.ShiftPressed ? -1 : 1;
        _suggestionCursor = _suggestionCursor < 0
            ? (step > 0 ? 0 : _suggestions.Count - 1)
            : (_suggestionCursor + step + _suggestions.Count) % _suggestions.Count;

        // setting Text does not raise TextChanged, so the list stays put while Tab cycles it
        _input.Text = _suggestions[_suggestionCursor].Text;
        _input.CaretColumn = _input.Text.Length;
        ShowSuggestions();
    }

    /// <summary>Closes the input box without sending.</summary>
    public void CloseInput(bool recaptureMouse = true)
    {
        if (!_input.Visible) return;

        _input.Visible = false;
        _hint.Visible = false;
        _input.ReleaseFocus();
        UiFocus.Set(this, false);

        if (recaptureMouse) MouseCapture.Capture();
    }

    private void OnSubmitted(string text)
    {
        text = text.Trim();
        CloseInput();

        if (text.Length == 0) return;

        _history.Add(text);
        if (_history.Count > 30) _history.RemoveAt(0);

        _chat.Send(text);
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        if (!_input.Visible)
        {
            // Enter opens the box; slash opens it pre-filled, the way most games do it.
            if (key.PhysicalKeycode is Key.Enter or Key.KpEnter)
            {
                OpenInput();
                GetViewport().SetInputAsHandled();
            }
            else if (key.PhysicalKeycode == Key.Slash)
            {
                OpenInput("/");
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        switch (key.PhysicalKeycode)
        {
            case Key.Escape:
                CloseInput();
                GetViewport().SetInputAsHandled();
                return;

            // Up and down walk back through what was sent, like a shell.
            case Key.Up when _history.Count > 0:
                _historyCursor = _historyCursor < 0
                    ? _history.Count - 1
                    : Math.Max(0, _historyCursor - 1);
                _input.Text = _history[_historyCursor];
                _input.CaretColumn = _input.Text.Length;
                RefreshSuggestions();
                GetViewport().SetInputAsHandled();
                return;

            case Key.Down when _historyCursor >= 0:
                _historyCursor++;
                if (_historyCursor >= _history.Count)
                {
                    _historyCursor = -1;
                    _input.Text = string.Empty;
                }
                else
                {
                    _input.Text = _history[_historyCursor];
                }
                _input.CaretColumn = _input.Text.Length;
                RefreshSuggestions();
                GetViewport().SetInputAsHandled();
                return;
        }
    }

    public override void _Process(double delta)
    {
        if (_input.Visible) return;

        _sinceLastLine += delta;
        if (_sinceLastLine < VisibleSeconds) return;

        // Fade back rather than vanishing, so the log stays out of the way of the view but
        // is still readable against a bright sky — which 0.18 alpha was not.
        float fade = (float)Math.Clamp((_sinceLastLine - VisibleSeconds) / 2.0, 0, 1);
        _logPanel.Modulate = new Color(1, 1, 1, Mathf.Lerp(1f, 0.45f, fade));
    }
}
