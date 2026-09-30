using Godot;
using UnitSport.Player;

namespace UnitSport.Vehicles;

/// <summary>
/// Everything a vehicle is, at the moment it changes hands: from the player who was driving it to
/// the world (getting out, crashing), or back (getting in). Travels over the network as a
/// Godot Dictionary, which is what a MultiplayerSpawner's spawn data and an RPC can carry.
/// </summary>
public readonly record struct VehicleState(
    RideKind Kind,
    Vector3 Position,
    float Yaw,
    Vector3 Velocity,
    float Health,
    bool EngineOn,
    bool Wrecked,
    float Throttle,
    double SpawnedAt,
    long Owner = 0,
    string Name = "",
    bool Headlights = false,
    bool RoofOpen = false,
    // a car's garage parts (CarTuning bits) and which of its doors stand open (CarRig bits):
    // they belong to this car, so they go wherever it goes — parked, claimed, a late joiner's spawn
    long Tuning = 0,
    byte DoorsOpen = 0,
    // a car's preset (CarSetups id, #40): it belongs to this car too
    int Setup = 0)
{
    public Godot.Collections.Dictionary ToDict() => new()
    {
        ["kind"] = (int)Kind,
        ["pos"] = Position,
        ["yaw"] = Yaw,
        ["vel"] = Velocity,
        ["hp"] = Health,
        ["engine"] = EngineOn,
        ["wrecked"] = Wrecked,
        ["throttle"] = Throttle,
        ["at"] = SpawnedAt,
        ["owner"] = Owner,
        ["name"] = Name,
        ["lights"] = Headlights,
        ["roof"] = RoofOpen,
        ["tune"] = Tuning,
        ["doors"] = DoorsOpen,
        ["setup"] = Setup,
    };

    public static VehicleState FromDict(Godot.Collections.Dictionary d) => new(
        (RideKind)d["kind"].AsInt32(),
        d["pos"].AsVector3(),
        d["yaw"].AsSingle(),
        d["vel"].AsVector3(),
        d["hp"].AsSingle(),
        d["engine"].AsBool(),
        d["wrecked"].AsBool(),
        d["throttle"].AsSingle(),
        d["at"].AsDouble(),
        d["owner"].AsInt64(),
        d["name"].AsString(),
        d.TryGetValue("lights", out var lights) && lights.AsBool(),
        d.TryGetValue("roof", out var roof) && roof.AsBool(),
        // from another peer: parts past their options read as Stock
        CarTuning.Unpack(d.TryGetValue("tune", out var tune) ? tune.AsInt64() : 0).Pack(),
        (byte)((d.TryGetValue("doors", out var doors) ? doors.AsInt32() : 0) & (15 | DriverDoorShuts)),
        // from another peer: out of range reads as Stock
        d.TryGetValue("setup", out var setup) ? CarSetups.Clamp(setup.AsInt32()) : 0);

    /// <summary>
    /// In <see cref="DoorsOpen"/> of a car just got out of: the driver's door is only open because
    /// they got out, and shuts behind them (<see cref="VehicleBody"/>). Not a door.
    /// </summary>
    public const byte DriverDoorShuts = 16;

    public static double Now => Time.GetUnixTimeFromSystem();
}
