using System;
using System.Collections.Generic;
using UnitSport.Adventure.Places;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Navigation;

// Plain C#, no Godot: linked into the unit tests (docs/notes/adventure/travel.md).

/// <summary>
/// How a server lets players travel (#1226, navigation plan 5.4): <c>Free</c> (the sandbox: every jump, and
/// adventure travel from anywhere for nothing), <c>Network</c> (<c>--adventure-travel network</c>: free jumps are
/// an admin's, players travel the network they unlocked), <c>Off</c> (<c>--no-teleport</c>: no jumps, no travel).
/// </summary>
public enum TravelMode : byte { Free = 0, Network = 1, Off = 2 }

/// <summary>A travel point's kind, from its POI kind.</summary>
public enum TravelKind : byte { None = 0, Rail = 1, Bus = 2, Ship = 3, CableCar = 4 }

/// <summary>Why a trip is refused; the board greys the row with <see cref="TravelNetwork.Why"/>.</summary>
public enum TravelRefusal : byte
{
    None = 0, Off, NoAdventure, InMatch, Down, Driving, Racing, NotAPoint, Locked, NotAtSign, SamePoint, Cooldown, CantAfford,
}

/// <summary>The last travel point a body stood at (two hits in a row unlock: a jump-cut through a station does not).</summary>
public sealed class UnlockTracker
{
    public string? Last;
}

/// <summary>A trip asked for: who asks, from where, to where. <see cref="FromSign"/> null = from the map, the Journal or the phone.</summary>
public readonly record struct TravelAsk(
    TravelMode Mode, bool Admin, bool Active, bool InMatch, bool Down, bool Driving, bool Racing,
    TravelBook Book, Poi? To, Poi? FromSign, double E, double N, long Now, long Balance);

/// <summary>
/// The travel network's rules (#1226, navigation plan 5): which POIs are travel points, their unlock radii, the
/// two-hit unlock, and whether a trip may go and at what fare. Rules only: the server node
/// (<c>AdventureTravel</c>) feeds them and acts on the answer.
/// </summary>
public static class TravelNetwork
{
    /// <summary>The 10 s between two trips.</summary>
    public const long CooldownS = 10;
    /// <summary>How close to a sign's foot the body must be (the client asks within 4 m; slack for the published position).</summary>
    public const double SignReachM = 9;
    /// <summary>The unlock check's search radius round the body.</summary>
    public const float NearM = 80;

    public static TravelKind KindOf(PoiKind k) => k switch
    {
        PoiKind.RailStation => TravelKind.Rail,
        PoiKind.BusStop => TravelKind.Bus,
        PoiKind.ShipLanding => TravelKind.Ship,
        PoiKind.CableCarStation => TravelKind.CableCar,
        _ => TravelKind.None,
    };

    public static bool IsPoint(Poi? p) => p != null && KindOf(p.Kind) != TravelKind.None;

    /// <summary>How near a body must stand to unlock a point (navigation plan 5.1).</summary>
    public static float UnlockRadius(TravelKind k) => k switch
    {
        TravelKind.Rail => 60,
        TravelKind.Bus => 30,
        TravelKind.Ship => 40,
        TravelKind.CableCar => 60,
        _ => 0,
    };

    /// <summary>The <c>TravelUnlocked.Kind</c> of a point: station, busstop, landing, cablecar.</summary>
    public static string Tag(TravelKind k) => k switch
    {
        TravelKind.Rail => "station",
        TravelKind.Bus => "busstop",
        TravelKind.Ship => "landing",
        TravelKind.CableCar => "cablecar",
        _ => "",
    };

    /// <summary>The board's line glyph: what one rides there.</summary>
    public static string LineName(TravelKind k) => k switch
    {
        TravelKind.Rail => "IR",
        TravelKind.Bus => "PostAuto",
        TravelKind.Ship => "Schiff",
        TravelKind.CableCar => "Luftseilbahn",
        _ => "",
    };

    /// <summary>
    /// One unlock tick for a body at (<paramref name="e"/>, <paramref name="n"/>): the nearest locked travel point in
    /// its radius among <paramref name="near"/>; returned only when it was the same at the tick before.
    /// </summary>
    public static Poi? CheckUnlock(TravelBook book, double e, double n, IReadOnlyList<Poi> near, UnlockTracker tracker)
    {
        Poi? best = null;
        double bd = double.MaxValue;
        foreach (var p in near)
        {
            var k = KindOf(p.Kind);
            if (k == TravelKind.None || book.Has(p.Id)) continue;
            double de = p.E - e, dn = p.N - n, d2 = de * de + dn * dn;
            float r = UnlockRadius(k);
            if (d2 <= (double)r * r && d2 < bd) { bd = d2; best = p; }
        }
        if (best == null) { tracker.Last = null; return null; }
        if (tracker.Last == best.Id) { tracker.Last = null; return best; }
        tracker.Last = best.Id;
        return null;
    }

