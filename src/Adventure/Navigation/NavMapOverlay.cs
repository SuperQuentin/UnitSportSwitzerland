using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// Navigation's layer on the adventure map (#1226, navigation plan 2 and 5.3), an <see cref="IMapOverlay"/>: the
/// unlocked travel points (blue squares with the kind's letter; zoomed out, clustered by locality, the cluster's shown
/// point chosen by <see cref="TravelNetwork.Rank"/>, a village's stop before a mere stop, with the count), this
/// player's pins (their icon) and the party's shared pins (outlined, with the owner's name). A click on a point opens
/// its card in the side panel (Travel where the rules allow it from here, Track); a right click (pad X at the centre
/// cross, VR the side panel's "Pin the centre") places a pin, or on a pin edits it (icon, note, share, Track, Remove).
/// Drawn only when the map redraws.
/// </summary>
public sealed partial class NavMapOverlay : IMapOverlay
{
    private const float ClusterBelowPxPerKm = 16, CellPx = 30, HitPx = 12;
    private static readonly Color PointBlue = new(0.12f, 0.36f, 0.82f);

    public static NavMapOverlay Instance { get; } = new();

    private readonly List<Poi> _points = new();
    private readonly List<(Poi Shown, int Count)> _clusters = new();
    private readonly Dictionary<(int, int), int> _cells = new();
    private readonly List<(Poi Poi, Vector2 At)> _drawnPoints = new();
    private readonly List<(Pin Pin, long Owner, Vector2 At)> _drawnPins = new();

    /// <summary>Hooks the overlay into the map (the client world, once).</summary>
    public static void Register()
    {
        AdventureMap.AddOverlay(Instance);
        AdventureMap.Building -= OnBuilding;
        AdventureMap.Building += OnBuilding;
    }

    private static void OnBuilding(AdventureMap map, VBoxContainer side)
    {
        if (AdventureTravel.Instance == null) return;
        var pin = UiKit.Button("Pin the centre");
        pin.Name = "PinCentre";
        pin.TooltipText = InputHints.Format("A pin where the centre cross is (right click on the map, or X on a pad, pins where you point)");
        pin.Pressed += () => { var (e, n) = map.Centre; map.ShowCard(PinCard(map, null, e, n)); };
        side.AddChild(pin);
    }

    public void Draw(MapView view, CanvasItem on)
    {
        _drawnPoints.Clear();
        _drawnPins.Clear();
        if (AdventureTravel.Instance is not { } travel || PlacesService.Instance is not { } places) return;
        var font = UiTheme.Font;
        var (w, s, e, n) = view.Bounds();
        // the unlocked travel points in view
        _points.Clear();
        foreach (string id in travel.MyBook.Unlocked)
            if (places.Catalogue.Resolve(id) is { } poi && poi.E >= w && poi.E <= e && poi.N >= s && poi.N <= n) _points.Add(poi);
        if (view.PxPerKm < ClusterBelowPxPerKm)
        {
            TravelNetwork.Cluster(_points, p => { var at = view.ToScreen(p.E, p.N); return (at.X, at.Y); }, CellPx, _clusters, _cells);
            foreach (var (shown, count) in _clusters) DrawPoint(on, font, shown, view.ToScreen(shown.E, shown.N), count);
        }
        else foreach (var poi in _points) DrawPoint(on, font, poi, view.ToScreen(poi.E, poi.N), 1);
        // pins: the party's (outlined, named), then mine on top
        foreach (var (owner, (name, pins)) in travel.PartyPins)
            foreach (var pin in pins) DrawPin(on, font, view, pin, owner, name);
        foreach (var pin in travel.MyPins.Pins) DrawPin(on, font, view, pin, 0, null);
    }

    private void DrawPoint(CanvasItem on, Font font, Poi poi, Vector2 at, int count)
    {
        float r = 6;
        on.DrawRect(new Rect2(at - new Vector2(r + 1.5f, r + 1.5f), new Vector2(2 * r + 3, 2 * r + 3)), new Color(0, 0, 0, 0.6f));
        on.DrawRect(new Rect2(at - new Vector2(r, r), new Vector2(2 * r, 2 * r)), PointBlue);
        string letter = TravelNetwork.KindOf(poi.Kind) switch { TravelKind.Bus => "B", TravelKind.Ship => "S", TravelKind.CableCar => "C", _ => "R" };
        on.DrawString(font, at + new Vector2(-r, 4.5f), letter, HorizontalAlignment.Center, 2 * r, 11, Colors.White);
        if (count > 1)
            on.DrawString(font, at + new Vector2(r + 3, -r + 2), "+" + (count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), HorizontalAlignment.Left, -1, 10, new Color(1, 1, 1, 0.85f));
        _drawnPoints.Add((poi, at));
    }

