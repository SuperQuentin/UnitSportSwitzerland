using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Vehicles;

/// <summary>
/// Everything a vehicle is, at the moment it changes hands: from the player who was driving it to
/// the world (getting out, crashing), or back (getting in). Travels over the network as a
/// Godot Dictionary, which is what a MultiplayerSpawner's spawn data and an RPC can carry. Its
/// position is LV95 (#185): it crosses the network, and the server keeps it for as long as the
/// vehicle stands there.
/// </summary>
public readonly record struct VehicleState(
    RideKind Kind,
    GlobalPos Position,
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
    int Setup = 0,
    // a truck's coupled trailer (TrailerCatalog code, with its load), or a lone trailer's own (#70)
    int Train = 0,
    // the articulation of every joint, rad: a parked train stands as it was left
    Vector3 Angles = default,
    // a truck's or a bus's lamps, doors, kneel and destination (Truck.PackFlags), and its own load 0..1
    int Flags = 0,
    float Load = 0.5f,
    // the live station its radio was left on (Audio.Live.Stations id, 0 = off; #179)
    int Radio = 0,
    // the CD in its stereo (Items.RadioPlay, empty = none; #211)
    string Cd = "",
    // parked in a hold (#418): the vehicle carrying it (a FootPlayer's name, or "v:" and a parked
    // vehicle's), the section, and where it stands in that section's frame; it goes where that goes
    string Carrier = "",
    int CarrierSection = 0,
    Vector3 CarrierPos = default,
    float CarrierYaw = 0f)
{
    /// <summary>The ride this state is: a car with its preset and parts, a truck with its trailer, a lone trailer.</summary>
    public Rideable? CreateRide()
    {
        if (Kind == RideKind.Trailer) return new ParkedTrailer(Train, Angles);
        if (HeavyCatalog.For(Kind) is { } heavy)
        {
            var truck = new Truck(heavy, Train, Load);
            truck.SetAngles(Angles);
            truck.UnpackFlags(Flags);
            return truck;
        }
        if (Airliner.For(Kind) is { } airliner)
        {
            airliner.UnpackFlags(Flags);
            return airliner;
        }
        // airstairs at the height they were left (#417)
        if (Kind == RideKind.Airstairs) { var stairs = new Airstairs(); stairs.UnpackFlags(Flags); return stairs; }
        return CarSetups.Ride(Kind, Setup, Tuning);
    }

    /// <summary>How many vehicles this state is to the server's count: a train is a truck and a trailer.</summary>
    public int Units => Kind != RideKind.Trailer && Train != 0 ? 2 : 1;

    public Godot.Collections.Dictionary ToDict()
    {
        var d = new Godot.Collections.Dictionary
        {
        ["kind"] = (int)Kind,
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
        ["train"] = Train,
        ["angles"] = Angles,
        ["flags"] = Flags,
        ["load"] = Load,
        ["radio"] = Radio,
        ["cd"] = Cd,
        };
        if (Carrier != "")
        {
            d["carrier"] = Carrier;
            d["csec"] = CarrierSection;
            d["cpos"] = CarrierPos;
            d["cyaw"] = CarrierYaw;
        }
        Position.Write(d);
        return d;
    }

    public static VehicleState FromDict(Godot.Collections.Dictionary d) => new(
        (RideKind)d["kind"].AsInt32(),
        GlobalPos.Read(d),
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
        d.TryGetValue("setup", out var setup) ? CarSetups.Clamp(setup.AsInt32()) : 0,
        // from another peer: an unknown trailer reads as none
        d.TryGetValue("train", out var train) ? TrailerCatalog.Clean(train.AsInt32()) : 0,
        d.TryGetValue("angles", out var angles) ? angles.AsVector3() : default,
        d.TryGetValue("flags", out var flags) ? flags.AsInt32() : 0,
        d.TryGetValue("load", out var load) ? Mathf.Clamp(load.AsSingle(), 0f, 1f) : 0.5f,
        // from another peer: an unknown station reads as off
        d.TryGetValue("radio", out var radio) && Audio.Live.Stations.For(radio.AsInt32()) != null ? radio.AsInt32() : 0,
        // from another peer: anything but a well-formed play reads as no CD
        d.TryGetValue("cd", out var cd) && Items.RadioPlay.Decode(cd.AsString()) is { } play ? play.Encode() : "",
        d.TryGetValue("carrier", out var carrier) ? carrier.AsString() : "",
        d.TryGetValue("csec", out var csec) ? Mathf.Clamp(csec.AsInt32(), 0, 15) : 0,
        d.TryGetValue("cpos", out var cpos) ? cpos.AsVector3().LimitLength(100f) : default,
        d.TryGetValue("cyaw", out var cyaw) ? cyaw.AsSingle() : 0f);

    /// <summary>Parked in a hold (#418): carried by <see cref="Carrier"/>.</summary>
    public bool InHold => Carrier != "";

    /// <summary>
    /// In <see cref="DoorsOpen"/> of a car just got out of: the driver's door is only open because
    /// they got out, and shuts behind them (<see cref="VehicleBody"/>). Not a door.
    /// </summary>
    public const byte DriverDoorShuts = 16;

    public static double Now => Time.GetUnixTimeFromSystem();
}
