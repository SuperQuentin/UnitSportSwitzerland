using System;
using System.Collections.Generic;

namespace UnitSport.Adventure.Navigation;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/travel.md).

/// <summary>
/// The <c>travel</c> section of a player's adventure progress (#1226, navigation plan 5): the travel points
/// it unlocked (canonical POI ids), the two travelcards it holds, its last trip, and the portable flag the
/// rewards plan's item sets. The server writes it (unlocks, cards, trips) and trusts it: the inventory is
/// the client's, so the cards' items are only their picture.
/// </summary>
public sealed class TravelBook
{
    public static readonly SectionKey<TravelBook> Section = new("travel", Version: 1);

    /// <summary>Canonical POI ids of the travel points unlocked.</summary>
    public HashSet<string> Unlocked { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The GA travelcard: rail, bus and ship free, cable cars half, and travel from anywhere.</summary>
    public bool HasGa { get; set; }
    /// <summary>The half-fare travelcard: every fare halved.</summary>
    public bool HasHalfFare { get; set; }
    /// <summary>Unix seconds of the last trip (the 10 s rule).</summary>
    public long LastTravel { get; set; }
    /// <summary>Trips made and francs spent on them (the Journal's Travel tab).</summary>
    public int Trips { get; set; }
    public long Spent { get; set; }
    /// <summary>The rewards plan's portable travel flag (5.6), at most one; null when none is set.</summary>
    public PortableFlag? Flag { get; set; }

    public bool Has(string poiId) => Unlocked.Contains(poiId);
}

/// <summary>Where a player planted its portable travel flag (LV95 m), and when (Unix seconds).</summary>
public sealed record PortableFlag(double E, double N, float Alt, long Placed);
