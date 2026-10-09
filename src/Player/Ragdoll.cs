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
///
/// <para>
/// In water (#380) every point is buoyed up by what of it is under the surface and dragged toward the
/// water's own motion, so a body thrown into a lake plunges, comes back up and floats chest up, low in
/// the water, riding and drifting with the waves. The surface over each point is asked once a frame
/// (<see cref="WaterProbe"/>), not every substep: the waves hardly move in a frame.
/// </para>
/// </summary>
public sealed class Ragdoll
{
    /// <summary>
    /// The water over a point: its surface's altitude and the surface's velocity there (the
    /// <see cref="World.WaterField"/>); false where there is no water.
    /// </summary>
    public delegate bool WaterProbe(Vector3 point, out float level, out Vector3 flow);

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
    /// fell straight through the car it had come out of.) 0.25 s stopped a bus driver against the
    /// side of the bus they had hit; 0.5 s takes the body through its glass and in (#751 playtest).
    /// </summary>
    [Core.Tunable("s the thrown body passes through vehicles: out of its own, through what it hit; 0.1-1")]
    public static float VehicleGrace = 0.5f;
    /// <summary>
    /// Water drag on a submerged point, 1/s and 1/m (on its velocity relative to the water). A body
    /// going in at 15 m/s is down to a few m/s within a body length, as a plunge is.
    /// </summary>
    private const float WaterDrag = 1.0f, WaterDragQuad = 0.6f;
    /// <summary>How deep under its own point a body part is in the water: from half-in to wholly in over this, metres.</summary>
    private const float PartDepth = 0.12f;

    private readonly Vector3[] _p = new Vector3[Avatar.HumanMeshBuilder.JointCount];
    private readonly Vector3[] _prev = new Vector3[Avatar.HumanMeshBuilder.JointCount];
    private readonly Vector3[] _start = new Vector3[Avatar.HumanMeshBuilder.JointCount];
    private readonly float[] _radius = new float[Avatar.HumanMeshBuilder.JointCount];
    /// <summary>
    /// Buoyancy over weight of each point, wholly under water: the chest (lungs) floats, the hips
    /// about do, the head and the limbs sink a little. A body lies face up or down with its chest
    /// at the surface and its legs hanging, as a person unconscious in water does.
    /// </summary>
    private readonly float[] _float = new float[Avatar.HumanMeshBuilder.JointCount];
    private readonly float[] _level = new float[Avatar.HumanMeshBuilder.JointCount];
    private readonly Vector3[] _flow = new Vector3[Avatar.HumanMeshBuilder.JointCount];
    private readonly List<(int A, int B, float Length, float Stiffness)> _sticks = new();
    private readonly List<(int A, int B, float Min)> _floors = new();
    private readonly Godot.Collections.Array<Rid> _exclude = new();
    private readonly List<(Rid Rid, float Until)> _graced = new();
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
    /// <summary>Points in water deeper than the body's own thickness, the last step: none on dry land.</summary>
    public int Wet { get; private set; }
    /// <summary>The surface's velocity at the hips, the last step (zero out of the water).</summary>
    public Vector3 Flow => _flow[(int)J.Hip];

    /// <summary>A point hit something at this speed into the surface (m/s): a thud, or a bone.</summary>
    public event Action<Vector3, float>? Struck;

    /// <summary>A point broke into a vehicle while passing through it (<see cref="VehicleGrace"/>), at this velocity: its glass goes.</summary>
    public event Action<Vector3, Vector3>? BrokeInto;

    /// <summary>
    /// The body stays in the vehicle it breaks into rather than going on through it: only the side
    /// it came in by lets it through (<see cref="EntryDepth"/>), and the far side holds it.
    /// </summary>
    public bool StaysIn { get; init; }

    /// <summary>Thrown out of a vehicle, so through glass. False for someone knocked over on foot: the vehicle that hit them is as solid as a wall.</summary>
    public bool PassesVehicles { get; init; } = true;