    /// <summary>
    /// Whether a body may travel as this ride: on foot or with its equipment (skis, wings), or on a bicycle (left
    /// behind); never driving or aboard a vehicle (dismount first).
    /// </summary>
    public static bool MayTravelAs(int rideKind) =>
        PlacesRules.Class(rideKind) == TravelClass.Foot || rideKind > 0 && BicycleCatalog.IsBicycle((RideKind)rideKind);

    public static double Distance(Poi a, double e, double n) => Math.Sqrt((a.E - e) * (a.E - e) + (a.N - n) * (a.N - n));

    /// <summary>
    /// Whether a trip may go, and its fare. Free mode and admins pay nothing and travel from anywhere; in Network
    /// a player travels to an unlocked point from an unlocked sign it stands at, or from anywhere with the GA.
    /// </summary>
    public static (TravelRefusal Why, Fare Fare) MayTravel(in TravelAsk a, TravelRules rules)
    {
        var none = new Fare(0, 0, 0, "");
        if (a.Mode == TravelMode.Off && !a.Admin) return (TravelRefusal.Off, none);
        if (!a.Active) return (TravelRefusal.NoAdventure, none);
        if (a.InMatch) return (TravelRefusal.InMatch, none);
        if (a.Down) return (TravelRefusal.Down, none);
        if (a.Driving) return (TravelRefusal.Driving, none);
        if (a.Racing) return (TravelRefusal.Racing, none);
        if (a.To is not { } to || !IsPoint(to)) return (TravelRefusal.NotAPoint, none);
        bool free = a.Mode == TravelMode.Free || a.Admin;
        if (!a.Book.Has(to.Id) && !a.Admin) return (TravelRefusal.Locked, none);
        if (a.FromSign is { } sign)
        {
            if (!IsPoint(sign) || !a.Book.Has(sign.Id) && !a.Admin || Distance(sign, a.E, a.N) > SignReachM)
                return (TravelRefusal.NotAtSign, none);
            if (sign.Id == to.Id) return (TravelRefusal.SamePoint, none);
        }
        else if (!free && !a.Book.HasGa) return (TravelRefusal.NotAtSign, none);
        if (!a.Admin && a.Now - a.Book.LastTravel < CooldownS && a.Book.LastTravel > 0) return (TravelRefusal.Cooldown, none);
        if (free) return (TravelRefusal.None, none);
        var fromKind = a.FromSign is { } s ? KindOf(s.Kind) : TravelKind.Rail;
        long fromLine = a.FromSign?.LineKey ?? 0;
        var fare = rules.Price(fromKind, fromLine, KindOf(to.Kind), to.LineKey, Distance(to, a.E, a.N), a.Book.HasGa, a.Book.HasHalfFare);
        if (fare.Chf > a.Balance) return (TravelRefusal.CantAfford, fare);
        return (TravelRefusal.None, fare);
    }

    /// <summary>The refusal as a player reads it.</summary>
    public static string Why(TravelRefusal r) => r switch
    {
        TravelRefusal.None => "",
        TravelRefusal.Off => "This server does not allow travelling.",
        TravelRefusal.NoAdventure => "No travelling now.",
        TravelRefusal.InMatch => "Not during a match.",
        TravelRefusal.Down => "Get up first.",
        TravelRefusal.Driving => "Get out of the vehicle first.",
        TravelRefusal.Racing => "Not during a race.",
        TravelRefusal.NotAPoint => "That is no travel point.",
        TravelRefusal.Locked => "Not unlocked yet: go there once.",
        TravelRefusal.NotAtSign => "Travel from a station sign (the GA card travels from anywhere).",
        TravelRefusal.SamePoint => "You are there.",
        TravelRefusal.Cooldown => "Wait a few seconds.",
        TravelRefusal.CantAfford => "Not enough on your account.",
        _ => r.ToString(),
    };

    /// <summary>How a cluster's shown point is chosen: a station before a landing, a cable car, a village's stop, any stop.</summary>
    public static int Rank(Poi p) => KindOf(p.Kind) switch
    {
        TravelKind.Rail => 0,
        TravelKind.Ship => 1,
        TravelKind.CableCar => 2,
        _ => p.Is(PoiFlags.VillageStop) ? 3 : 4,
    };

    /// <summary>
    /// Clusters points by screen cell (<paramref name="cellPx"/>) for a zoomed-out map: one shown point per cell
    /// (<see cref="Rank"/>, then the first), with how many it stands for. <paramref name="screen"/> maps a point
    /// to pixels; <paramref name="cells"/> is a reused scratch map.
    /// </summary>
    public static void Cluster(IReadOnlyList<Poi> points, Func<Poi, (float X, float Y)> screen, float cellPx,
        List<(Poi Shown, int Count)> into, Dictionary<(int, int), int> cells)
    {
        into.Clear();
        cells.Clear();
        foreach (var p in points)
        {
            var (x, y) = screen(p);
            var cell = ((int)Math.Floor(x / cellPx), (int)Math.Floor(y / cellPx));
            if (!cells.TryGetValue(cell, out int i))
            {
                cells[cell] = into.Count;
                into.Add((p, 1));
                continue;
            }
            var (shown, count) = into[i];
            into[i] = (Rank(p) < Rank(shown) ? p : shown, count + 1);
        }
    }
}
