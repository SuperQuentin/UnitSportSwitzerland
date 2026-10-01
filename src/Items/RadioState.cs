using Godot;

namespace UnitSport.Items;

/// <summary>
/// A radio at the moment it enters the world (thrown, or put back by the server when its
/// thrower left) — the spawn data every peer builds its <see cref="RadioBody"/> from, as a
/// Godot Dictionary like <c>VehicleState</c>.
/// </summary>
/// <param name="Owner">Peer that simulates the fall; 0 for the server.</param>
/// <param name="StartedAt">Server clock (<c>Net.ClockSync.ServerNow</c>) at which the CD began.</param>
/// <param name="Settled">Already at rest: frozen where it stands, no physics.</param>
/// <param name="Length">Seconds the CD lasts (what a thrown radio was playing carries on).</param>
public readonly record struct RadioState(
    string Name,
    long Owner,
    Vector3 Position,
    float Yaw,
    Vector3 Velocity,
    int CdId = 0,
    double StartedAt = 0,
    bool Playing = false,
    bool Settled = false,
    float Length = 0)
{
    public Godot.Collections.Dictionary ToDict() => new()
    {
        ["name"] = Name,
        ["owner"] = Owner,
        ["pos"] = Position,
        ["yaw"] = Yaw,
        ["vel"] = Velocity,
        ["cd"] = CdId,
        ["at"] = StartedAt,
        ["playing"] = Playing,
        ["settled"] = Settled,
        ["len"] = Length,
    };

    public static RadioState FromDict(Godot.Collections.Dictionary d) => new(
        d["name"].AsString(),
        d["owner"].AsInt64(),
        d["pos"].AsVector3(),
        d["yaw"].AsSingle(),
        d["vel"].AsVector3(),
        d["cd"].AsInt32(),
        d["at"].AsDouble(),
        d["playing"].AsBool(),
        d["settled"].AsBool(),
        d.TryGetValue("len", out var len) ? len.AsSingle() : 0f);
}
