using Godot;
using UnitSport.Net;
using UnitSport.Ui;

namespace UnitSport.Core;

/// <summary>
/// The chat: floating lines while you play, a menu-styled panel while you type.
///
/// <para>
/// Closed, there is no panel at all: each line pops up bottom-left as outlined text, stays a few
/// seconds and fades away. Enter (or <c>/</c>, pre-filled) opens a glass panel in the menu look
/// (<see cref="UiTheme"/>) with the scrollback, the completions and the input; Enter sends, Esc
/// cancels, Up/Down walk back through what was sent.
/// </para>
///
/// <para>
/// Completion (<see cref="ChatCompleter"/>): the list sits right above the input, the first entry's
/// missing letters are drawn faintly after the caret. Tab takes the highlighted entry and walks on
/// (Shift+Tab back), Right at the end of the line takes it too, a click takes any row. When a Tab
/// leaves nothing else to choose — "/sp" became "/spawn " — the list moves on to the arguments.
/// </para>
///
/// <para>
/// While the box has focus it registers with <see cref="UiFocus"/>, which is what stops the
/// movement controllers — they read physical keys directly — from walking the player around as
/// you type.
/// </para>
/// </summary>
public partial class ChatUi : CanvasLayer
{
    /// <summary>Lines kept in the scrollback.</summary>
    private const int MaxLines = 80;

    /// <summary>How long a floating line stays, then how long it takes to fade.</summary>
    private const double FloatSeconds = 9.0, FadeSeconds = 1.2, FadeInSeconds = 0.18;

    /// <summary>Floating lines on screen at once; an older one leaves early.</summary>
    private const int MaxFloating = 7;

    private const int Width = 480, Left = 16, PanelBottom = -24, PanelMargin = 10;
    /// <summary>Between the input and the scrollback above it, and between two lines (both lists).</summary>
    private const int ColumnGap = 8, LineGap = 3;

    /// <summary>Completions shown at once; Tab scrolls through the rest.</summary>
    private const int MaxRows = 6;

    private ChatManager _chat = null!;
    private ChatCompleter _completer = null!;

    private VBoxContainer _feed = null!;
    private readonly List<(Control Line, double Age)> _floating = [];

    private PanelContainer _panel = null!;
    private ScrollContainer _scroll = null!;
    private VBoxContainer _log = null!;
    private PanelContainer _popup = null!;
    private VBoxContainer _rows = null!;
    private Label _usage = null!;
    private LineEdit _input = null!;
    private Label _ghost = null!;

    private readonly List<string> _history = [];
    private int _historyCursor = -1;

    /// <summary>What Tab offers for the text as it was last typed, and which one is in the box (-1: none yet).</summary>
    private IReadOnlyList<Suggestion> _suggestions = [];
    private int _suggestionCursor = -1;
    private int _lastCaret = -1;

    public static ChatUi Create(ChatManager chat, ChatCompleter completer) =>
        new() { Name = "ChatUi", _chat = chat, _completer = completer };

    /// <summary>True while the input box is taking keystrokes.</summary>
    public bool IsTyping => _panel.Visible;

    public override void _Ready()
    {
        Layer = 15;   // above the GPX HUD, below the teleport search and the mode menu

        // floating lines: bottom-left, newest at the bottom, growing upward
        _feed = new VBoxContainer
        {
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = Left, OffsetRight = Left + Width,
            OffsetTop = -96, OffsetBottom = -96,
            GrowVertical = Control.GrowDirection.Begin,
            Alignment = BoxContainer.AlignmentMode.End,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _feed.AddThemeConstantOverride("separation", LineGap);
        AddChild(_feed);

        BuildPanel();
        // once the input has a height, the floating lines line up with the scrollback's
        Callable.From(AlignFeed).CallDeferred();

        _chat.LineReceived += (line, kind) => Callable.From(() => Append(line, kind)).CallDeferred();
        _chat.Kicked += reason => Callable.From(
            () => Append($"You were kicked: {reason}", ChatKind.Error)).CallDeferred();
        // the names a player completion asked for have arrived
        _chat.NamesReceived += () => Callable.From(() => { if (IsTyping) RefreshSuggestions(); }).CallDeferred();

        Append("Press Enter to chat, / for commands.", ChatKind.System);
        // "--chatopen [seconds]" opens the input after that long, for screenshotting it against the floating lines
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--chatopen");
        if (at >= 0)
            GetTree().CreateTimer(at + 1 < args.Length && double.TryParse(args[at + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double wait) ? wait : 4).Timeout += () => OpenInput();
    }

    /// <summary>
    /// The panel that opens for typing — a short scrollback and the input — and the completion
    /// popup, which floats over the scrollback right above the input instead of taking room of its own.
    /// </summary>
    private void BuildPanel()
    {
        _panel = new PanelContainer
        {
            Theme = UiTheme.Get(),
            AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = Left, OffsetRight = Left + Width,
            OffsetTop = PanelBottom, OffsetBottom = PanelBottom,
            GrowVertical = Control.GrowDirection.Begin,
            Visible = false,
        };
        // see-through enough that the world stays in view behind the conversation
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.55f, 10, PanelMargin));
        AddChild(_panel);

        var column = UiKit.VBox(ColumnGap);
        _panel.AddChild(column);

        _scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 140),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        column.AddChild(_scroll);

