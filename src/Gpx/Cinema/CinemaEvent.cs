using Godot;

namespace UnitSport.Gpx.Cinema;

/// <summary>
/// Something in the run worth pointing a camera at.
///
/// <para>
/// Kinds are grouped by where they come from, because that is what decides when they can be
/// found: the kinematic and geometric ones fall out of the track alone, while the scenery ones
/// need map tiles loaded around the route.
/// </para>
/// </summary>
public enum CinemaEventKind
{
    // --- structural ---
    Start,
    Finish,

    // --- kinematic, from the track ---
    SpeedSurge,
    Fade,
    Stop,
    PeakSpeed,
    GaitChange,

    // --- elevation, from the draped terrain ---
    ClimbOnset,
    DescentOnset,
    Summit,
    Valley,
    Wall,
    AscentMilestone,

    // --- route geometry ---
    Bend,
    Hairpin,
    Switchbacks,
    LongStraight,

    // --- scenery, from the map tiles ---
    Rooftop,
    Village,
    Structure,
    Cableway,
    Water,
    NamedPlace,
    BorderCrossing,
    HighGround,

    // --- race, several ghosts ---
    Overtake,
}

/// <summary>
/// One scored moment on the timeline.
/// </summary>
/// <param name="Time">Track time in seconds — when it happens, not when to cut.</param>
/// <param name="Strength">
/// 0-1. What lets the director spend its best shots, and its screen time, on the best moments
/// rather than on whatever came first.
/// </param>
/// <param name="Where">
/// World position the event is about. For most kinds that is the runner; for scenery it is the
/// rooftop, pylon or summit itself, which is where the camera wants to go.
/// </param>
/// <param name="Label">Shown on a name card where there is one — a summit or a pass.</param>
public readonly record struct CinemaEvent(
    double Time,
    CinemaEventKind Kind,
    float Strength,
    Vector3 Where,
    string? Label = null)
{
}
