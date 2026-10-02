using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Loot;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Items;

/// <summary>
/// The smart binoculars' screen (#165): held in hand — no aiming, no zoom — next to a building's
/// door, or inside it, they read out that one building's loot: its kind, how many containers and
/// locked ones, and every item it can give with the chance that at least one container holds it
/// when restocked (<see cref="LootTables.BuildingTable"/>), best first. Only odds, never what a
/// container really holds or what was taken. Client-only, a child of <see cref="ItemController"/>.
/// At a shop's door (#273) they read its catalogue instead: the shop's type, every line with its
/// price here and the chance it is in stock when restocked (<see cref="ShopTables.Chance"/>).
///
/// <para>
/// The odds need the building's plan (<see cref="InteriorManager.GetOrCreate"/>, generated on a
/// worker or fetched from the server), so the panel says "scanning…" until it lands; plans and
/// tables are cached by building. Notes: <c>docs/notes/items/smart-binoculars.md</c>.
/// </para>
/// </summary>
public partial class SmartBinocularsHud : CanvasLayer
{
    /// <summary>How near a door (from the player) the binoculars pick its building up.</summary>
    public const float DoorReach = 8f;
    private const int MaxRows = 12;
    private const double TableSeconds = 60;   // occasions change the odds; recompute now and then

    /// <summary>The smart binoculars are the item in hand.</summary>
    public bool Held { get; set; }
    /// <summary>Whose position picks the building.</summary>
    public FootPlayer? Player { get; set; }

    /// <summary>The building being read out ("" for none), and its table once the plan is in (for probes).</summary>
    public string BuildingKey { get; private set; } = "";
    public IReadOnlyList<(ItemId Item, double Chance)>? Table => _table;

    private sealed partial class View : Control
    {
        public Action<View>? Drawer;
        public override void _Draw() => Drawer?.Invoke(this);
    }

    private readonly View _view = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Dictionary<string, InteriorLayout?> _layouts = new();
    private readonly HashSet<string> _inflight = new();
    private readonly Dictionary<string, (double At, List<(ItemId, double)> Rows)> _tables = new();
    private InteriorLayout? _layout;
    private List<(ItemId Item, double Chance)>? _table;
    private BuildingKind _kind;

    public SmartBinocularsHud()
    {
        Name = "SmartBinocularsHud";
        Layer = 11;   // under the inventory (12): opening the pack covers it
    }

    public override void _Ready()
    {
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _view.Drawer = Draw;
        AddChild(_view);
        _view.Visible = false;
    }

    public override void _Process(double delta)
    {
        _view.Visible = Held && Player != null && IsInstanceValid(Player);
        if (!_view.Visible) { BuildingKey = ""; return; }

        // inside: this building; outside: the door within reach, nearest first
        string key = "";
        var interiors = InteriorManager.Instance;
        if (Player!.Indoors && interiors?.Current is { } here)
        {
            key = here.Key;
            _kind = here.Kind;
        }
        else if (DoorIndex.Nearest(Player.GlobalPosition, DoorReach) is { } door)
        {
            key = door.Key.ToString();
            _kind = door.Kind;
        }
        if (key != BuildingKey)
        {
            BuildingKey = key;
            _layout = null;
            _table = null;
        }
        if (key != "" && interiors != null) Resolve(interiors, key);
        _view.QueueRedraw();
    }

    private void Resolve(InteriorManager manager, string key)
    {
        if (!_layouts.TryGetValue(key, out var layout))
        {
            if (_inflight.Add(key)) Load(manager, key);
            return;
        }
        _layout = layout;
        if (layout == null) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (!_tables.TryGetValue(key, out var t) || now - t.At > TableSeconds)
            _tables[key] = t = (now, layout.Shop != ShopType.None ? ShopTable(layout) : LootTables.BuildingTable(layout));
        _table = t.Rows;
    }

