using System;
using Godot;
using UnitSport.Avatar;

namespace UnitSport.Trailer;

// The film kit (#882): what the three UFO films (#864, #865, #866) stage beyond the director's first
// shots. Read with docs/notes/trailer/director.md, "The film kit".

/// <summary>
/// A saucer's switches at one moment of a shot: its tractor beam, its rim lamps, its ramp (true
/// open, false shut, null: as the craft does by itself, open 0.8 s after landing) and its legs (true
/// down, false up, null: down under 7 m of free height). For an actor, <see cref="Cast.Ufo"/>; for a
/// prop, <see cref="Craft.Switches"/>.
/// </summary>
public readonly record struct UfoSwitches(bool Beam = false, bool Lights = true, bool? Ramp = null, bool? Legs = null)
{
    /// <summary>The beam on from <paramref name="on"/> to <paramref name="off"/> s, lamps lit, the rest automatic.</summary>
    public static Func<double, UfoSwitches> BeamBetween(double on, double off) => t => new(Beam: t >= on && t < off);
}

/// <summary>A landed saucer an actor walks down the ramp of (<see cref="Cast.DownRamp"/>): an actor's, or one of the shot's craft.</summary>
public readonly record struct RampOf(int Index, bool OfCraft)
{
    /// <summary>The ramp of actor <paramref name="k"/>'s saucer (landed, ramp down).</summary>
    public static RampOf Actor(int k) => new(k, false);

    /// <summary>The ramp of the shot's craft <paramref name="k"/> (<see cref="Shot.Craft"/>, ramp switched open).</summary>
    public static RampOf Craft(int k) => new(k, true);
}

/// <summary>
/// An item's effect at <paramref name="T"/> s into the shot's roll (like a caption) at actor
/// <paramref name="Actor"/>'s hand, <paramref name="Up"/> m higher, as its owner would send it
/// (<see cref="Items.ItemEvents"/>, played here only): an alphorn's call
/// (<see cref="Items.ItemEventKind.Horn"/>), a fondue, a flare, smoke, a fishing cast
/// (<see cref="Items.ItemEventKind.FishCast"/> with <see cref="At"/> the float's spot on the water;
/// the line runs from the actor's rod until <see cref="Items.ItemEventKind.FishEnd"/>, whose
/// <see cref="Extra"/> is a fish's item id to land it, or "snap").
/// </summary>
public sealed record ItemCue(double T, int Actor, Items.ItemEventKind Kind, float Up = 0f)
{
    /// <summary>The event's free text (<see cref="Items.ItemEvent.Extra"/>): a landed fish's item id, "snap".</summary>
    public string Extra { get; init; } = "";
    /// <summary>Where it happens instead of the hand: a fishing float's spot, a smoke canister's.</summary>
    public Spot? At { get; init; }
}

/// <summary>Which of the game's own screens a <see cref="ScreenCue"/> shows.</summary>
public enum GameScreen
{
    /// <summary>The country map of the title screen (<see cref="Ui.MapScreen.Create"/>): what is downloaded, picking more to download.</summary>
    Map,
    /// <summary>"Where do you want to start?" (<see cref="Ui.MapScreen.CreateLanding"/>), its marker at <see cref="ScreenCue.At"/>.</summary>
    Landing,
    /// <summary>A PNG over the whole frame (<see cref="ScreenCue.Png"/>): a screenshot of a screen the director cannot open itself (the movie studio, a GPX replay).</summary>
    Png,
}

/// <summary>
/// One of the game's screens over the frame from <paramref name="T"/> s into the roll for
/// <paramref name="Seconds"/>, drawn by the game's own UI (the captions stay on top): a cutaway, as a
/// player would see it.
/// </summary>
public sealed record ScreenCue(double T, double Seconds, GameScreen Kind)
{
    /// <summary><see cref="GameScreen.Landing"/>: the marker's place.</summary>
    public Spot? At { get; init; }
    /// <summary><see cref="GameScreen.Png"/>: the picture (a path the game can read).</summary>
    public string? Png { get; init; }
}

/// <summary>
/// A UFO prop of a shot (#861's rigs, drawn and switched by the director, never flown): a saucer
/// standing on its legs at <paramref name="At"/> (H 0) or hovering (H above the ground), its front to
/// compass <paramref name="Heading"/>; crashed with <see cref="Nose"/>, <see cref="Bank"/>,
/// <see cref="Sink"/> and <see cref="Smoke"/>; or the <see cref="Mothership"/>. Its beam is real: what
/// is under it rises (<see cref="Prop.Beamable"/> props, a staged herd's cows, parked vehicles, people
/// standing still). An actor walks down its ramp with <see cref="Cast.DownRamp"/>.
/// </summary>
public sealed record Craft(Spot At, float Heading = 0f)
{
    /// <summary>The 121 m mothership instead of a saucer: scenery in the sky, its lamps turning; its beam is drawn only (it reaches no ground).</summary>
    public bool Mothership { get; init; }
    /// <summary>Degrees its front dips into the ground (a crash), negative: lifts.</summary>
    public float Nose { get; init; }
    /// <summary>Degrees it rolls onto its right side, negative: its left.</summary>
    public float Bank { get; init; }
    /// <summary>Metres it is put lower than standing on its legs' feet (a crash with its legs up: <see cref="UfoMeshBuilder.Belly"/> puts the belly on the ground).</summary>
    public float Sink { get; init; }
    /// <summary>Dark smoke rising from it (a crash).</summary>
    public bool Smoke { get; init; }
    /// <summary>Its switches over the shot's time (as an actor's script: from the actors' go, the pre-roll included); null: lamps lit, beam off, ramp shut.</summary>
    public Func<double, UfoSwitches>? Switches { get; init; }

    /// <summary>
    /// A crashed saucer: legs up, its belly (2.2 m over its feet) 0.5 m into the ground, nose down 12°,
    /// rolled 8°, smoking, its lamps flickering. Give the shot 3 s of pre-roll for the smoke to rise.
    /// </summary>
    public static Craft Crashed(Spot at, float heading) => new(at, heading)
    {
        Nose = 12f, Bank = 8f, Sink = UfoMeshBuilder.Belly + 0.5f, Smoke = true,
        Switches = t => new UfoSwitches(Lights: Mathf.PosMod((float)t, 2.7f) > 0.35f, Ramp: false, Legs: false),
    };
}
