using System.Collections.Generic;
using Godot;
using UnitSport.Player;

namespace UnitSport.Trailer;

/// <summary>
/// The trailer's shots (#706), in film order: the storyboard (<c>docs/trailer/storyboard.md</c>)
/// as data. Places are LV95; a spot's height is above the ground unless made with <see cref="Spot.Alt"/>.
/// </summary>
public static class TrailerScript
{
    private static readonly RideKind Ae86 = (RideKind)(CarCatalog.First + 0);

    public static readonly IReadOnlyList<Shot> Shots = new List<Shot>
    {
        new()
        {
            Number = 1, Name = "Alpenglow", FromBar = 1, Bars = 3, Feature = "swissALTI3D relief, sunrise",
            Hour = 7.0, FadeIn = 1.5,
            Keys =
            [
                new(0, Pt.Alt(2627300, 1092800, 3330), Pt.Alt(2617049, 1091673, 4300), 50),
                new(6.0, Pt.Alt(2626950, 1092720, 3370), Pt.Alt(2617049, 1091673, 4330), 50),
            ],
            Captions = [new(1.2, 4.6, "The whole of Switzerland.")],
        },
        new()
        {
            Number = 2, Name = "Car test", FromBar = 4, Bars = 2, Feature = "autopilot test",
            Hour = 10,
            Cast =
            [
                new() { Ride = Ae86, At = new Spot(2583250, 1113250), Heading = 60, Drive = Drive.Road, Arc = 0 },
            ],
            Keys =
            [
                new(0, Pt.On(0, 4f, 1.4f, 6f), Pt.On(0, 0f, 1f, -4f), 28),
                new(4, Pt.On(0, -4f, 1.6f, 7f), Pt.On(0, 0f, 1f, -4f), 28),
            ],
            Smooth = 0.15f, Preroll = 4,
        },
        new()
        {
            Number = 3, Name = "Paraglider test", FromBar = 25, Bars = 2, Feature = "flight test",
            Hour = 14,
            Cast =
            [
                new() { Ride = RideKind.Paraglider, At = new Spot(2631750, 1170250, 400), Heading = 250, Drive = Drive.Fly, Launch = 10 },
            ],
            Keys =
            [
                new(0, Pt.On(0, 25f, 6f, 20f), Pt.On(0, 0f, 0f, 0f), 35),
                new(4, Pt.On(0, 25f, 4f, -20f), Pt.On(0, 0f, 0f, 0f), 35),
            ],
            Smooth = 0.3f, Preroll = 2,
        },
    };
}
