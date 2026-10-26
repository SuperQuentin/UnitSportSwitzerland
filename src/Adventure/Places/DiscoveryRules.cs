using System;
using System.Collections.Generic;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

// Plain C#, no Godot: linked into the unit tests (DiscoveryRulesTests; docs/notes/adventure/places.md).

/// <summary>How a body travels, as far as finding places goes.</summary>
public enum TravelClass
{
    /// <summary>Reveals nothing: every ground vehicle, boats, trains and lifts, airliners, aboard any vehicle.</summary>
    None,
    /// <summary>On foot: walking, running, climbing, swimming, skis, the paraglider, the wingsuit, the pigeon.</summary>
    Foot,
    /// <summary>A light aircraft: a small plane, a glider, a helicopter.</summary>
    LightAircraft,
}

/// <summary>
/// The decided travel rule (#1224, <c>docs/plans/adventure-overview.md</c> "Decisions taken"): places and fog
/// count only on foot or in a light aircraft. One table keyed on the replicated ride kind, so changing the
/// rule is a one-line edit here. <see cref="Aboard"/> stands for "on a vehicle that is not one's own ride":
/// a deck, a lift, a seat in someone else's vehicle that is not a light aircraft.
/// </summary>
public static class PlacesRules
{
    /// <summary>The ride kind given for a body aboard something that carries it (a deck, a ski lift).</summary>
    public const int Aboard = -1;

    public static TravelClass Class(int rideKind) => rideKind switch
    {
        (int)RideKind.OnFoot or (int)RideKind.Skis or (int)RideKind.Snowboard or (int)RideKind.Wingsuit
            or (int)RideKind.Parachute or (int)RideKind.Paraglider or (int)RideKind.Pigeon => TravelClass.Foot,
        (int)RideKind.Helicopter or (int)RideKind.Plane => TravelClass.LightAircraft,
        _ => TravelClass.None,
    };

    /// <summary>Whether a body travelling as <paramref name="rideKind"/> finds places and clears fog.</summary>
    public static bool Reveals(int rideKind) => Class(rideKind) != TravelClass.None;

    /// <summary>What the stamp says about how it was earned.</summary>
    public static StampMode ModeOf(int rideKind) => rideKind switch
    {
        (int)RideKind.Skis or (int)RideKind.Snowboard => StampMode.Snow,
        (int)RideKind.Wingsuit or (int)RideKind.Parachute or (int)RideKind.Paraglider => StampMode.Wings,
        (int)RideKind.Pigeon => StampMode.Pigeon,
        (int)RideKind.Helicopter or (int)RideKind.Plane => StampMode.Aircraft,
        _ => StampMode.OnFoot,
    };

    /// <summary>
    /// The fastest a body may honestly go as this ride, m/s (the jump rule): on foot 40 (the wings 70), any
    /// ground vehicle 90, anything that flies 320.
    /// </summary>
    public static float MaxSpeed(int rideKind) => rideKind switch
    {
        (int)RideKind.OnFoot or (int)RideKind.Skis or (int)RideKind.Snowboard or (int)RideKind.Pigeon => 40,
        (int)RideKind.Wingsuit or (int)RideKind.Parachute or (int)RideKind.Paraglider => 70,
        (int)RideKind.Helicopter or (int)RideKind.Plane or (int)RideKind.A320 or (int)RideKind.Freighter
            or (int)RideKind.An124 or (int)RideKind.Ufo => 320,
        Aboard => 320,
        _ => 90,
    };
}

/// <summary>Where a body is and how it travels, sampled once a tick by the places service.</summary>
/// <param name="Alt">Altitude, metres (LV95 heights).</param>
/// <param name="Agl">Height above the ground, metres; NaN when the ground there is unknown.</param>
/// <param name="RideKind">The effective ride: the host's for a passenger, <see cref="PlacesRules.Aboard"/> on a deck or lift.</param>
/// <param name="Out">Knocked out, dead or otherwise not counting (a Battle Royale round is the service's <c>Active</c>).</param>
public readonly record struct PlacesBody(double E, double N, double Alt, float Agl, int RideKind, bool Out = false);

/// <summary>One peer's discovery state between ticks: where it was, the jump anchor, the POIs in range last tick.</summary>
public sealed class DiscoveryTracker
{
    public bool HasLast;
    public double LastE, LastN;
    /// <summary>A jump (or a sanctioned arrival) landed here; nothing counts until the body has moved <see cref="DiscoveryRules.JumpClearM"/> from it.</summary>
    public bool Anchored;
    public double AnchorE, AnchorN;
    /// <summary>POIs in range on the previous tick (two consecutive hits discover).</summary>
    public HashSet<Poi> HitLast = new();
    public HashSet<Poi> HitNow = new();
    public int Jumps;
}

/// <summary>
/// Whether a body finds a place (#1224, places plan 3): per-kind radii and height conditions, two
/// consecutive hits, the jump rule and sanctioned arrivals. Pure: the service feeds it once a second.
/// </summary>
public static class DiscoveryRules
{
    /// <summary>A jump's landing zone: nothing within it counts until the body has left it by itself.</summary>
    public const float JumpClearM = 300;
    /// <summary>The fog revealed round a body each tick.</summary>
    public const float FogRadiusM = 300;
    /// <summary>A step longer than the ride allows by this slack is a jump (lag, a frame late).</summary>
    private const double SlackM = 25;

