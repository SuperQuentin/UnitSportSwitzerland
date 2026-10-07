using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Player;

namespace UnitSport.Trailer;

/// <summary>A place in LV95 and a height: above the ground (<paramref name="Agl"/>) or above the sea.</summary>
public readonly record struct Spot(double E, double N, float H = 0f, bool Agl = true)
{
    /// <summary>The same place, <paramref name="h"/> above the sea.</summary>
    public static Spot Alt(double e, double n, float h) => new(e, n, h, Agl: false);

    /// <summary><paramref name="metres"/> along a compass bearing (0 north, 90 east), same height.</summary>
    public Spot Toward(float bearing, float metres) =>
        this with { E = E + metres * Math.Sin(Mathf.DegToRad(bearing)), N = N + metres * Math.Cos(Mathf.DegToRad(bearing)) };

    public Spot Up(float h) => this with { H = h };
}

/// <summary>
/// A point a camera key stands at or looks at: a world <see cref="Spot"/>, a point in an actor's
/// travel frame (right, up, back: metres, back = behind it), or for a look only, a compass
/// direction from the eye.
/// </summary>
public sealed record Pt
{
    public Spot? World { get; private init; }
    public int Actor { get; private init; } = -1;
    public Vector3 Offset { get; private init; }
    public float Bearing { get; private init; } = float.NaN;
    public float Pitch { get; private init; }

    public static implicit operator Pt(Spot s) => new() { World = s };
    public static Pt At(double e, double n, float agl) => new() { World = new Spot(e, n, agl) };
    public static Pt Alt(double e, double n, float alt) => new() { World = Spot.Alt(e, n, alt) };

    /// <summary>In actor <paramref name="actor"/>'s frame: metres to its right, above its feet, behind it.</summary>
    public static Pt On(int actor, float right = 0f, float up = 1f, float back = 0f) =>
        new() { Actor = actor, Offset = new Vector3(right, up, back) };

    /// <summary>
    /// On the shot's road <paramref name="route"/>, <paramref name="arc"/> m along it (counted like
    /// <see cref="Cast.Arc"/>), <paramref name="right"/> m to the right of its way, <paramref name="up"/> m
    /// over the ground: a camera at a bend the cars will reach.
    /// </summary>
    public static Pt Road(string route, float arc, float right = 0f, float up = 1.5f) =>
        new() { Route = route, Offset = new Vector3(right, up, arc) };

    public string? Route { get; private init; }

    /// <summary>
    /// From actor <paramref name="actor"/>'s own seat: its ride's first-person eye
    /// (<see cref="Rideable.FirstPersonEye"/>), or <paramref name="ahead"/> m ahead of it along the way
    /// its body faces: the driver's view through the windscreen.
    /// </summary>
    public static Pt Cockpit(int actor, float ahead = 0f, float up = 0f) => new() { Actor = actor, Seat = true, Offset = new Vector3(0f, up, ahead) };

    public bool Seat { get; private init; }

    /// <summary>A look along a compass bearing (0 north, 90 east), pitched up (+) or down (−), degrees.</summary>
    public static Pt Dir(float bearing, float pitch = 0f) => new() { Bearing = bearing, Pitch = pitch };

    public bool IsDirection => !float.IsNaN(Bearing);
}

/// <summary>
/// A camera key: where the eye is and what it looks at, <paramref name="T"/> seconds into the shot
/// (or into its <see cref="Shot.KeysFrom"/> path), through a lens of <paramref name="Lens"/> mm
/// (full frame), rolled <paramref name="Roll"/> degrees.
/// </summary>
public sealed record Key(double T, Pt Eye, Pt Look, float Lens = 35f, float Roll = 0f);

/// <summary>A line of text over the picture from <paramref name="T"/> for <paramref name="Seconds"/>.</summary>
public sealed record Caption(double T, double Seconds, string Text, string? Sub = null, bool Title = false);

/// <summary>What moves an actor.</summary>
public enum Drive
{
    /// <summary>Stands (or sits on its machine) where it was put.</summary>
    Stand,
    /// <summary>The race autopilot along its road (<see cref="AutoPilot.For"/>): cars, lean-steered mounts, runners.</summary>
    Road,
    /// <summary>Scripted ground controls, <see cref="Cast.Controls"/>.</summary>
    Controls,
    /// <summary>Launched into the air at <see cref="Cast.Launch"/> m/s, then <see cref="Cast.Flight"/>.</summary>
    Fly,
    /// <summary>On foot, <see cref="Cast.Walk"/>.</summary>
    Walk,
    /// <summary>
    /// Along its road at <see cref="Cast.Speed"/>, steered at a point ahead (pure pursuit): any
    /// ground machine, trucks and buses too, which have no race autopilot.
    /// </summary>
    Follow,
}

/// <summary>
/// One actor of a shot: a figure, on foot or on a machine, put at a spot (or on the road nearest it)
/// and moved by its <see cref="Drive"/>. Times given to the scripts are seconds since the actors
/// started moving (the pre-roll included).
/// </summary>
public sealed record Cast
{
    public RideKind Ride { get; init; } = RideKind.OnFoot;
    public required Spot At { get; init; }
    /// <summary>Compass bearing it faces, degrees (on a road: which way along it, roughly).</summary>
    public float Heading { get; init; }
    public Drive Drive { get; init; } = Drive.Stand;

