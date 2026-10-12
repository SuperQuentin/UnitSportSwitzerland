using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// A model with car doors worked one by one (#261): a car's (<see cref="CarRig"/>) or the pickup's
/// (<see cref="HeavyRig"/>, #463). Each door is a bit of <see cref="DoorsOpen"/>; G opens or shuts
/// the one a player stands at, E opens it and then gets in through it, a VR hand grips it.
/// </summary>
public interface IHingedDoors
{
    /// <summary>How many hinged doors; 0: the vehicle is got into as a whole.</summary>
    int DoorCount { get; }
    /// <summary>Doors open, one bit each.</summary>
    byte DoorsOpen { get; set; }
    /// <summary>The middle of a door in world space (the door shut).</summary>
    Vector3 DoorCentre(byte bit);
    /// <summary>The hinge node a door swings on, for outlining it; null for a bit it has not got.</summary>
    Node3D? DoorPivot(byte bit);
    /// <summary>The bit of the door whose middle is nearest a world point, and how far it is.</summary>
    (byte Bit, float Distance) NearestDoor(Vector3 point);
}
