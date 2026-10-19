using System.Collections.Generic;
using Godot;
using UnitSport.Items;
using UnitSport.Player;
using static UnitSport.Trailer.StoryScript;

namespace UnitSport.Trailer;

/// <summary>
/// A test film for the film kit (#882), not a film to show: every addition the three UFO films use,
/// one shot each, on the fixture lake (no map needed):
/// <c>CHUNKS=fixture:lake ARGS="--at 2600000,1200000 --trailer-allow-generated" FILM=ufo tools/trailer.sh stills</c>.
/// The beach of the fixture is at <see cref="Beach"/>, the water east of it, open land west.
/// Its own silent "song" of 120 BPM, eight shots of four bars.
/// </summary>
public static class UfoDemoScript
{
    private static readonly Song Silence = new("UFO kit demo", 120.0, 0.0, 64.0, "", "");

    /// <summary>The fixture's start: a gravel beach, the lake from 40 m east.</summary>
    public static readonly Spot Beach = new(2_600_000, 1_200_000);

    private static readonly Character Zorp = new("ZORP", new Color(0.55f, 1f, 0.55f), Avatar.Aliens.Zorp.Looks, Avatar.Aliens.Zorp.Wears);

    private static readonly Spot Wreck = Beach.Toward(270, 150), Landed = Beach.Toward(270, 300),
        Field = Beach.Toward(250, 450), Lot = Beach.Toward(230, 260), Camp = Beach.Toward(300, 90);

    private static FlightInput Hover(double t) => new(Vector2.Zero, 0f, 0f, 0f, 0f, false, false, 0f);