    private void DrawPin(CanvasItem on, Font font, MapView view, Pin pin, long owner, string? name)
    {
        var at = view.ToScreen(pin.E, pin.N);
        if (at.X < -20 || at.Y < -20 || at.X > view.Size.X + 20 || at.Y > view.Size.Y + 20) return;
        var color = NavIcons.ColorOf(pin.Icon);
        // a pin: a stalk and a round head with the icon
        var head = at - new Vector2(0, 12);
        on.DrawLine(at, head, new Color(0, 0, 0, 0.7f), 2);
        on.DrawCircle(head, 9, owner != 0 ? Colors.White : new Color(0, 0, 0, 0.65f));
        on.DrawCircle(head, owner != 0 ? 7.5f : 8, new Color(0.08f, 0.09f, 0.11f));
        NavIcons.Draw(on, pin.Icon, head, 5.5f, color);
        string label = name != null ? (pin.Note.Length > 0 ? $"{name}: {pin.Note}" : name) : pin.Note;
        if (label.Length > 0)
        {
            on.DrawString(font, head + new Vector2(11, 5), label, HorizontalAlignment.Left, -1, 11, new Color(0, 0, 0, 0.8f));
            on.DrawString(font, head + new Vector2(10, 4), label, HorizontalAlignment.Left, -1, 11, Colors.White);
        }
        _drawnPins.Add((pin, owner, head));
    }

    public bool Click(Vector2 lv95, MouseButton button)
    {
        if (GameShell.Instance?.Top is not AdventureMap map || AdventureTravel.Instance == null) return false;
        double e = lv95.X + AdventureMap.LatticeE, n = lv95.Y + AdventureMap.LatticeN;
        var at = map.View.ToScreen(e, n);
        // a pin first (they stand over the points)
        (Pin Pin, long Owner)? pin = null;
        float best = HitPx * HitPx;
        foreach (var (p, owner, head) in _drawnPins)
        {
            float d = head.DistanceSquaredTo(at);
            if (d < best) { best = d; pin = (p, owner); }
        }
        if (pin is { } hit)
        {
            map.ShowCard(hit.Owner == 0 ? PinCard(map, hit.Pin, hit.Pin.E, hit.Pin.N) : PartyPinCard(hit.Pin, hit.Owner));
            return true;
        }
        if (button == MouseButton.Right)
        {
            map.ShowCard(PinCard(map, null, e, n));
            return true;
        }
        Poi? point = null;
        best = HitPx * HitPx;
        foreach (var (poi, p) in _drawnPoints)
        {
            float d = p.DistanceSquaredTo(at);
            if (d < best) { best = d; point = poi; }
        }
        if (point == null) return false;
        map.ShowCard(PointCard(point));
        return true;
    }

    // ---- cards ------------------------------------------------------------------------------------

    private static bool Tracking(double e, double n) =>
        AdventureService.Instance?.TrackedPlace is { } t && Math.Abs(t.E - e) < 1 && Math.Abs(t.N - n) < 1;

    private static Button TrackButton(string label, double e, double n)
    {
        bool tracked = Tracking(e, n);
        var track = UiKit.Button(tracked ? "Untrack" : "Track", primary: !tracked);
        track.Name = "Track";
        track.Pressed += () =>
        {
            if (AdventureService.Instance is not { } svc) return;
            if (Tracking(e, n)) svc.UntrackPlace();
            else svc.TrackPlace(label, e, n, 0);
            track.Text = Tracking(e, n) ? "Untrack" : "Track";
        };
        return track;
    }

    /// <summary>A travel point's card: what it is, Travel (where the rules allow it from here) and Track.</summary>
    public static Control PointCard(Poi poi)
    {
        var col = UiKit.VBox(8);
        col.Name = "TravelPointCard";
        var travel = AdventureTravel.Instance!;
        col.AddChild(UiKit.Text(poi.Name, UiTheme.FontHeading, UiTheme.Text, bold: true));
        col.AddChild(UiKit.Text($"{TravelNetwork.LineName(TravelNetwork.KindOf(poi.Kind))} · {poi.Canton}", UiTheme.FontSmall, UiTheme.TextDim));
        var here = AdventureCompass.Me?.Invoke();
        var (down, driving) = DepartureBoard.BodyState?.Invoke() ?? (false, false);
        var (why, fare) = here is { } h ? travel.Preview(poi, null, h, down, driving) : (TravelRefusal.NoAdventure, default);
        var line = UiKit.Text(why == TravelRefusal.None ? fare.Chf == 0 ? "Travel there: free" : $"Travel there: CHF {fare.Chf} ({fare.Basis})" : TravelNetwork.Why(why),
            UiTheme.FontSmall, why == TravelRefusal.None ? UiTheme.Good : UiTheme.Warn);
        line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        col.AddChild(line);
        var buttons = UiKit.HBox(8);
        var go = UiKit.Button("Travel", primary: why == TravelRefusal.None);
        go.Name = "Travel";
        go.Disabled = why != TravelRefusal.None;
        go.Pressed += () =>
        {
            travel.AskTravel(poi.Id);
            GameShell.Instance?.Back();
        };
        buttons.AddChild(go);
        buttons.AddChild(TrackButton(poi.Name, poi.E, poi.N));
        col.AddChild(buttons);
        return col;
    }

