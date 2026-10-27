using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// The departure board (#1226, navigation plan 5.3): one <see cref="Control"/> used as the Journal's Travel tab (from
/// anywhere: a trip needs the GA there) and, at a sign, alone in <see cref="DepartureScreen"/>. The SBB look in its own
/// colours: dark blue rows, white text, amber for the focused one. One row per unlocked destination, nearest first,
/// filtered by a search line: its line glyph (IR, PostAuto, Schiff, Luftseilbahn), the canton, a "departs in 3'"
/// flavour column, the fare (halved or free with a card); a row the rules refuse is greyed with the reason (the server
/// checks again). At a station the SBB ticket office sells the half-fare and the GA. Rebuilt on the travel section's
/// change and on a balance change, never per frame. Keys: ↑ ↓ / type to search, Enter; pad D-pad, A; VR the ray.
/// </summary>
public partial class DepartureBoard : VBoxContainer
{
    public const string TabId = "travel";
    private const int MaxRows = 40;

    private static readonly Color RowBlue = new(0.04f, 0.13f, 0.32f, 0.96f), RowBlueDim = new(0.06f, 0.1f, 0.18f, 0.9f),
        Board = new(0.02f, 0.06f, 0.16f, 0.98f);

    private readonly Poi? _sign;
    private LineEdit _search = null!;
    private VBoxContainer _rows = null!;
    private Label _status = null!, _summary = null!;
    private HBoxContainer _office = null!;
    private readonly List<(Poi Poi, double D)> _found = new();

    /// <summary>The rows shown (checks): destination and whether it may go now.</summary>
    public List<(Poi Poi, bool Ok, int Fare)> Rows { get; } = new();