    public static readonly IReadOnlyList<Shot> Shots =
    [
        new()
        {
            Number = 1, Name = "Crashed", FromBar = 1, Bars = 4, Hour = 17, Style = "cartoon", Song = Silence,
            Feature = "Craft.Crashed: tilted, dug in, smoking, its lamps flickering",
            Craft = [Craft.Crashed(Wreck, 135)],
            Keys =
            [
                new(0, Wreck.Toward(200, 24).Up(3f), Wreck.Up(1.5f), 28),
                new(8, Wreck.Toward(230, 20).Up(2.5f), Wreck.Up(1.5f), 28),
            ],
            Preroll = 0.5,
        },
        new()
        {
            Number = 2, Name = "Down the ramp", FromBar = 5, Bars = 4, Hour = 17, Style = "cartoon", Song = Silence,
            Feature = "Cast.DownRamp: an alien walks down a landed saucer's ramp",
            Craft = [new Craft(Landed, 90) { Switches = _ => new UfoSwitches(Ramp: true) }],
            Cast =
            [
                new()
                {
                    Who = Zorp, At = Landed, Drive = Drive.Walk, DownRamp = RampOf.Craft(0),
                    Walk = t => (t > 0.8 && t < 6.5 ? new Vector2(0, 0.45f) : Vector2.Zero, false),
                },
            ],
            Keys =
            [
                new(0, Landed.Toward(130, 13).Up(1.7f), Landed.Toward(90, 3).Up(1.6f), 30),
                new(8, Landed.Toward(120, 12).Up(1.6f), Landed.Toward(90, 6).Up(1.4f), 30),
            ],
            Bubbles = [new(4.0, 0, "Take me to your fondue.")],
            Preroll = 0.5,
        },
        new()
        {
            Number = 3, Name = "Beam a cow", FromBar = 9, Bars = 4, Hour = 17, Style = "cartoon", Song = Silence,
            Feature = "Craft hovering, its beam on cue lifting a staged cow and a beamable fondue pot",
            Craft = [new Craft(Field.Up(16f), 200) { Switches = UfoSwitches.BeamBetween(1.0, 99) }],
            Herds = [new Cows(Field, Count: 1, Radius: 1.2f)],
            Props = [new(Field.Toward(60, 2.5f), 0, default, default) { Item = ItemId.FonduePot, Scale = 1.5f, Beamable = true }],
            Keys =
            [
                new(0, Field.Toward(200, 34).Up(2f), Field.Up(6f), 24),
                new(8, Field.Toward(210, 32).Up(2.5f), Field.Up(11f), 24),
            ],
            Preroll = 0.5,
        },
        new()
        {
            Number = 4, Name = "A saucer takes a car", FromBar = 13, Bars = 4, Hour = 17, Style = "cartoon", Song = Silence,
            Feature = "A saucer actor (Drive.Fly, Cast.Ufo) hovering, its beam lifting a parked car (Prop.Vehicle)",
            Cast = [new() { Ride = RideKind.Ufo, Drive = Drive.Fly, At = Lot.Up(17f), Heading = 0, Flight = Hover, Ufo = UfoSwitches.BeamBetween(1.0, 99) }],
            Props = [new(Lot, 30, default, default) { Vehicle = (RideKind)8 }],
            Keys =
            [
                new(0, Lot.Toward(160, 30).Up(2f), Lot.Up(7f), 24),
                new(8, Lot.Toward(170, 28).Up(2.5f), Lot.Up(11f), 24),
            ],
            Preroll = 1.0,
        },
        new()
        {
            Number = 5, Name = "Mothership", FromBar = 17, Bars = 4, Hour = 17.5, Style = "cartoon", Song = Silence,
            Feature = "Craft.Mothership with its beam, the Battle Royale zone wall",
            Craft = [new Craft(Beach.Toward(270, 700).Up(320f), 0) { Mothership = true, Switches = UfoSwitches.BeamBetween(2.0, 99) }],
            Zone = (Beach, 450f),
            Keys =
            [
                new(0, Beach.Toward(90, 10).Up(2f), Beach.Toward(270, 700).Up(300f), 24),
                new(8, Beach.Toward(90, 14).Up(2f), Beach.Toward(270, 700).Up(330f), 24),
            ],
            Preroll = 0.5,
        },
        new()
        {
            Number = 6, Name = "Swim and fish", FromBar = 21, Bars = 4, Hour = 17, Style = "cartoon", Song = Silence,
            Feature = "Cast.Swim from a dry Board spot; a fishing line from an actor's rod (ItemCues FishCast, FishEnd)",
            Cast =
            [
                new() { Who = Nina, At = Beach.Toward(90, 75), Board = Beach.Toward(90, 12), Swim = true, Heading = 70, Drive = Drive.Walk,
                    Walk = t => (new Vector2(0, 0.6f), false) },
                new() { Who = Lea, At = Beach.Toward(90, 30).Toward(0, 6), Heading = 100, Item = (int)ItemId.FishingRod },
            ],
            ItemCues =
            [
                new(0.4, 1, ItemEventKind.FishCast) { At = Beach.Toward(90, 52).Toward(0, 9) },
                new(6.0, 1, ItemEventKind.FishEnd) { Extra = ((int)ItemId.BrownTrout).ToString() },
            ],
            Keys =
            [
                new(0, Beach.Toward(90, 22).Toward(180, 6).Up(2.2f), Beach.Toward(90, 55).Up(0.5f), 28),
                new(8, Beach.Toward(90, 20).Toward(180, 8).Up(2.4f), Beach.Toward(90, 70).Up(0.5f), 28),
            ],
            Preroll = 1.0,
        },
        new()
        {
            Number = 7, Name = "The map", FromBar = 25, Bars = 4, Hour = 17, Style = "cartoon", Song = Silence,
            Feature = "Shot.Screens: the game's own map screen over the frame",
            Keys = [new(0, Beach.Up(2f), Beach.Toward(90, 50), 28)],
            Screens = [new(0.5, 7, GameScreen.Map)],
            Captions = [new(1.0, 5.5, "the game's own map", Title: false)],
            Preroll = 0.5,
        },
        new()
        {
            Number = 8, Name = "Camp", FromBar = 29, Bars = 4, Hour = 17, Style = "cartoon", Song = Silence, Occasion = "christmas",
            Feature = "Prop.Seated on the ground, Prop.Placed (campfire), Prop.Piece (a wooden wall), Cast.Radio, Cast.Headwear, Cast.Fight, Cast.Actions, Shot.Occasion",
            Props =
            [
                new(Camp, 90, default, default) { Seated = Jonas },
                new(Camp.Toward(90, 2.2f), 0, default, default) { Placed = PlacedKind.Campfire },
                new(Camp.Toward(0, 5f), 180, default, default) { Piece = Build.PieceKind.Wall, Material = Build.BuildMaterial.Wood },
            ],
            Cast =
            [
                new() { Who = Marco, At = Camp.Toward(150, 3f), Heading = 330, Item = (int)ItemId.Radio, Radio = 1, Headwear = 3 },
                new() { Who = Lea, At = Camp.Toward(200, 3.5f), Heading = 40, Fight = t => t < 2 ? 2 : 17 + (int)(t / 1.2) % 2 },
                new() { Who = Nina, At = Camp.Toward(240, 5f), Heading = 90, Drive = Drive.Walk,
                    Walk = _ => (Vector2.Zero, false), Actions = t => (t % 1.6 > 0.8, false) },
            ],
            Keys =
            [
                new(0, Camp.Toward(180, 10).Up(2.2f), Camp.Toward(90, 1).Up(0.8f), 28),
                new(8, Camp.Toward(200, 9).Up(2.0f), Camp.Toward(90, 1).Up(0.8f), 28),
            ],
            Preroll = 0.5,
        },
    ];
}
