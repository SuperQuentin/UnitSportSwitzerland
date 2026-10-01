using Godot;
using J = UnitSport.Avatar.HumanMeshBuilder.Joint;

namespace UnitSport.Player;

/// <summary>
/// A figure gone limp (#214): the twenty joints of <see cref="Avatar.HumanMeshBuilder"/> as
/// verlet points, held together by sticks and thrown into the world.
///
/// <para>
/// Points, not physics bodies: the figure has no skeleton — it is one mesh rebuilt every frame
/// from joint positions — so a point per joint is exactly what it can draw, and twenty points
/// with a handful of distance constraints are cheaper and far steadier than twenty rigid bodies
/// and their joints. The torso and head are braced into near-rigid blocks, the spine and neck
/// bend a little, the limbs swing freely about the elbow and knee with only a floor on how far
/// they fold.
/// </para>
///
/// <para>
/// The world is met by raycasts, one per point per substep from where it was to where it goes,
/// plus the terrain's height as a floor, so a fast throw does not tunnel through a wall. Every
/// peer runs its own copy from the same launch (<see cref="FootPlayer"/> replicates the launch
/// velocity, not the points); the remote copies are steered onto the owner's replicated
/// position, so they land where the owner did without being kept in lockstep.
/// </para>
/// </summary>
public sealed class Ragdoll
{
    private const float Gravity = 9.8f;
    private const float Substep = 1f / 90f;
    private const int Iterations = 8;
    /// <summary>
    /// Sliding friction, per second: a body skids a metre or two, then stops. Per second, not per
    /// contact — taken off every substep, a fixed fraction glued the feet where they landed and the
    /// figure stood upright on the bonnet instead of folding at the knees.
    /// </summary>
    private const float Friction = 6f;
    private const float Bounce = 0.15f;
    /// <summary>
    /// Contacts ignore vehicles this long: the body starts in the seat, inside the car's hull, and
    /// has to get out through the glass. Only for this long — after it, the car is solid again and
    /// the body lands on its bonnet or its roof. (Excluded for good, as it first was, the body
    /// fell straight through the car it had come out of.)
    /// </summary>
    private const float VehicleGrace = 0.25f;

    private readonly Vector3[] _p = new Vector3[Avatar.HumanMeshBuilder.JointCount];
    private readonly Vector3[] _prev = new Vector3[Avatar.HumanMeshBuilder.JointCount];
    private readonly Vector3[] _start = new Vector3[Avatar.HumanMeshBuilder.JointCount];
    private readonly float[] _radius = new float[Avatar.HumanMeshBuilder.JointCount];
    private readonly List<(int A, int B, float Length, float Stiffness)> _sticks = new();
    private readonly List<(int A, int B, float Min)> _floors = new();
    private readonly Godot.Collections.Array<Rid> _exclude = new();
    private readonly List<Rid> _graced = new();
    private readonly PhysicsRayQueryParameters3D _ray = new();
    private float _accum, _still;
    private Vector3 _restAt;

    /// <summary>Seconds of simulated time since the throw.</summary>
    public float Age { get; private set; }
    /// <summary>Lying still: time to get up.</summary>
    public bool Resting { get; private set; }
    public ReadOnlySpan<Vector3> Points => _p;
    public Vector3 Pelvis => _p[(int)J.Hip];
    /// <summary>Middle of the body, between hips and chest: what a camera looks at.</summary>
    public Vector3 Centre => (_p[(int)J.Hip] + _p[(int)J.Chest]) * 0.5f;
    /// <summary>Velocity of the hips over the last substep, m/s.</summary>
    public Vector3 Velocity => (_p[(int)J.Hip] - _prev[(int)J.Hip]) / Substep;

    /// <summary>A point hit something at this speed into the surface (m/s): a thud, or a bone.</summary>
    public event Action<Vector3, float>? Struck;

