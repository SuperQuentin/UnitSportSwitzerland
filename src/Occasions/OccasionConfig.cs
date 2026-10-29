using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace UnitSport.Occasions;

/// <summary>One date range an occasion runs in, inclusive at both ends.</summary>
/// <remarks>
/// Either both ends are <c>MM-DD</c>, which recurs every year and may wrap over new year
/// (<c>12-20</c> → <c>01-06</c>), or both are <c>YYYY-MM-DD</c> for a one-off such as an Olympics,
/// or <c>From</c> is a weekday of the month (<c>fri#1</c>, <c>sat#-1</c>) and <c>To</c> the same
/// token or <c>+N</c> days after it (<see cref="OccasionWeekdays"/>).
/// </remarks>
public sealed class DateWindow
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";

    public DateWindow() { }

    public DateWindow(string from, string to)
    {
        From = from;
        To = to;
    }
}

/// <summary>Which facets an occasion runs with. Absent from the file means on.</summary>
public sealed class FacetToggles
{
    public bool Decorations { get; set; } = true;
    public bool Atmosphere { get; set; } = true;
    public bool Audio { get; set; } = true;
    public bool Loot { get; set; } = true;
    public bool Hunt { get; set; } = true;
    public bool Hats { get; set; } = true;
    public bool Games { get; set; } = true;

    public OccasionFacets ToFlags() =>
        (Decorations ? OccasionFacets.Decorations : 0)
        | (Atmosphere ? OccasionFacets.Atmosphere : 0)
        | (Audio ? OccasionFacets.Audio : 0)
        | (Loot ? OccasionFacets.Loot : 0)
        | (Hunt ? OccasionFacets.Hunt : 0)
        | (Hats ? OccasionFacets.Hats : 0)
        | (Games ? OccasionFacets.Games : 0);
}

/// <summary>
/// When and how one occasion runs: the part of an occasion that is policy rather than content.
/// The content — props, palette, sounds — is the <see cref="Occasion"/> class with the same id.
/// </summary>
public sealed class OccasionEntry
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Among overlapping occasions, the highest decides single-valued things like the sky.</summary>
    public int Priority { get; set; }

    public List<DateWindow> Schedule { get; set; } = new();
    public FacetToggles Facets { get; set; } = new();

    /// <summary>False locks the cosmetic part on: a player's "Off" is ignored for this one.</summary>
    public bool AllowClientOptOut { get; set; } = true;
}

public sealed class OccasionConfigFile
{
    public List<OccasionEntry> Occasions { get; set; } = new();
}

/// <summary>
/// Reads <c>user://occasions.json</c> over the built-in defaults.
///
/// <para>
/// The defaults are code, so a build always knows its own occasions; the file is written out on
/// first run so an operator has something to edit rather than a schema to guess. Entries are
/// matched by id and the file's version wins whole; an id only the code knows (an occasion added
/// in a later build) is still appended, so an old file never hides a new occasion.
/// </para>
/// </summary>
public static class OccasionConfig
{
    public const string File = "user://occasions.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Halloween through September and October, Christmas through November and December, UFO
    /// sightings on the first Friday of every month and the morning after (#863; the saucers only
    /// come at night, 21:00-05:00 game time, <see cref="SightingPlanner"/>).
    /// </summary>
    public static List<OccasionEntry> Defaults()
    {
        var entries = new List<OccasionEntry>
        {
            new()
            {
                Id = OccasionIds.Halloween, Priority = 10,
                Schedule = { new DateWindow("09-01", "10-31") },
                Facets = new FacetToggles { Games = false },
            },
            new()
            {
                Id = OccasionIds.Christmas, Priority = 10,
                Schedule = { new DateWindow("11-01", "12-31") },
                Facets = new FacetToggles { Games = false },
            },
            new()
            {
                // below the seasons: on a Halloween Friday, Halloween keeps its sky
                Id = OccasionIds.Ufo, Priority = 5,
                Schedule = { new DateWindow("fri#1", "+1") },
                // no loot, no hunt, no hat: the saucers (Decorations), the green night, the wail
                Facets = new FacetToggles { Loot = false, Hunt = false, Hats = false, Games = false },
            },
        };
        // festivals (#1269): the check-only festival exists only under --festival test; the Älplerfest's entry is below
        if (Festivals.FestivalCatalog.TestRequested)
            entries.Add(new OccasionEntry
            {
                Id = Festivals.FestivalCatalog.TestId, Priority = 8,
                Schedule = { new DateWindow("03-10", "03-14") },
                Facets = new FacetToggles { Atmosphere = false, Audio = true, Loot = false, Hunt = false, Hats = false },
            });
        // the Alpine herders' festival (#1269 part 5): 14-24 August; the sky stays the season's, the games and the flags run
        entries.Add(new OccasionEntry
        {
            Id = Festivals.FestivalCatalog.AelplerfestId, Priority = 8,
            Schedule = { new DateWindow("08-14", "08-24") },
            Facets = new FacetToggles { Atmosphere = false, Loot = false, Hunt = false, Hats = false },
        });
        // the cow drive (#1269 part 7): 20 September to 5 October, the herd, the sign and the fair's ring
        entries.Add(new OccasionEntry
        {
            Id = Festivals.FestivalCatalog.DesalpeId, Priority = 8,
            Schedule = { new DateWindow("09-20", "10-05") },
            Facets = new FacetToggles { Atmosphere = false, Loot = false, Hunt = false, Hats = false },
        });
        return entries;
    }

    public static List<OccasionEntry> Load()
    {
        var merged = Defaults();
        try
        {
            if (!Godot.FileAccess.FileExists(File))
            {
                Save(merged);
                return merged;
            }

            using var file = Godot.FileAccess.Open(File, Godot.FileAccess.ModeFlags.Read);
            var fromFile = JsonSerializer.Deserialize<OccasionConfigFile>(file.GetAsText(), JsonOptions);
            if (fromFile?.Occasions is { } entries) merged = Merge(merged, entries);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[occasions] could not read {File}, using defaults: {e.Message}");
        }
        return merged;
    }

    /// <summary>The file's entries in the file's order, then any default the file does not mention.</summary>
    public static List<OccasionEntry> Merge(List<OccasionEntry> defaults, List<OccasionEntry> fromFile)
    {
        var result = new List<OccasionEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in fromFile)
        {
            if (string.IsNullOrWhiteSpace(e.Id) || !seen.Add(e.Id)) continue;
            e.Id = e.Id.Trim().ToLowerInvariant();
            e.Schedule ??= new();
            e.Facets ??= new();
            result.Add(e);
        }
        foreach (var d in defaults)
            if (seen.Add(d.Id)) result.Add(d);
        return result;
    }

    private static void Save(List<OccasionEntry> entries)
    {
        try
        {
            // the check-only festival (--festival test) never lands in a player's file
            Core.JsonStore.Save(File, new OccasionConfigFile { Occasions = entries.Where(e => e.Id != Festivals.FestivalCatalog.TestId).ToList() }, JsonOptions);
        }
        catch (Exception e)
        {
            GD.PushWarning($"[occasions] could not write {File}: {e.Message}");
        }
    }
}