    /// <summary>
    /// A pin's card: the six icons (keys 1-6, D-pad, the ray), a note, "Share with party", and Pin (new) or Save,
    /// Track, Remove (one's own). <paramref name="pin"/> null for a new one at (<paramref name="e"/>, <paramref name="n"/>).
    /// </summary>
    public static Control PinCard(AdventureMap map, Pin? pin, double e, double n)
    {
        var col = new PinCardBox();
        col.Name = "PinCard";
        col.AddThemeConstantOverride("separation", 8);
        var travel = AdventureTravel.Instance!;
        col.AddChild(UiKit.Text(pin == null ? "New pin" : "Pin", UiTheme.FontHeading, UiTheme.Text, bold: true));
        byte icon = pin?.Icon ?? 0;
        var icons = new HFlowContainer();
        var group = new ButtonGroup();
        var iconButtons = new List<Button>();
        for (byte i = 0; i < PinBook.Icons; i++)
        {
            byte which = i;
            var b = new Button { Text = $"{i + 1} {NavIcons.Names[i]}", ToggleMode = true, ButtonGroup = group, ButtonPressed = i == icon, FocusMode = Control.FocusModeEnum.All };
            b.AddThemeColorOverride("font_color", NavIcons.ColorOf(i));
            b.Toggled += on => { if (on) icon = which; };
            icons.AddChild(b);
            iconButtons.Add(b);
        }
        col.Icons = iconButtons;
        col.AddChild(icons);
        var note = new LineEdit { Text = pin?.Note ?? "", PlaceholderText = "A note (optional)", MaxLength = PinBook.NoteMax };
        col.AddChild(note);
        var share = new CheckButton { Text = "Share with party", ButtonPressed = pin?.Shared ?? false, FocusMode = Control.FocusModeEnum.All };
        share.TooltipText = "Your party sees it on its map and compass";
        col.AddChild(share);
        col.AddChild(UiKit.Text(FormattableString.Invariant($"{e:F0} / {n:F0}"), UiTheme.FontSmall, UiTheme.TextFaint));
        var buttons = UiKit.HBox(8);
        var save = UiKit.Button(pin == null ? "Pin" : "Save", primary: true);
        save.Name = "SavePin";
        save.Pressed += () =>
        {
            if (pin == null) travel.AskPinAdd(e, n, icon, note.Text, share.ButtonPressed);
            else travel.AskPinEdit(pin.Id, icon, note.Text, share.ButtonPressed);
            map.ShowCard(UiKit.Text(pin == null ? "Pinned." : "Saved.", UiTheme.FontSmall, UiTheme.Good));
        };
        buttons.AddChild(save);
        buttons.AddChild(TrackButton(note.Text.Length > 0 ? note.Text : NavIcons.Names[icon], e, n));
        if (pin != null)
        {
            var remove = UiKit.Button("Remove");
            remove.Name = "RemovePin";
            remove.Pressed += () =>
            {
                if (Tracking(pin.E, pin.N)) AdventureService.Instance?.UntrackPlace();
                travel.AskPinRemove(pin.Id);
                map.ShowCard(UiKit.Text("Removed.", UiTheme.FontSmall, UiTheme.TextDim));
            };
            buttons.AddChild(remove);
        }
        col.AddChild(buttons);
        if (pin == null && travel.MyPins.Pins.Count >= PinBook.Max)
        {
            save.Disabled = true;
            col.AddChild(UiKit.Text($"{PinBook.Max} pins at most: remove one first.", UiTheme.FontSmall, UiTheme.Warn));
        }
        Callable.From(() => { if (GodotObject.IsInstanceValid(iconButtons[icon])) iconButtons[icon].GrabFocus(); }).CallDeferred();
        return col;
    }

    /// <summary>A party member's shared pin: whose, its note, Track.</summary>
    private static Control PartyPinCard(Pin pin, long owner)
    {
        var col = UiKit.VBox(8);
        string who = AdventureTravel.Instance?.PartyPins.TryGetValue(owner, out var entry) == true ? entry.Name : "A party member";
        col.AddChild(UiKit.Text($"{who}'s pin", UiTheme.FontHeading, UiTheme.Text, bold: true));
        if (pin.Note.Length > 0) col.AddChild(UiKit.Text(pin.Note, UiTheme.FontBody, UiTheme.Text));
        col.AddChild(TrackButton(pin.Note.Length > 0 ? $"{who}: {pin.Note}" : $"{who}'s pin", pin.E, pin.N));
        return col;
    }

    /// <summary>The pin card: keys 1-6 pick the icon while the note is not being typed.</summary>
    private sealed partial class PinCardBox : VBoxContainer
    {
        public List<Button> Icons = new();

        public override void _UnhandledKeyInput(InputEvent e)
        {
            if (e is not InputEventKey { Pressed: true, Echo: false } k || UiFocus.TextEntryActive || GetViewport().GuiGetFocusOwner() is LineEdit) return;
            int i = (int)(k.PhysicalKeycode - Key.Key1);
            if (i < 0 || i >= Icons.Count) return;
            Icons[i].ButtonPressed = true;
            Icons[i].GrabFocus();
            GetViewport().SetInputAsHandled();
        }
    }
}
