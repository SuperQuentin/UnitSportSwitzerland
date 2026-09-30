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
    // a car's garage parts (CarTuning bits) and which of its doors stand open (CarRig bits):
    // they belong to this car, so they go wherever it goes — parked, claimed, a late joiner's spawn
    long Tuning = 0,
    byte DoorsOpen = 0)
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
        ["tune"] = Tuning,
        ["doors"] = DoorsOpen,
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
        // from another peer: parts past their options read as Stock
        CarTuning.Unpack(d["tune"].AsInt64()).Pack(),
        (byte)(d["doors"].AsInt32() & (15 | DriverDoorShuts)));

    /// <summary>
    /// In <see cref="DoorsOpen"/> of a car just got out of: the driver's door is only open because
    /// they got out, and shuts behind them (<see cref="VehicleBody"/>). Not a door.
    /// </summary>
    public const byte DriverDoorShuts = 16;

    public static double Now => Time.GetUnixTimeFromSystem();
}
