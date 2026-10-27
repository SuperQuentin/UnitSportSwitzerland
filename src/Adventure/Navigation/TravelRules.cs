using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace UnitSport.Adventure.Navigation;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/fares.md).

/// <summary>A fare tariff by distance: degressive francs per kilometre in bands, a minimum fare.</summary>
public sealed record DistanceTariff(double MinFare, (double FromKm, double PerKm)[] Bands)
{
    /// <summary>The full 2nd-class fare for <paramref name="km"/> tariff kilometres, before rounding.</summary>
    public double Price(double km)
    {
        double sum = 0;
        for (int i = 0; i < Bands.Length; i++)
        {
            double from = Bands[i].FromKm, to = i + 1 < Bands.Length ? Bands[i + 1].FromKm : double.MaxValue;
            if (km <= from) break;
            sum += (Math.Min(km, to) - from) * Bands[i].PerKm;
        }
        return Math.Max(MinFare, sum);
    }
}

/// <summary>What a trip costs and why (the board's price column, the server's charge).</summary>
public readonly record struct Fare(int Chf, double Km, double FullChf, string Basis);

/// <summary>
/// The fares of the travel network (#1226, navigation plan 5.7), data in <c>res://assets/adventure/fares.json</c>
/// so the December timetable change is an edit there. Rail and bus on the SBB distance tariff (the straight line
/// times <see cref="Detour"/>, there is no rail graph to route on), ships on the lake operators' tariff, a cable
/// car its line's one-way price (a default for lines without one). The half-fare card halves every fare, the GA
/// makes rail, bus and ship free and halves cable cars. Parts are rounded up to <see cref="RoundTo"/> as the
/// SBB does, the trip to whole in-game francs (the bank keeps whole francs).
/// </summary>
public sealed class TravelRules
{
    public double Detour { get; private init; } = 1.25;
    public double RoundTo { get; private init; } = 0.2;
    public DistanceTariff Rail { get; private init; } = new(3.0, new[] { (0.0, 0.5) });
    public DistanceTariff Ship { get; private init; } = new(8.0, new[] { (0.0, 1.2) });
    public double CableCarDefault { get; private init; } = 30;
    public Dictionary<long, (string Name, double Price)> CableCars { get; } = new();
    /// <summary>The card prices and the day they change (the December timetable change).</summary>
    public (int Now, int Next, DateTime From) HalfFareCard { get; private init; } = (190, 195, new DateTime(2026, 12, 13));
    public (int Now, int Next, DateTime From) GaCard { get; private init; } = (3995, 4095, new DateTime(2026, 12, 13));

    /// <summary>Built-in values when the file is missing (tests, a broken install).</summary>
    public static TravelRules Default { get; } = new();

    /// <summary>The price of a card on a day: the new one from the timetable change.</summary>
    public static int CardPrice((int Now, int Next, DateTime From) card, DateTime today) => today.Date >= card.From ? card.Next : card.Now;

    /// <summary>Reads <c>fares.json</c>; a missing part keeps its default.</summary>
    public static TravelRules Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var d = Default;
        var rules = new TravelRules
        {
            Detour = Num(root, "detour", d.Detour),
            RoundTo = Num(root, "roundTo", d.RoundTo),
            Rail = root.TryGetProperty("rail", out var rail) ? Tariff(rail, d.Rail) : d.Rail,
            Ship = root.TryGetProperty("ship", out var ship) ? Tariff(ship, d.Ship) : d.Ship,
            CableCarDefault = root.TryGetProperty("cableCar", out var cc) ? Num(cc, "default", d.CableCarDefault) : d.CableCarDefault,
            HalfFareCard = root.TryGetProperty("cards", out var cards) && cards.TryGetProperty("halfFare", out var hf) ? Card(hf, d.HalfFareCard) : d.HalfFareCard,
            GaCard = root.TryGetProperty("cards", out var cards2) && cards2.TryGetProperty("ga", out var ga) ? Card(ga, d.GaCard) : d.GaCard,
        };
        if (root.TryGetProperty("cableCar", out var c2) && c2.TryGetProperty("lines", out var lines))
            foreach (var line in lines.EnumerateObject())
                if (long.TryParse(line.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long key))
                    rules.CableCars[key] = (line.Value.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", Num(line.Value, "price", rules.CableCarDefault));
        return rules;
    }

    private static double Num(JsonElement e, string name, double fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;

    private static DistanceTariff Tariff(JsonElement e, DistanceTariff fallback)
    {
        var bands = new List<(double, double)>();
        if (e.TryGetProperty("bands", out var arr))
            foreach (var b in arr.EnumerateArray()) bands.Add((Num(b, "fromKm", 0), Num(b, "perKm", 0)));
        bands.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new DistanceTariff(Num(e, "minFare", fallback.MinFare), bands.Count > 0 ? bands.ToArray() : fallback.Bands);
    }

    private static (int, int, DateTime) Card(JsonElement e, (int Now, int Next, DateTime From) fallback)
    {
        int now = (int)Num(e, "price", fallback.Now);
        int next = (int)Num(e, "nextPrice", now);
        var from = e.TryGetProperty("nextFrom", out var f) && DateTime.TryParse(f.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var day)
            ? day.Date : fallback.From;
        return (now, next, from);
    }

    /// <summary>Rounded up to the tariff's step (CHF 0.20).</summary>
    public double Round(double chf) => RoundTo <= 0 ? chf : Math.Ceiling(chf / RoundTo - 1e-9) * RoundTo;

    /// <summary>A cable car's one-way price: its line's, or the default.</summary>
    public double CableCarPrice(long lineKey) => CableCars.TryGetValue(lineKey, out var l) ? l.Price : CableCarDefault;

    /// <summary>
    /// The fare of a trip between two travel points <paramref name="straightM"/> apart. A ship trip (landing to
    /// landing) is on the ship tariff; any other on the rail and bus tariff; a cable-car station at the far end adds
    /// its line's price (the last leg up), and two ends of one line cost that line's price only.
    /// </summary>
    public Fare Price(TravelKind from, long fromLine, TravelKind to, long toLine, double straightM, bool ga, bool halfFare)
    {
        double km = straightM / 1000.0 * Detour;
        double ride = 0, lift = 0;
        string basis;
        bool sameLine = from == TravelKind.CableCar && to == TravelKind.CableCar && fromLine != 0 && fromLine == toLine;
        if (sameLine)
        {
            lift = CableCarPrice(toLine);
            basis = "cable car";
        }
        else if (from == TravelKind.Ship && to == TravelKind.Ship)
        {
            ride = Round(Ship.Price(km));
            basis = "ship";
        }
        else
        {
            ride = Round(Rail.Price(km));
            basis = to == TravelKind.Bus || from == TravelKind.Bus ? "rail and bus" : "rail";
            if (to == TravelKind.CableCar) { lift = CableCarPrice(toLine); basis += " + cable car"; }
        }
        double full = ride + lift;
        if (ga) { ride = 0; lift = Round(lift / 2); }
        else if (halfFare) { ride = Round(ride / 2); lift = Round(lift / 2); }
        return new Fare((int)Math.Ceiling(ride + lift - 1e-9), km, full, basis);
    }
}
