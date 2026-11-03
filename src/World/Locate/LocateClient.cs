using Godot;
using UnitSport.Adventure;
using UnitSport.Adventure.Places;
using UnitSport.Ui;

namespace UnitSport.World.Locate;

/// <summary>
/// The chat coordinates' client half (#1277): <c>/track @E,N [name] | off</c> makes a point the tracked
/// target navigation already draws (<see cref="AdventureService.TrackPlace"/>: the target marker, the compass,
/// the guiding wind, #1226), and <c>/map @E,N [name]</c> opens the adventure map on it with a pin
/// (<see cref="LocateMapPin"/>). Both are this screen's alone: a server never hears of them. The chat's Track
/// and Map buttons send exactly these lines.
/// </summary>
public static class LocateClient
{
    /// <summary>How close counts as there for a tracked point, m.</summary>
    public const float ArriveRadius = 25f;

    /// <summary>The line to show (empty: none; and whether it is a refusal) for a /track or /map line; null for any other line.</summary>
    public static (string Line, bool Failed)? Run(string text)
    {
        string t = text.Trim();
        string verb = t.Split(' ', 2)[0].ToLowerInvariant();
        if (verb is not ("/track" or "/map")) return null;
        string rest = t.Length > verb.Length ? t[verb.Length..].Trim() : "";

        if (verb == "/track")
        {
            if (AdventureService.Instance is not { } adventure)
                return ("Nothing to track with here (not in a Battle Royale round, not on the title screen).", true);
            if (rest.Length == 0)
                return adventure.TrackedPlace is { } p
                    ? ($"Tracking {p.Label} {LocateCoords.Format(p.E, p.N)}. /track off to stop.", false)
                    : ("Usage: /track @E,N [name] | off", false);
            if (rest.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                if (adventure.TrackedPlace == null) return ("Nothing tracked.", false);
                adventure.UntrackPlace();
                return ("Stopped tracking.", false);
            }
            if (!Split(rest, out double e, out double n, out string label)) return ("Usage: /track @E,N [name] | off", true);
            // the same point again: the button is a toggle
            if (adventure.TrackedPlace is { } now && Math.Abs(now.E - e) < 1 && Math.Abs(now.N - n) < 1)
            {
                adventure.UntrackPlace();
                return ("Stopped tracking.", false);
            }
            adventure.TrackPlace(label, e, n, ArriveRadius);
            return ($"Tracking {label}: follow the marker. /track off to stop.", false);
        }

        if (!Split(rest, out double me, out double mn, out string mlabel)) return ("Usage: /map @E,N [name]", true);
        if (!AdventureMap.Available) return ("The map is not here right now (it opens while adventuring, not in a Battle Royale round).", true);
        LocateMapPin.Show(mlabel, me, mn);
        return ("", false);
    }

    /// <summary>"@E,N name words": the point, and the name (the coordinates themselves when there is none).</summary>
    private static bool Split(string rest, out double e, out double n, out string label)
    {
        label = "";
        var found = LocateCoords.FindAll(rest.StartsWith('@') ? rest : "@" + rest);
        if (found is not [var c, ..])
        {
            e = n = 0;
            return false;
        }
        (e, n) = (c.E, c.N);
        string text = rest.StartsWith('@') ? rest : "@" + rest;
        label = text[(c.Start + c.Length)..].Trim();
        if (label.Length == 0) label = LocateCoords.Format(e, n);
        if (label.Length > 40) label = label[..40];
        return true;
    }
}

/// <summary>
/// The point the chat's Map button opened the adventure map on (#1277): a red pin with its name, centred when
/// the map opens. An <see cref="IMapOverlay"/>, drawn only when the map redraws; it stays until another point
/// is shown.
/// </summary>
public sealed class LocateMapPin : IMapOverlay
{
    public static LocateMapPin Instance { get; } = new();
    private static readonly Color Red = new(0.86f, 0.16f, 0.14f);

    private (string Label, double E, double N)? _pin;

    /// <summary>The pin on the map (checks).</summary>
    public static (string Label, double E, double N)? Shown => Instance._pin;

    /// <summary>Hooks the pin into the map (the client world, once).</summary>
    public static void Register()
    {
        AdventureMap.RemoveOverlay(Instance);
        AdventureMap.AddOverlay(Instance);
    }

    /// <summary>Pins the point and opens the map on it (or centres an open one).</summary>
    public static void Show(string label, double e, double n)
    {
        Instance._pin = (label, e, n);
        AdventureMap.OpenAt(e, n);
    }

    public void Draw(MapView view, CanvasItem on)
    {
        if (_pin is not { } p) return;
        var at = view.ToScreen(p.E, p.N);
        // a drop pin: a disc on a short stem, its tip on the point
        on.DrawLine(at, at + new Vector2(0, -14), new Color(0, 0, 0, 0.7f), 3f);
        on.DrawLine(at, at + new Vector2(0, -14), Red, 1.5f);
        on.DrawCircle(at + new Vector2(0, -18), 7.5f, new Color(0, 0, 0, 0.6f));
        on.DrawCircle(at + new Vector2(0, -18), 6f, Red);
        on.DrawCircle(at + new Vector2(0, -18), 2.2f, Colors.White);
        var font = UiTheme.Font;
        on.DrawString(font, at + new Vector2(11, -14), p.Label, HorizontalAlignment.Left, -1, 13, new Color(0, 0, 0, 0.75f));
        on.DrawString(font, at + new Vector2(10, -15), p.Label, HorizontalAlignment.Left, -1, 13, Colors.White);
    }

    public bool Click(Vector2 lv95, MouseButton button) => false;
}