    public DepartureBoard(Poi? sign)
    {
        _sign = sign;
        Name = "DepartureBoard";
        AddThemeConstantOverride("separation", 8);
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public DepartureBoard() : this(null) { }

    /// <summary>Registers the Journal's Travel tab (the client world, once).</summary>
    public static void Register() =>
        AdventureLog.RegisterTab(TabId, "Travel", 40, () => new DepartureBoard(null), () => AdventureLog.Adventuring && AdventureTravel.Instance != null);

    public override void _Ready()
    {
        _summary = UiKit.Text("", UiTheme.FontSmall, UiTheme.TextDim);
        _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_summary);
        _office = UiKit.HBox(8);
        AddChild(_office);
        _search = new LineEdit { PlaceholderText = "Search a destination…", ClearButtonEnabled = true };
        _search.TextChanged += _ => Rebuild();
        _search.TextSubmitted += _ => (_rows.GetChildCount() > 0 ? _rows.GetChild<Button>(0) : null)?.EmitSignal(BaseButton.SignalName.Pressed);
        AddChild(_search);
        var frame = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        frame.AddThemeStyleboxOverride("panel", UiTheme.Flat(Board, 6, 6, 6));
        AddChild(frame);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 240) };
        frame.AddChild(scroll);
        _rows = UiKit.VBox(3);
        _rows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_rows);
        _status = UiKit.Text("", UiTheme.FontSmall, UiTheme.Warn);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddChild(_status);
        if (AdventureService.Instance is { } svc) svc.SectionChanged += OnSection;
        if (AdventureTravel.Instance is { } travel) travel.Refused += OnRefused;
        if (Bank.Instance is { } bank) bank.BalanceChanged += OnBalance;
        Rebuild();
    }

    public override void _ExitTree()
    {
        if (AdventureService.Instance is { } svc) svc.SectionChanged -= OnSection;
        if (AdventureTravel.Instance is { } travel) travel.Refused -= OnRefused;
        if (Bank.Instance is { } bank) bank.BalanceChanged -= OnBalance;
    }

    private void OnSection(string id)
    {
        if (id == TravelBook.Section.Id) Rebuild();
    }

    private void OnBalance(long _) => Rebuild();

    private void OnRefused(string why) => _status.Text = why;

    /// <summary>Focuses the first row (a pad opens the board on a destination).</summary>
    public void FocusFirst()
    {
        if (_rows.GetChildCount() > 0 && _rows.GetChild(0) is Button b && !b.Disabled) b.GrabFocus();
        else _search.GrabFocus();
    }

    private static GlobalPos? Here => AdventureCompass.Me?.Invoke();

    /// <summary>Whether the local body may not travel as it is (driving, down), as the server will see it.</summary>
    public static Func<(bool Down, bool Driving)>? BodyState { get; set; }

    private void Rebuild()
    {
        foreach (var c in _rows.GetChildren()) { _rows.RemoveChild(c); c.QueueFree(); }
        foreach (var c in _office.GetChildren()) { _office.RemoveChild(c); c.QueueFree(); }
        Rows.Clear();
        if (AdventureTravel.Instance is not { } travel || PlacesService.Instance is not { } places) return;
        var book = travel.MyBook;
        var at = Here ?? new GlobalPos(_sign?.E ?? 0, _sign?.N ?? 0, 0);
        var (down, driving) = BodyState?.Invoke() ?? (false, false);
        int cantons = travel.CantonsOf(book);
        long balance = Bank.Instance?.Balance ?? 0;
        string cards = book.HasGa ? "GA travelcard" : book.HasHalfFare ? "Half-fare travelcard" : "No travelcard";
        _summary.Text = $"{(_sign != null ? $"{TravelNetwork.LineName(TravelNetwork.KindOf(_sign.Kind))} · {_sign.Name} · " : "")}"
            + $"{book.Unlocked.Count} point{(book.Unlocked.Count == 1 ? "" : "s")} in {cantons} canton{(cantons == 1 ? "" : "s")} · {cards} · account CHF {balance}"
            + (travel.Mode == TravelMode.Free ? " · free travel on this server" : travel.Mode == TravelMode.Off ? " · travel is off on this server" : "");
        if (_sign == null && travel.Mode == TravelMode.Network && !book.HasGa)
            _summary.Text += "\nFrom here: walk to a station sign, or get the GA travelcard to travel from anywhere.";
        // the SBB ticket office, at a station
        if (_sign is { Kind: PoiKind.RailStation } station)
        {
            _office.AddChild(UiKit.Text("Ticket office:", UiTheme.FontSmall, UiTheme.TextDim));
            int half = TravelRules.CardPrice(travel.Rules.HalfFareCard, AdventureTravel.Today), ga = TravelRules.CardPrice(travel.Rules.GaCard, AdventureTravel.Today);
            var buyHalf = UiKit.Button(book.HasHalfFare ? "Half-fare: yours" : $"Half-fare card · CHF {half}");
            buyHalf.Name = "BuyHalfFare";
            buyHalf.Disabled = book.HasHalfFare || balance < half;
            buyHalf.Pressed += () => Modal.Confirm(this, "Half-fare travelcard", $"Buy the half-fare travelcard for CHF {half} from your account? Every fare halved.", "Buy", () => travel.AskBuy(false, station.Id));
            var buyGa = UiKit.Button(book.HasGa ? "GA: yours" : $"GA travelcard · CHF {ga}");
            buyGa.Name = "BuyGa";
            buyGa.Disabled = book.HasGa || balance < ga;
            buyGa.Pressed += () => Modal.Confirm(this, "GA travelcard", $"Buy the GA travelcard for CHF {ga} from your account? Trains, buses and ships free, cable cars half, travel from anywhere.", "Buy", () => travel.AskBuy(true, station.Id));
            _office.AddChild(buyHalf);
            _office.AddChild(buyGa);
        }
        _office.Visible = _office.GetChildCount() > 0;

        string q = PlaceIndex.Normalize(_search.Text.Trim());
        _found.Clear();
        foreach (string id in book.Unlocked)
        {
            if (places.Catalogue.Resolve(id) is not { } poi || _sign != null && poi.Id == _sign.Id) continue;
            if (q.Length > 0 && !PlaceIndex.Normalize(poi.Name).Contains(q, StringComparison.Ordinal)) continue;
            _found.Add((poi, TravelNetwork.Distance(poi, at.E, at.N)));
        }
        _found.Sort((a, b) => a.D.CompareTo(b.D));
        if (_found.Count == 0)
        {
            _rows.AddChild(UiKit.Text(book.Unlocked.Count == 0
                ? "No destinations yet. Walk past a station, a bus stop, a landing or a cable car: its sign lights up, and it is yours to travel to."
                : "Nothing matches.", UiTheme.FontSmall, UiTheme.TextDim));
            return;
        }
        int i = 0;
        foreach (var (poi, d) in _found)
        {
            if (i >= MaxRows) break;
            var (why, fare) = travel.Preview(poi, _sign, at, down, driving);
            Rows.Add((poi, why == TravelRefusal.None, fare.Chf));
            _rows.AddChild(Row(poi, d, why, fare, i));
            i++;
        }
        if (_found.Count > MaxRows)
            _rows.AddChild(UiKit.Text($"… {_found.Count - MaxRows} more: search by name", UiTheme.FontSmall, UiTheme.TextFaint));
    }

    private Control Row(Poi poi, double d, TravelRefusal why, Fare fare, int index)
    {
        bool ok = why == TravelRefusal.None;
        var kind = TravelNetwork.KindOf(poi.Kind);
        var b = new Button
        {
            Name = "Row" + index,
            FocusMode = FocusModeEnum.All,
            Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(0, 38),
            TooltipText = ok ? $"Travel to {poi.Name}" : TravelNetwork.Why(why),
        };
        b.AddThemeStyleboxOverride("normal", UiTheme.Flat(ok ? RowBlue : RowBlueDim, 4, 10, 4));
        b.AddThemeStyleboxOverride("hover", UiTheme.Flat(RowBlue.Lightened(0.12f), 4, 10, 4));
        b.AddThemeStyleboxOverride("focus", UiTheme.Flat(RowBlue, 4, 10, 4, UiTheme.Amber, 2));
        b.AddThemeStyleboxOverride("pressed", UiTheme.Flat(UiTheme.Amber.Darkened(0.4f), 4, 10, 4));
        b.AddThemeStyleboxOverride("disabled", UiTheme.Flat(RowBlueDim, 4, 10, 4));
        var row = UiKit.HBox(10);
        row.MouseFilter = MouseFilterEnum.Ignore;
        row.SetAnchorsPreset(LayoutPreset.FullRect);
        row.OffsetLeft = 10;
        row.OffsetRight = -10;
        b.AddChild(row);
        Color ink = ok ? Colors.White : new Color(1, 1, 1, 0.45f);
        Label Cell(string text, float width, Color color, bool bold = false, bool expand = false)
        {
            var l = UiKit.Text(text, UiTheme.FontSmall, color, bold: bold);
            l.MouseFilter = MouseFilterEnum.Ignore;
            l.VerticalAlignment = VerticalAlignment.Center;
            l.CustomMinimumSize = new Vector2(width, 0);
            l.ClipText = true;
            if (expand) l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(l);
            return l;
        }
        // "departs in": a flavour figure, the same for a destination all session
        int departs = 1 + (int)(poi.Hash % 9);
        Cell($"{departs}'", 30, ok ? UiTheme.Amber : ink, bold: true);
        Cell(TravelNetwork.LineName(kind), 92, ink, bold: true);
        Cell(poi.Name, 0, ink, bold: true, expand: true);
        Cell(poi.Canton, 30, ink);
        Cell(NavFormat.Distance(d), 64, ink);
        Cell(ok ? fare.Chf == 0 ? "free" : $"CHF {fare.Chf}" : TravelNetwork.Why(why), ok ? 70 : 220, ok ? ink : UiTheme.Warn);
        b.Disabled = !ok;
        var to = poi;
        b.Pressed += () => Go(to);
        return b;
    }

    private void Go(Poi to)
    {
        _status.Text = "";
        AdventureTravel.Instance?.AskTravel(to.Id, _sign?.Id ?? "");
        // the board closes, so the curtain is seen; a refusal re-opens nothing (the toast says why)
        if (GameShell.Instance is { Top: not null } shell) shell.Back();
    }
}

/// <summary>The departure board at a sign (E / Y there): a framed page over the world, Esc / B closes it.</summary>
public partial class DepartureScreen : Screen
{
    private Poi? _sign;
    public DepartureBoard Board { get; private set; } = null!;

    public static DepartureScreen? Open(Poi sign)
    {
        if (GameShell.Instance is not { InWorld: true } shell || shell.Top != null) return null;
        var s = new DepartureScreen { Name = "Departures", _sign = sign };
        shell.Push(s);
        return s;
    }

    public override void _Ready()
    {
        var dim = new ColorRect { Color = new Color(0.01f, 0.012f, 0.02f, 0.4f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var (body, _) = Framed(_sign?.Name ?? "Departures", InputHints.Format("Pick a destination · {ui_cancel} closes"), new Vector2(760, 520));
        Board = new DepartureBoard(_sign);
        body.AddChild(Board);
    }

    public override void OnShown() => Board.CallDeferred(DepartureBoard.MethodName.FocusFirst);
}
