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
    /// <summary>Lines kept in the scrollback (a ring: older ones leave).</summary>
    public const int MaxScrollback = 300;

    /// <summary>How long a floating line stays, then how long it takes to fade.</summary>
    private const double FloatSeconds = 9.0, FadeSeconds = 1.2, FadeInSeconds = 0.18;

    /// <summary>Floating lines on screen at once; an older one leaves early.</summary>
    private const int MaxFloating = 7;

    /// <summary>A repeated join/leave or admin line inside this many seconds becomes one line with a count.</summary>
    private const double CoalesceSeconds = 5;

    // PanelBottom clears the hotbar (Items/InventoryUi: 50 px slots, 14 px off the bottom) on a narrow window
    private const int Width = 480, Left = 16, PanelBottom = -76, PanelMargin = 10;
    /// <summary>Between the input and the scrollback above it, and between two lines (both lists).</summary>
    private const int ColumnGap = 8, LineGap = 3;

    /// <summary>Completions shown at once; Tab scrolls through the rest.</summary>
    private const int MaxRows = 6;

    private ChatManager _chat = null!;
    private ChatCompleter _completer = null!;

    private VBoxContainer _feed = null!;
    private readonly List<Floating> _floating = [];

    private sealed class Floating
    {
        public RichTextLabel View = null!;
        public double Age;
        public string Base = "";
        public ChatKind Kind;
        public int Count = 1;
    }

    private PanelContainer _panel = null!;
    private ScrollContainer _scroll = null!;
    private Button _newChip = null!;
    private int _pending;

    // the last line, for repeat coalescing, and the last speaker, for grouped messages
    private string _lastText = "";
    private ChatKind _lastKind;
    private double _lastAt;
    private int _repeat;
    private RichTextLabel? _lastLabel;
    private string _lastSpeaker = "";
    private double _lastSpeakerAt;

    // the command browser (/help or the Commands button)
    private Control _helpLayer = null!;
    private PanelContainer _helpPanel = null!;
    private LineEdit _helpSearch = null!;
    private VBoxContainer _helpRows = null!;
    private ScrollContainer _helpScroll = null!;
    private VBoxContainer _log = null!;
    private PanelContainer _popup = null!;
    private VBoxContainer _rows = null!;
    private Label _usage = null!;
    private LineEdit _input = null!;
    private Label _ghost = null!;
    /// <summary>The "/" button and the picker it opens (#1277): the completions as buttons, for a pad and a VR ray.</summary>
    private Button _slash = null!;
    private ScrollContainer _pickerScroll = null!;
    private HFlowContainer _picker = null!;
    private Label _pickerUsage = null!;
    /// <summary>The choices a coordinate opens (#1277): Teleport, Track, Show on map, Copy.</summary>
    private Control _choicesLayer = null!;
    private PanelContainer _choices = null!;
    private VBoxContainer _choiceList = null!;
    private Label _choiceTitle = null!;

    /// <summary>The choices shown (checks), empty when closed.</summary>
    public IEnumerable<string> ChoiceLabels => _choicesLayer.Visible
        ? _choiceList.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion()).Select(b => b.Text) : [];
    private bool _pickerOn;

    /// <summary>The chat on this screen, for the quick menu's Chat wedge.</summary>
    public static ChatUi? Instance { get; private set; }

    /// <summary>Picker buttons shown (checks): their labels, in order.</summary>
    public IEnumerable<string> PickerLabels => _pickerOn
        ? _picker.GetChildren().OfType<Button>().Where(b => !b.IsQueuedForDeletion()).Select(b => b.Text) : [];

    /// <summary>The command browser is open (checks).</summary>
    public bool IsHelpOpen => _helpLayer.Visible;

    /// <summary>The command rows the browser shows, in order (checks).</summary>
    public IEnumerable<string> HelpLabels => _helpRows.FindChildren("*", "Button", true, false).OfType<Button>()
        .Where(b => !b.IsQueuedForDeletion() && b.IsVisibleInTree()).Select(b => b.Text);

    /// <summary>Lines sent, oldest first; which one is in the box (-1: the draft); what was typed before Up.</summary>
    private readonly List<string> _history = [];
    private int _historyCursor = -1;
    private string _draft = "";

    /// <summary>What Tab offers for the text as it was last typed, and which one is in the box (-1: none yet).</summary>
    private IReadOnlyList<Suggestion> _suggestions = [];
    private int _suggestionCursor = -1;
    private int _lastCaret = -1;

    /// <summary>The player's chat text scale, applied to lines as they are drawn.</summary>
    private static float TextScale => Math.Clamp(GameSettings.Current.ChatFontScale, 0.8f, 1.6f);
    private static int BodyPx => Mathf.Max(11, Mathf.RoundToInt(UiTheme.FontBody * TextScale));
    private static int SmallPx => Mathf.Max(10, Mathf.RoundToInt(UiTheme.FontSmall * TextScale));
    private static int TinyPx => Mathf.Max(9, Mathf.RoundToInt(UiTheme.FontTiny * TextScale));

    public static ChatUi Create(ChatManager chat, ChatCompleter completer) =>
        new() { Name = "ChatUi", _chat = chat, _completer = completer };

    /// <summary>True while the input box is taking keystrokes.</summary>
    public bool IsTyping => _panel.Visible;

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        GameSettings.Changed -= OnSettingsChanged;
        if (Instance == this) Instance = null;
    }

    /// <summary>The player moved a chat setting: the open panel follows at once (lines already drawn keep their size).</summary>
    private void OnSettingsChanged()
    {
        if (_panel.Visible)
        {
            ApplyPanelStyle();
            _input.AddThemeFontSizeOverride("font_size", BodyPx);
            _ghost.AddThemeFontSizeOverride("font_size", BodyPx);
            _input.CustomMinimumSize = new Vector2(0, Mathf.Max(28, Mathf.RoundToInt(26 * TextScale)));
            Callable.From(AlignFeed).CallDeferred();
        }
    }

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
        BuildChoices();
        BuildHelp();
        GameSettings.Changed += OnSettingsChanged;
        // once the input has a height, the floating lines line up with the scrollback's
        Callable.From(AlignFeed).CallDeferred();

        _chat.LineReceived += (line, kind) => Callable.From(() => Append(line, kind)).CallDeferred();
        _chat.Kicked += reason => Callable.From(
            () => Append($"You were kicked: {reason}", ChatKind.Error)).CallDeferred();
        // /help opens the command browser instead of dumping a wall of lines into the chat
        _chat.HelpRequested += query => Callable.From(() => ShowHelp(query)).CallDeferred();
        // the names a player completion asked for have arrived
        _chat.NamesReceived += () => Callable.From(() => { if (IsTyping) RefreshSuggestions(); }).CallDeferred();

        Append(Platform.IsTouch ? "CHAT (top right) to chat, / for commands." : "Press Enter to chat, / for commands; /help opens the command list.", ChatKind.System);
        // "--chatdemo" alone floats a line of each kind, for a screenshot of the closed chat's look
        if (CmdArgs.Has("--chatdemo") && !CmdArgs.Has("--chatopen"))
            GetTree().CreateTimer(18).Timeout += DemoLines;
        // "--chatopen [seconds]" opens the input after that long, for screenshotting it against the floating lines;
        // "--chatsend <line>" sends a line a few seconds before (a /locate answer to picture, #1277), and
        // "--chatpicker [text]" opens it on the pad's picker instead, with that text in the box
        if (CmdArgs.Has("--chatopen"))
        {
            double at = CmdArgs.Double("--chatopen") ?? 4;
            if (CmdArgs.Value("--chatsend", notFlag: true) is { } say)
                GetTree().CreateTimer(Math.Max(0.5, at - 6)).Timeout += () => _chat.Send(say);
            GetTree().CreateTimer(at).Timeout += () =>
            {
                if (!CmdArgs.Has("--chatpicker"))
                {
                    OpenInput();
                    // "--chatdemo": a line of every kind, for a screenshot of the look
                    if (CmdArgs.Has("--chatdemo")) DemoLines();
                    // "--chathelp [text]": the command browser, for a screenshot or a quick look
                    if (CmdArgs.Has("--chathelp")) ShowHelp(CmdArgs.Value("--chathelp", notFlag: true) ?? "");
                    // "--chatchoices": the choices of the newest line with a point, as a pad's A opens them
                    if (CmdArgs.Has("--chatchoices") && _log.GetChildren().OfType<RichTextLabel>().LastOrDefault(l => l.HasMeta(FirstPoint)) is { } last)
                        Callable.From(() => OpenChoicesOn(last)).CallDeferred();
                    return;
                }
                OpenForPad();
                if (CmdArgs.Value("--chatpicker", notFlag: true) is { } typed) { SetText(typed); FocusPicker(); }
            };
        }
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
        ApplyPanelStyle();
        AddChild(_panel);

        var column = UiKit.VBox(ColumnGap);
        _panel.AddChild(column);

        // a slim header: what this is, a way to the commands, and a jump-to-newest when reading history
        var header = UiKit.HBox(6);
        header.AddChild(UiKit.Text("CHAT", TinyPx, UiTheme.Amber, bold: true));
        header.AddChild(UiKit.Spacer(0, 0, expand: true));
        _newChip = Chip("New messages ↓", "Jump to the newest line");
        _newChip.CustomMinimumSize = new Vector2(0, 24);
        _newChip.AddThemeFontSizeOverride("font_size", TinyPx);
        _newChip.Visible = false;
        _newChip.Pressed += ScrollToEnd;
        header.AddChild(_newChip);
        var commands = Chip("Commands", "Every command, searchable (or type /help)");
        commands.CustomMinimumSize = new Vector2(0, 24);
        commands.AddThemeFontSizeOverride("font_size", TinyPx);
        commands.Pressed += () => ShowHelp("");
        header.AddChild(commands);
        column.AddChild(header);

        _scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 180),
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
        _usage = UiKit.Text("", SmallPx, UiTheme.TextDim);
        _usage.Visible = false;
        popupColumn.AddChild(UiKit.Margin(_usage, 10, 3, 10, 3));

        _input = new LineEdit
        {
            PlaceholderText = "Message  ·  / commands  ·  Tab completes",
            MaxLength = ChatManager.MaxMessageLength,
            CaretBlink = true,
            ContextMenuEnabled = false,
        };
        _input.AddThemeFontSizeOverride("font_size", BodyPx);
        _input.CustomMinimumSize = new Vector2(0, Mathf.Max(28, Mathf.RoundToInt(26 * TextScale)));
        _input.TextSubmitted += OnSubmitted;
        _input.TextChanged += _ => RefreshSuggestions();
        _input.GuiInput += OnInputGui;
        _input.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // the picker (#1277): what Tab offers, as buttons a pad's D-pad or a VR ray can reach, over the input
        _pickerScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            Visible = false,
        };
        column.AddChild(_pickerScroll);
        _picker = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _picker.AddThemeConstantOverride("h_separation", 4);
        _picker.AddThemeConstantOverride("v_separation", 4);
        _pickerScroll.AddChild(_picker);
        // with the picker open, the usage line sits over it rather than in the popup that would cover it
        _pickerUsage = UiKit.Text("", SmallPx, UiTheme.TextDim);
        _pickerUsage.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _pickerUsage.Visible = false;
        column.AddChild(_pickerUsage);
        column.MoveChild(_pickerUsage, _pickerScroll.GetIndex());

        var inputRow = UiKit.HBox(6);
        _slash = Chip("/", "The commands as buttons");
        _slash.ToggleMode = true;
        _slash.CustomMinimumSize = new Vector2(38, 0);
        _slash.Pressed += () => SetPicker(!_pickerOn, focus: true);
        inputRow.AddChild(_slash);
        inputRow.AddChild(_input);
        column.AddChild(inputRow);

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
        _ghost.AddThemeFontSizeOverride("font_size", BodyPx);
        _input.AddChild(_ghost);
    }

    /// <summary>Reads the player's opacity when the panel opens, so a settings change shows at once.</summary>
    private void ApplyPanelStyle() =>
        _panel.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel((float)GameSettings.Current.ChatOpacity, 12, PanelMargin));

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

    /// <summary>Lines in the log (for <c>--chatcheck</c>): never more than <see cref="MaxScrollback"/>.</summary>
    public int LogLines => _log.GetChildCount();

    /// <summary><c>--chatdemo</c>: one line of each kind, to see the scrollback's look.</summary>
    private void DemoLines()
    {
        Append("Quentin: anyone for a fondue on the Gornergrat?", ChatKind.Say);
        Append("Quentin: I am in, meet you at the station.", ChatKind.Say);
        Append("* Marie waves at Quentin", ChatKind.System);
        Append("Alice whispers: psst, over here", ChatKind.Whisper);
        Append("Teleported to Zermatt", ChatKind.Private);
        Append("Unknown command '/nope'. Try /help.", ChatKind.Error);
        Append("[server] The server restarts in 5 minutes.", ChatKind.Admin);
    }

    public void Append(string line, ChatKind kind)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        bool stay = !IsTyping || AtBottom();

        // grouped: the same player talking again shortly after keeps the name out of the way
        string? speaker = ChatManager.SpeakerOf(line, kind);
        bool grouped = kind == ChatKind.Say && speaker != null
            && speaker == _lastSpeaker && now - _lastSpeakerAt < 90;
        if (kind == ChatKind.Say && speaker != null) (_lastSpeaker, _lastSpeakerAt) = (speaker, now);
        else if (kind != ChatKind.Say) (_lastSpeaker, _lastSpeakerAt) = ("", 0);

        // a repeated join/leave or admin line becomes one line with a count instead of a wall of them
        bool coalesce = kind is ChatKind.System or ChatKind.Admin
            && kind == _lastKind && line == _lastText && now - _lastAt < CoalesceSeconds
            && _lastLabel is { } last && IsInstanceValid(last);
        if (coalesce)
        {
            _repeat++;
            SetLine(_lastLabel!, $"{line}  ×{_repeat}", kind, false, false);
        }
        else
        {
            AppendLog(line, kind, grouped);
        }
        _lastText = line;
        _lastKind = kind;
        _lastAt = now;

        // no floating copy while the panel is open: the scrollback already shows the line
        if (!IsTyping) AppendFloat(line, kind, now);

        if (IsTyping)
        {
            if (stay) ScrollToEnd();
            else ShowPending();
        }
    }

    private void AppendLog(string line, ChatKind kind, bool grouped)
    {
        var logged = Line(line, kind, floating: false, grouped);
        if (World.Locate.LocateCoords.FindAll(line).Count > 0) MakeClickable(logged, line, kind);
        _log.AddChild(logged);
        _lastLabel = logged;
        _repeat = 1;
        // out of the tree first: QueueFree only frees at the end of the frame, so the count never fell and
        // this loop spun forever once the log was full, queueing the same line again and again (a frozen
        // client growing ~100 MB/s: a 100-player match's kill feed passed 80 lines, #837)
        while (_log.GetChildCount() > MaxScrollback)
        {
            var old = _log.GetChild(0);
            _log.RemoveChild(old);
            old.QueueFree();
        }
    }

    private void AppendFloat(string line, ChatKind kind, double now)
    {
        if (_floating.Count > 0)
        {
            var last = _floating[^1];
            if (last.Base == line && last.Kind == kind && last.Age < CoalesceSeconds)
            {
                last.Count++;
                last.Age = 0;
                SetLine(last.View, $"{line}  ×{last.Count}", kind, floating: true, grouped: false);
                return;
            }
        }

        var floating = Line(line, kind, floating: true, grouped: false);
        floating.Modulate = new Color(1, 1, 1, 0);
        _feed.AddChild(floating);
        _floating.Add(new Floating { View = floating, Base = line, Kind = kind });
        while (_floating.Count > MaxFloating)
        {
            _floating[0].View.QueueFree();
            _floating.RemoveAt(0);
        }
    }

    /// <summary>True when the scrollback is at (or within a few pixels of) its newest line.</summary>
    private bool AtBottom()
    {
        var bar = _scroll.GetVScrollBar();
        return bar.MaxValue - bar.Page - _scroll.ScrollVertical <= 10;
    }

    private void ShowPending()
    {
        _pending++;
        _newChip.Text = $"{_pending} new ↓";
        _newChip.Visible = true;
    }

    private static string? StampText() =>
        GameSettings.Current.ChatTimestamps ? System.DateTime.Now.ToString("HH:mm") : null;

    /// <summary>One line as rich text: a player's name in amber, the rest by kind. Floating lines carry an
    /// outline and a shadow instead of a box, so they read against snow and sky alike.</summary>
    private static RichTextLabel Line(string line, ChatKind kind, bool floating, bool grouped)
    {
        var label = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        Fill(label, line, kind, floating ? null : StampText(), grouped);
        label.AddThemeFontOverride("normal_font", UiTheme.Font);
        label.AddThemeFontOverride("bold_font", UiTheme.Bold);
        // one size in both lists, so a line does not jump when the panel opens over it
        label.AddThemeFontSizeOverride("normal_font_size", BodyPx);
        label.AddThemeFontSizeOverride("bold_font_size", BodyPx);
        label.AddThemeColorOverride("default_color", UiTheme.Text);

        if (floating)
        {
            // strong enough to read over snow and sky, which is the whole point of the outline
            label.AddThemeConstantOverride("outline_size", 6);
            label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.88f));
            label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.65f));
            label.AddThemeConstantOverride("shadow_offset_x", 1);
            label.AddThemeConstantOverride("shadow_offset_y", 2);
            label.AddThemeConstantOverride("shadow_outline_size", 9);
        }
        return label;
    }

    private static void SetLine(RichTextLabel label, string line, ChatKind kind, bool floating, bool grouped)
    {
        label.Clear();
        Fill(label, line, kind, floating ? null : StampText(), grouped);
    }

    /// <summary>A kind's mark, so the difference between kinds does not rely on colour alone.</summary>
    private static string Glyph(ChatKind kind) => kind switch
    {
        ChatKind.System => "·",
        ChatKind.Admin => "!",
        ChatKind.Private => ">",
        ChatKind.Whisper => "@",
        ChatKind.Error => "×",
        _ => "",
    };

    private static string Markup(string line, ChatKind kind, bool grouped)
    {
        if (kind == ChatKind.Say)
        {
            // "Name: text" — the server formats it so; a name never holds ": "
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
                return grouped
                    ? $"      {Escape(line[(colon + 2)..])}"
                    : $"[b][color=#{UiTheme.Amber.ToHtml(false)}]{Escape(line[..colon])}[/color][/b]  {Escape(line[(colon + 2)..])}";
            return Escape(line);
        }
        string glyph = Glyph(kind);
        string prefix = glyph.Length == 0 ? "" : $"[color=#{ColorFor(kind).ToHtml(false)}]{glyph}[/color]  ";
        return $"{prefix}[color=#{ColorFor(kind).ToHtml(false)}]{Escape(line)}[/color]";
    }

    private static string Escape(string text) =>
        Mentions(World.Locate.LocateCoords.Highlight(text.Replace("[", "[lb]"), c => Mark + c + Mark));

    /// <summary>
    /// Paints <c>@YourName</c> in amber, so a mention is visible at a glance. Uses the name saved for
    /// this player; a server may have made it unique with a suffix, in which case there is no match.
    /// </summary>
    private static string Mentions(string text)
    {
        if (!GameSettings.Current.ChatMentions) return text;
        string me = PlayerRegistry.Sanitize(GameSettings.Current.PlayerName);
        if (me.Length < 2) return text;

        var painted = new System.Text.StringBuilder(text.Length + 16);
        int i = 0;
        while (i < text.Length)
        {
            int at = text.IndexOf('@', i);
            if (at < 0) { painted.Append(text, i, text.Length - i); break; }
            painted.Append(text, i, at - i);
            int end = at + 1 + me.Length;
            bool match = end <= text.Length
                && string.Compare(text, at + 1, me, 0, me.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (end == text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] is '_' or '-'));
            if (match)
            {
                painted.Append($"[b][color=#{UiTheme.Amber.ToHtml(false)}]{text[at..end]}[/color][/b]");
                i = end;
            }
            else
            {
                painted.Append('@');
                i = at + 1;
            }
        }
        return painted.ToString();
    }

    /// <summary>Wraps each coordinate in the markup, for <see cref="Fill"/> to cut at: a character no chat line carries.</summary>
    private const string Mark = "\u0001";

    /// <summary>
    /// The line's rich text; each <c>@E,N</c> in it (#1277) drawn as a pin and the coordinates in amber, the two one
    /// link whose meta is "E,N|name" (<see cref="ShowChoices"/>).
    /// </summary>
    private static void Fill(RichTextLabel label, string line, ChatKind kind, string? stamp, bool grouped)
    {
        string prefix = stamp == null ? "" : $"[color=#{UiTheme.TextFaint.ToHtml(false)}]{stamp}[/color]  ";
        string markup = Markup(line, kind, grouped);
        if (!markup.Contains(Mark))
        {
            label.Text = prefix + markup;
            return;
        }
        if (prefix.Length > 0) label.AppendText(prefix);
        string? speaker = kind == ChatKind.Say && line.IndexOf(": ", StringComparison.Ordinal) is > 0 and var colon ? line[..colon] : null;
        var coords = World.Locate.LocateCoords.FindAll(line);
        string[] parts = markup.Split(Mark);
        // even parts are text, odd ones a coordinate, in the order FindAll gives them
        for (int i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 0)
            {
                label.AppendText(parts[i]);
                continue;
            }
            int k = i / 2;
            if (k >= coords.Count) { label.AppendText(parts[i]); continue; }
            var c = coords[k];
            label.PushMeta(MetaOf(c, World.Locate.LocateCoords.LabelOf(line, c, speaker)), RichTextLabel.MetaUnderline.OnHover);
            label.AddImage(Icons.Pin, 15, 15, UiTheme.Amber, InlineAlignment.Center);
            label.PushBold();
            label.PushColor(UiTheme.Amber);
            // a no-break space, and a word joiner after the comma: the pin and its coordinates wrap as one word
            label.AddText(" " + parts[i].Replace(",", ",⁠"));
            label.Pop();
            label.Pop();
            label.Pop();
        }
    }

    // ---- coordinates (#1277) ---------------------------------------------------------------

    /// <summary>A small button for the chat's rows: the picker's words, a coordinate's actions.</summary>
    private static Button Chip(string text, string tooltip = "")
    {
        var b = new Button { Text = text, TooltipText = tooltip, FocusMode = Control.FocusModeEnum.All, CustomMinimumSize = new Vector2(0, 30) };
        b.AddThemeFontSizeOverride("font_size", SmallPx);
        return b;
    }

    /// <summary>
    /// A scrollback line carrying <c>@E,N</c> (#1277): its pin and coordinates are a link (a click, a VR ray), and the
    /// line takes focus so a pad reaches it (A opens its first point). Either opens <see cref="ShowChoices"/>.
    /// </summary>
    private void MakeClickable(RichTextLabel label, string line, ChatKind kind)
    {
        label.MouseFilter = Control.MouseFilterEnum.Pass;
        label.FocusMode = Control.FocusModeEnum.All;
        label.AddThemeStyleboxOverride("focus", UiTheme.Flat(new Color(UiTheme.Amber, 0.14f), 6, 2, 2));
        label.MetaClicked += meta => ShowChoices(meta.AsString(), GetViewport().GetMousePosition());
        var c = World.Locate.LocateCoords.FindAll(line)[0];
        string? speaker = kind == ChatKind.Say && line.IndexOf(": ", StringComparison.Ordinal) is > 0 and var colon ? line[..colon] : null;
        label.SetMeta(FirstPoint, MetaOf(c, World.Locate.LocateCoords.LabelOf(line, c, speaker)));
        label.GuiInput += e =>
        {
            if (!e.IsActionPressed("ui_accept")) return;
            OpenChoicesOn(label);
            label.AcceptEvent();
        };
    }

    private static readonly StringName FirstPoint = "locate_first";

    private static string MetaOf(World.Locate.CoordMatch c, string name) =>
        $"{c.E.ToString(System.Globalization.CultureInfo.InvariantCulture)},{c.N.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{name}";

    /// <summary>A pad's A on a focused line: the choices of its first point, under it.</summary>
    private void OpenChoicesOn(RichTextLabel label) =>
        ShowChoices(label.GetMeta(FirstPoint).AsString(), label.GlobalPosition + new Vector2(24, label.Size.Y));

    private void BuildChoices()
    {
        // a full-screen catcher: a click beside the list closes it
        _choicesLayer = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop, Theme = UiTheme.Get() };
        _choicesLayer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _choicesLayer.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true }) HideChoices();
        };
        AddChild(_choicesLayer);
        _choices = new PanelContainer();
        var style = UiTheme.GlassPanel(0.97f, 8, 6);
        style.ShadowSize = 12;
        _choices.AddThemeStyleboxOverride("panel", style);
        _choicesLayer.AddChild(_choices);
        var col = UiKit.VBox(2);
        _choices.AddChild(col);
        _choiceTitle = UiKit.Text("", SmallPx, UiTheme.TextDim);
        col.AddChild(UiKit.Margin(_choiceTitle, 6, 2, 6, 2));
        _choiceList = UiKit.VBox(2);
        col.AddChild(_choiceList);
    }

    /// <summary>
    /// The choices for one point, beside <paramref name="at"/>: Teleport (where allowed), Track, Show on map, Copy.
    /// They send the lines a player could type (<c>/go</c>, <c>/track</c>, <c>/map</c>), so the server's rules apply.
    /// Esc / B or a click beside it closes it.
    /// </summary>
    public void ShowChoices(string meta, Vector2 at)
    {
        int bar = meta.IndexOf('|');
        if (!World.Locate.LocateCoords.TryParse(bar < 0 ? meta : meta[..bar], out double e, out double n)) return;
        string where = World.Locate.LocateCoords.Format(e, n);
        string name = bar < 0 ? where : meta[(bar + 1)..];
        _choiceTitle.Text = name == where ? where : $"{name}  {where}";
        foreach (Node b in _choiceList.GetChildren())
        {
            _choiceList.RemoveChild(b);
            b.QueueFree();
        }
        void Add(string text, string tip, Action pick)
        {
            var b = Chip(text, tip);
            b.Alignment = HorizontalAlignment.Left;
            b.Pressed += pick;
            _choiceList.AddChild(b);
        }
        if (Permissions.CanTeleport) Add("Teleport", $"Go to {name}", () => { HideChoices(); CloseInput(); _chat.Send($"/go {where}"); });
        Add("Track", "Point the way: the marker and the compass", () => { HideChoices(); CloseInput(); _chat.Send($"/track {where} {name}"); });
        Add("Show on map", "Open the map on it", () => { HideChoices(); CloseInput(recaptureMouse: false); _chat.Send($"/map {where} {name}"); });
        Add("Copy", "Copy the coordinates, to paste in a message", () =>
        {
            DisplayServer.ClipboardSet(where);
            HideChoices();
            Append($"Copied {where}.", ChatKind.Private);
        });
        _choicesLayer.Visible = true;
        // beside the point, kept on screen once it has a size
        _choices.Position = at;
        Callable.From(() =>
        {
            var view = GetViewport().GetVisibleRect().Size;
            var size = _choices.GetCombinedMinimumSize();
            _choices.Size = size;
            _choices.Position = new Vector2(Math.Clamp(at.X, 8, view.X - size.X - 8), Math.Clamp(at.Y, 8, view.Y - size.Y - 8));
            if (_choiceList.GetChildren().OfType<Button>().FirstOrDefault() is { } first) first.GrabFocus();
        }).CallDeferred();
    }

    private void HideChoices()
    {
        if (!_choicesLayer.Visible) return;
        _choicesLayer.Visible = false;
        if (_panel.Visible && !_pickerOn) _input.GrabFocus();
    }

    // ---- the command browser (/help) -------------------------------------------------------

    /// <summary>
    /// A proper command list: grouped, searchable, each row clickable. Opened by <c>/help</c>,
    /// <c>/help &lt;word&gt;</c> (pre-filtered) or the Commands button, and closed with Esc / B.
    /// Nothing is sent to the server; the table is the same one Tab completes from.
    /// </summary>
    private void BuildHelp()
    {
        _helpLayer = new Control { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop, Theme = UiTheme.Get() };
        _helpLayer.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _helpLayer.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) HideHelp(); };
        AddChild(_helpLayer);

        // a dimmer behind the panel, so the world and the chat behind it recede
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), MouseFilter = Control.MouseFilterEnum.Ignore };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _helpLayer.AddChild(dim);

        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _helpLayer.AddChild(centre);

        var view = GetViewport().GetVisibleRect().Size;
        _helpPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(Mathf.Max(320, Mathf.Min(760, view.X - 40)), 0),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var style = UiTheme.GlassPanel(0.98f, 14, 18);
        style.ShadowSize = 26;
        _helpPanel.AddThemeStyleboxOverride("panel", style);
        centre.AddChild(_helpPanel);

        var column = UiKit.VBox(8);
        _helpPanel.AddChild(column);

        var head = UiKit.HBox(10);
        head.AddChild(UiKit.Text("Commands", UiTheme.FontHeading, UiTheme.Amber, bold: true));
        head.AddChild(UiKit.Spacer(0, 0, expand: true));
        var close = Chip("Close", "Esc");
        close.Pressed += HideHelp;
        head.AddChild(close);
        column.AddChild(head);

        _helpSearch = new LineEdit
        {
            PlaceholderText = "Search a command, an argument, or what it does",
            ClearButtonEnabled = true,
            ContextMenuEnabled = false,
        };
        _helpSearch.AddThemeFontSizeOverride("font_size", BodyPx);
        _helpSearch.TextChanged += _ => BuildHelpRows();
        column.AddChild(_helpSearch);

        _helpScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, Mathf.Clamp(view.Y * 0.62f, 160, 420)),
        };
        column.AddChild(_helpScroll);
        _helpRows = UiKit.VBox(0);
        _helpRows.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _helpScroll.AddChild(_helpRows);

        column.AddChild(UiKit.Text("Click a command to put it in the box.  Esc closes.", TinyPx, UiTheme.TextFaint));
    }

    /// <summary>Opens the command browser; <paramref name="query"/> filters it (a bare name selects that command's group).</summary>
    public void ShowHelp(string query = "")
    {
        if (!_panel.Visible) OpenInput();
        _helpLayer.Visible = true;
        _helpSearch.Text = query;
        BuildHelpRows();
        _helpSearch.GrabFocus();
        _helpSearch.CaretColumn = _helpSearch.Text.Length;
        UiFocus.Set(this, true);
    }

    private void HideHelp()
    {
        if (!_helpLayer.Visible) return;
        _helpLayer.Visible = false;
        if (_panel.Visible && !_pickerOn) _input.GrabFocus();
    }

    /// <summary>Rebuilds the grouped rows from the completer's table, filtered by the search box.</summary>
    private void BuildHelpRows()
    {
        foreach (Node row in _helpRows.GetChildren())
        {
            _helpRows.RemoveChild(row);
            row.QueueFree();
        }

        string query = _helpSearch.Text.Trim();
        var visible = ChatCompleter.Visible(Permissions.Online, Permissions.IsAdmin).ToHashSet();
        var matching = ChatCompleter.All
            .Where(c => visible.Contains(c.Name))
            .Where(c => query.Length == 0
                || c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Args.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.About.Contains(query, StringComparison.OrdinalIgnoreCase)
                || c.Group.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matching.Count == 0)
        {
            _helpRows.AddChild(UiKit.Text($"Nothing matches “{query}”.", SmallPx, UiTheme.TextDim));
            return;
        }

        foreach (string group in ChatCompleter.GroupNames)
        {
            var inGroup = matching.Where(c => c.Group == group).ToList();
            if (inGroup.Count == 0) continue;
            var heading = UiKit.Text(group.ToUpperInvariant(), TinyPx, UiTheme.TextFaint, bold: true);
            _helpRows.AddChild(UiKit.Margin(heading, 0, 10, 0, 4));
            foreach (var c in inGroup) _helpRows.AddChild(HelpRow(c));
        }
    }

    /// <summary>
    /// One command: a clickable name and arguments, then what it does underneath (wrapped, so a long
    /// <c>/locate</c> argument list never runs off the panel).
    /// </summary>
    private Control HelpRow(ChatCompleter.CommandInfo command)
    {
        string args = command.Args.Length == 0 ? "" : "  " + command.Args;
        var box = UiKit.VBox(0);
        var row = new Button
        {
            Text = $"/{command.Name}{args}",
            Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(0, 24),
            ClipText = true,
            TooltipText = command.Args.Length == 0 ? $"/{command.Name}" : $"/{command.Name} {command.Args}",
        };
        row.AddThemeFontSizeOverride("font_size", SmallPx);
        row.AddThemeColorOverride("font_color", command.Admin ? UiTheme.Amber : UiTheme.Text);
        row.AddThemeColorOverride("font_hover_color", UiTheme.Amber);
        row.Pressed += () => CloseHelpInto($"/{command.Name} ");
        box.AddChild(row);

        var about = UiKit.Text(command.About, TinyPx, UiTheme.TextFaint);
        about.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(UiKit.Margin(about, 10, 0, 10, 4));
        return box;
    }

    /// <summary>Puts a command in the box and closes the browser, with the caret after it.</summary>
    private void CloseHelpInto(string text)
    {
        _helpLayer.Visible = false;
        _panel.Visible = true;
        SetText(text);
        _input.GrabFocus();
        _input.CaretColumn = text.Length;
    }

    // ---- the picker (#1277) ----------------------------------------------------------------

    /// <summary>
    /// Opens the chat for a pad or a VR hand (the quick menu's Chat wedge): the picker shown with the commands,
    /// the first one focused. B closes it.
    /// </summary>
    public void OpenForPad()
    {
        OpenInput("/");
        SetPicker(true, focus: true);
    }

    private void SetPicker(bool on, bool focus)
    {
        _pickerOn = on;
        _pickerScroll.Visible = on;
        if (!on) _pickerUsage.Visible = false;
        _slash.SetPressedNoSignal(on);
        if (on && _input.Text.Length == 0) SetText("/");
        else ShowSuggestions();
        BuildPicker();
        if (on && focus) FocusPicker();
        else if (!on) _input.GrabFocus();
    }

    private void FocusPicker() => Callable.From(() =>
    {
        if (_picker.GetChildren().OfType<Button>().FirstOrDefault(b => !b.IsQueuedForDeletion()) is { } first) first.GrabFocus();
    }).CallDeferred();

    /// <summary>The words that may come next, then the back key, Send and Close; rebuilt with the suggestions.</summary>
    private void BuildPicker()
    {
        if (!_pickerOn) return;
        foreach (Node b in _picker.GetChildren())
        {
            _picker.RemoveChild(b);
            b.QueueFree();
        }
        _pickerUsage.Text = _completer.Usage(_input.Text) ?? "";
        _pickerUsage.Visible = _pickerUsage.Text.Length > 0;
        foreach (var s in _suggestions)
        {
            var b = Chip(s.Label, s.Detail);
            string text = s.Text;
            b.Pressed += () => { SetText(text); FocusPicker(); };
            _picker.AddChild(b);
        }
        var back = Chip("Back", "Take the last word back");
        back.Pressed += () => { SetText(BackOneWord(_input.Text)); FocusPicker(); };
        _picker.AddChild(back);
        var send = Chip("Send", "Send the line");
        send.ThemeTypeVariation = Ui.Skins.SkinTheme.Primary;
        send.Pressed += () => OnSubmitted(_input.Text);
        _picker.AddChild(send);
        var close = Chip("Close");
        close.Pressed += () => CloseInput();
        _picker.AddChild(close);
        // the panel grows with the words, up to four rows, then the picker scrolls: measured once the flow
        // has wrapped them at the panel's width. One connection at a time: several SetText calls in a
        // frame (pressing picker buttons) would otherwise connect the same callable repeatedly.
        if (_pickerFitQueued) return;
        _pickerFitQueued = true;
        GetTree().ProcessFrame += FitPicker;
    }

    private bool _pickerFitQueued;

    private const float PickerMaxHeight = 4 * 34;

    private void FitPicker()
    {
        _pickerFitQueued = false;
        GetTree().ProcessFrame -= FitPicker;
        if (!_pickerOn) return;
        _pickerScroll.CustomMinimumSize = new Vector2(0, Math.Min(_picker.GetCombinedMinimumSize().Y, PickerMaxHeight));
    }

    /// <summary>"/locate church + spire" -> "/locate church + "; "/locate " -> "/"; "/" -> "".</summary>
    public static string BackOneWord(string text)
    {
        string t = text.TrimEnd();
        int cut = t.LastIndexOf(' ');
        if (cut < 0) return t.Length > 1 && t.StartsWith('/') ? "/" : "";
        return t[..(cut + 1)];
    }

    private static Color ColorFor(ChatKind kind) => kind switch
    {
        ChatKind.System => new Color(0.70f, 0.76f, 0.84f),
        ChatKind.Admin => UiTheme.Amber,
        ChatKind.Private => new Color(0.55f, 0.85f, 0.70f),
        ChatKind.Whisper => new Color(0.78f, 0.66f, 0.98f),
        ChatKind.Error => UiTheme.Bad,
        _ => UiTheme.Text,
    };

    private void ScrollToEnd()
    {
        _pending = 0;
        _newChip.Visible = false;
        // after the container has laid the new line out
        Callable.From(() => _scroll.ScrollVertical = (int)_scroll.GetVScrollBar().MaxValue).CallDeferred();
    }

    // ---- open / close ----------------------------------------------------------------------

    /// <summary>Opens the panel and takes the keyboard.</summary>
    public void OpenInput(string prefill = "")
    {
        ApplyPanelStyle();
        _panel.Visible = true;
        // the text size may have changed in the settings since the panel was built
        _input.AddThemeFontSizeOverride("font_size", BodyPx);
        _input.CustomMinimumSize = new Vector2(0, Mathf.Max(28, Mathf.RoundToInt(26 * TextScale)));
        _ghost.AddThemeFontSizeOverride("font_size", BodyPx);
        Callable.From(AlignFeed).CallDeferred();
        _feed.Visible = false;   // the scrollback shows the same lines
        _input.Text = prefill;
        _input.CaretColumn = prefill.Length;
        _input.GrabFocus();
        _historyCursor = -1;
        RefreshSuggestions();
        ScrollToEnd();

        // The fly camera holds the pointer captured; typing needs it back.
        MouseCapture.Mode = Input.MouseModeEnum.Visible;
        UiFocus.Set(this, true);
    }

    /// <summary>Closes the panel without sending.</summary>
    public void CloseInput(bool recaptureMouse = true)
    {
        if (!_panel.Visible) return;

        _helpLayer.Visible = false;
        _panel.Visible = false;
        _popup.Visible = false;
        _feed.Visible = true;
        _choicesLayer.Visible = false;
        _newChip.Visible = false;
        _pending = 0;
        _pickerOn = false;
        _pickerScroll.Visible = false;
        _pickerUsage.Visible = false;
        _slash.SetPressedNoSignal(false);
        _input.ReleaseFocus();
        UiFocus.Set(this, false);

        if (recaptureMouse) MouseCapture.Capture();
    }

    private void OnSubmitted(string text)
    {
        text = text.Trim();
        CloseInput();

        if (text.Length == 0) return;

        // the same line sent twice in a row is one step back, as in a shell
        if (_history.Count == 0 || _history[^1] != text) _history.Add(text);
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
        BuildPicker();
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
            line.AddChild(UiKit.Text(s.Label, SmallPx + 1));
            if (s.Detail.Length > 0)
            {
                var detail = UiKit.Text(s.Detail, SmallPx, UiTheme.TextFaint);
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
        // with the picker open its buttons are the list: the popup keeps only the usage line
        _rows.Visible = _suggestions.Count > 0 && !_pickerOn;

        string? usage = _completer.Usage(_input.Text);
        _usage.Visible = usage != null;
        _usage.Text = usage ?? "";

        _popup.Visible = _panel.Visible && !_pickerOn && (_suggestions.Count > 0 || usage != null);
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
                  + UiTheme.Font.GetStringSize(text, HorizontalAlignment.Left, -1, BodyPx).X;
        string tail = target[text.Length..];
        float tailWidth = UiTheme.Font.GetStringSize(tail, HorizontalAlignment.Left, -1, BodyPx).X;
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
    /// line takes the ghost; Up and Down walk through what was sent. Handled on the box itself
    /// because a focused LineEdit would otherwise keep the arrows and hand Tab to focus navigation.
    /// </summary>
    private void OnInputGui(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } key) return;

        if (key.PhysicalKeycode is Key.Up or Key.Down)
        {
            _input.AcceptEvent();
            WalkHistory(key.PhysicalKeycode == Key.Up ? -1 : 1);
            return;
        }

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

    /// <summary>Pad B (and a VR hand's, through <c>XrPad</c>) closes the open chat, as Esc does.</summary>
    public override void _Input(InputEvent @event)
    {
        // the command browser sits above everything: Esc / B closes it before the chat itself
        if (_helpLayer.Visible && (@event.IsActionPressed("ui_cancel") || @event is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape }))
        {
            HideHelp();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (_choicesLayer.Visible && (@event.IsActionPressed("ui_cancel") || @event is InputEventKey { Pressed: true, PhysicalKeycode: Key.Escape }))
        {
            HideChoices();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!_panel.Visible || @event is InputEventKey || !@event.IsActionPressed("ui_cancel")) return;
        CloseInput();
        GetViewport().SetInputAsHandled();
    }

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

        if (key.PhysicalKeycode == Key.Escape)
        {
            CloseInput();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// Up (-1) and Down (+1) through what was sent, like a shell: the first Up keeps what was
    /// being typed, and Down past the newest line brings it back.
    /// </summary>
    private void WalkHistory(int step)
    {
        if (_history.Count == 0) return;

        int next = (_historyCursor < 0 ? _history.Count : _historyCursor) + step;
        if (next < 0 || (_historyCursor < 0 && step > 0)) return;   // already at the oldest, or at the draft

        if (_historyCursor < 0) _draft = _input.Text;
        _historyCursor = next >= _history.Count ? -1 : next;
        SetText(_historyCursor < 0 ? _draft : _history[_historyCursor]);
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
            var entry = _floating[i];
            entry.Age += delta;
            if (entry.Age >= FloatSeconds + FadeSeconds)
            {
                entry.View.QueueFree();
                _floating.RemoveAt(i);
                continue;
            }

            float alpha = entry.Age < FadeInSeconds
                ? (float)(entry.Age / FadeInSeconds)
                : 1f - (float)Math.Clamp((entry.Age - FloatSeconds) / FadeSeconds, 0, 1);
            entry.View.Modulate = new Color(1, 1, 1, alpha);
        }
    }
}
