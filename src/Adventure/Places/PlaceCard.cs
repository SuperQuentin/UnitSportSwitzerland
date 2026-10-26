using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Places;

/// <summary>
/// A place's card (#1224, places plan 7.2), the same in the adventure map's side panel and the Atlas: the
/// stamp (drawn, no texture), the name, kind, height and canton, the fact (a curated card's, else the
/// generated line and its Wikidata facts), and the actions: Track, and Show on map or Show in Atlas.
/// </summary>
public static partial class PlaceCard
{
    /// <summary>The fact shown for a POI: the curated card's, else generated.</summary>
    public static string FactOf(Poi poi, PoiCatalogue cat) =>
        PoiCatalogueHost.Cards.TryGetValue(poi, out var card) ? card.Fact : AtlasFacts.For(poi, cat, AtlasFactsStore.For(poi));

    public static string TitleOf(Poi poi) =>
        PoiCatalogueHost.Cards.TryGetValue(poi, out var card) && card.Title.Length > 0 ? card.Title : poi.Name;

    public static string KindLine(Poi poi)
    {
        string text = Capital(AtlasFacts.KindName(poi.Kind));
        if (poi.Kind != PoiKind.Lake && poi.Elevation > 0) text += $" · {AtlasFacts.Num(poi.Elevation)} m";
        if (poi.Canton.Length > 0) text += $" · {AtlasChapters.NameOf(poi.Canton)}";
        return text;
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <param name="onMap">The "Show on map" action (the Atlas); null on the map, which shows "Show in Atlas".</param>
    public static Control Build(Poi poi, PoiCatalogue cat, PlacesProgress? mine, Action? onMap)
    {
        var col = UiKit.VBox(8);
        var stamp = mine?.StampOf(poi);
        var head = UiKit.HBox(12);
        head.AddChild(new StampMark(poi, stamp) { CustomMinimumSize = new Vector2(72, 72) });
        var names = UiKit.VBox(2);
        names.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        names.AddChild(UiKit.Text(TitleOf(poi), UiTheme.FontHeading, UiTheme.Text, bold: true));
        names.AddChild(UiKit.Text(KindLine(poi), UiTheme.FontSmall, UiTheme.TextDim));
        names.AddChild(UiKit.Text(stamp is { } s ? $"Stamped {StampDay.Text(s.Day)}, {StampDay.ModeText(s.Mode)}"
            : "Not found yet", UiTheme.FontSmall, stamp != null ? UiTheme.Good : UiTheme.TextFaint));
        if (mine != null && mine.Synced.Contains(poi.Id)) names.AddChild(UiKit.Text("View synced", UiTheme.FontSmall, UiTheme.Good));
        head.AddChild(names);
        col.AddChild(head);
        var fact = UiKit.Text(FactOf(poi, cat), UiTheme.FontBody, UiTheme.Text);
        fact.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        fact.CustomMinimumSize = new Vector2(260, 0);
        col.AddChild(fact);
        var buttons = UiKit.HBox(8);
        var svc = AdventureService.Instance;
        bool tracked = svc?.TrackedPlace is { } t && Math.Abs(t.E - poi.E) < 1 && Math.Abs(t.N - poi.N) < 1;
        var track = UiKit.Button(tracked ? "Untrack" : "Track", primary: !tracked);
        track.Name = "Track";
        track.Pressed += () =>
        {
            if (svc == null) return;
            if (svc.TrackedPlace is { } now && Math.Abs(now.E - poi.E) < 1 && Math.Abs(now.N - poi.N) < 1)
            {
                svc.UntrackPlace();
                track.Text = "Track";
            }
            else
            {
                svc.TrackPlace(TitleOf(poi), poi.E, poi.N, Math.Max(DiscoveryRules.RadiusOf(poi), 30));
                track.Text = "Untrack";
            }
        };
        buttons.AddChild(track);
        if (onMap != null)
        {
            var show = UiKit.Button("Show on map");
            show.Pressed += onMap;
            buttons.AddChild(show);
        }
        else
        {
            var atlas = UiKit.Button("Show in Atlas");
            atlas.Pressed += () => AtlasPage.Open(poi);
            buttons.AddChild(atlas);
        }
        col.AddChild(buttons);
        return col;
    }

    /// <summary>The stamp, drawn: a ring, the kind's glyph, the canton; grey and dashed when not found yet.</summary>
    public partial class StampMark : Control
    {
        private readonly Poi _poi;
        private readonly Stamp? _stamp;

        public StampMark(Poi poi, Stamp? stamp)
        {
            _poi = poi;
            _stamp = stamp;
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public StampMark() : this(null!, null) { }

        public override void _Draw()
        {
            var c = Size / 2;
            float r = Mathf.Min(Size.X, Size.Y) / 2 - 3;
            var ink = _stamp != null ? new Color(0.78f, 0.16f, 0.14f) : new Color(1, 1, 1, 0.25f);
            if (_stamp != null) DrawCircle(c, r, new Color(0.96f, 0.93f, 0.86f, 0.12f));
            DrawArc(c, r, 0, Mathf.Tau, 48, ink, 2.5f);
            DrawArc(c, r - 6, 0, Mathf.Tau, 48, ink, 1f);
            Glyph(this, _poi.Kind, c, r * 0.45f, ink);
            if (_poi.Canton.Length > 0)
                DrawString(UiTheme.Bold, c + new Vector2(-r, r * 0.72f), _poi.Canton, HorizontalAlignment.Center, r * 2, 11, ink);
        }
    }

    /// <summary>A kind's glyph, drawn with primitives (the map's icons and the stamps): a peak's triangle, a lake's wave...</summary>
    public static void Glyph(CanvasItem on, PoiKind kind, Vector2 c, float s, Color ink)
    {
        switch (kind)
        {
            case PoiKind.Peak or PoiKind.SummitCross:
                on.DrawColoredPolygon([c + new Vector2(-s, s * 0.7f), c + new Vector2(0, -s), c + new Vector2(s, s * 0.7f)], ink);
                break;
            case PoiKind.Pass:
                on.DrawPolyline([c + new Vector2(-s, -s * 0.4f), c + new Vector2(0, s * 0.4f), c + new Vector2(s, -s * 0.4f)], ink, 2.5f);
                break;
            case PoiKind.Lake or PoiKind.Waterfall or PoiKind.ShipLanding or PoiKind.Dam:
                on.DrawArc(c + new Vector2(-s * 0.5f, 0), s * 0.5f, 0, Mathf.Pi, 8, ink, 2.5f);
                on.DrawArc(c + new Vector2(s * 0.5f, 0), s * 0.5f, Mathf.Pi, Mathf.Tau, 8, ink, 2.5f);
                break;
            case PoiKind.Castle or PoiKind.LookoutTower:
                on.DrawRect(new Rect2(c.X - s * 0.6f, c.Y - s * 0.6f, s * 1.2f, s * 1.4f), ink);
                break;
            case PoiKind.Church or PoiKind.Chapel:
                on.DrawLine(c + new Vector2(0, -s), c + new Vector2(0, s), ink, 2.5f);
                on.DrawLine(c + new Vector2(-s * 0.55f, -s * 0.35f), c + new Vector2(s * 0.55f, -s * 0.35f), ink, 2.5f);
                break;
            case PoiKind.RailStation or PoiKind.BusStop or PoiKind.CableCarStation:
                on.DrawCircle(c, s * 0.75f, ink);
                break;
            default:
                on.DrawRect(new Rect2(c.X - s * 0.5f, c.Y - s * 0.5f, s, s), ink, filled: false, width: 2);
                break;
        }
    }
}

/// <summary>
/// The Atlas's Wikidata facts (<c>atlas_facts.json</c>, #1224): read once from the chunk directory, else
/// the copy the server sent into the cache (<c>AssetKind.AtlasFacts</c>), by tag and hash. Missing: nothing.
/// </summary>
public static class AtlasFactsStore
{
    private static Dictionary<(PoiKind, ulong), WikiFact>? _facts;

    public static WikiFact? For(Poi poi)
    {
        if (_facts == null) Load();
        return _facts!.GetValueOrDefault((poi.Kind, poi.Hash));
    }

    private static void Load()
    {
        _facts = new();
        var dirs = new List<string>();
        // --pois <file>: its facts beside it
        if (CmdArgs.Value("--pois") is { Length: > 0 } file && file != "fixture")
            dirs.Add(System.IO.Path.GetDirectoryName(ProjectSettings.GlobalizePath(file)) ?? "");
        dirs.Add(TerrainPaths.FindChunkDir());
        dirs.Add(TerrainPaths.FindCacheDir());
        foreach (string dir in dirs)
        {
            string path = System.IO.Path.Combine(dir, "atlas_facts.json");
            if (!System.IO.File.Exists(path)) continue;
            try
            {
                _facts = AtlasFacts.ParseFacts(System.IO.File.ReadAllText(path));
                GD.Print($"[atlas] {_facts.Count} Wikidata facts ({path})");
                return;
            }
            catch (Exception e) { GD.PushWarning($"[atlas] {path}: {e.Message}"); }
        }
    }

    /// <summary>A new world or a new file from the server: read again on next use.</summary>
    public static void Reset() => _facts = null;
}
