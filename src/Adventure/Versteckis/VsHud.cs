using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Ui;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// The game on screen (#1268, plan "Devices"), CanvasLayer 10 like the Battle Royale's: the role banner and the timer at
/// the top; the hiders left and the score top right; the tools' charges and the objective's prompt above the hotbar; the
/// boundary warning within 10 m of the line; a short feed; the blind screen while the seeker counts; the Disguise radial;
/// the round-end and results tables. Built once; every text is set when what it shows changes, never per frame.
/// Looks from <c>ui/style-guide</c>: dark glass, one amber accent, outlined text over the world.
/// </summary>
public partial class VsHud : CanvasLayer
{
    private readonly VersteckisManager _vs;
    private Control _root = null!;
    private Label _banner = null!, _sub = null!, _right = null!, _tools = null!, _prompt = null!, _warn = null!, _feed = null!;
    private ColorRect _blind = null!;
    private Label _count = null!;
    private PanelContainer _table = null!;
    private VBoxContainer _rows = null!;
    private Label _tableTitle = null!;
    private PanelContainer _radial = null!;
    private GridContainer _radialGrid = null!;
    private readonly List<(string Text, double At)> _lines = new();
    private Key _shown;

    /// <summary>What the HUD shows, compared each frame (no allocation) to decide whether any text must change.</summary>
    private readonly record struct Key(int Game, VsPhase Phase, int Secs, int Hiders, int Score, int Round, VsRole Role, bool Caught, int Sniffs,
        int Moos, bool Decoy, int Sprint, int Change, int Miss, int Offer, int Hold, int Line, int Feed, bool Disguised, int Prop, int Entrants);

    public VsHud(VersteckisManager vs)
    {
        _vs = vs;
        Name = "VsHud";
        Layer = 10;
    }

    /// <summary>The blind screen shows (checks).</summary>
    public bool Blind => _blind.Visible;
    /// <summary>The radial is open (checks).</summary>
    public bool RadialOpen => _radial.Visible;
    /// <summary>A table shows (checks).</summary>
    public bool TableShown => _table.Visible;
    public string BannerText => _banner.Text;

    public override void _Ready()
    {
        _root = new Control { Theme = UiTheme.Get(), MouseFilter = Control.MouseFilterEnum.Ignore };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        // the blind screen: under everything else, so the timer still reads over it
        _blind = new ColorRect { Color = new Color(0, 0, 0, 1), Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _blind.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(_blind);
        _count = Outlined(UiTheme.FontTitle, UiTheme.Text);
        _count.HorizontalAlignment = HorizontalAlignment.Center;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _blind.AddChild(_count);

        var top = UiKit.VBox(2);
        top.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
        top.Position = new Vector2(-300, 16);
        top.Size = new Vector2(600, 60);
        top.MouseFilter = Control.MouseFilterEnum.Ignore;
        _root.AddChild(top);
        _banner = Outlined(UiTheme.FontHeading, UiTheme.Text, bold: true);
        _banner.HorizontalAlignment = HorizontalAlignment.Center;
        top.AddChild(_banner);
        _sub = Outlined(UiTheme.FontSmall, UiTheme.TextDim);
        _sub.HorizontalAlignment = HorizontalAlignment.Center;
        top.AddChild(_sub);

        _right = Outlined(UiTheme.FontSmall, UiTheme.Text);
        _right.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        _right.Position = new Vector2(-316, 16);
        _right.Size = new Vector2(300, 120);
        _right.HorizontalAlignment = HorizontalAlignment.Right;
        _root.AddChild(_right);

        _feed = Outlined(UiTheme.FontSmall, UiTheme.TextDim);
        _feed.Position = new Vector2(16, 120);
        _feed.Size = new Vector2(420, 120);
        _root.AddChild(_feed);

        var bottom = UiKit.VBox(2);
        bottom.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        bottom.Position = new Vector2(-360, -190);
        bottom.Size = new Vector2(720, 80);
        bottom.MouseFilter = Control.MouseFilterEnum.Ignore;
        _root.AddChild(bottom);
        _warn = Outlined(UiTheme.FontBody, UiTheme.Bad, bold: true);
        _warn.HorizontalAlignment = HorizontalAlignment.Center;
        bottom.AddChild(_warn);
        _prompt = Outlined(UiTheme.FontBody, UiTheme.Amber);
        _prompt.HorizontalAlignment = HorizontalAlignment.Center;
        bottom.AddChild(_prompt);
        _tools = Outlined(UiTheme.FontSmall, UiTheme.Text);
        _tools.HorizontalAlignment = HorizontalAlignment.Center;
        bottom.AddChild(_tools);

        // the round-end and results table
        _table = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _table.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.78f, 10, 16));
        _table.SetAnchorsPreset(Control.LayoutPreset.Center);
        _table.Position = new Vector2(-260, -170);
        _table.CustomMinimumSize = new Vector2(520, 0);
        _root.AddChild(_table);
        var tv = UiKit.VBox(4);
        _table.AddChild(tv);
        _tableTitle = UiKit.Text("", UiTheme.FontHeading, UiTheme.Text, bold: true);
        tv.AddChild(_tableTitle);
        tv.AddChild(UiKit.Line());
        _rows = UiKit.VBox(2);
        tv.AddChild(_rows);

