using Godot;
using UnitSport.Core;

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
    GlobalPos Position,
    float Yaw,
    Vector3 Velocity,
    int CdId = 0,
    double StartedAt = 0,
    bool Playing = false,
    bool Settled = false,
    float Length = 0,
    float Volume = RadioLoudness.Default,
    // which radio this is to the object containers (#689): given by the server; 0 = not persisted
    long Oid = 0,
    // its voice/music mix (#1153), 0..1 each: 1 and 1 plays the CD as burnt
    float Voice = 1f,
    float Music = 1f)
{
    /// <summary>
    /// The state on the wire (a spawn, an RPC): ECEF, true north and the local ENU (#1503); in memory the heading and
    /// velocity are in the LV95 grid's axes at <see cref="Position"/>; the object containers save this form too (G2 A2b).
    /// </summary>
    public Godot.Collections.Dictionary ToDict()
    {
        var enu = Lv95Grid.Axes(Position);
        var d = new Godot.Collections.Dictionary
        {
        ["name"] = Name,
        ["owner"] = Owner,
        ["yaw"] = enu.TrueYaw(Yaw),
        ["vel"] = enu.ToEnu(Velocity),
        ["cd"] = CdId,
        ["at"] = StartedAt,
        ["playing"] = Playing,
        ["settled"] = Settled,
        ["len"] = Length,
        ["vol"] = Volume,
        };
        if (Oid != 0) d["oid"] = Oid;
        if (Voice != 1f || Music != 1f) { d["voice"] = Voice; d["music"] = Music; }
        Position.Write(d);
        return d;
    }

    /// <summary>A state from <see cref="ToDict"/> (the wire, and the object containers since G2 A2b) or a container record saved before it (LV95 and grid north: its migration), told apart by their keys.</summary>
    public static RadioState FromDict(Godot.Collections.Dictionary d)
    {
        var s = Read(d);
        if (!GlobalPos.IsWire(d)) return s;
        var enu = Lv95Grid.Axes(s.Position);
        return s with { Yaw = enu.Yaw(s.Yaw), Velocity = enu.FromEnu(s.Velocity) };
    }

    private static RadioState Read(Godot.Collections.Dictionary d) => new(
        d["name"].AsString(),
        d["owner"].AsInt64(),
        GlobalPos.Read(d),
        d["yaw"].AsSingle(),
        d["vel"].AsVector3(),
        d["cd"].AsInt32(),
        d["at"].AsDouble(),
        d["playing"].AsBool(),
        d["settled"].AsBool(),
        d.TryGetValue("len", out var len) ? len.AsSingle() : 0f,
        d.TryGetValue("vol", out var vol) ? RadioLoudness.Clamp(vol.AsSingle()) : RadioLoudness.Default,
        d.TryGetValue("oid", out var oid) ? oid.AsInt64() : 0,
        d.TryGetValue("voice", out var voice) ? Math.Clamp(voice.AsSingle(), 0f, 1f) : 1f,
        d.TryGetValue("music", out var music) ? Math.Clamp(music.AsSingle(), 0f, 1f) : 1f);
}
