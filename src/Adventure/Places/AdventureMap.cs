using Godot;
using UnitSport.Core;
using UnitSport.Map;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Places;

/// <summary>
/// Something another plan draws on the adventure map (#1224, places plan 6): navigation's pins and travel
/// points. Registered with <see cref="AdventureMap.AddOverlay"/>; drawn after the places, on the map's top
/// layer, only when the map redraws. <c>lv95</c> is relative to the lattice's south-west corner
/// (<see cref="AdventureMap.LatticeE"/>, <see cref="AdventureMap.LatticeN"/>), in metres.
/// </summary>
public interface IMapOverlay
{
    void Draw(MapView view, CanvasItem on);
    /// <summary>A click on the map, before it selects a place; true when the overlay took it.</summary>
    bool Click(Vector2 lv95, MouseButton button);
}

/// <summary>
/// The adventure map (#1224, places plan 6): M while the adventure is on for the player (the place search
/// otherwise, the Battle Royale map in a round). The relief of <see cref="MapRelief"/>, this player's fog
/// (<see cref="FogTexture"/>, a shader), the canton borders, the places it has found and the landmarks it
/// has sighted under revealed fog (grey), the player's arrow, and every <see cref="IMapOverlay"/>. A side
/// panel searches the places and shows the selected one's card (Track, Show in Atlas). Redrawn only when
/// something changes (<c>general/perf-no-per-frame-allocations</c>). Docs: <c>docs/notes/adventure/map.md</c>.
/// </summary>
public partial class AdventureMap : Screen
{
    public const double LatticeE = FogMask.MinE, LatticeN = FogMask.MinN;

    private static readonly List<IMapOverlay> Overlays = new();

    /// <summary>Adds a layer of another plan's (navigation's pins, travel points); kept for the session.</summary>
    public static void AddOverlay(IMapOverlay overlay)
    {
        if (!Overlays.Contains(overlay)) Overlays.Add(overlay);
    }

    public static void RemoveOverlay(IMapOverlay overlay) => Overlays.Remove(overlay);

    /// <summary>The local player's position and heading (yaw, radians), set by the client world.</summary>
    public static Func<(GlobalPos At, float Yaw)?>? Me { get; set; }
    /// <summary>The old place search (teleporting, where the server allows it), set by the client world.</summary>
    public static Action? PlaceSearch { get; set; }

    /// <summary>The map is on for this player now: M opens it.</summary>
    public static bool Available => PlacesService.Instance != null && AdventureLog.Adventuring;

    private Poi? _focus;
    private MapCanvasView _canvas = null!;
    private VBoxContainer _cardBox = null!;
    private LineEdit _search = null!;
    private ItemList _results = null!;
    private readonly List<Poi> _found = new();
    private PlacesService? _places;

    /// <summary>The place selected (checks).</summary>
    public Poi? Selected { get; private set; }

    public static AdventureMap Create(Poi? focus = null) => new() { Name = "AdventureMap", _focus = focus };

    /// <summary>M: opens the map, or closes it when it is in front.</summary>
    public static void Toggle()
    {
        if (GameShell.Instance is not { InWorld: true } shell) return;
        if (shell.Top is AdventureMap) shell.Back();
        else if (shell.Top == null) shell.Push(Create());
    }

    /// <summary>Opens the map on a place (the Atlas's "Show on map").</summary>
    public static void Open(Poi? focus)
    {
        if (GameShell.Instance is not { InWorld: true } shell) return;
        if (shell.Top is AdventureMap open) { if (focus != null) open.Select(focus, centre: true); return; }
        shell.Push(Create(focus));
    }

