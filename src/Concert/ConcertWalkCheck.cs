using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>
/// <c>--concertwalkcheck --world flat</c> (#974): an imported set's collision, walked. A rig with a
/// hand-made set in front of Geneva's stage (a deck 1 m high, five 0.2 m steps up to it, a truss tower,
/// a lamp) is built as an import is, by <see cref="ConcertRig"/>. A capsule the player's size then walks
/// up the stairs onto the deck and must stand on it; another walks into the tower and must be stopped.
/// A beam straight down on the deck must stop at its top (the floor beside it: on the ground), and the
/// crowd's plan must keep off the deck but not off open floor. The pure judging is in
/// <c>MvrCollisionTests</c>; this checks the bodies.
/// </summary>
public partial class ConcertWalkCheck : Node3D
{
    public static bool Requested => CmdArgs.Has("--concertwalkcheck");

    /// <summary>The player's capsule (<c>FootPlayer</c>, as <c>ShellWalkProbe</c>): radius and height, m; a brisk pace, m/s.</summary>
    private const float Radius = 0.3f, Tall = 1.8f, Pace = 2.5f;
    private const float DeckTop = 1f;

    private ConcertRig _rig = null!;
    private Vector3 _c;
    private CharacterBody3D _climber = null!, _bumper = null!;
    private readonly List<Vector3> _route = new();
    private int _next = 1, _frames, _failures, _settle;
    private double _t;
    private bool _done;

    private void Check(bool ok, FormattableString what)
    {
        if (!ok) _failures++;
        GD.Print($"[concertwalk] {(ok ? "ok  " : "FAIL")} {FormattableString.Invariant(what)}");
    }

    public override void _Ready()
    {
        var e = ConcertPlanner.Plan(StadiumCatalog.Geneva, 1);
        var bare = new StagePlan(e);
        // the set 18 m in front of the deck's edge and 12 m aside, clear of a catwalk and the front of house
        var c2 = bare.Origin + bare.Forward * 18f + new Vector2(-bare.Forward.Y, bare.Forward.X) * 12f;
        _c = new Vector3(c2.X, 0f, c2.Y);
        var rig = new Mvr.RigData { Source = "walkcheck" };
        void Piece(Mvr.RigData.MeshRow m, Vector3 at)
        {
            rig.Objects.Add(new Mvr.RigData.ObjectRow { Mesh = rig.Meshes.Count, Xf = Mvr.RigData.X(new Transform3D(Basis.Identity, at)) });
            rig.Meshes.Add(m);
        }
        Piece(Boxes((new Vector3(-2, 0, -1.5f), new Vector3(2, DeckTop, 1.5f))), _c);
        Piece(Boxes(Enumerable.Range(0, 5).Select(k => (new Vector3(-0.6f, 0, k * 0.3f), new Vector3(0.6f, (k + 1) * 0.2f, (k + 1) * 0.3f))).ToArray()),
            _c + new Vector3(0, 0, -3f));
        Piece(Boxes((new Vector3(-0.2f, 0, -0.2f), new Vector3(0.2f, 4f, 0.2f))), _c + new Vector3(5, 0, 0));
        Piece(Boxes((Vector3.Zero, new Vector3(0.1f, 0.2f, 0.15f))), _c + new Vector3(-4, 0, 0));

        _rig = new ConcertRig(e) { Imported = rig, WithCrowd = false };
        AddChild(_rig);
        Check(_rig.ObjectHulls == 2 && _rig.ObjectBoxes == 1 && _rig.ObjectTriangles == 0,
            $"the deck and the stairs are hulls, the tower a box, the lamp nothing ({_rig.ObjectHulls} hulls, {_rig.ObjectBoxes} boxes, {_rig.ObjectTriangles} triangles)");
        var deck = new Vector2(_c.X, _c.Z);
        Check(_rig.Plan!.Blocked(deck, 0f) && !bare.Blocked(deck, 0f), $"the crowd keeps off the deck, which the bare stage did not");
        // the stage's own crew zones (#985) may cover that floor: the import must add nothing to them there
        var behind = deck + new Vector2(0, 6f);
        Check(_rig.Plan.Blocked(behind, 0.5f) == bare.Blocked(behind, 0.5f), $"and not off the open floor behind it");

        _route.AddRange(new[] { _c + new Vector3(0, 0, -7f), _c + new Vector3(0, 0, -3.4f), _c + new Vector3(0, DeckTop, -1.2f), _c + new Vector3(0, DeckTop, 0) });
        _climber = Walker("Climber", _route[0]);
        _bumper = Walker("Bumper", _c + new Vector3(5, 0, -3f));
    }

