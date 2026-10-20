using System.Collections.Generic;
using UnitSport.Ui.Bubbles;
using static UnitSport.Trailer.StoryScript;
using static UnitSport.Trailer.TrailerScript;

namespace UnitSport.Trailer;

/// <summary>
/// A test film for the speech bubbles (#862), not a film to show: <c>FILM=bubbles
/// tools/trailer.sh stills</c>. The story's four friends on the Gornergrat, each shape of bubble
/// at once, then a wide frame that puts speakers at both edges and a voice off. Its own silent
/// "song" of 120 BPM, two shots of three bars.
/// </summary>
public static class BubbleDemoScript
{
    private static readonly Song Silence = new("Bubble demo", 120.0, 0.0, 12.0, "", "");

    private static Cast[] Four() =>
    [
        new() { Who = Lea, At = Crew.Toward(270, 1.6f), Heading = 90 },
        new() { Who = Marco, At = Crew.Toward(0, 1.6f), Heading = 180 },
        new() { Who = Nina, At = Crew.Toward(90, 1.6f), Heading = 270 },
        new() { Who = Jonas, At = Crew.Toward(180, 1.6f), Heading = 0 },
    ];

    public static readonly IReadOnlyList<Shot> Shots =
    [
        new()
        {
            Number = 1, Name = "Four voices", FromBar = 1, Bars = 3, Hour = 15, Style = "cartoon", Song = Silence,
            Cast = Four(),
            Keys =
            [
                new(0, Crew.Toward(205, 7.5f).Up(2.2f), Crew.Up(1.2f), 28),
                new(6, Crew.Toward(215, 7.0f).Up(2.0f), Crew.Up(1.2f), 28),
            ],
            Bubbles =
            [
                new(0.0, 0, "Who took the fondue?") { Seconds = 6 },
                new(0.5, 1, "It was right here!", BubbleShape.Shout) { Seconds = 5.5 },
                new(1.0, 2, "psst... look up", BubbleShape.Whisper) { Seconds = 5 },
                new(1.5, 3, "I should not have eaten it all.", BubbleShape.Think) { Seconds = 4.4 },
            ],
            Preroll = 0.5,
        },
        new()
        {
            Number = 2, Name = "Edges", FromBar = 4, Bars = 3, Hour = 15, Style = "cartoon", Song = Silence,
            Cast = Four(),
            Keys =
            [
                new(0, Crew.Toward(170, 3.4f).Up(1.6f), Crew.Toward(350, 1.0f).Up(1.5f), 20),
                new(6, Crew.Toward(190, 3.4f).Up(1.6f), Crew.Toward(10, 1.0f).Up(1.5f), 20),
            ],
            Bubbles =
            [
                new(0.0, 0, "Over there, behind the hotel! A light in the sky, coming down fast!"),
                new(0.3, 2, "That is no plane.", BubbleShape.Shout),
                new(0.6, -1, "This is Zermatt control. Please stay calm.") { At = new(0.97f, 0.12f), Seconds = 5 },
                new(1.2, 1, "Nobody touches my cheese.", BubbleShape.Think),
            ],
            Preroll = 0.5,
        },
    ];
}