        _log = UiKit.VBox(LineGap);
        _log.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // a short scrollback sits at the bottom, by the input, where the floating lines were
        _log.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _log.Alignment = BoxContainer.AlignmentMode.End;
        _scroll.AddChild(_log);

        // added after the panel, so it draws on top of the scrollback
        _popup = new PanelContainer
        {
            Theme = UiTheme.Get(),
            AnchorTop = 1, AnchorBottom = 1,
            GrowVertical = Control.GrowDirection.Begin,
            Visible = false,
        };
        var popupStyle = UiTheme.GlassPanel(0.97f, 8, 4);
        popupStyle.ShadowSize = 12;
        _popup.AddThemeStyleboxOverride("panel", popupStyle);
        AddChild(_popup);

        var popupColumn = UiKit.VBox(1);
        _popup.AddChild(popupColumn);
        _rows = UiKit.VBox(1);
        popupColumn.AddChild(_rows);
        _usage = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _usage.Visible = false;
        popupColumn.AddChild(UiKit.Margin(_usage, 10, 3, 10, 3));

        _input = new LineEdit
        {
            PlaceholderText = "Say something  ·  / for commands  ·  Tab completes",
            MaxLength = 240,
            CaretBlink = true,
            ContextMenuEnabled = false,
        };
        _input.TextSubmitted += OnSubmitted;
        _input.TextChanged += _ => RefreshSuggestions();
        _input.GuiInput += OnInputGui;
        column.AddChild(_input);

        // the rest of the highlighted completion, drawn faintly after what was typed
        _ghost = new Label
        {
            AnchorBottom = 1,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _ghost.AddThemeColorOverride("font_color", UiTheme.TextFaint);
        _ghost.AddThemeFontOverride("font", UiTheme.Font);
        _ghost.AddThemeFontSizeOverride("font_size", UiTheme.FontBody);
        _input.AddChild(_ghost);
    }

    /// <summary>
    /// Puts the floating lines exactly where the same lines sit in the open panel's scrollback:
    /// inside its margin, their last line just above the input. Opening the chat then only adds
    /// the glass and the box behind lines that stay where they were.
    /// </summary>
    private void AlignFeed()
    {
        float bottom = PanelBottom - PanelMargin - _input.GetCombinedMinimumSize().Y - ColumnGap;
        _feed.OffsetLeft = Left + PanelMargin;
        _feed.OffsetRight = Left + Width - PanelMargin;
        _feed.OffsetBottom = bottom;
        _feed.OffsetTop = bottom;
    }

    // ---- lines -----------------------------------------------------------------------------

    /// <summary>Adds a line to the scrollback and floats it up on screen.</summary>
    public void Append(string line, ChatKind kind)
    {
        _log.AddChild(Line(line, kind, floating: false));
        while (_log.GetChildCount() > MaxLines) _log.GetChild(0).QueueFree();
        if (IsTyping) ScrollToEnd();

        var floating = Line(line, kind, floating: true);
        floating.Modulate = new Color(1, 1, 1, 0);
        _feed.AddChild(floating);
        _floating.Add((floating, 0));
        while (_floating.Count > MaxFloating)
        {
            _floating[0].Line.QueueFree();
            _floating.RemoveAt(0);
        }
    }