    /// <summary>A shop's catalogue as odds of finding each line in stock, best first; prices go in <see cref="_prices"/>.</summary>
    private List<(ItemId, double)> ShopTable(InteriorLayout layout)
    {
        bool season = ShopService.Instance?.HuntingSeason() ?? true;
        double markup = ShopTables.Markup(layout.Key);
        var rows = new List<(ItemId, double)>();
        foreach (var line in ShopTables.Catalogue(layout.Shop))
        {
            _prices[(layout.Key, line.Id)] = ShopTables.Price(ShopService.ValueOf(line.Id), markup);
            rows.Add((line.Id, ShopTables.Chance(layout.Shop, line.Id, season)));
        }
        rows.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return rows;
    }

    private readonly Dictionary<(string, ItemId), int> _prices = new();

    private async void Load(InteriorManager manager, string key)
    {
        InteriorLayout? layout = null;
        try { layout = await manager.GetOrCreate(key); }
        catch (Exception e) { GD.PushWarning($"[smartbin] plan for {key}: {e.Message}"); }
        if (!IsInsideTree()) return;
        _inflight.Remove(key);
        if (_layouts.Count > 200) { _layouts.Clear(); _tables.Clear(); _prices.Clear(); }   // bound the memory; the manager keeps its own cache
        _layouts[key] = layout;
    }

    // ---- drawing ------------------------------------------------------------------------------

    /// <summary>Low to high: dim red, amber, green.</summary>
    public static Color ChanceColor(double p)
    {
        float t = Mathf.Clamp((float)p / 0.6f, 0f, 1f);
        return t < 0.5f
            ? new Color(0.85f, 0.25f, 0.20f).Lerp(new Color(0.98f, 0.72f, 0.12f), t * 2f)
            : new Color(0.98f, 0.72f, 0.12f).Lerp(new Color(0.35f, 0.90f, 0.40f), (t - 0.5f) * 2f);
    }

    private static string Pct(double p) => p <= 0 ? "0%" : p < 0.01 ? "<1%" : $"{Math.Round(p * 100):F0}%";

    private static string KindName(BuildingKind k) => k switch
    {
        BuildingKind.House => "House",
        BuildingKind.Apartment => "Apartment block",
        BuildingKind.Commercial => "Shop / office",
        BuildingKind.Industrial => "Works / warehouse",
        BuildingKind.Agricultural => "Farm building",
        BuildingKind.Sacral => "Church",
        BuildingKind.Civic => "Public building",
        BuildingKind.Annex => "Shed",
        BuildingKind.UnderConstruction => "Building site",
        BuildingKind.Garage => "Garage",
        _ => "Building",
    };