    /// <summary>On a road: metres along it from the point nearest <see cref="At"/> (negative: behind).</summary>
    public float Arc { get; init; }
    /// <summary>On a road: which road. Actors naming the same one share it (built once, from the first one's spot).</summary>
    public string? Route { get; init; }
    /// <summary>On a road: a point it drives toward, else the way it faces.</summary>
    public Spot? Toward { get; init; }
    /// <summary>The autopilot's skill (1 = a good driver) and aggression.</summary>
    public float Skill { get; init; } = 1f;
    public float Aggression { get; init; } = 0.2f;
    /// <summary>The autopilot starts only after this many seconds (a grid start, a car that waits).</summary>
    public double GoAt { get; init; }

    /// <summary>Metres a second it holds along its road (<see cref="Drive.Follow"/>).</summary>
    public float Speed { get; init; } = 12f;

    public Func<double, RideInput>? Controls { get; init; }
    public Func<double, FlightInput>? Flight { get; init; }
    /// <summary>Metres a second along <see cref="Heading"/> when launched (<see cref="Drive.Fly"/>), or put on the water.</summary>
    public float Launch { get; init; }
    /// <summary>Launched climbing (+) or diving (−) this many degrees.</summary>
    public float Climb { get; init; }
    /// <summary>A trailer coupled behind its truck (<see cref="TrailerCatalog"/> index), −1 = none.</summary>
    public int Trailer { get; init; } = -1;
    /// <summary>On foot: a wish in its own frame (x right, y ahead, length ≤ 1) and whether it runs.</summary>
    public Func<double, (Vector2 Wish, bool Run)>? Walk { get; init; }

    /// <summary>A dance move or emote (<see cref="FootPlayer.DanceId"/>), 0 = none.</summary>
    public int Dance { get; init; }
    /// <summary>The seed of its looks (<see cref="Avatar.Appearance.ForSeed"/>).</summary>
    public int Seed { get; init; } = 1;
    public bool Lights { get; init; }
    /// <summary>A car preset (<see cref="CarSetups"/>), 0 = none.</summary>
    public int Setup { get; init; }
    /// <summary>Seen from its own seat (<see cref="Pt.Cockpit"/>): its machine draws the cockpit, as for its driver.</summary>
    public bool FirstPerson { get; init; }
    /// <summary>What it holds (<see cref="Items.ItemId"/>), 0 = nothing.</summary>
    public int Item { get; init; }
    /// <summary>Its machine's doors (<see cref="FootPlayer.DoorsOpen"/>): the freighter's ramp is bit 3.</summary>
    public byte Doors { get; init; }
    /// <summary>A boat: a dry spot to get in at, before it is put on the water at <see cref="At"/>.</summary>
    public Spot? Board { get; init; }
    /// <summary>A boat: how deep its body is put under the surface, m (the steamer floats at 1.6).</summary>
    public float Draught { get; init; } = 0.2f;
}

/// <summary>
/// A plain box standing on the ground (a wall to crash into), <paramref name="Bearing"/> its long
/// side's normal; or, with <see cref="Actor"/>, <see cref="Ahead"/> m in front of that actor once it
/// is placed, square across its way; or across a road at an arc.
/// </summary>
public sealed record Prop(Spot At, float Bearing, Vector3 Size, Color Colour)
{
    public int Actor { get; init; } = -1;
    public float Ahead { get; init; }
    /// <summary>Or on the shot's road <see cref="Route"/>, <see cref="Arc"/> m along it, square across it.</summary>
    public string? Route { get; init; }
    public float Arc { get; init; }
}

/// <summary>
/// One shot of the trailer: where and when, who is in it, how the camera moves, what it says.
/// It runs from the start of bar <see cref="FromBar"/> for <see cref="Bars"/> bars of the song.
/// </summary>
public sealed record Shot
{
    public required int Number { get; init; }
    public required string Name { get; init; }
    public required int FromBar { get; init; }
    public required int Bars { get; init; }
    /// <summary>The feature it shows, for the log and the storyboard.</summary>
    public string Feature { get; init; } = "";

    /// <summary>Hour of the day when it rolls (pre-roll included).</summary>
    public double Hour { get; init; } = 11;
    /// <summary>A whole day lasts this many real minutes during the shot (a time-lapse); null: the clock stands.</summary>
    public float? MinutesPerDay { get; init; }
    public string Style { get; init; } = "ps1";
    /// <summary>The sea state for the lakes, 0..1; null: <see cref="CalmSea"/>.</summary>
    public float? Sea { get; init; }
    public const float CalmSea = 0.15f;
    /// <summary>Traffic cars around the camera; null: the player's setting.</summary>
    public int? Traffic { get; init; }

    public IReadOnlyList<Cast> Cast { get; init; } = Array.Empty<Cast>();
    public IReadOnlyList<Prop> Props { get; init; } = Array.Empty<Prop>();
    public required IReadOnlyList<Key> Keys { get; init; }
    /// <summary>Key time at the shot's first frame: shots that carry one move on each start further along it.</summary>
    public double KeysFrom { get; init; }
    /// <summary>The camera's damping, s (0: none): an eye on a bouncing car rides smoothly.</summary>
    public float Smooth { get; init; }
    /// <summary>A handheld drift of the camera, degrees.</summary>
    public float Shake { get; init; }
    /// <summary>Seconds the actors move before the camera rolls (cars up to speed).</summary>
    public double Preroll { get; init; } = 2;

    public IReadOnlyList<Caption> Captions { get; init; } = Array.Empty<Caption>();
    /// <summary>Seconds of fade from black at the start and to black at the end.</summary>
    public double FadeIn { get; init; }
    public double FadeOut { get; init; }

    public double Start => Song.Bar(FromBar);
    public double End => Math.Min(Song.Bar(FromBar + Bars), Song.End);
    public double Length => End - Start;
}