        // the Disguise radial: the area's kinds as big buttons (mouse, pad focus, touch, a VR laser)
        _radial = new PanelContainer { Visible = false };
        _radial.AddThemeStyleboxOverride("panel", UiTheme.GlassPanel(0.9f, 12, 18));
        _radial.SetAnchorsPreset(Control.LayoutPreset.Center);
        _radial.Position = new Vector2(-270, -140);
        _radial.CustomMinimumSize = new Vector2(540, 0);
        _root.AddChild(_radial);
        var rv = UiKit.VBox(8);
        _radial.AddChild(rv);
        rv.AddChild(UiKit.Text("Disguise as…", UiTheme.FontHeading, UiTheme.Text, bold: true));
        rv.AddChild(UiKit.Text(InputHints.Format("What this village has. {ui_cancel} closes; Aim + {use_item} copies the prop you look at."),
            UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        _radialGrid = new GridContainer { Columns = 4 };
        _radialGrid.AddThemeConstantOverride("h_separation", 8);
        _radialGrid.AddThemeConstantOverride("v_separation", 8);
        rv.AddChild(_radialGrid);
    }

    private static Label Outlined(int size, Color color, bool bold = false)
    {
        var l = UiKit.Text("", size, color, bold);
        l.MouseFilter = Control.MouseFilterEnum.Ignore;
        l.AddThemeConstantOverride("outline_size", 4);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.55f));
        l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.45f));
        l.AddThemeConstantOverride("shadow_offset_x", 1);
        l.AddThemeConstantOverride("shadow_offset_y", 2);
        l.AddThemeConstantOverride("shadow_outline_size", 6);
        return l;
    }

    /// <summary>A line in the feed (a catch, a miss, a sniff), for 4 s.</summary>
    public void Feed(string line)
    {
        _lines.Add((line, GameClock.Now));
        if (_lines.Count > 5) _lines.RemoveAt(0);
        _feedDirty = true;
    }

    private bool _feedDirty;

    public override void _Process(double delta)
    {
        var s = _vs.Mine;
        var e = _vs.MyEntry;
        if (_lines.Count > 0 && GameClock.Now - _lines[0].At > 4) { _lines.RemoveAt(0); _feedDirty = true; }
        if (s == null || e == null || !_vs.InGame && s.Phase != VsPhase.Lobby)
        {
            if (_shown.Game != 0 || _root.Visible)
            {
                _shown = default;
                _root.Visible = false;
                CloseRadial();
            }
            return;
        }
        _root.Visible = true;
        double now = ClockSync.SimNow;
        int secs = (int)Math.Ceiling(Math.Max(0, s.PhaseEnds - now));
        var me = _vs.LocalPlayer();
        int line = 99;
        if (me != null && s.Phase is VsPhase.Hide or VsPhase.Seek)
        {
            var (pe, pn) = OriginOf(me);
            line = (int)Math.Floor(VsRules.ToLine(s.E, s.N, s.Radius, pe, pn));
        }
        var offer = _vs.Offer;
        var key = new Key(s.Id, s.Phase, secs, s.HidersLeft, e.Score, s.Round, e.Role, e.Caught, e.Sniffs, e.Moos, e.DecoyUsed,
            (int)Math.Ceiling(Math.Max(0, e.SprintReadyAt - now)), (int)Math.Ceiling(Math.Max(0, e.ChangeReadyAt - now)),
            (int)Math.Ceiling(Math.Max(0, _vs.MissedAt + VsRules.MissCooldown - GameClock.Now)), offer?.Site ?? -1,
            (int)((offer?.Progress ?? 0) * 20), Math.Clamp(line, -99, 99), _feedDirty ? _lines.Count + 1000 : _lines.Count, e.Disguised, (int)e.Prop,
            s.Entrants.Count);
        if (key == _shown) return;
        bool tableChanged = key.Phase != _shown.Phase || key.Round != _shown.Round || key.Entrants != _shown.Entrants || key.Game != _shown.Game;
        _shown = key;
        _feedDirty = false;
        Show(s, e, secs, line, offer, tableChanged);
    }

    private (double E, double N) OriginOf(Node3D body) =>
        _vs.Origin is { } o ? o.ToLv95(body.GlobalPosition) : (0, 0);

    private void Show(VsState s, VsEntrant e, int secs, int line, (int Site, string Prompt, float Progress)? offer, bool tableChanged)
    {
        string clock = $"{secs / 60}:{secs % 60:00}";
        bool seeker = e.Role == VsRole.Seeker;
        (_banner.Text, _sub.Text) = s.Phase switch
        {
            VsPhase.Lobby => ($"VERSTECKIS · {s.Label}", $"{s.Humans} joined · {clock} to start" + (s.Opener == _vs.Me ? " · Activities › Versteckis › Start" : "")),
            VsPhase.Gather => ($"VERSTECKIS · {s.Label}", $"To the square · {s.Rounds} rounds"),
            VsPhase.Hide => (seeker ? $"YOU SEEK · COUNT {clock}" : e.Caught ? "SPECTATING" : $"HIDE · {clock}",
                seeker ? "Eyes shut at the church door" : $"Round {s.Round}/{s.Rounds} · {Who(s, s.Seeker)} seeks · pick a prop, find a spot"),
            VsPhase.Seek => (seeker ? $"SEEK · {clock}" : e.Caught ? $"FOUND · {clock}" : $"HIDDEN · {clock}",
                $"Round {s.Round}/{s.Rounds} · " + (seeker ? "tag the props that are players" : $"{Who(s, s.Seeker)} is seeking")),
            VsPhase.RoundEnd => ($"ROUND {s.Round} OVER", $"Next round in {clock}"),
            VsPhase.Results => ("VERSTECKIS · RESULTS", $"Back in {clock}"),
            _ => ("", ""),
        };
        _right.Text = s.Phase is VsPhase.Hide or VsPhase.Seek
            ? $"{s.HidersLeft} hiding" + HidingKinds(s) + $"\nScore {e.Score}" + (s.Awake ? "\nThe village is awake" : "")
            : $"Score {e.Score}";
        // the blind screen: the seeker in Hide (the server holds it at the door and withholds the hiders anyway)
        bool blind = s.Phase == VsPhase.Hide && seeker && _vs.InGame;
        _blind.Visible = blind;
        if (blind) _count.Text = $"…{secs}\nCounting at the church door";
        _tools.Text = s.Phase is VsPhase.Hide or VsPhase.Seek && !e.Caught ? Tools(s, e) : "";
        _prompt.Text = offer is { } o ? InputHints.Format(o.Prompt) + (o.Progress > 0 ? $"  {(int)(o.Progress * 100)}%" : "") : "";
        _warn.Text = line < VsRules.WarnInside && s.Phase is VsPhase.Hide or VsPhase.Seek && !e.Caught
            ? line >= 0 ? $"The line is {line} m away" : seeker ? "Outside the line: you cannot tag" : "OUTSIDE THE LINE: back in or you are found"
            : "";
        _feed.Text = string.Join("\n", _lines.Select(l => l.Text));
        if (tableChanged) Table(s);
    }

    private static string Who(VsState s, long peer) => s.Find(peer)?.Name ?? "?";

    private static string HidingKinds(VsState s)
    {
        var kinds = s.Entrants.Where(x => x.Hiding).GroupBy(x => x.Prop).Select(g => g.Count() > 1 ? $"{VsProps.Of(g.Key).Name} ×{g.Count()}" : VsProps.Of(g.Key).Name).ToList();
        return kinds.Count > 0 ? ": " + string.Join(", ", kinds) : "";
    }

    private string Tools(VsState s, VsEntrant e)
    {
        double now = ClockSync.SimNow;
        if (e.Role == VsRole.Seeker)
        {
            double miss = _vs.MissedAt + VsRules.MissCooldown - GameClock.Now;
            return InputHints.Format("{use_item} Tag") + (miss > 0 ? $" (in {miss:F0} s)" : "") + $" · Sniff {e.Sniffs}/{VsRules.SniffCharges}";
        }
        string prop = e.Disguised ? VsProps.Of(e.Prop).Name : e.Holding >= 0 ? "yourself (holding)" : e.DisguiseBackAt > 0 ? "yourself for a moment" : VsProps.Of(e.Prop).Name;
        double sprint = e.SprintReadyAt - now, change = e.ChangeReadyAt - now;
        return $"You are {prop}" + (s.Phase == VsPhase.Seek && change > 0 ? $" (change in {change:F0} s)" : "")
            + (e.DecoyUsed ? " · Decoy used" : " · Decoy ready")
            + (sprint > 0 ? $" · Gump {sprint:F0} s" : InputHints.Format(" · {sprint} Gump"))
            + $" · Muuh {e.Moos}/{VsRules.MooMax}";
    }

    /// <summary>The round-end table (the round's points and where everyone hid) and the results (the totals, the top).</summary>
    private void Table(VsState s)
    {
        bool show = s.Phase is VsPhase.RoundEnd or VsPhase.Results;
        _table.Visible = show;
        if (!show) return;
        foreach (var child in _rows.GetChildren()) { _rows.RemoveChild(child); child.QueueFree(); }
        _tableTitle.Text = s.Phase == VsPhase.Results ? $"Versteckis at {s.Label}" : $"Round {s.Round} of {s.Rounds}";
        foreach (var x in s.Entrants.Where(x => x.Played > 0).OrderByDescending(x => s.Phase == VsPhase.Results ? x.Score : x.RoundScore))
        {
            var row = UiKit.HBox(8);
            bool top = s.Phase == VsPhase.Results && s.Winners.Contains(x.Peer);
            string who = x.Name + (x.Bot ? " [bot]" : "") + (x.Peer == _vs.Me ? "  (you)" : "");
            var name = UiKit.Text(who, UiTheme.FontBody, top ? UiTheme.Amber : x.Bot ? UiTheme.TextDim : UiTheme.Text, bold: top);
            name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(name);
            string what = s.Phase == VsPhase.Results ? (top ? "top · " : "") + $"{x.Played} rounds"
                : x.Role == VsRole.Seeker ? "seeker" : s.Pins.FirstOrDefault(p => p.Peer == x.Peer) is { } pin
                    ? $"{VsProps.Of(pin.Prop).Name}{(pin.Caught ? ", found" : ", hidden")}" : x.Role.ToString().ToLowerInvariant();
            row.AddChild(UiKit.Text(what, UiTheme.FontSmall, UiTheme.TextDim));
            row.AddChild(UiKit.Text(s.Phase == VsPhase.Results ? $"{x.Score}" : $"+{x.RoundScore}", UiTheme.FontBody, UiTheme.Text, bold: true));
            _rows.AddChild(row);
        }
    }

    // ---- the Disguise radial ----

    private Control? _focusBack;

    /// <summary>Opens the radial of the kinds the area holds; picking one asks the server.</summary>
    public void OpenRadial(IReadOnlyList<VsProp> kinds, VsProp current)
    {
        foreach (var child in _radialGrid.GetChildren()) { _radialGrid.RemoveChild(child); child.QueueFree(); }
        Button? first = null;
        foreach (var k in kinds)
        {
            var b = UiKit.Button(VsProps.Of(k).Name, primary: k == current, minWidth: 120);
            b.CustomMinimumSize = new Vector2(120, 48);
            var kind = k;
            b.Pressed += () => { _vs.Disguise(kind); CloseRadial(); };
            _radialGrid.AddChild(b);
            first ??= b;
        }
        _radial.Visible = true;
        _focusBack = GetViewport().GuiGetFocusOwner();
        MouseCapture.Mode = Input.MouseModeEnum.Visible;
        first?.CallDeferred(Control.MethodName.GrabFocus);
        GD.Print($"[vs] radial: {string.Join(", ", kinds)}");
    }

    public void CloseRadial()
    {
        if (!_radial.Visible) return;
        _radial.Visible = false;
        if (_focusBack != null && IsInstanceValid(_focusBack)) _focusBack.GrabFocus();
        MouseCapture.Capture();
    }

    /// <summary>For checks: picks a kind as a click on its button would.</summary>
    public void PickInRadial(VsProp kind)
    {
        _vs.Disguise(kind);
        CloseRadial();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_radial.Visible && e.IsActionPressed("ui_cancel"))
        {
            CloseRadial();
            GetViewport().SetInputAsHandled();
        }
    }
}