    private void Draw(View v)
    {
        var font = ThemeDB.FallbackFont;
        var size = v.Size;
        var cyan = new Color(0.3f, 0.9f, 1f);
        bool ready = BuildingKey != "" && _layout != null && _table != null;
        int rows = ready ? Math.Max(1, Math.Min(MaxRows, _table!.Count)) : 0;
        var panel = new Rect2(size.X - 300, 90, 280, ready ? 96 + rows * 22 : 80);
        v.DrawRect(panel, new Color(0.02f, 0.05f, 0.07f, 0.86f));
        v.DrawRect(panel, new Color(cyan, 0.8f), false, 1.5f);
        v.DrawString(font, panel.Position + new Vector2(10, 18), "LOOT SCAN", HorizontalAlignment.Left, -1, 11, new Color(0.45f, 0.85f, 0.95f));

        if (BuildingKey == "")
        {
            v.DrawString(font, panel.Position + new Vector2(10, 44), "Stand at a building's door", HorizontalAlignment.Left, 260, 15, Colors.White);
            return;
        }
        if (!(ready && _layout!.Shop != ShopType.None))
            v.DrawString(font, panel.Position + new Vector2(10, 40), KindName(_kind), HorizontalAlignment.Left, 260, 17, Colors.White);
        if (!ready)
        {
            bool failed = _layouts.TryGetValue(BuildingKey, out var l) && l == null;
            v.DrawString(font, panel.Position + new Vector2(10, 64), failed ? "no reading" : "scanning…", HorizontalAlignment.Left, 260, 14, new Color(0.7f, 0.8f, 0.85f));
            return;
        }

        var shop = _layout!.Shop;
        int machines = _layout.Furniture.Count(f => f.Type == FurnitureType.VendingMachine);
        if (shop != ShopType.None)
        {
            v.DrawString(font, panel.Position + new Vector2(10, 40), $"{ShopTables.Name(shop)} shop", HorizontalAlignment.Left, 260, 17, new Color(0.98f, 0.78f, 0.35f));
            v.DrawString(font, panel.Position + new Vector2(10, 60), $"{_table!.Count} lines" + (machines > 0 ? ", a PAUSA machine" : ""),
                HorizontalAlignment.Left, 260, 13, new Color(0.75f, 0.85f, 0.9f));
            v.DrawString(font, panel.Position + new Vector2(10, 78), "price here, chance in stock", HorizontalAlignment.Left, 260, 11, new Color(0.55f, 0.7f, 0.75f));
        }
        else
        {
            int containers = _layout.Furniture.Count(f => LootTables.IsLootable(f.Type));
            int locked = _layout.Furniture.Count(f => LootTables.IsLocked(f.Type));
            string sub = $"{containers} containers" + (locked > 0 ? $", {locked} locked" : "") + (machines > 0 ? ", a PAUSA machine" : "");
            v.DrawString(font, panel.Position + new Vector2(10, 60), sub, HorizontalAlignment.Left, 260, 13,
                locked > 0 ? new Color(0.98f, 0.78f, 0.35f) : new Color(0.75f, 0.85f, 0.9f));
            v.DrawString(font, panel.Position + new Vector2(10, 78), "chance to find, any container", HorizontalAlignment.Left, 260, 11, new Color(0.55f, 0.7f, 0.75f));
        }

        if (_table!.Count == 0)
            v.DrawString(font, panel.Position + new Vector2(10, 104), "nothing to find here", HorizontalAlignment.Left, 260, 14, new Color(0.8f, 0.6f, 0.6f));
        for (int i = 0; i < Math.Min(MaxRows, _table.Count); i++)
        {
            var (item, p) = _table[i];
            float y = panel.Position.Y + 88 + i * 22;
            DrawIcon(v, item, new Vector2(panel.Position.X + 10, y), 18);
            int price = 0;
            bool priced = shop != ShopType.None && _prices.TryGetValue((_layout.Key, item), out price);
            v.DrawString(font, new Vector2(panel.Position.X + 34, y + 15), ItemDefs.Get(item)?.Name ?? item.ToString(), HorizontalAlignment.Left, priced ? 110 : 150, 13, Colors.White);
            if (priced)
                v.DrawString(font, new Vector2(panel.Position.X + 144, y + 15), $"{price}.-", HorizontalAlignment.Right, 40, 12, new Color(0.98f, 0.78f, 0.35f));
            // a bar for the chance, and the number
            var bar = new Rect2(panel.Position.X + 186, y + 5, 50, 9);
            v.DrawRect(bar, new Color(1, 1, 1, 0.08f));
            v.DrawRect(bar with { Size = new Vector2(bar.Size.X * (float)Math.Clamp(p, 0, 1), bar.Size.Y) }, ChanceColor(p));
            v.DrawString(font, new Vector2(panel.Position.X + 236, y + 15), Pct(p), HorizontalAlignment.Right, 36, 13, ChanceColor(p));
        }
    }

    private static void DrawIcon(CanvasItem c, ItemId id, Vector2 at, float px)
    {
        if (ItemIcons.Get(id) is not { } icon) return;
        c.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
        c.DrawTextureRect(icon, new Rect2(at, new Vector2(px, px)), false);
    }
}