    public override void _Ready()
    {
        _places = PlacesService.Instance;
        var dim = new ColorRect { Color = new Color(0.01f, 0.012f, 0.02f, 0.45f), MouseFilter = MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);
        var view = GetViewportRect().Size;
        var (body, _) = Framed("Map", InputHints.Format("Drag to move, wheel to zoom, click a place · {teleport} closes"),
            new Vector2(Mathf.Min(view.X - 32, 1240), Mathf.Min(view.Y - 32, 700)));
        var row = UiKit.HBox(12);
        row.SizeFlagsVertical = SizeFlags.ExpandFill;
        body.AddChild(row);

        _canvas = new MapCanvasView(this) { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        row.AddChild(_canvas);

        var side = UiKit.VBox(8);
        side.CustomMinimumSize = new Vector2(320, 0);
        row.AddChild(side);
        _search = new LineEdit { PlaceholderText = "Search a place…", ClearButtonEnabled = true };
        _search.TextChanged += Search;
        side.AddChild(_search);
        _results = new ItemList { CustomMinimumSize = new Vector2(0, 140), Visible = false, FocusMode = FocusModeEnum.All };
        _results.ItemActivated += i => PickResult((int)i);
        _results.ItemClicked += (i, _, _) => PickResult((int)i);
        side.AddChild(_results);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        side.AddChild(scroll);
        _cardBox = UiKit.VBox(8);
        _cardBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_cardBox);
        _cardBox.AddChild(UiKit.Text("Click a place on the map, or search one.", UiTheme.FontSmall, UiTheme.TextDim));
        var legend = UiKit.Text("Coloured: stamped · grey ?: seen, not reached · clouds: not explored yet", UiTheme.FontSmall, UiTheme.TextFaint);
        legend.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        side.AddChild(legend);
        if (PlaceSearch != null)
        {
            var places = UiKit.Button("Town search");
            places.TooltipText = "The town search: go there where the server allows teleporting";
            places.Pressed += () => { Shell.Back(); PlaceSearch?.Invoke(); };
            side.AddChild(places);
        }

        // centred on the player, or the place asked for
        if (Me?.Invoke() is { } me) _canvas.View.FocusOn(me.At.E, me.At.N, 32);
        if (_focus != null) Select(_focus, centre: true);
        if (AdventureService.Instance is { } svc) svc.SectionChanged += OnSection;
    }

    public override void _ExitTree()
    {
        if (AdventureService.Instance is { } svc) svc.SectionChanged -= OnSection;
    }

    private void OnSection(string id)
    {
        if (id != PlacesProgress.Section.Id) return;
        _canvas.Redraw();
        if (Selected != null) ShowCard(Selected);
    }

    public override void OnShown() => _canvas.CallDeferred(Control.MethodName.GrabFocus);

    private void Search(string text)
    {
        _found.Clear();
        _results.Clear();
        var cat = _places?.Catalogue;
        string q = PlaceIndex.Normalize(text.Trim());
        if (cat != null && q.Length >= 2)
        {
            var mine = AdventureService.Instance?.Mine.Get(PlacesProgress.Section);
            var fog = _places!.MyFog;
            // only what this player has found or seen: the map is no list of secrets
            foreach (var poi in cat.All)
            {
                if (!PlaceIndex.Normalize(poi.Name).Contains(q, StringComparison.Ordinal)) continue;
                if (mine?.Has(poi) != true && !fog.Revealed(poi.E, poi.N)) continue;
                _found.Add(poi);
                if (_found.Count >= 12) break;
            }
            foreach (var poi in _found) _results.AddItem($"{PlaceCard.TitleOf(poi)} · {AtlasFacts.KindName(poi.Kind)}");
        }
        _results.Visible = _found.Count > 0;
    }

    private void PickResult(int i)
    {
        if (i < 0 || i >= _found.Count) return;
        Select(_found[i], centre: true);
    }

    /// <summary>Selects a place: its card in the side panel, centred if asked.</summary>
    public void Select(Poi poi, bool centre)
    {
        Selected = poi;
        if (centre) _canvas.View.FocusOn(poi.E, poi.N, Math.Max(_canvas.View.PxPerKm, 32));
        ShowCard(poi);
        _canvas.Redraw();
    }

