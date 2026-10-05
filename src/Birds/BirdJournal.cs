using System.Text;
using System.Text.Json;
using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Birds;

/// <summary>
/// The field journal: every species seen and bagged, and the hunt's score. Saved to
/// <c>user://birds.json</c> by species <b>name</b>, like the inventory, so reordering the catalogue
/// never moves a record. Local only. J opens the panel, which lists the whole catalogue so the
/// player can see what is still out there.
/// </summary>
public partial class BirdJournal : CanvasLayer
{
    private const string File = "user://birds.json";

    public sealed class Entry
    {
        public int Seen { get; set; }
        public int Bagged { get; set; }
        /// <summary>Protected, or game out of season: shot anyway.</summary>
        public int Illegal { get; set; }
    }

    private sealed class SaveData
    {
        public int Score { get; set; }
        public Dictionary<string, Entry> Species { get; set; } = new();
    }

    private SaveData _data = new();
    private PanelContainer _panel = null!;
    private RichTextLabel _text = null!;
    private Label _summary = null!;
    private Label _title = null!;
    private Label _hint = null!;
    private Button _birdsTab = null!, _fishTab = null!;
    /// <summary>The fish page (#493): the catch book and every species of Swiss waters.</summary>
    private bool _fish;

    public int Score => _data.Score;
    public int SeenCount => _data.Species.Count(kv => kv.Value.Seen > 0);
    public bool IsOpen => _panel.Visible;

    public Entry? this[BirdSpecies s] => _data.Species.GetValueOrDefault(s.Name);

    public override void _Ready()
    {
        Name = "BirdJournal";
        Layer = 11;
        Load();

        var centre = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(centre);

        _panel = new PanelContainer { Visible = false, CustomMinimumSize = new Vector2(760, 520) };
        var style = new StyleBoxFlat { BgColor = new Color(0.05f, 0.06f, 0.08f, 0.94f) };
        style.SetContentMarginAll(18);
        style.SetCornerRadiusAll(6);
        _panel.AddThemeStyleboxOverride("panel", style);
        centre.AddChild(_panel);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);
        _panel.AddChild(rows);

