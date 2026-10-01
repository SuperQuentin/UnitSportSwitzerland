using Godot;

namespace UnitSport.Avatar;

/// <summary>What a deck box is for: always solid, a shut door's leaf (gone while it is open), a step at an open door.</summary>
public enum DeckPart { Solid, DoorShut, DoorStep }

/// <summary>
/// One collision box of a <see cref="VehicleDeck"/>, in its section's node frame (−Z forward,
/// ground origin): a middle, a size, a turn (a ramp is a tilted box). <see cref="Door"/> is the door
/// a <see cref="DeckPart.DoorShut"/> or <see cref="DeckPart.DoorStep"/> belongs to.
/// </summary>
public readonly record struct DeckBox(Vector3 Centre, Vector3 Size, Basis Basis, DeckPart Part = DeckPart.Solid, int Door = -1);

/// <summary>
/// A vehicle you can walk around in (#162), one section of it: what is solid (walls with real door
/// holes, floors, ramps where the floor changes height, seats, poles), the volume that counts as
/// aboard, and what a standing passenger can hold on to. Built with the vehicle's model, in the
/// section rig's node frame. Buses first; a train's coach, a boat's deck or a plane's cabin is
/// another builder filling the same record (<c>docs/notes/player/walk-aboard.md</c>).
/// </summary>
/// <param name="Section">Which section of the vehicle (a bus's front or rear half).</param>
/// <param name="Boxes">The collision.</param>
/// <param name="Aboard">Standing inside this box, node frame, is being aboard: carried by the vehicle.</param>
/// <param name="Holds">Poles a standing passenger steadies on when beside one, as points on the floor plan (x, z).</param>
public sealed record VehicleDeck(int Section, DeckBox[] Boxes, Aabb Aboard, Vector2[] Holds);

/// <summary>
/// Collects a deck's boxes from the same numbers its model is built from: authored space (+Z forward,
/// stations in metres behind the section's front, z = cg − station), turned into node space the way
/// <see cref="MeshScratch.Build()"/> turns the mesh.
/// </summary>
public sealed class DeckBuilder
{
    private readonly float _cg;
    private readonly List<DeckBox> _boxes = new();
    private readonly List<Vector2> _holds = new();

    public DeckBuilder(float cg) => _cg = cg;

    private static Vector3 Node(Vector3 authored) => new(-authored.X, authored.Y, -authored.Z);

    /// <summary>A box between two stations, from <paramref name="y0"/> to <paramref name="y1"/>, <paramref name="width"/> across, centred at authored x.</summary>
    public void Along(float fromAt, float toAt, float y0, float y1, float width, float x = 0f, DeckPart part = DeckPart.Solid, int door = -1)
    {
        if (toAt - fromAt < 0.01f || y1 - y0 < 0.005f || width < 0.005f) return;
        _boxes.Add(new DeckBox(Node(new Vector3(x, (y0 + y1) * 0.5f, _cg - (fromAt + toAt) * 0.5f)),
            new Vector3(width, y1 - y0, toAt - fromAt), Basis.Identity, part, door));
    }

    /// <summary>A box at an authored point, its size, axis-aligned.</summary>
    public void Box(Vector3 centre, Vector3 size, DeckPart part = DeckPart.Solid, int door = -1) =>
        _boxes.Add(new DeckBox(Node(centre), size, Basis.Identity, part, door));

    /// <summary>
    /// A ramp along the section from station <paramref name="lowAt"/> (floor <paramref name="lowY"/>)
    /// to <paramref name="highAt"/> (floor <paramref name="highY"/>), <paramref name="width"/> across:
    /// the walk has no step-up, so a floor that changes height is a slope to it, whatever the model draws.
    /// </summary>
    public void RampAlong(float lowAt, float lowY, float highAt, float highY, float width, float x = 0f, DeckPart part = DeckPart.Solid, int door = -1)
    {
        // authored: the slope rises toward highAt; its top face runs from (lowAt, lowY) to (highAt, highY)
        var low = new Vector3(x, lowY, _cg - lowAt);
        var high = new Vector3(x, highY, _cg - highAt);
        Ramp(low, high, width, part, door);
    }

    /// <summary>A ramp across the section (a door's step, sideways out of the wall).</summary>
    public void RampAcross(float at, float lowX, float lowY, float highX, float highY, float width, DeckPart part = DeckPart.Solid, int door = -1) =>
        Ramp(new Vector3(lowX, lowY, _cg - at), new Vector3(highX, highY, _cg - at), width, part, door);

    /// <summary>A slab 0.1 m thick whose top face is the slope from the low edge's middle to the high edge's (authored).</summary>
    private void Ramp(Vector3 low, Vector3 high, float width, DeckPart part, int door)
    {
        var run = high - low;
        float length = run.Length();
        var flat = run with { Y = 0 };
        if (length < 0.01f || flat.LengthSquared() < 1e-6f) return;
        // a right-handed frame: x across the ramp (level), z up its slope, y out of its face
        var along = run / length;
        var side = Vector3.Up.Cross(flat.Normalized()).Normalized();
        var up = along.Cross(side).Normalized();
        const float thick = 0.1f;
        var centre = (low + high) * 0.5f - up * thick * 0.5f;
        // node space: the half turn about Y applies to the basis' columns as it does to points
        var basis = new Basis(Node(side), Node(up), Node(along));
        _boxes.Add(new DeckBox(Node(centre), new Vector3(width, thick, length), basis.Orthonormalized(), part, door));
    }

    /// <summary>A pole or rail to hold, as an authored point on the floor plan.</summary>
    public void Hold(float x, float at) => _holds.Add(new Vector2(-x, -(_cg - at)));

    public VehicleDeck Build(int section, Aabb aboardAuthored)
    {
        // the aboard box, turned: x and z change sign, so the corner does too
        var a = aboardAuthored;
        var min = new Vector3(-a.End.X, a.Position.Y, -a.End.Z);
        var aboard = new Aabb(min, a.Size);
        return new VehicleDeck(section, _boxes.ToArray(), aboard, _holds.ToArray());
    }
}