    /// <param name="joints">World joint positions in <see cref="Avatar.HumanMeshBuilder.Joint"/> order.</param>
    /// <param name="velocity">Launch velocity of the whole body.</param>
    /// <param name="spin">Angular velocity (rad/s, world axis × rate) about the body's middle: a tumble.</param>
    /// <param name="self">Never collided with: the thrown player's own body.</param>
    public Ragdoll(ReadOnlySpan<Vector3> joints, Vector3 velocity, Vector3 spin, Rid self, uint mask)
    {
        var centre = Vector3.Zero;
        foreach (var j in joints) centre += j;
        centre /= joints.Length;
        for (int i = 0; i < _p.Length; i++)
        {
            _p[i] = joints[i];
            var v = velocity + spin.Cross(joints[i] - centre);
            _prev[i] = joints[i] - v * Substep;
            _radius[i] = 0.07f;
        }
        _radius[(int)J.HeadTop] = 0.11f;
        _radius[(int)J.HeadBase] = 0.1f;
        _radius[(int)J.Chest] = 0.13f;
        _radius[(int)J.Waist] = 0.12f;
        _radius[(int)J.Hip] = 0.12f;

        // braced blocks: every pair stays as it started, so the block keeps its shape
        Brace(J.HeadTop, J.HeadBase, J.Neck);
        Brace(J.Neck, J.Chest, J.ShoulderL, J.ShoulderR);
        Brace(J.Waist, J.Hip, J.HipL, J.HipR);
        // the spine and neck: stiff, not rigid
        Stick(J.Chest, J.Waist, 1f);
        Stick(J.Chest, J.Hip, 0.25f);
        Stick(J.ShoulderL, J.HipL, 0.15f);
        Stick(J.ShoulderR, J.HipR, 0.15f);
        Stick(J.HeadBase, J.Chest, 0.3f);
        Stick(J.HeadTop, J.Chest, 0.2f);
        // the limbs: bones keep their length, joints swing
        Stick(J.ShoulderL, J.ElbowL, 1f); Stick(J.ElbowL, J.WristL, 1f);
        Stick(J.ShoulderR, J.ElbowR, 1f); Stick(J.ElbowR, J.WristR, 1f);
        Stick(J.HipL, J.KneeL, 1f); Stick(J.KneeL, J.AnkleL, 1f); Stick(J.AnkleL, J.ToeL, 1f); Stick(J.KneeL, J.ToeL, 1f);
        Stick(J.HipR, J.KneeR, 1f); Stick(J.KneeR, J.AnkleR, 1f); Stick(J.AnkleR, J.ToeR, 1f); Stick(J.KneeR, J.ToeR, 1f);
        // an elbow or knee folds only so far: the hand does not pass through the shoulder
        _floors.Add(((int)J.ShoulderL, (int)J.WristL, 0.14f));
        _floors.Add(((int)J.ShoulderR, (int)J.WristR, 0.14f));
        _floors.Add(((int)J.HipL, (int)J.AnkleL, 0.22f));
        _floors.Add(((int)J.HipR, (int)J.AnkleR, 0.22f));
        // and the limbs stay out of the chest
        _floors.Add(((int)J.WristL, (int)J.Chest, 0.16f));
        _floors.Add(((int)J.WristR, (int)J.Chest, 0.16f));
        _floors.Add(((int)J.KneeL, (int)J.KneeR, 0.12f));

        if (self.IsValid) _exclude.Add(self);
        _ray.CollisionMask = mask;
        _ray.Exclude = _exclude;
    }

    private void Stick(J a, J b, float stiffness) =>
        _sticks.Add(((int)a, (int)b, _p[(int)a].DistanceTo(_p[(int)b]), stiffness));

    private void Brace(params J[] block)
    {
        for (int i = 0; i < block.Length; i++)
            for (int k = i + 1; k < block.Length; k++)
                Stick(block[i], block[k], 1f);
    }

    /// <summary>Moves every point by <paramref name="by"/>, velocity kept: a remote copy steered onto the owner's.</summary>
    public void Shift(Vector3 by)
    {
        for (int i = 0; i < _p.Length; i++) { _p[i] += by; _prev[i] += by; }
    }

    /// <summary>Advances <paramref name="dt"/> seconds in fixed substeps.</summary>
    /// <param name="ground">Terrain height under a point, or null (indoors, no data).</param>
    public void Step(float dt, PhysicsDirectSpaceState3D space, Func<Vector3, float?> ground)
    {
        _accum = Mathf.Min(_accum + dt, Substep * 8);
        while (_accum >= Substep)
        {
            _accum -= Substep;
            Substeps(space, ground);
        }
    }