    /// <summary>
    /// One line as rich text: a player's name in amber, the rest by kind. Floating lines carry an
    /// outline and a shadow instead of a box, so they read against snow and sky alike.
    /// </summary>
    private static RichTextLabel Line(string line, ChatKind kind, bool floating)
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Text = Markup(line, kind),
        };
        label.AddThemeFontOverride("normal_font", UiTheme.Font);
        label.AddThemeFontOverride("bold_font", UiTheme.Bold);
        // one size in both lists, so a line does not jump when the panel opens over it
        label.AddThemeFontSizeOverride("normal_font_size", UiTheme.FontBody);
        label.AddThemeFontSizeOverride("bold_font_size", UiTheme.FontBody);
        label.AddThemeColorOverride("default_color", UiTheme.Text);

        if (floating)
        {
            label.AddThemeConstantOverride("outline_size", 4);
            label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.55f));
            label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.45f));
            label.AddThemeConstantOverride("shadow_offset_x", 1);
            label.AddThemeConstantOverride("shadow_offset_y", 2);
            label.AddThemeConstantOverride("shadow_outline_size", 6);
        }
        return label;
    }

    private static string Markup(string line, ChatKind kind)
    {
        if (kind == ChatKind.Say)
        {
            // "Name: text" — the server formats it so; a name never holds ": "
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
                return $"[b][color=#{UiTheme.Amber.ToHtml(false)}]{Escape(line[..colon])}[/color][/b]  {Escape(line[(colon + 2)..])}";
            return Escape(line);
        }
        return $"[color=#{ColorFor(kind).ToHtml(false)}]{Escape(line)}[/color]";
    }

    private static string Escape(string text) => text.Replace("[", "[lb]");

    private static Color ColorFor(ChatKind kind) => kind switch
    {
        ChatKind.System => new Color(0.70f, 0.76f, 0.84f),
        ChatKind.Admin => UiTheme.Amber,
        ChatKind.Private => new Color(0.55f, 0.85f, 0.70f),
        ChatKind.Error => UiTheme.Bad,
        _ => UiTheme.Text,
    };

    private void ScrollToEnd() =>
        // after the container has laid the new line out
        Callable.From(() => _scroll.ScrollVertical = (int)_scroll.GetVScrollBar().MaxValue).CallDeferred();

    // ---- open / close ----------------------------------------------------------------------

    /// <summary>Opens the panel and takes the keyboard.</summary>
    public void OpenInput(string prefill = "")
    {
        _panel.Visible = true;
        _feed.Visible = false;   // the scrollback shows the same lines
        _input.Text = prefill;
        _input.CaretColumn = prefill.Length;
        _input.GrabFocus();
        _historyCursor = -1;
        RefreshSuggestions();
        ScrollToEnd();

        // The fly camera holds the pointer captured; typing needs it back.
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
    }

    /// <summary>Closes the panel without sending.</summary>
    public void CloseInput(bool recaptureMouse = true)
    {
        if (!_panel.Visible) return;

        _panel.Visible = false;
        _popup.Visible = false;
        _feed.Visible = true;
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

    // ---- completion ------------------------------------------------------------------------

    /// <summary>Recomputes what Tab offers for the text in the box, and shows it.</summary>
    private void RefreshSuggestions()
    {
        string text = _input.Text;
        // a completion that is already exactly the line has nothing left to give
        _suggestions = _completer.Complete(text).Where(s => s.Text != text).ToList();
        _suggestionCursor = -1;
        BuildRows();
        ShowSuggestions();
    }

    private void BuildRows()
    {
        foreach (Node row in _rows.GetChildren()) row.QueueFree();

        for (int i = 0; i < _suggestions.Count; i++)
        {
            var s = _suggestions[i];
            var row = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
            var line = UiKit.HBox(12);
            line.MouseFilter = Control.MouseFilterEnum.Ignore;
            line.AddChild(UiKit.Text(s.Label, UiTheme.FontSmall + 1));
            if (s.Detail.Length > 0)
            {
                var detail = UiKit.Text(s.Detail, UiTheme.FontSmall, UiTheme.TextFaint);
                detail.ClipText = true;
                detail.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                line.AddChild(detail);
            }
            foreach (var child in line.GetChildren()) ((Control)child).MouseFilter = Control.MouseFilterEnum.Ignore;
            row.AddChild(line);

            int index = i;
            row.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
                Take(index);
                _input.GrabFocus();
            };
            _rows.AddChild(row);
        }
    }

    /// <summary>Highlights the entry Tab would take, shows the usage line and the ghost text.</summary>
    private void ShowSuggestions()
    {
        int highlighted = Math.Max(_suggestionCursor, 0);
        // a window of rows that follows the highlight, so a long list never pushes the panel off screen
        int first = Math.Max(0, highlighted - MaxRows + 1);
        int i = 0;
        foreach (Node node in _rows.GetChildren())
        {
            if (node.IsQueuedForDeletion()) continue;
            var row = (PanelContainer)node;
            row.Visible = i >= first && i < first + MaxRows;
            bool on = i++ == highlighted;
            row.AddThemeStyleboxOverride("panel", UiTheme.Flat(on ? new Color(UiTheme.Amber, 0.16f) : new Color(0, 0, 0, 0), 6, 10, 4));
            ((Label)row.GetChild(0).GetChild(0)).AddThemeColorOverride("font_color", on ? UiTheme.Amber : UiTheme.Text);
        }
        _rows.Visible = _suggestions.Count > 0;

        string? usage = _completer.Usage(_input.Text);
        _usage.Visible = usage != null;
        _usage.Text = usage ?? "";

        _popup.Visible = _panel.Visible && (_suggestions.Count > 0 || usage != null);
        if (_popup.Visible) PlacePopup();

        UpdateGhost();
    }

    /// <summary>
    /// Sits the popup on the input's top edge, as wide as the input, growing upward over the
    /// scrollback. Worked out from the panel's own sizes: on the frame the panel opens nothing
    /// has been laid out yet.
    /// </summary>
    private void PlacePopup()
    {
        float bottom = PanelBottom - PanelMargin - _input.GetCombinedMinimumSize().Y - 4;
        _popup.OffsetLeft = Left + PanelMargin;
        _popup.OffsetRight = Left + Width - PanelMargin;
        _popup.OffsetBottom = bottom;
        _popup.OffsetTop = bottom;   // grows upward to its minimum height
    }

    /// <summary>The missing tail of the first completion, faint after the caret — only while nothing is cycled yet.</summary>
    private void UpdateGhost()
    {
        _ghost.Visible = false;
        string text = _input.Text;
        if (_suggestionCursor >= 0 || _suggestions.Count == 0 || _input.CaretColumn != text.Length) return;

        string target = _suggestions[0].Text;
        if (!target.StartsWith(text, StringComparison.OrdinalIgnoreCase) || target.Length == text.Length) return;

        var style = _input.GetThemeStylebox("normal");
        float x = style.ContentMarginLeft
                  + UiTheme.Font.GetStringSize(text, HorizontalAlignment.Left, -1, UiTheme.FontBody).X;
        string tail = target[text.Length..];
        float tailWidth = UiTheme.Font.GetStringSize(tail, HorizontalAlignment.Left, -1, UiTheme.FontBody).X;
        // a line that has started scrolling sideways would put the tail in the wrong place
        if (x + tailWidth > _input.Size.X - style.ContentMarginRight) return;

        _ghost.Text = tail;
        _ghost.OffsetLeft = x;
        _ghost.OffsetRight = x + tailWidth + 4;
        _ghost.Visible = true;
    }

    /// <summary>Puts a completion in the box. The only one left: moves on to what comes after it.</summary>
    private void Take(int index)
    {
        _input.Text = _suggestions[index].Text;
        _input.CaretColumn = _input.Text.Length;

        // setting Text does not raise TextChanged, so the list stays put while Tab cycles it
        if (_suggestions.Count == 1)
        {
            RefreshSuggestions();
            return;
        }
        _suggestionCursor = index;
        ShowSuggestions();
    }

    /// <summary>
    /// Tab takes the highlighted completion and walks on (Shift+Tab back); Right at the end of the
    /// line takes the ghost. Handled on the box itself because a focused LineEdit would otherwise
    /// hand Tab to focus navigation.
    /// </summary>
    private void OnInputGui(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key) return;

        if (key.PhysicalKeycode == Key.Right && _ghost.Visible)
        {
            _input.AcceptEvent();
            Take(0);
            return;
        }

        if (key.PhysicalKeycode != Key.Tab) return;
        _input.AcceptEvent();

        if (_suggestions.Count == 0) return;
        int step = key.ShiftPressed ? -1 : 1;
        int next = _suggestionCursor < 0
            ? (step > 0 ? 0 : _suggestions.Count - 1)
            : (_suggestionCursor + step + _suggestions.Count) % _suggestions.Count;
        Take(next);
    }

    // ---- keys ------------------------------------------------------------------------------

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        if (!_panel.Visible)
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
                SetText(_history[_historyCursor]);
                GetViewport().SetInputAsHandled();
                return;

            case Key.Down when _historyCursor >= 0:
                _historyCursor++;
                if (_historyCursor >= _history.Count)
                {
                    _historyCursor = -1;
                    SetText(string.Empty);
                }
                else
                {
                    SetText(_history[_historyCursor]);
                }
                GetViewport().SetInputAsHandled();
                return;
        }
    }

    private void SetText(string text)
    {
        _input.Text = text;
        _input.CaretColumn = text.Length;
        RefreshSuggestions();
    }

    // ---- floating lines --------------------------------------------------------------------

    public override void _Process(double delta)
    {
        // the ghost follows the caret: arrows and clicks move it without changing the text
        if (_panel.Visible && _input.CaretColumn != _lastCaret)
        {
            _lastCaret = _input.CaretColumn;
            UpdateGhost();
        }

        for (int i = _floating.Count - 1; i >= 0; i--)
        {
            var (line, age) = _floating[i];
            age += delta;
            if (age >= FloatSeconds + FadeSeconds)
            {
                line.QueueFree();
                _floating.RemoveAt(i);
                continue;
            }
            _floating[i] = (line, age);

            float alpha = age < FadeInSeconds
                ? (float)(age / FadeInSeconds)
                : 1f - (float)Math.Clamp((age - FloatSeconds) / FadeSeconds, 0, 1);
            line.Modulate = new Color(1, 1, 1, alpha);
        }
    }
}
