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

/// <summary>A door's button (#162): which door, where it is and which way it faces, in the section's node frame.</summary>
public readonly record struct DeckButton(int Door, Vector3 At, Vector3 Normal);

/// <summary>
/// A hold (#418): a box in the section's node frame where a ground vehicle driven in is carried by
/// this one (a freighter's hold, later a ferry's car deck). Its floor is the box's bottom.
/// </summary>
public readonly record struct CargoBay(Vector3 Centre, Vector3 Size)
{
    /// <summary>A vehicle standing at <paramref name="local"/> (its ground point, node frame) is in it (<see cref="Vehicles.CargoFit.Inside"/>).</summary>
    public bool Contains(Vector3 local, float grow)
    {
        var p = local - Centre;
        return Vehicles.CargoFit.Inside(p.X, p.Y, p.Z, Size.X, Size.Y, Size.Z, grow);
    }

    /// <summary>Only this kind is carried in it (a boat trailer's boat, #463); 0: any ground vehicle that fits.</summary>
    public Player.RideKind Only { get; init; }

    /// <summary>A vehicle of <paramref name="kind"/> may be carried in it.</summary>
    public bool Takes(Player.RideKind kind) => Only == 0 || Only == kind;

    /// <summary>A hull this size (across, height, length) fits in it, driven in nose or tail first.</summary>
    public bool Fits(Vector3 hull) => Vehicles.CargoFit.Fits(hull.X, hull.Y, hull.Z, Size.X, Size.Y, Size.Z);
}

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
public sealed record VehicleDeck(int Section, DeckBox[] Boxes, Aabb Aboard, Vector2[] Holds)
{
    /// <summary>The buttons anyone presses to open or shut a door, inside and out.</summary>
    public DeckButton[] Buttons { get; init; } = System.Array.Empty<DeckButton>();

    /// <summary>The holds a ground vehicle can be driven into and carried in (#418).</summary>
    public CargoBay[] CargoBays { get; init; } = System.Array.Empty<CargoBay>();

    /// <summary>
    /// Only a hold, nothing to walk (a boat trailer's cradle, #463): the vehicle is not walkable for
    /// it, and nobody is ever aboard it.
    /// </summary>
    public bool CargoOnly { get; init; }

    /// <summary>
    /// The floor plan aboard, node frame (x, z), a polygon round its edge; null: the whole
    /// <see cref="Aboard"/> box. A ship's hull tapers to its stem (#303): out past the rail at the
    /// bow, inside the box but over the water, is not aboard.
    /// </summary>
    public Vector2[]? Plan { get; init; }

    /// <summary>
    /// Standing at <paramref name="local"/> (node frame) is being aboard: inside the box grown by
    /// <paramref name="grow"/>, and within <paramref name="grow"/> of the plan when there is one.
    /// </summary>
    public bool Contains(Vector3 local, float grow)
    {
        if (!Aboard.Grow(grow).HasPoint(local)) return false;
        if (Plan is not { Length: >= 3 } plan) return true;
        var p = new Vector2(local.X, local.Z);
        bool inside = false;
        float near = float.MaxValue;
        for (int i = 0, j = plan.Length - 1; i < plan.Length; j = i++)
        {
            var a = plan[i];
            var b = plan[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            if (grow > 0f)
            {
                var ab = b - a;
                float t = ab.LengthSquared() > 1e-8f ? Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f) : 0f;
                near = Mathf.Min(near, (a + ab * t).DistanceSquaredTo(p));
            }
        }
        return inside || near <= grow * grow;
    }
}

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
    private readonly List<DeckButton> _buttons = new();

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

    /// <summary>
    /// A wall (or a rail) <paramref name="thick"/> thick from plan point (<paramref name="x0"/>, station
    /// <paramref name="at0"/>) to (<paramref name="x1"/>, <paramref name="at1"/>), authored x: along a
    /// hull that tapers, where <see cref="Along"/> only runs straight down the section.
    /// </summary>
    public void Wall(float x0, float at0, float x1, float at1, float y0, float y1, float thick, DeckPart part = DeckPart.Solid, int door = -1)
    {
        var a = new Vector3(x0, 0, _cg - at0);
        var b = new Vector3(x1, 0, _cg - at1);
        var run = b - a;
        float length = run.Length();
        if (length < 0.01f || y1 - y0 < 0.005f) return;
        // authored: the box's z along the run; node space turns the run as it turns points
        var centre = ((a + b) * 0.5f) with { Y = (y0 + y1) * 0.5f };
        var along = Node(run / length);
        var basis = new Basis(Vector3.Up.Cross(along).Normalized(), Vector3.Up, along);
        _boxes.Add(new DeckBox(Node(centre), new Vector3(thick, y1 - y0, length), basis.Orthonormalized(), part, door));
    }

    private readonly List<Vector2> _plan = new();

    /// <summary>The floor plan aboard (<see cref="VehicleDeck.Plan"/>): the next corner round its edge, authored x and station.</summary>
    public void PlanAt(float x, float at) => _plan.Add(new Vector2(-x, -(_cg - at)));

    /// <summary>A door's button at an authored point, facing authored <paramref name="normal"/>.</summary>
    public void Button(int door, Vector3 at, Vector3 normal) => _buttons.Add(new DeckButton(door, Node(at), Node(normal)));

    private readonly List<CargoBay> _bays = new();

    /// <summary>A hold vehicles are carried in (<see cref="VehicleDeck.CargoBays"/>): authored centre and size (x, y, length).</summary>
    public void CargoBay(Vector3 centre, Vector3 size, Player.RideKind only = 0) => _bays.Add(new CargoBay(Node(centre), size) { Only = only });

    /// <summary>A deck that is only a hold (<see cref="VehicleDeck.CargoOnly"/>): nothing solid, nobody aboard.</summary>
    public VehicleDeck BuildCargo(int section) =>
        new(section, System.Array.Empty<DeckBox>(), new Aabb(new Vector3(0f, -1000f, 0f), Vector3.Zero), System.Array.Empty<Vector2>())
        {
            CargoBays = _bays.ToArray(),
            CargoOnly = true,
        };

    /// <summary>A pole or rail to hold, as an authored point on the floor plan.</summary>
    public void Hold(float x, float at) => _holds.Add(new Vector2(-x, -(_cg - at)));

    public VehicleDeck Build(int section, Aabb aboardAuthored)
    {
        // the aboard box, turned: x and z change sign, so the corner does too
        var a = aboardAuthored;
        var min = new Vector3(-a.End.X, a.Position.Y, -a.End.Z);
        var aboard = new Aabb(min, a.Size);
        return new VehicleDeck(section, _boxes.ToArray(), aboard, _holds.ToArray())
        {
            Buttons = _buttons.ToArray(),
            CargoBays = _bays.ToArray(),
            Plan = _plan.Count >= 3 ? _plan.ToArray() : null,
        };
    }
}