    /// <summary>How deep past where it broke in a vehicle's sides still let a body that <see cref="StaysIn"/> through, metres: its near wall, not its far one.</summary>
    private const float EntryDepth = 0.6f;
    /// <summary>A face this steep or steeper is a side (windscreen, window, panel) a thrown body breaks through; flatter is a roof or a floor, which holds it.</summary>
    private const float SideFace = 0.5f;
    /// <summary>Where the body first broke into each vehicle body, and that face's normal.</summary>
    private readonly Dictionary<Rid, (Vector3 At, Vector3 Normal)> _entered = new();
    /// <summary>The hulls of the vehicles it broke into, and their boxes: their roofs, which a ray from inside cannot see, hold the body in.</summary>
    private readonly List<(Node3D Hull, Aabb Box)> _cabins = new();

    /// <summary>How much of its speed into a vehicle's roof or side a body keeps, bounced off it (the world's surfaces: <see cref="Bounce"/>).</summary>
    [Core.Tunable("share of the speed into a vehicle a thrown body bounces back with; 0-0.8")]
    public static float VehicleBounce = 0.4f;
    private bool _ownFound;

    private readonly PhysicsPointQueryParameters3D _inside = new();

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
            _float[i] = 0.97f;
            _level[i] = float.NaN;
        }
        // all told 1.08: a body with its lungs full floats with a little of it out, and comes back
        // up from a plunge at ~0.8 m/s (the first try, 0.99, sank slowly and stayed 2 m down)
        _float[(int)J.Chest] = 2.2f;
        _float[(int)J.Waist] = 1.4f;
        _float[(int)J.Hip] = 1.1f;
        _float[(int)J.ShoulderL] = _float[(int)J.ShoulderR] = 1.15f;
        _float[(int)J.HeadBase] = _float[(int)J.HeadTop] = 1f;
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

    /// <summary>Into the new frame after an origin shift (#185): every point, and the velocity turned with it.</summary>
    public void Apply(Core.OriginShift shift)
    {
        for (int i = 0; i < _p.Length; i++)
        {
            _p[i] = shift.Point(_p[i]);
            _prev[i] = shift.Point(_prev[i]);
            _start[i] = shift.Point(_start[i]);
        }
        _restAt = shift.Point(_restAt);
    }

    /// <summary>Moves every point by <paramref name="by"/>, velocity kept: a remote copy steered onto the owner's.</summary>
    public void Shift(Vector3 by)
    {
        for (int i = 0; i < _p.Length; i++) { _p[i] += by; _prev[i] += by; }
    }

    /// <summary>Advances <paramref name="dt"/> seconds in fixed substeps.</summary>
    /// <param name="ground">Terrain height under a point, or null (indoors, no data).</param>
    /// <param name="water">The water over a point (#380), or null: no water anywhere.</param>
    public void Step(float dt, PhysicsDirectSpaceState3D space, Func<Vector3, float?> ground, WaterProbe? water = null)
    {
        // the surface over each point, once a frame: a wave moves a few centimetres in one
        int wet = 0;
        for (int i = 0; i < _p.Length; i++)
        {
            if (water != null && water(_p[i], out float level, out var flow))
            {
                _level[i] = level;
                _flow[i] = flow;
                if (level - _p[i].Y > PartDepth) wet++;
            }
            else
            {
                _level[i] = float.NaN;
                _flow[i] = Vector3.Zero;
            }
        }
        Wet = wet;
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
        ReleaseGraced(space);
        float g = Gravity * Substep * Substep;
        for (int i = 0; i < _p.Length; i++)
        {
            _start[i] = _p[i];
            var v = (_p[i] - _prev[i]) * 0.999f;
            if (!float.IsNaN(_level[i])) v = InWater(i, v);
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
            Ceilings(i);
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

    /// <summary>
    /// A point's move this substep in water: buoyed up by the share of it under the surface, and
    /// dragged toward the water's motion (the waves' orbit at the surface, dying away with depth).
    /// </summary>
    private Vector3 InWater(int i, Vector3 move)
    {
        float sub = _level[i] - _p[i].Y;
        float share = Mathf.Clamp((sub + PartDepth) / (2f * PartDepth), 0f, 1f);
        if (share <= 0f) return move;
        var flow = _flow[i] * Mathf.Clamp(1f - (sub - 0.3f) / 1.5f, 0f, 1f);
        var rel = move / Substep - flow;
        rel *= Mathf.Exp(-(WaterDrag + WaterDragQuad * rel.Length()) * share * Substep);
        var v = flow + rel;
        v.Y += Gravity * _float[i] * share * Substep;
        return v * Substep;
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

    /// <summary>
    /// What the body passes through is solid again once its time is up and no joint is inside it: a
    /// body half through a wall when it turned solid stood stuck upright in it (#751 playtest).
    /// </summary>
    private void ReleaseGraced(PhysicsDirectSpaceState3D space)
    {
        if (!_ownFound && PassesVehicles)
        {
            // the vehicle it is thrown from: whatever holds the seated body when it starts
            _ownFound = true;
            _inside.CollisionMask = _ray.CollisionMask;
            foreach (var p in _p)
            {
                _inside.Position = p;
                foreach (var hit in space.IntersectPoint(_inside, 8))
                    if (IsVehicle(hit["collider"].AsGodotObject()) && hit["rid"].AsRid() is var rid && !_exclude.Contains(rid))
                    {
                        _exclude.Add(rid);
                        _graced.Add((rid, VehicleGrace));
                    }
            }
            _ray.Exclude = _exclude;
        }
        bool due = false;
        foreach (var g in _graced) due |= Age >= g.Until;
        if (!due) return;
        _held.Clear();
        _inside.CollisionMask = _ray.CollisionMask;
        foreach (var p in _p)
        {
            _inside.Position = p;
            foreach (var hit in space.IntersectPoint(_inside, 8)) _held.Add(hit["rid"].AsRid());
        }
        for (int k = _graced.Count - 1; k >= 0; k--)
        {
            if (Age < _graced[k].Until || _held.Contains(_graced[k].Rid)) continue;
            _exclude.Remove(_graced[k].Rid);
            _graced.RemoveAt(k);
            _ray.Exclude = _exclude;
        }
    }

    private readonly HashSet<Rid> _held = new(), _broke = new();
    private float _brokeAt = -1f;

    /// <summary>A vehicle's hull, an articulated bus's rear section, or a vehicle's walkable deck (a bus's deck walls stopped the body between two buses).</summary>
    private static bool IsVehicle(GodotObject? what) => what is Node n
        && (n is Vehicles.VehicleBody || n.GetParent() is Vehicles.VehicleBody || n.IsInGroup(FootPlayer.DeckGroup));

    /// <summary>
    /// The point broke through this vehicle's face, within <see cref="VehicleGrace"/>: a side, and
    /// for a body that <see cref="StaysIn"/> only the side it came in by. Face by face, never the
    /// whole vehicle: excluded whole, a bus let the body out through its roof (#751 playtest).
    /// </summary>
    private bool BreaksThrough(GodotObject what, Rid rid, Vector3 at, Vector3 normal)
    {
        if (!PassesVehicles || Mathf.Abs(normal.Y) > SideFace) return false;
        // once in, its sides go on letting the body through after the grace: ended halfway, a body
        // going through hung in the far wall, and one staying in left its legs outside (#751 playtest)
        if (_entered.TryGetValue(rid, out var entry)) return !StaysIn || (at - entry.At).Dot(-entry.Normal) < EntryDepth;
        if (Age >= VehicleGrace) return false;
        _entered[rid] = (at, normal);
        if (what is Node n && n.GetNodeOrNull<CollisionShape3D>("Hull") is { Shape: { } shape } hull)
            _cabins.Add((hull, shape is BoxShape3D b ? new Aabb(-b.Size / 2, b.Size) : shape.GetDebugMesh().GetAabb()));
        return true;
    }

    /// <summary>
    /// A vehicle's roof from inside: a point under it and in its footprint that would rise through
    /// it bounces off it instead. Inside the hull a ray sees none of the hull's faces, and a head
    /// went up through a bus's roof and the body stood up on it (#751 playtest).
    /// </summary>
    private void Ceilings(int i)
    {
        foreach (var (hull, box) in _cabins)
        {
            if (!GodotObject.IsInstanceValid(hull)) continue;
            var frame = hull.GlobalTransform;
            var inv = frame.AffineInverse();
            var local = inv * _p[i];
            float top = box.End.Y - _radius[i];
            if (local.Y <= top || (inv * _start[i]).Y > top + 0.05f
                || local.X < box.Position.X || local.X > box.End.X || local.Z < box.Position.Z || local.Z > box.End.Z) continue;
            float into = ((_p[i] - _prev[i]) / Substep).Dot(frame.Basis.Y.Normalized());
            _p[i] = frame * (local with { Y = top });
            var down = -frame.Basis.Y.Normalized();
            var v = (_p[i] - _prev[i]) / Substep;
            float vn = v.Dot(down);
            var after = (v - down * vn) * Mathf.Max(0f, 1f - Friction * Substep) - down * Mathf.Min(vn, 0f) * VehicleBounce;
            _prev[i] = _p[i] - after * Substep;
            if (into > 3f) Struck?.Invoke(_p[i], into);
        }
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
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var hit = space.IntersectRay(_ray);
            if (hit.Count == 0) return;
            var what = hit["collider"].AsGodotObject();
            var normal = hit["normal"].AsVector3();
            var at = hit["position"].AsVector3();
            // never other players' capsules
            if (what is FootPlayer)
            {
                _exclude.Add(hit["rid"].AsRid());
                _ray.Exclude = _exclude;
                continue;
            }
            // through a vehicle's glass and panels: on from just past the face (a ray from inside a
            // shape does not see it, so the point goes on until the next face that counts)
            if (IsVehicle(what) && hit["rid"].AsRid() is var rid && BreaksThrough(what, rid, at, normal))
            {
                if (!_broke.Contains(rid) && _broke.Add(rid) && Age - _brokeAt > 0.15f)
                {
                    _brokeAt = Age;
                    BrokeInto?.Invoke(at, (_p[i] - _prev[i]) / Substep);
                }
                _ray.From = at + dir * 0.01f;
                continue;
            }
            float into = -((_p[i] - _prev[i]) / Substep).Dot(normal);
            // Stopped on its own line of motion, a radius off the surface. Not the hit point plus
            // the normal: on a slope that shifts the point downhill by r·sinθ every substep, and a
            // body lying on a hillside slid down it at a steady 3-4 m/s, friction or no friction.
            float across = dir.Dot(normal);
            _p[i] = across < -0.15f
                ? _start[i] + dir * Mathf.Max(0f, (_radius[i] + (at - _start[i]).Dot(normal)) / across)
                : at + normal * _radius[i];
            Contact(i, normal, into, IsVehicle(what) ? VehicleBounce : Bounce);
            return;
        }
    }

    /// <summary>A contact's velocity: into the surface mostly gone, along it mostly gone, a hard one reported.</summary>
    private void Contact(int i, Vector3 normal, float into, float bounce = Bounce)
    {
        var v = (_p[i] - _prev[i]) / Substep;
        float vn = v.Dot(normal);
        var tangent = v - normal * vn;
        var after = tangent * Mathf.Max(0f, 1f - Friction * Substep) - normal * Mathf.Min(vn, 0f) * bounce + normal * Mathf.Max(vn, 0f);
        _prev[i] = _p[i] - after * Substep;
        if (into > 3f) Struck?.Invoke(_p[i], into);
    }
}