    private void ShowCard(Poi poi)
    {
        foreach (var c in _cardBox.GetChildren()) c.QueueFree();
        if (_places == null) return;
        _cardBox.AddChild(PlaceCard.Build(poi, _places.Catalogue, AdventureService.Instance?.Mine.Get(PlacesProgress.Section), onMap: null));
    }

    /// <summary>M closes it again (the world's handler does not run while a page is open).</summary>
    public override void _UnhandledInput(InputEvent e)
    {
        if (!IsVisibleInTree() || !e.IsPressed() || e.IsEcho() || UiFocus.TextEntryActive || GetViewport().GuiGetFocusOwner() is LineEdit) return;
        if (e.IsActionPressed(PlayerInput.Teleport))
        {
            GetViewport().SetInputAsHandled();
            Shell.Back();
        }
    }

    // ===================================================================================================

    /// <summary>The map itself: the relief here, the fog and the top layer as two children drawn over it.</summary>
    public partial class MapCanvasView : Control
    {
        private static readonly int[] Steps = [2, 4, 8, 16, 32, 64, 128];

        public readonly MapView View = new(Steps);
        private readonly AdventureMap _map;
        private readonly Control _fog, _top;
        private static CountryData? _country;
        private Vector2 _last;
        private Vector2? _press;
        private bool _dragging;
        private int _fogVersion = -1;
        private bool _reliefDrawn;
        private readonly List<(Poi Poi, Vector2 At)> _drawn = new();
        private readonly List<(Vector2 At, float Width)> _labels = new();

        public MapCanvasView(AdventureMap map)
        {
            _map = map;
            FocusMode = FocusModeEnum.All;
            MouseFilter = MouseFilterEnum.Stop;
            ClipContents = true;
            TextureFilter = TextureFilterEnum.Linear;
            _fog = new FogLayer(this) { MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.Linear };
            _fog.SetAnchorsPreset(LayoutPreset.FullRect);
            _top = new TopLayer(this) { MouseFilter = MouseFilterEnum.Ignore };
            _top.SetAnchorsPreset(LayoutPreset.FullRect);
        }

        public MapCanvasView() : this(null!) { }

        public override void _Ready()
        {
            AddChild(_fog);
            AddChild(_top);
            Resized += Redraw;
            if (_country == null)
                try { _country = CountryData.LoadEmbedded(); }
                catch (Exception e) { GD.PushWarning($"[map] no canton borders: {e.Message}"); }
        }

        public void Redraw()
        {
            View.Size = Size;
            QueueRedraw();
            _fog.QueueRedraw();
            _top.QueueRedraw();
        }

