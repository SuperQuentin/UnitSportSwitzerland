using System.Text;
using System.Text.Json;
using Godot;
using UnitSport.Core;
using UnitSport.Ui;

namespace UnitSport.Birds;

/// <summary>
/// The field journal's data: every species seen and bagged, and the hunt's score. Saved to
/// <c>user://birds.json</c> by species <b>name</b>, like the inventory, so reordering the catalogue
/// never moves a record. Local only. Shown as the <b>Birds</b> and <b>Fish</b> tabs of the Journal
/// (J, <see cref="AdventureLog"/>, #1223), which list the whole catalogue so the player can see what is
/// still out there.
/// </summary>
public partial class BirdJournal : Node
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

    public int Score => _data.Score;
    public int SeenCount => _data.Species.Count(kv => kv.Value.Seen > 0);

    public Entry? this[BirdSpecies s] => _data.Species.GetValueOrDefault(s.Name);

    public override void _Ready()
    {
        Name = "BirdJournal";
        Load();
        // --journal: the Journal open on the Birds tab, for a screenshot
        if (CmdArgs.Has("--journal"))
            GetTree().CreateTimer(1.5).Timeout += () => AdventureLog.Open(AdventureLog.BirdsTab);
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

    /// <summary>The Birds tab: the score, then every species, seen ones in bold, game in green, protected in grey.</summary>
    public Control BuildBirdsPage()
    {
        int bagged = _data.Species.Values.Sum(e => e.Bagged);
        var sb = new StringBuilder("[table=5]");
        foreach (var s in BirdCatalog.All)
        {
            var e = _data.Species.GetValueOrDefault(s.Name);
            string colour = s.IsGame ? "#8ad07a" : "#9098a0";
            string name = e is { Seen: > 0 } ? $"[b]{s.Name}[/b]" : s.Name;
            sb.Append($"[cell][color={colour}]{name}[/color]  [/cell]");
            sb.Append($"[cell][i]{s.Latin}[/i]  [/cell]");
            sb.Append(System.FormattableString.Invariant($"[cell]{s.Length * 100:0} cm / {s.Wingspan * 100:0} cm, {s.MinAltitude}–{s.MaxAltitude} m, {s.Presence}  [/cell]"));
            sb.Append($"[cell]{(s.IsGame ? $"season {s.SeasonFrom}–{s.SeasonTo}" : "protected")}  [/cell]");
            sb.Append($"[cell]{(e == null ? "" : $"seen {e.Seen}, bagged {e.Bagged}{(e.Illegal > 0 ? $", [color=#e05040]illegal {e.Illegal}[/color]" : "")}")}[/cell]");
        }
        sb.Append("[/table]");
        return Page($"Seen {SeenCount} / {BirdCatalog.All.Length} species    Bagged {bagged}    Score {_data.Score}", sb.ToString(),
            "Green: game species (season in months). Grey: protected. Bold: seen.");
    }

    /// <summary>The Fish tab (#493): the catch book and every species of Swiss waters.</summary>
    public static Control BuildFishPage()
    {
        var all = Items.Fishing.FishCatalog.All;
        int landed = all.Sum(f => Items.Fishing.FishJournal.Of(f)?.Landed ?? 0);
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
        return Page($"Landed {Items.Fishing.FishJournal.SpeciesLanded} / {all.Length} species    Fish on the bank {landed}", sb.ToString(),
            "Green: may be kept (minimum length, closed months). Grey: protected, always released. Bold: landed.");
    }

    /// <summary>A summary line, the scrolling table, and its legend.</summary>
    private static Control Page(string summary, string table, string legend)
    {
        var col = UiKit.VBox(8);
        col.AddChild(UiKit.Text(summary, UiTheme.FontBody, UiTheme.Text, bold: true));
        var text = new RichTextLabel
        {
            BbcodeEnabled = true, ScrollActive = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FocusMode = Control.FocusModeEnum.All, Text = table,
        };
        text.AddThemeFontOverride("normal_font", UiTheme.Font);
        text.AddThemeFontOverride("bold_font", UiTheme.Bold);
        text.AddThemeFontSizeOverride("normal_font_size", UiTheme.FontSmall);
        text.AddThemeFontSizeOverride("bold_font_size", UiTheme.FontSmall);
        col.AddChild(text);
        col.AddChild(UiKit.Text(legend, UiTheme.FontTiny, UiTheme.TextFaint, wrap: true));
        return col;
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