    /// <summary>The horizontal radius a kind is found within (the file's when it has one).</summary>
    public static float RadiusOf(Poi p) => p.DiscoverRadius > 0 ? p.DiscoverRadius : DefaultRadius(p.Kind);

    public static float DefaultRadius(PoiKind kind) => kind switch
    {
        PoiKind.Peak or PoiKind.SummitCross => 40,
        PoiKind.Hut or PoiKind.Waterfall or PoiKind.Church or PoiKind.Chapel or PoiKind.LookoutTower => 50,
        PoiKind.Pass or PoiKind.Castle or PoiKind.Dam => 80,
        PoiKind.Lake => 60,
        PoiKind.Bridge or PoiKind.BusStop => 30,
        PoiKind.ShipLanding => 40,
        _ => 60,   // stations, cable cars
    };

    /// <summary>Whether a body is at a POI by its kind's rules (not counting the two hits, the jump rule or what was found).</summary>
    public static bool InRange(Poi p, in PlacesBody b)
    {
        var travel = PlacesRules.Class(b.RideKind);
        if (travel == TravelClass.None || b.Out) return false;
        double de = b.E - p.E, dn = b.N - p.N, d2 = de * de + dn * dn;
        float r = RadiusOf(p);
        double dz = Math.Abs(b.Alt - p.Elevation);
        // above the ground: unknown ground counts as height above the place itself
        double agl = float.IsNaN(b.Agl) ? b.Alt - p.Elevation : b.Agl;
        bool within = d2 <= (double)r * r;
        return p.Kind switch
        {
            // a summit is climbed, never flown to
            PoiKind.Peak or PoiKind.SummitCross or PoiKind.Hut => travel == TravelClass.Foot && within && dz <= 25,
            PoiKind.Pass => within && dz <= 30,
            PoiKind.Lake => agl < 150 && PoiCatalogue.InLake(p, b.E, b.N, r),
            PoiKind.Castle or PoiKind.Dam or PoiKind.Waterfall or PoiKind.Church or PoiKind.Chapel or PoiKind.LookoutTower
                => within && agl < 150,
            PoiKind.Bridge => within && agl < 20,
            PoiKind.RailStation or PoiKind.ShipLanding or PoiKind.CableCarStation => within && agl < 50,
            PoiKind.BusStop => within && agl < 20,
            _ => within && agl < 150,
        };
    }

    /// <summary>Whether a step of <paramref name="metres"/> in <paramref name="seconds"/> is more than the ride can do.</summary>
    public static bool IsJump(double metres, double seconds, int rideKind) =>
        metres > PlacesRules.MaxSpeed(rideKind) * Math.Max(seconds, 0.05) * 1.25 + SlackM;

    /// <summary>A server-sanctioned move (travel, an accepted invite): treated as a jump, so it lands where nothing is earned.</summary>
    public static void Arrived(DiscoveryTracker t, double e, double n)
    {
        t.Anchored = true;
        (t.AnchorE, t.AnchorN) = (e, n);
        (t.LastE, t.LastN, t.HasLast) = (e, n, true);
        t.HitLast.Clear();
    }

    /// <summary>
    /// One tick: moves the tracker, then adds to <paramref name="discovered"/> the POIs of
    /// <paramref name="near"/> not yet <paramref name="found"/> that were in range on this tick and the one
    /// before. Returns whether the body earns this tick (reveals fog): its ride reveals, it is not out, and
    /// it is not within a jump's landing zone.
    /// </summary>
    public static bool Step(DiscoveryTracker t, in PlacesBody b, double seconds, IReadOnlyList<Poi> near,
        Func<Poi, bool> found, List<Poi> discovered)
    {
        discovered.Clear();
        if (t.HasLast)
        {
            double de = b.E - t.LastE, dn = b.N - t.LastN;
            if (IsJump(Math.Sqrt(de * de + dn * dn), seconds, b.RideKind))
            {
                t.Anchored = true;
                (t.AnchorE, t.AnchorN) = (b.E, b.N);
                t.Jumps++;
            }
        }
        (t.LastE, t.LastN, t.HasLast) = (b.E, b.N, true);
        if (t.Anchored)
        {
            double ae = b.E - t.AnchorE, an = b.N - t.AnchorN;
            if (ae * ae + an * an >= (double)JumpClearM * JumpClearM) t.Anchored = false;
        }
        bool earns = !t.Anchored && !b.Out && PlacesRules.Reveals(b.RideKind);
        t.HitNow.Clear();
        if (earns)
            foreach (var p in near)
            {
                if (found(p) || !InRange(p, b)) continue;
                t.HitNow.Add(p);
                if (t.HitLast.Contains(p)) discovered.Add(p);
            }
        (t.HitLast, t.HitNow) = (t.HitNow, t.HitLast);
        return earns;
    }
}
