using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// The tracked target as navigation draws it (#1226, navigation plan 3), client side: the service's
/// <see cref="AdventureService.TrackedTarget"/> (a quest's step, or a place, a pin, a party member tracked with
/// <see cref="AdventureService.TrackPlace"/>, the newest choice winning), plus its altitude: a catalogue POI's own
/// (a summit's), else the ground there when the tile is loaded. Re-read on <c>TrackedChanged</c>, never polled; the
/// compass, the marker and the gust read <see cref="Current"/>.
/// </summary>
public static class NavTarget
{
    /// <summary>The ground height at a point (LV95), set by the client world.</summary>
    public static Func<double, double, float?>? GroundAt { get; set; }

    private static (string Label, double E, double N, float RadiusM)? _cached;
    private static float? _alt;
    private static readonly List<Poi> Near = new();
    private static int _version;

    /// <summary>Bumped whenever the target changes (consumers rebuild their text then).</summary>
    public static int Version => Refresh();

    /// <summary>What is tracked now, or null.</summary>
    public static (string Label, double E, double N, float RadiusM)? Current
    {
        get
        {
            Refresh();
            return _cached;
        }
    }

    /// <summary>The target's altitude, metres, or null when not known yet (its tile not loaded).</summary>
    public static float? Alt
    {
        get
        {
            Refresh();
            if (_alt == null && _cached is { } t) _alt = GroundAt?.Invoke(t.E, t.N);
            return _alt;
        }
    }

    private static int Refresh()
    {
        var now = AdventureService.Instance?.TrackedTarget;
        if (now == _cached) return _version;
        _cached = now;
        _alt = null;
        _version++;
        if (now is { } t && PlacesService.Instance is { } places)
        {
            // a catalogue POI there: its own altitude (a summit, not the ground beside it)
            places.Catalogue.Near(t.E, t.N, 5, Near);
            foreach (var p in Near)
                if (p.Elevation > 0) { _alt = p.Elevation; break; }
        }
        return _version;
    }
}
