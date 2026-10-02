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

        var title = UiTheme.Title("Field journal — birds of Switzerland");
        rows.AddChild(title);

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

        var hint = new Label { Text = Core.InputHints.Format("{bird_journal} / Esc closes. Green = game species (season in months), grey = protected.") };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        rows.AddChild(hint);

        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--journal") >= 0)
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

    private void Refresh()
    {
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
