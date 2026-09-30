using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Loot;
using UnitSport.Terrain.Format;

namespace UnitSport.Items;

/// <summary>
/// The smart binoculars' screen, drawn over the normal binocular overlay: the target item, a
/// searchable picker for it, and on every building in view the chance (%) that one of its
/// containers yields it (<see cref="LootTables.BuildingChance"/>). The building nearest the screen
/// centre also gets a panel with its three best containers. Only chances are shown, never what a
/// container really holds. Client-only, a child of <see cref="ItemController"/>.
///
/// <para>
/// A building's chance needs its plan (<see cref="InteriorManager.GetOrCreate"/>, which may have
/// to generate it on a worker thread), so plans are fetched a few per second, nearest the centre
/// first, and kept; a marker shows "..." until its plan is in. Notes: <c>docs/notes/items/smart-binoculars.md</c>.
/// </para>
/// </summary>
public partial class SmartBinocularsHud : CanvasLayer
{
    public const float Range = 400f;
    private const int MaxMarkers = 14;
    private const float FetchesPerSecond = 3f;
    private const string SaveFile = "user://smart_binoculars.cfg";

    /// <summary>The optics are raised and looked through (the overlay is up).</summary>
    public bool Active { get; set; }

    /// <summary>The smart binoculars are the item in hand; the picker closes when they are not.</summary>
    public bool Held { get; set; }

    public ItemId Target { get; private set; } = ItemId.Francs;
    public bool PickerOpen { get; private set; }

    private sealed partial class View : Control
    {
        public Action<View>? Drawer;
        public override void _Draw() => Drawer?.Invoke(this);
    }

    private readonly View _view = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Dictionary<string, InteriorLayout?> _layouts = new();
    private readonly HashSet<string> _inflight = new();
    private readonly Dictionary<(string, ItemId), LootTables.BuildingOdds> _odds = new();
    private float _tokens = FetchesPerSecond;

    private struct Marker { public string Key; public Vector2 Screen; public float Distance; public BuildingKind Kind; }
    private readonly List<Marker> _markers = new();

    // picker
    private string _search = "";
    private int _selected;
    private List<ItemId> _filtered = new();

    public SmartBinocularsHud()
    {
        Name = "SmartBinocularsHud";
        Layer = 13;   // over the inventory's 12, where the binocular overlay is drawn
    }

    public override void _Ready()
    {
        _view.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _view.Drawer = Draw;
        AddChild(_view);
        _view.Visible = false;

        var cfg = new ConfigFile();
        if (cfg.Load(SaveFile) == Error.Ok && Enum.TryParse<ItemId>(cfg.GetValue("smart", "target", "Francs").AsString(), out var saved)
            && LootTables.Targets().Contains(saved))
            Target = saved;
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--smarttarget");   // "--smarttarget <item>": pick the target for a screenshot
        if (at >= 0 && at + 1 < args.Length && Enum.TryParse<ItemId>(args[at + 1], true, out var t)) Target = t;
        if (Array.IndexOf(args, "--smartpicker") >= 0) _openPicker = true;   // screenshot: open the picker once held
    }

    public override void _ExitTree() => UiFocus.Set(this, false);

    // ---- target picker ----------------------------------------------------------------------

    public void OpenPicker()
    {
        if (PickerOpen) { ClosePicker(); return; }
        PickerOpen = true;
        _search = "";
        Refilter();
        _selected = Math.Max(0, _filtered.IndexOf(Target));
        UiFocus.Set(this, true);   // typing searches instead of walking
    }

    public void ClosePicker()
    {
        if (!PickerOpen) return;
        PickerOpen = false;
        UiFocus.Set(this, false);
    }

    private void Choose(ItemId id)
    {
        Target = id;
        var cfg = new ConfigFile();
        cfg.SetValue("smart", "target", id.ToString());
        cfg.Save(SaveFile);
        ClosePicker();
    }

