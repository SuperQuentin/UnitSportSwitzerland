using System;
using System.Collections.Generic;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/places.md).

/// <summary>
/// The <c>places</c> section of a player's adventure progress (#1224): the stamps, the viewpoints synced,
/// and the canton milestones already raised. Ids are canonical POI ids (<c>peak:rigi-kulm.3fa9c01b2e</c>);
/// look one up through <see cref="PoiCatalogue.Resolve"/>, so a renamed slug still matches.
/// </summary>
public sealed class PlacesProgress
{
    public static readonly SectionKey<PlacesProgress> Section = new("places", Version: 1);

    /// <summary>Canonical POI id -> its stamp.</summary>
    public Dictionary<string, Stamp> Found { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Viewpoints synced (their disc revealed).</summary>
    public HashSet<string> Synced { get; set; } = new(StringComparer.Ordinal);
    /// <summary>Canton code -> the highest landmark percentage raised (25, 50, 75, 100); <c>chapter:&lt;name&gt;</c> -> 100 once a chapter is complete.</summary>
    public Dictionary<string, int> Milestones { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The stamp of a POI, matched on its tag and hash (a stored id with an older slug still counts).</summary>
    public Stamp? StampOf(Poi poi)
    {
        if (Found.TryGetValue(poi.Id, out var s)) return s;
        foreach (var (id, stamp) in Found)
            if (PoiId.TryParse(id, out var kind, out ulong hash) && kind == poi.Kind && hash == poi.Hash) return stamp;
        return null;
    }

    public bool Has(Poi poi) => Found.ContainsKey(poi.Id) || StampOf(poi) != null;
}

/// <summary>A place found: the day (since 2026-01-01, <see cref="StampDay"/>) and how (<see cref="StampMode"/>).</summary>
public sealed record Stamp(int Day, byte Mode);

/// <summary>How a stamp was earned, shown on the Atlas card ("on foot").</summary>
public enum StampMode : byte
{
    OnFoot = 0,
    /// <summary>Skis or a snowboard.</summary>
    Snow = 1,
    /// <summary>A paraglider, a wingsuit or a parachute.</summary>
    Wings = 2,
    /// <summary>A light aircraft: a small plane, a glider, a helicopter.</summary>
    Aircraft = 3,
    Pigeon = 4,
    /// <summary>Given: a viewpoint's sync of an unvisited POI, or a reward.</summary>
    Given = 5,
}

/// <summary>Stamp days: whole days since 2026-01-01 (UTC).</summary>
public static class StampDay
{
    public static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static int Of(DateTime utc) => Math.Max(0, (int)Math.Floor((utc - Epoch).TotalDays));
    public static int Today => Of(DateTime.UtcNow);
    public static DateTime ToDate(int day) => Epoch.AddDays(day);

    /// <summary>"14 Oct 2026", invariant.</summary>
    public static string Text(int day) => ToDate(day).ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    public static string ModeText(byte mode) => (StampMode)mode switch
    {
        StampMode.OnFoot => "on foot",
        StampMode.Snow => "on skis",
        StampMode.Wings => "on wings",
        StampMode.Aircraft => "by air",
        StampMode.Pigeon => "as a pigeon",
        _ => "given",
    };
}

/// <summary>
/// The <c>fog</c> section (#1224): the map's revealed 250 m cells, as <see cref="FogMask.Encode"/> writes
/// them (deflated, base64). The service keeps a <see cref="FogMask"/> in memory and writes this only to
/// save; the owner gets the fog by its own RPCs (<c>FogFull</c>, <c>FogDelta</c>), never by <c>SectionSync</c>.
/// </summary>
public sealed class FogProgress
{
    public static readonly SectionKey<FogProgress> Section = new("fog", Version: 1);

    /// <summary>The mask's own layout version.</summary>
    public int V { get; set; } = 1;
    public string Mask { get; set; } = "";
}

/// <summary>A canton's landmarks reached a step (25, 50, 75, 100 %), raised once each (places plan 7.3).</summary>
public sealed record CantonMilestone(long Peer, string Key, string Canton, int Percent) : AdventureEvent(Peer, Key);

/// <summary>Every canton of an Atlas chapter at 100 % of its landmarks (places plan 7.3), raised once.</summary>
public sealed record ChapterCompleted(long Peer, string Key, string Chapter) : AdventureEvent(Peer, Key);