    private void Substeps(PhysicsDirectSpaceState3D space, Func<Vector3, float?> ground)
    {
        Age += Substep;
        if (Age >= VehicleGrace && _graced.Count > 0)
        {
            foreach (var rid in _graced) _exclude.Remove(rid);
            _graced.Clear();
            _ray.Exclude = _exclude;
        }
        float g = Gravity * Substep * Substep;
        for (int i = 0; i < _p.Length; i++)
        {
            _start[i] = _p[i];
            var v = (_p[i] - _prev[i]) * 0.999f;
            _prev[i] = _p[i];
            _p[i] += v + Vector3.Down * g;
        }

        // Joints fold the way they bend: a straight leg under load is a column that never buckles,
        // and the figure stood upright against a wall on the bonnet. A small push on each knee and
        // elbow toward its bend (or, dead straight, toward the toes / the front) lets them give.
        Fold(J.HipL, J.KneeL, J.AnkleL, _p[(int)J.ToeL] - _p[(int)J.AnkleL], 4f * g / Gravity);
        Fold(J.HipR, J.KneeR, J.AnkleR, _p[(int)J.ToeR] - _p[(int)J.AnkleR], 4f * g / Gravity);
        var front = (_p[(int)J.ShoulderL] - _p[(int)J.Hip]).Cross(_p[(int)J.ShoulderR] - _p[(int)J.Hip]);
        Fold(J.ShoulderL, J.ElbowL, J.WristL, -front, 2f * g / Gravity);
        Fold(J.ShoulderR, J.ElbowR, J.WristR, -front, 2f * g / Gravity);

        for (int it = 0; it < Iterations; it++)
        {
            foreach (var (a, b, length, stiffness) in _sticks)
            {
                var d = _p[b] - _p[a];
                float len = d.Length();
                if (len < 1e-5f) continue;
                var fix = d * ((len - length) / len * 0.5f * stiffness);
                _p[a] += fix;
                _p[b] -= fix;
            }
            foreach (var (a, b, min) in _floors)
            {
                var d = _p[b] - _p[a];
                float len = d.Length();
                if (len >= min || len < 1e-5f) continue;
                var fix = d * ((len - min) / len * 0.5f);
                _p[a] += fix;
                _p[b] -= fix;
            }
        }

        for (int i = 0; i < _p.Length; i++)
        {
            Collide(i, space);
            if (ground(_p[i]) is float h && _p[i].Y < h + _radius[i])
            {
                float into = (_prev[i].Y - _p[i].Y) / Substep;
                _p[i].Y = h + _radius[i];
                Contact(i, Vector3.Up, into);
            }
        }

        // Still when the middle of the body stays within a hand's width for most of a second,
        // whatever its points do: one wedged between a wall and a bonnet can twitch forever.
        if (Centre.DistanceTo(_restAt) > 0.12f) { _restAt = Centre; _still = 0f; }
        else _still += Substep;
        if ((Age > 1.2f && _still > 0.6f) || Age > 8f) Resting = true;
    }

    /// <summary>Nudges the middle joint of a limb by <paramref name="push"/> metres, away from the line between its ends.</summary>
    private void Fold(J root, J mid, J end, Vector3 fallback, float push)
    {
        var a = _p[(int)root];
        var b = _p[(int)end];
        var axis = b - a;
        if (axis.LengthSquared() < 1e-6f) return;
        axis = axis.Normalized();
        var off = _p[(int)mid] - a;
        var bend = off - axis * off.Dot(axis);
        if (bend.LengthSquared() < 0.0004f) bend = fallback - axis * fallback.Dot(axis);
        if (bend.LengthSquared() < 1e-8f) return;
        _p[(int)mid] += bend.Normalized() * push;
    }

    /// <summary>The swept move of one point against the world: stopped a radius short of what it meets.</summary>
    private void Collide(int i, PhysicsDirectSpaceState3D space)
    {
        var move = _p[i] - _start[i];
        float len = move.Length();
        if (len < 1e-6f) move = Vector3.Down * 1e-3f;
        var dir = move.Normalized();
        _ray.From = _start[i];
        _ray.To = _p[i] + dir * _radius[i];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var hit = space.IntersectRay(_ray);
            if (hit.Count == 0) return;
            var what = hit["collider"].AsGodotObject();
            // never other players' capsules; the car it came out of, until it is clear of it
            bool graced = what is Vehicles.VehicleBody && Age < VehicleGrace;
            if (what is FootPlayer || graced)
            {
                var rid = hit["rid"].AsRid();
                _exclude.Add(rid);
                if (graced) _graced.Add(rid);
                _ray.Exclude = _exclude;
                continue;
            }
            var normal = hit["normal"].AsVector3();
            var at = hit["position"].AsVector3();
            float into = -((_p[i] - _prev[i]) / Substep).Dot(normal);
            // Stopped on its own line of motion, a radius off the surface. Not the hit point plus
            // the normal: on a slope that shifts the point downhill by r·sinθ every substep, and a
            // body lying on a hillside slid down it at a steady 3-4 m/s, friction or no friction.
            float across = dir.Dot(normal);
            _p[i] = across < -0.15f
                ? _start[i] + dir * Mathf.Max(0f, (_radius[i] + (at - _start[i]).Dot(normal)) / across)
                : at + normal * _radius[i];
            Contact(i, normal, into);
            return;
        }
    }

    /// <summary>A contact's velocity: into the surface mostly gone, along it mostly gone, a hard one reported.</summary>
    private void Contact(int i, Vector3 normal, float into)
    {
        var v = (_p[i] - _prev[i]) / Substep;
        float vn = v.Dot(normal);
        var tangent = v - normal * vn;
        var after = tangent * Mathf.Max(0f, 1f - Friction * Substep) - normal * Mathf.Min(vn, 0f) * Bounce + normal * Mathf.Max(vn, 0f);
        _prev[i] = _p[i] - after * Substep;
        if (into > 3f) Struck?.Invoke(_p[i], into);
    }
}