        _title = UiTheme.Title("Field journal — birds of Switzerland");
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 12);
        _title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        top.AddChild(_title);
        _birdsTab = new Button { Text = "Birds", ToggleMode = true, ButtonPressed = true };
        _fishTab = new Button { Text = "Fish", ToggleMode = true };
        _birdsTab.Pressed += () => ShowPage(false);
        _fishTab.Pressed += () => ShowPage(true);
        top.AddChild(_birdsTab);
        top.AddChild(_fishTab);
        rows.AddChild(top);

        _summary = new Label();
        rows.AddChild(_summary);

        _text = new RichTextLabel
        {
            BbcodeEnabled = true, ScrollActive = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(720, 430), FocusMode = Control.FocusModeEnum.All,
        };
        _text.AddThemeFontSizeOverride("normal_font_size", 13);
        _text.AddThemeFontSizeOverride("bold_font_size", 13);
        rows.AddChild(_text);

        _hint = new Label();
        _hint.AddThemeFontSizeOverride("font_size", 12);
        _hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        rows.AddChild(_hint);

        if (CmdArgs.Has("--journal"))
            GetTree().CreateTimer(1.5).Timeout += Open;
    }

    public void Seen(BirdSpecies s)
    {
        Get(s).Seen++;
        Save();
    }

    /// <summary>Records a bird shot and returns the points it earned (negative for a protected or out-of-season bird).</summary>
    public int Bag(BirdSpecies s, int month)
    {
        var e = Get(s);
        int points;
        if (s.InSeason(month))
        {
            e.Bagged++;
            points = s.Points;
        }
        else
        {
            e.Illegal++;
            points = s.IsGame ? -100 : -250;
        }
        _data.Score += points;
        Save();
        return points;
    }

    private Entry Get(BirdSpecies s)
    {
        if (!_data.Species.TryGetValue(s.Name, out var e)) _data.Species[s.Name] = e = new Entry();
        return e;
    }

    public void Open()
    {
        // the rod in hand opens the fish page (#493)
        _fish = Items.ItemController.Instance?.Inventory.HeldId == Items.ItemId.FishingRod;
        Refresh();
        _panel.Visible = true;
        UiFocus.Set(this, true);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _text.GrabFocus();
    }

    public void Close()
    {
        _panel.Visible = false;
        UiFocus.Set(this, false);
        Core.MouseCapture.Capture();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!e.IsPressed() || e.IsEcho()) return;
        if (IsOpen && (e.IsActionPressed(PlayerInput.BirdJournal) || e.IsActionPressed(PlayerInput.Menu) || e.IsActionPressed("ui_cancel")))
            Close();
        else if (!IsOpen && !UiFocus.TextEntryActive && e.IsActionPressed(PlayerInput.BirdJournal))
            Open();
        else return;
        GetViewport().SetInputAsHandled();
    }

    private void ShowPage(bool fish)
    {
        _fish = fish;
        Refresh();
    }

    private void Refresh()
    {
        _birdsTab.SetPressedNoSignal(!_fish);
        _fishTab.SetPressedNoSignal(_fish);
        _title.Text = _fish ? "Field journal — fish of Swiss waters" : "Field journal — birds of Switzerland";
        _hint.Text = Core.InputHints.Format(_fish
            ? "{bird_journal} / Esc closes. Green = may be kept (minimum length, closed months), grey = protected: always released."
            : "{bird_journal} / Esc closes. Green = game species (season in months), grey = protected.");
        if (_fish)
        {
            RefreshFish();
            return;
        }
        int bagged = _data.Species.Values.Sum(e => e.Bagged);
        _summary.Text = $"Seen {SeenCount} / {BirdCatalog.All.Length} species    Bagged {bagged}    Score {_data.Score}";

        var sb = new StringBuilder("[table=5]");
        foreach (var s in BirdCatalog.All)
        {
            var e = _data.Species.GetValueOrDefault(s.Name);
            string colour = s.IsGame ? "#8ad07a" : "#9098a0";
            string name = e is { Seen: > 0 } ? $"[b]{s.Name}[/b]" : s.Name;
            sb.Append($"[cell][color={colour}]{name}[/color]  [/cell]");
            sb.Append($"[cell][i]{s.Latin}[/i]  [/cell]");
            sb.Append($"[cell]{s.Length * 100:0} cm / {s.Wingspan * 100:0} cm, {s.MinAltitude}–{s.MaxAltitude} m, {s.Presence}  [/cell]");
            sb.Append($"[cell]{(s.IsGame ? $"season {s.SeasonFrom}–{s.SeasonTo}" : "protected")}  [/cell]");
            sb.Append($"[cell]{(e == null ? "" : $"seen {e.Seen}, bagged {e.Bagged}{(e.Illegal > 0 ? $", [color=#e05040]illegal {e.Illegal}[/color]" : "")}")}[/cell]");
        }
        sb.Append("[/table]");
        _text.Text = sb.ToString();
    }

    private void RefreshFish()
    {
        var all = Items.Fishing.FishCatalog.All;
        int landed = all.Sum(f => Items.Fishing.FishJournal.Of(f)?.Landed ?? 0);
        _summary.Text = $"Landed {Items.Fishing.FishJournal.SpeciesLanded} / {all.Length} species    Fish on the bank {landed}";
        var sb = new StringBuilder("[table=5]");
        foreach (var f in all)
        {
            var e = Items.Fishing.FishJournal.Of(f);
            string colour = f.Protected ? "#9098a0" : "#8ad07a";
            string name = e is { Landed: > 0 } ? $"[b]{f.Name}[/b]" : f.Name;
            string rules = f.Culled ? "invasive: killed"
                : f.Protected ? $"protected ({f.RedList})"
                : string.Join(", ", new[] { f.MinCm > 0 ? $"min {f.MinCm:0} cm" : "", f.ClosedText != "" ? $"closed {f.ClosedText}" : "" }.Where(x => x != ""));
            sb.Append($"[cell][color={colour}]{name}[/color]  [/cell]");
            sb.Append($"[cell][i]{f.Latin}[/i], {f.German}  [/cell]");
            sb.Append($"[cell]{f.Waters}, {f.Basins}, {f.AltMin}–{f.AltMax} m  [/cell]");
            sb.Append($"[cell]{(rules == "" ? "open all year" : rules)}{(f.Introduced ? ", introduced" : "")}  [/cell]");
            sb.Append(e == null ? "[cell][/cell]" : System.FormattableString.Invariant(
                $"[cell]landed {e.Landed}, kept {e.Kept}, best {e.BestCm:0} cm / {e.BestKg:0.00} kg ({e.BestWhere})[/cell]"));
        }
        sb.Append("[/table]");
        _text.Text = sb.ToString();
    }

    private void Load()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(File)) return;
            using var f = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Read);
            _data = JsonSerializer.Deserialize<SaveData>(f.GetAsText()) ?? new SaveData();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[birds] could not read {File}: {e.Message}; starting a new journal");
            _data = new SaveData();
        }
    }

    private void Save()
    {
        try { Core.JsonStore.Save(File, _data); }
        catch (Exception e) { GD.PushWarning($"[birds] could not write {File}: {e.Message}"); }
    }
}
