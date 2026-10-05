using System.Text.Json;
using Godot;

namespace UnitSport.Items.Fishing;

/// <summary>
/// The catch book (#493), like the cantonal Fangstatistik every permit holder keeps: per species, how many
/// were landed, kept and released, the best length and weight and where. Saved to <c>user://fishing.json</c>
/// by species <b>name</b> (reordering the catalogue never moves a record). Local only; the field journal
/// (<c>Birds.BirdJournal</c>, its Fish page) shows it.
/// </summary>
public static class FishJournal
{
    private const string File = "user://fishing.json";

    public sealed class Entry
    {
        public int Landed { get; set; }
        public int Kept { get; set; }
        public float BestCm { get; set; }
        public float BestKg { get; set; }
        public string BestWhere { get; set; } = "";
    }

    private static Dictionary<string, Entry>? _data;

    /// <summary>False for the checks: they record nothing on the real save.</summary>
    public static bool Persist { get; set; } = true;

    private static Dictionary<string, Entry> Data => _data ??= Load();

    public static Entry? Of(FishSpecies s) => Data.GetValueOrDefault(s.Name);

    public static int SpeciesLanded => Data.Count(kv => kv.Value.Landed > 0);

    /// <summary>Records a fish on the bank; true when it is the best of its species so far.</summary>
    public static bool Record(Catch c, string where)
    {
        if (!Data.TryGetValue(c.Species.Name, out var e)) Data[c.Species.Name] = e = new Entry();
        bool best = e.Landed > 0 && c.Kg > e.BestKg;
        e.Landed++;
        if (c.Kept) e.Kept++;
        if (c.Kg > e.BestKg)
        {
            e.BestKg = MathF.Round(c.Kg, 2);
            e.BestCm = c.Cm;
            e.BestWhere = where;
        }
        Save();
        return best;
    }

    /// <summary>Forgets everything in memory (the checks start from an empty book).</summary>
    public static void Reset() => _data = new();

    private static Dictionary<string, Entry> Load()
    {
        if (!Persist) return new();
        try
        {
            if (!Godot.FileAccess.FileExists(File)) return new();
            using var f = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Read);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(f.GetAsText()) ?? new();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[fishing] could not read {File}: {ex.Message}; starting a new catch book");
            return new();
        }
    }

    private static void Save()
    {
        if (!Persist || _data == null) return;
        try { Core.JsonStore.Save(File, _data); }
        catch (Exception ex) { GD.PushWarning($"[fishing] could not write {File}: {ex.Message}"); }
    }
}