    private CharacterBody3D Walker(string name, Vector3 feet)
    {
        var w = new CharacterBody3D { Name = name, FloorMaxAngle = Mathf.DegToRad(52f), FloorSnapLength = 0.4f, Position = feet + Vector3.Up * (Tall / 2 + 0.15f) };
        w.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = Radius, Height = Tall } });
        AddChild(w);
        return w;
    }

    private static void Move(CharacterBody3D w, Vector2 want, float dt)
    {
        var v = w.Velocity;
        v.X = want.X;
        v.Z = want.Y;
        v.Y = w.IsOnFloor() ? 0f : v.Y - 9.81f * dt;
        w.Velocity = v;
        w.MoveAndSlide();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        float dt = (float)delta;
        _t += delta;
        if (++_frames == 10)
        {
            // the bodies are in the space now: a beam down onto the deck, and one beside it onto the ground
            float onDeck = _rig.BeamReach(_c + new Vector3(0, 8, 0), Vector3.Down, 30f);
            float beside = _rig.BeamReach(_c + new Vector3(-8, 8, 6), Vector3.Down, 30f);
            Check(Math.Abs(onDeck - (8 - DeckTop)) < 0.15f, $"a beam down onto the deck stops on it after {onDeck:F2} m (its top {8 - DeckTop:F2} m down)");
            Check(beside > 7.5f, $"a beam beside it goes on to the floor ({beside:F2} m)");
        }

        // the climber, waypoint to waypoint
        var at = _climber.GlobalPosition;
        var target = _route[Math.Min(_next, _route.Count - 1)];
        var flat = new Vector2(target.X - at.X, target.Z - at.Z);
        if (_next < _route.Count && flat.Length() < 0.3f) _next++;
        Move(_climber, _next < _route.Count ? flat.Normalized() * Math.Min(Pace, flat.Length() / dt) : Vector2.Zero, dt);
        // the bumper, straight through the tower
        Move(_bumper, new Vector2(0, Pace), dt);

        // a second standing on the deck once there, or out of time
        if (_next >= _route.Count && _climber.IsOnFloor()) _settle++;
        if (_settle < 60 && _t < 12) return;
        _done = true;
        float feet = _climber.GlobalPosition.Y - Tall / 2;
        Check(_next >= _route.Count && Math.Abs(feet - DeckTop) < 0.12f && _climber.IsOnFloor(),
            $"a walker went up the stairs and stands on the deck: feet at {feet:F2} m (deck {DeckTop:F2} m), waypoint {_next} of {_route.Count}, on floor {_climber.IsOnFloor()}");
        float passed = _bumper.GlobalPosition.Z - _c.Z;
        Check(passed < -0.2f - Radius + 0.05f, $"a walker into the truss tower is stopped before it ({passed:F2} m from its centre)");
        GD.Print($"[concertwalk] RESULT: {(_failures == 0 ? "ok" : $"FAILED ({_failures})")}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }


    private static Mvr.RigData.MeshRow Boxes(params (Vector3 Lo, Vector3 Hi)[] boxes)
    {
        int[] faces = { 0, 1, 3, 0, 3, 2, 4, 6, 7, 4, 7, 5, 0, 4, 5, 0, 5, 1, 2, 3, 7, 2, 7, 6, 0, 2, 6, 0, 6, 4, 1, 5, 7, 1, 7, 3 };
        var p = new List<Vector3>();
        var idx = new List<int>();
        foreach (var (lo, hi) in boxes)
        {
            int at = p.Count;
            for (int i = 0; i < 8; i++) p.Add(new Vector3((i & 1) == 0 ? lo.X : hi.X, (i >> 1 & 1) == 0 ? lo.Y : hi.Y, (i >> 2 & 1) == 0 ? lo.Z : hi.Z));
            foreach (int f in faces) idx.Add(at + f);
        }
        return Mvr.RigData.Quantise(p, idx, new Color(0.4f, 0.4f, 0.45f));
    }
}