    private void Refilter()
    {
        string q = _search.Trim();
        _filtered = LootTables.Targets()
            .Where(id => q.Length == 0 || ItemDefs.Get(id)!.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _filtered.Count - 1));
    }

    private void Move(int by)
    {
        if (_filtered.Count == 0) return;
        _selected = Mathf.PosMod(_selected + by, _filtered.Count);
    }

    public override void _Input(InputEvent e)
    {
        if (!PickerOpen) return;
        bool handled = true;
        if (e is InputEventKey { Pressed: true } k)
        {
            switch (k.Keycode)
            {
                case Key.Escape or Key.Tab: ClosePicker(); break;
                case Key.Up: Move(-1); break;
                case Key.Down: Move(1); break;
                case Key.Pageup: Move(-8); break;
                case Key.Pagedown: Move(8); break;
                case Key.Enter or Key.KpEnter: if (_filtered.Count > 0) Choose(_filtered[_selected]); break;
                case Key.Backspace:
                    if (_search.Length > 0) { _search = _search[..^1]; Refilter(); }
                    break;
                default:
                    if (k.Unicode >= 32 && k.Unicode != 127)
                    {
                        _search += char.ConvertFromUtf32((int)k.Unicode);
                        _selected = 0;
                        Refilter();
                    }
                    else handled = false;
                    break;
            }
        }
        else if (e is InputEventMouseButton { Pressed: true } mb)
        {
            if (mb.ButtonIndex == MouseButton.WheelUp) Move(-1);
            else if (mb.ButtonIndex == MouseButton.WheelDown) Move(1);
            else if (e.IsActionPressed(PlayerInput.UseItem) && _filtered.Count > 0) Choose(_filtered[_selected]);
            else handled = false;
        }
        else if (e is InputEventJoypadButton { Pressed: true })
        {
            if (e.IsActionPressed(PlayerInput.NextItem)) Move(1);
            else if (e.IsActionPressed(PlayerInput.PrevItem)) Move(-1);
            else if (e.IsActionPressed(PlayerInput.UseItem) && _filtered.Count > 0) Choose(_filtered[_selected]);
            else if (e.IsActionPressed("ui_cancel")) ClosePicker();
            else handled = false;
        }
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }

    // ---- markers and plan fetching ---------------------------------------------------------

    private bool _openPicker;

    public override void _Process(double delta)
    {
        if (_openPicker && Held) { _openPicker = false; OpenPicker(); }
        if (PickerOpen && !Held) ClosePicker();
        _view.Visible = Active || PickerOpen;
        _markers.Clear();
        if (!_view.Visible) return;

        var cam = GetViewport().GetCamera3D();
        if (cam != null && Active)
        {
            var size = GetViewport().GetVisibleRect().Size;
            var centre = size * 0.5f;
            var origin = cam.GlobalPosition;
            var seen = new HashSet<string>();
            foreach (var d in DoorIndex.All())
            {
                float dist = origin.DistanceTo(d.World);
                if (dist > Range) continue;
                var at = d.World + Vector3.Up * 3.5f;
                if (cam.IsPositionBehind(at)) continue;
                var s = cam.UnprojectPosition(at);
                if (s.X < 40 || s.Y < 40 || s.X > size.X - 40 || s.Y > size.Y - 40) continue;
                string key = d.Key.ToString();
                if (!seen.Add(key)) continue;
                _markers.Add(new Marker { Key = key, Screen = s, Distance = dist, Kind = d.Kind });
            }
            _markers.Sort((a, b) => a.Screen.DistanceSquaredTo(centre).CompareTo(b.Screen.DistanceSquaredTo(centre)));
            if (_markers.Count > MaxMarkers) _markers.RemoveRange(MaxMarkers, _markers.Count - MaxMarkers);
            Fetch((float)delta);
        }
        _view.QueueRedraw();
    }

    /// <summary>A few plan requests per second, nearest the screen centre first; the rest wait their turn.</summary>
    private void Fetch(float dt)
    {
        _tokens = Mathf.Min(FetchesPerSecond, _tokens + dt * FetchesPerSecond);
        if (InteriorManager.Instance is not { } manager) return;
        foreach (var m in _markers)
        {
            if (_tokens < 1f || _inflight.Count >= 2) break;
            if (_layouts.ContainsKey(m.Key) || _inflight.Contains(m.Key)) continue;
            _tokens -= 1f;
            _inflight.Add(m.Key);
            Load(manager, m.Key);
        }
    }

    private async void Load(InteriorManager manager, string key)
    {
        InteriorLayout? layout = null;
        try { layout = await manager.GetOrCreate(key); }
        catch (Exception e) { GD.PushWarning($"[smartbin] plan for {key}: {e.Message}"); }
        if (!IsInsideTree()) return;
        _inflight.Remove(key);
        if (_layouts.Count > 400) { _layouts.Clear(); _odds.Clear(); }   // bound the memory; the manager keeps its own cache
        _layouts[key] = layout;
        _view.QueueRedraw();
    }

    private LootTables.BuildingOdds? OddsOf(string key)
    {
        if (!_layouts.TryGetValue(key, out var layout) || layout == null) return null;
        if (!_odds.TryGetValue((key, Target), out var odds))
            _odds[(key, Target)] = odds = LootTables.BuildingChance(layout, Target);
        return odds;
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

    private void Draw(View v)
    {
        var font = ThemeDB.FallbackFont;
        var size = v.Size;
        var def = ItemDefs.Get(Target)!;

        // the target, top centre
        var bar = new Rect2(size.X * 0.5f - 170, 22, 340, 52);
        v.DrawRect(bar, new Color(0.02f, 0.06f, 0.08f, 0.8f));
        v.DrawRect(bar.Grow(-1), new Color(0.3f, 0.9f, 1f, 0.8f), false, 1.5f);
        DrawIcon(v, Target, new Vector2(bar.Position.X + 8, bar.Position.Y + 10), 2);
        v.DrawString(font, bar.Position + new Vector2(50, 22), "TARGET", HorizontalAlignment.Left, -1, 11, new Color(0.45f, 0.85f, 0.95f));
        v.DrawString(font, bar.Position + new Vector2(50, 42), def.Name, HorizontalAlignment.Left, 280, 20, Colors.White);
        if (!PickerOpen)
            v.DrawString(font, new Vector2(0, bar.End.Y + 16), InputHints.Format("{use_item} change target"),
                HorizontalAlignment.Center, size.X, 13, new Color(0.7f, 0.85f, 0.9f));

        var centre = size * 0.5f;
        for (int i = 0; i < _markers.Count; i++)
        {
            var m = _markers[i];
            var odds = OddsOf(m.Key);
            bool noPlan = _layouts.TryGetValue(m.Key, out var l) && l == null;
            if (odds == null && noPlan) continue;   // nothing to tell
            string text = odds is { } o ? Pct(o.Any) : "...";
            var col = odds is { } oo ? ChanceColor(oo.Any) : new Color(0.7f, 0.8f, 0.85f);
            var box = new Rect2(m.Screen + new Vector2(-28, -11), new Vector2(56, 22));
            v.DrawRect(box, new Color(0.02f, 0.05f, 0.07f, 0.82f));
            v.DrawRect(box, col, false, 2f);
            v.DrawString(font, box.Position + new Vector2(0, 16), text, HorizontalAlignment.Center, box.Size.X, 15, col);
            v.DrawLine(m.Screen + new Vector2(0, 11), m.Screen + new Vector2(0, 18), col, 2f);
            v.DrawString(font, m.Screen + new Vector2(-28, 32), $"{m.Distance:F0} m", HorizontalAlignment.Center, 56, 11, new Color(0.75f, 0.85f, 0.9f));

        }
        if (_markers.Count > 0 && OddsOf(_markers[0].Key) is { } best) DrawPanel(v, _markers[0], best, size);   // last, over the markers

        // centre pip, so "nearest the centre" is something you can aim
        v.DrawCircle(centre, 2f, new Color(0.3f, 0.9f, 1f, 0.9f));

        if (PickerOpen) DrawPicker(v, size);
    }

    /// <summary>The nearest-to-centre building: its kind, total chance and three best containers.</summary>
    private static void DrawPanel(View v, Marker m, LootTables.BuildingOdds odds, Vector2 size)
    {
        var font = ThemeDB.FallbackFont;
        int rows = Math.Min(3, odds.Containers.Count);
        var panel = new Rect2(m.Screen + new Vector2(38, -14), new Vector2(200, 34 + Math.Max(1, rows) * 18));
        if (panel.End.X > size.X - 10) panel.Position = new Vector2(m.Screen.X - 38 - panel.Size.X, panel.Position.Y);
        if (panel.End.Y > size.Y - 10) panel.Position = new Vector2(panel.Position.X, size.Y - 10 - panel.Size.Y);
        v.DrawRect(panel, new Color(0.02f, 0.05f, 0.07f, 0.88f));
        v.DrawRect(panel, ChanceColor(odds.Any), false, 1.5f);
        v.DrawString(font, panel.Position + new Vector2(8, 18), $"{m.Kind}: {Pct(odds.Any)} any", HorizontalAlignment.Left, -1, 14, Colors.White);
        if (rows == 0)
            v.DrawString(font, panel.Position + new Vector2(8, 38), "no container holds it", HorizontalAlignment.Left, -1, 13, new Color(0.7f, 0.75f, 0.8f));
        for (int i = 0; i < rows; i++)
        {
            var c = odds.Containers[i];
            string name = c.Count > 1 ? $"{LootTables.Describe(c.Type)} x{c.Count}" : LootTables.Describe(c.Type);
            var y = panel.Position.Y + 38 + i * 18;
            v.DrawString(font, new Vector2(panel.Position.X + 8, y), name, HorizontalAlignment.Left, 120, 13, new Color(0.85f, 0.9f, 0.92f));
            v.DrawString(font, new Vector2(panel.Position.X + 8, y), Pct(c.Group), HorizontalAlignment.Right, 184, 13, ChanceColor(c.Group));
        }
    }

    private void DrawPicker(View v, Vector2 size)
    {
        var font = ThemeDB.FallbackFont;
        const int visible = 10;
        const float row = 30f;
        var panel = new Rect2(size.X * 0.5f - 160, size.Y * 0.5f - 190, 320, 70 + visible * row + 24);
        v.DrawRect(panel, new Color(0.02f, 0.05f, 0.07f, 0.94f));
        v.DrawRect(panel, new Color(0.3f, 0.9f, 1f), false, 2f);
        v.DrawString(font, panel.Position + new Vector2(12, 24), "Pick a target item", HorizontalAlignment.Left, -1, 16, Colors.White);
        v.DrawString(font, panel.Position + new Vector2(12, 50), _search.Length > 0 ? $"search: {_search}_" : "type to search...",
            HorizontalAlignment.Left, -1, 13, new Color(0.6f, 0.85f, 0.95f));

        int first = Math.Clamp(_selected - visible / 2, 0, Math.Max(0, _filtered.Count - visible));
        for (int i = 0; i < visible && first + i < _filtered.Count; i++)
        {
            var id = _filtered[first + i];
            var r = new Rect2(panel.Position + new Vector2(8, 64 + i * row), new Vector2(panel.Size.X - 16, row - 2));
            bool sel = first + i == _selected;
            if (sel) v.DrawRect(r, new Color(0.15f, 0.45f, 0.55f, 0.9f));
            DrawIcon(v, id, r.Position + new Vector2(4, 4), 1);
            v.DrawString(font, r.Position + new Vector2(32, 19), ItemDefs.Get(id)!.Name, HorizontalAlignment.Left, -1, 15,
                id == Target ? new Color(0.4f, 1f, 0.6f) : Colors.White);
        }
        if (_filtered.Count == 0)
            v.DrawString(font, panel.Position + new Vector2(12, 90), "nothing matches", HorizontalAlignment.Left, -1, 14, new Color(0.8f, 0.6f, 0.6f));
        v.DrawString(font, new Vector2(panel.Position.X, panel.End.Y - 8),
            InputHints.Format("wheel / arrows move, {use_item} / Enter picks, Esc closes"),
            HorizontalAlignment.Center, panel.Size.X, 11, new Color(0.6f, 0.7f, 0.75f));
    }

    private static void DrawIcon(CanvasItem c, ItemId id, Vector2 at, int scale)
    {
        if (ItemIcons.Get(id) is not { } icon) return;
        c.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
        c.DrawTextureRect(icon, new Rect2(at, new Vector2(ItemIcons.Size, ItemIcons.Size) * (scale * 1.5f)), false);
    }
}