        public override void _Draw()
        {
            View.Size = Size;
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.05f, 0.06f, 0.08f));
            if (MapRelief.TryGet() is { } relief) View.DrawSampled(this, relief, MapRelief.Lattice());
        }

        private sealed partial class FogLayer : Control
        {
            private readonly MapCanvasView _c;
            private static Shader? _shader;
            public FogLayer(MapCanvasView c) => _c = c;
            public FogLayer() : this(null!) { }

            public override void _Ready()
            {
                _shader ??= GD.Load<Shader>("res://src/Adventure/Places/fog_map.gdshader");
                var m = new ShaderMaterial { Shader = _shader };
                m.SetShaderParameter("style", Styles.StyleKit.Style switch
                {
                    Styles.VisualStyle.Ps1 => 0,
                    Styles.VisualStyle.Cartoon => 1,
                    _ => 2,
                });
                Material = m;
            }

            public override void _Draw() => _c.View.DrawSampled(this, FogTexture.Get(), FogTexture.Lattice);
        }

        private sealed partial class TopLayer : Control
        {
            private readonly MapCanvasView _c;
            public TopLayer(MapCanvasView c) => _c = c;
            public TopLayer() : this(null!) { }
            public override void _Draw() => _c.DrawTop(this);
        }

        private void DrawTop(CanvasItem on)
        {
            View.DrawBorders(on, _country, new Color(1, 1, 1, 0.22f));
            DrawPlaces(on);
            DrawMe(on);
            foreach (var overlay in Overlays)
            {
                try { overlay.Draw(View, on); }
                catch (Exception e) { GD.PushWarning($"[map] overlay {overlay.GetType().Name}: {e.Message}"); }
            }
            // the centre, for the stick and the A button
            var c = Size / 2;
            on.DrawLine(c - new Vector2(7, 0), c + new Vector2(7, 0), new Color(1, 1, 1, 0.5f), 1);
            on.DrawLine(c - new Vector2(0, 7), c + new Vector2(0, 7), new Color(1, 1, 1, 0.5f), 1);
        }

        private void DrawPlaces(CanvasItem on)
        {
            _drawn.Clear();
            _labels.Clear();
            if (_map._places is not { } places) return;
            var cat = places.Catalogue;
            var fog = places.MyFog;
            var mine = AdventureService.Instance?.Mine.Get(PlacesProgress.Section);
            var (w, s, e, n) = View.Bounds();
            int ppk = View.PxPerKm;
            var font = UiTheme.Font;
            foreach (var poi in cat.All)
            {
                if (poi.E < w || poi.E > e || poi.N < s || poi.N > n) continue;
                bool found = mine?.Found.ContainsKey(poi.Id) == true;
                bool landmark = poi.Tier <= 2 || cat.IsLandmark(poi);
                // minor places only when stamped and zoomed in; landmarks once sighted under revealed fog
                if (!found && (!landmark || !fog.Revealed(poi.E, poi.N))) continue;
                if (!landmark && ppk < 32) continue;
                if (landmark && !cat.IsLandmark(poi) && ppk < 4 && !found) continue;
                var at = View.ToScreen(poi.E, poi.N);
                float r = cat.IsLandmark(poi) ? 7 : landmark ? 5.5f : 3.5f;
                bool selected = _map.Selected == poi;
                on.DrawCircle(at, r + 2, new Color(0, 0, 0, 0.55f));
                if (found)
                {
                    on.DrawCircle(at, r + 1, selected ? UiTheme.Amber : new Color(0.96f, 0.94f, 0.88f));
                    PlaceCard.Glyph(on, poi.Kind, at, r * 0.7f, new Color(0.78f, 0.16f, 0.14f));
                }
                else
                {
                    on.DrawCircle(at, r + 1, selected ? UiTheme.Amber : new Color(0.55f, 0.57f, 0.6f));
                    on.DrawString(font, at + new Vector2(-r, r * 0.6f), "?", HorizontalAlignment.Center, r * 2, (int)(r * 1.8f), new Color(0.12f, 0.12f, 0.14f));
                }
                _drawn.Add((poi, at));
                if (ppk >= 16 && (found || cat.IsLandmark(poi)))
                {
                    string label = PlaceCard.TitleOf(poi);
                    float width = label.Length * 6.2f;
                    if (Collides(at, width)) continue;
                    _labels.Add((at, width));
                    on.DrawString(font, at + new Vector2(r + 5, 5), label, HorizontalAlignment.Left, -1, 12, new Color(0, 0, 0, 0.8f));
                    on.DrawString(font, at + new Vector2(r + 4, 4), label, HorizontalAlignment.Left, -1, 12, new Color(1, 1, 1, 0.95f));
                }
            }
        }

        private bool Collides(Vector2 at, float width)
        {
            foreach (var (other, ow) in _labels)
                if (Math.Abs(at.Y - other.Y) < 13 && at.X < other.X + ow + 8 && other.X < at.X + width + 8) return true;
            return false;
        }

        private void DrawMe(CanvasItem on)
        {
            if (Me?.Invoke() is not { } me) return;
            var at = View.ToScreen(me.At.E, me.At.N);
            // the yaw turns from north (screen up), clockwise
            var fwd = new Vector2(Mathf.Sin(-me.Yaw), -Mathf.Cos(me.Yaw));
            var side = new Vector2(-fwd.Y, fwd.X);
            Vector2[] arrow = [at + fwd * 11, at - fwd * 7 + side * 7, at - fwd * 3, at - fwd * 7 - side * 7];
            on.DrawColoredPolygon(arrow, new Color(0, 0, 0, 0.6f));
            Vector2[] inner = [at + fwd * 9, at - fwd * 5.5f + side * 5.5f, at - fwd * 2, at - fwd * 5.5f - side * 5.5f];
            on.DrawColoredPolygon(inner, UiTheme.Amber);
        }

        public override void _GuiInput(InputEvent e)
        {
            switch (e)
            {
                case InputEventMouseMotion motion:
                    var moved = motion.Position - _last;
                    _last = motion.Position;
                    if (_press is { } down && !_dragging && motion.Position.DistanceTo(down) > 5) _dragging = true;
                    if (_dragging || (motion.ButtonMask & (MouseButtonMask.Right | MouseButtonMask.Middle)) != 0)
                    {
                        View.Pan(moved);
                        Redraw();
                    }
                    AcceptEvent();
                    break;
                case InputEventMouseButton button:
                    _last = button.Position;
                    if (button.ButtonIndex == MouseButton.WheelUp && button.Pressed) { if (View.ZoomAt(button.Position, +1)) Redraw(); }
                    else if (button.ButtonIndex == MouseButton.WheelDown && button.Pressed) { if (View.ZoomAt(button.Position, -1)) Redraw(); }
                    else if (button.ButtonIndex == MouseButton.Left)
                    {
                        GrabFocus();
                        if (button.Pressed) { _press = button.Position; _dragging = false; }
                        else
                        {
                            if (!_dragging) ClickAt(button.Position, MouseButton.Left);
                            _press = null;
                            _dragging = false;
                        }
                    }
                    AcceptEvent();
                    break;
                case InputEventScreenDrag drag:
                    View.Pan(drag.Relative);
                    Redraw();
                    AcceptEvent();
                    break;
                case InputEventMagnifyGesture pinch:
                    if (View.ZoomAt(pinch.Position, pinch.Factor > 1 ? 1 : -1)) Redraw();
                    AcceptEvent();
                    break;
            }
        }

        private void ClickAt(Vector2 at, MouseButton button)
        {
            var (e, n) = View.ToLv95(at);
            var rel = new Vector2((float)(e - LatticeE), (float)(n - LatticeN));
            foreach (var overlay in Overlays)
                if (overlay.Click(rel, button)) { Redraw(); return; }
            Poi? best = null;
            float bd = 14 * 14;
            foreach (var (poi, p) in _drawn)
            {
                float d = p.DistanceSquaredTo(at);
                if (d < bd) { bd = d; best = poi; }
            }
            if (best != null) _map.Select(best, centre: false);
        }

        public override void _Process(double delta)
        {
            if (!_reliefDrawn && MapRelief.TryGet() != null) { _reliefDrawn = true; Redraw(); }
            if (FogTexture.Version != _fogVersion) { _fogVersion = FogTexture.Version; _fog.QueueRedraw(); _top.QueueRedraw(); }
            if (!HasFocus()) return;
            var pan = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
            if (pan != Vector2.Zero)
            {
                View.Pan(-pan * (float)(620 * delta));
                Redraw();
            }
            if (!XR.XrSession.Active)
            {
                if (Input.IsActionJustPressed(PlayerInput.MapZoomIn) && View.ZoomAt(Size / 2, +1)) Redraw();
                if (Input.IsActionJustPressed(PlayerInput.MapZoomOut) && View.ZoomAt(Size / 2, -1)) Redraw();
            }
            if (Input.IsActionJustPressed("ui_accept")) ClickAt(Size / 2, MouseButton.Left);
        }
    }
}
