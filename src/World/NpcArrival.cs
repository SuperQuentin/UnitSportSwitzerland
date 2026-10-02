using Godot;
using UnitSport.Player;

namespace UnitSport.World;

/// <summary>How a race NPC comes to its grid slot.</summary>
public enum ArrivalStyle
{
    /// <summary>Up the road from behind the start, into the slot (sometimes a handbrake flick, sometimes late on the brakes).</summary>
    Behind,
    /// <summary>From ahead: a three-point turn across the road, then back into the slot.</summary>
    KTurn,
    /// <summary>From ahead: a handbrake U-turn where there is room, then back into the slot.</summary>
    JTurn,
    /// <summary>From ahead: one and a half donuts where there is room, then back into the slot.</summary>
    Donut,
}

/// <summary>
/// A race NPC drives to its grid slot instead of appearing in it (issue #51). It appears out of
/// sight on the race road — behind the start or ahead of the grid — and comes to its slot with the
/// real car through <see cref="FootPlayer.RideControls"/> only: from behind it drives up and
/// settles; from ahead it has to turn round, with a three-point turn, a handbrake U-turn or a
/// donut, then rolls back into the slot.
///
/// <para>
/// <b>Server</b> (<see cref="Lane"/>, <see cref="Plan"/>): the lane is the road behind the start
/// plus the race route; each NPC gets a style and an entry point on it, hidden from every player
/// (far enough, or round a bend) and clear of every body. The slot is only final at the close of
/// entry, so the NPC aims at its provisional slot (its index among the entrants so far) and
/// <see cref="SetSlot"/> moves the target when the grid is sent (a later entrant pushes every slot
/// 8 m up the road).
/// </para>
///
/// <para>
/// <b>Order</b>: the from-ahead NPCs take the front slots and settle rear-first (each rolls back
/// down its column, past the empty slots ahead of it); the from-behind NPCs take the rear slots
/// and settle front-first (each drives up its column, past the empty slots behind it). Their entry
/// points are spaced along the lane in that order, so the queue on the road is already the order.
/// Nothing drives through a body: a car stops behind whatever is in its way, and goes round a
/// parked one where the road has room.
/// </para>
/// </summary>
public sealed class NpcArrival
{
    /// <summary>How far past the grid (and behind the start) an NPC appears, and how far apart two do.</summary>
    public const float MinAway = 160f, Spacing = 45f, Search = 260f;
    /// <summary>The route kept ahead of the start in the lane: the grid plus where an NPC appears.</summary>
    public const float Reach = 750f;

    /// <summary>A body on the road, as an arriving NPC sees it.</summary>
    public readonly record struct Body(Vector3 Position, Vector3 Velocity, bool Npc);

    /// <summary>One NPC's arrival, chosen by the server.</summary>
    public readonly record struct Entry(int Slot, ArrivalStyle Style, int Variant, Vector3 At, float Yaw);

    // ====================================================================================
    // server: the lane and the entry points
    // ====================================================================================

    /// <summary>
    /// The road an NPC arrives on: the road behind the start (reversed, so the lane runs in the race
    /// direction) then the race route up to <see cref="Reach"/>. <c>Zero</c> is the lane arc of the
    /// route's start: a route arc <c>s</c> is the lane arc <c>Zero + s</c>.
    /// </summary>
    public static (Vector3[] Centre, float[] Width, float Zero) Lane(RaceRoute route)
    {
        var c = new List<Vector3>();
        var w = new List<float>();
        // Behind[0] is the route's own first point
        for (int i = route.Behind.Count - 1; i >= 1; i--) { c.Add(route.Behind[i]); w.Add(route.BehindWidth[i]); }
        float zero = 0;
        for (int i = 1; i < c.Count; i++) zero += c[i].DistanceTo(c[i - 1]);
        if (c.Count > 0) zero += c[^1].DistanceTo(route.Centre[0]);
        for (int i = 0; i < route.Centre.Count && route.Arc[i] <= Reach; i++) { c.Add(route.Centre[i]); w.Add(route.Width[i]); }
        return (c.ToArray(), w.ToArray(), zero);
    }

    /// <summary>A provisional slot in lane terms: arc and lateral offset (+ right of the race direction).</summary>
    public static (float S, float Lat) SlotOnLane(RaceRoute lane, float zero, int slot, int count)
    {
        float s = zero + RaceCourse.StartArc(slot, count);
        float side = slot % 2 == 0 ? -1f : 1f;   // as RaceCourse.Slot
        return (s, side * WidthAt(lane, s) * 0.25f);
    }

    /// <summary>
    /// Styles and entry points for <paramref name="n"/> NPCs entering as slots
    /// <paramref name="firstSlot"/>.. of <paramref name="count"/>. Deterministic in
    /// <paramref name="seed"/>; <paramref name="only"/> forces a style (the check).
    /// </summary>
    public static List<Entry> Plan(RaceRoute lane, float zero, int firstSlot, int n, int count,
        IReadOnlyList<Vector3> players, IReadOnlyList<Vector3> bodies, int seed, ArrivalStyle? only = null)
    {
        var rng = new System.Random(seed);
        float gridFront = zero + RaceCourse.StartArc(0, count), gridRear = zero + RaceCourse.StartArc(count - 1, count);
        int behindRoom = gridRear - MinAway < 10f ? 0 : (int)((gridRear - MinAway - 10f) / Spacing) + 1;
        int ahead = only is { } o ? (o == ArrivalStyle.Behind ? 0 : n) : n / 2 + (n % 2 == 1 ? rng.Next(2) : 0);
        ahead = Mathf.Clamp(ahead, n - behindRoom, n);
        var turns = new[] { ArrivalStyle.KTurn, ArrivalStyle.JTurn, ArrivalStyle.Donut };
        int turn = rng.Next(3);

        var list = new Entry[n];
        var taken = new List<float>();
        // from ahead: the front slots, rearmost first (it settles first, so it is nearest)
        for (int j = 0; j < ahead; j++)
        {
            int slot = firstSlot + ahead - 1 - j;
            float s = Pick(lane, gridFront + MinAway + Spacing * j, +1f, players, bodies, taken);
            var style = only ?? turns[(turn + j) % 3];
            list[slot - firstSlot] = At(lane, s, -1f, slot, style, rng.Next(3));
        }
        // from behind: the rear slots, frontmost first
        for (int j = 0; j < n - ahead; j++)
        {
            int slot = firstSlot + ahead + j;
            float s = Pick(lane, gridRear - MinAway - Spacing * j, -1f, players, bodies, taken);
            list[slot - firstSlot] = At(lane, s, +1f, slot, ArrivalStyle.Behind, rng.Next(3));
        }
        return list.ToList();
    }

    /// <summary>Facing <paramref name="dir"/> along the lane, on its own side of the road (the right).</summary>
    private static Entry At(RaceRoute lane, float s, float dir, int slot, ArrivalStyle style, int variant)
    {
        var t = TangentAt(lane, s) * dir;
        var p = PointAt(lane, s) + t.Cross(Vector3.Up) * WidthAt(lane, s) * 0.22f + Vector3.Up * 1.5f;
        return new Entry(slot, style, variant, p, Mathf.Atan2(-t.X, -t.Z));
    }

    /// <summary>
    /// The first lane arc from <paramref name="from"/> outward that no player sees and no body
    /// stands on; the farthest free one if none is hidden. Hidden: 220 m from every player, or 110 m
    /// with the road bending 15 m away from the straight line between (a bend, a crest's trees).
    /// </summary>
    private static float Pick(RaceRoute lane, float from, float dir, IReadOnlyList<Vector3> players,
        IReadOnlyList<Vector3> bodies, List<float> taken)
    {
        float end = lane.Arc[^1] - 5f;
        float best = float.NaN;
        for (float s = Mathf.Clamp(from, 5f, end); s >= 5f && s <= end && Mathf.Abs(s - from) <= Search; s += dir * 10f)
        {
            var p = PointAt(lane, s);
            if (bodies.Any(b => RaceRoute.Flat(b - p).Length() < 15f) || taken.Any(t => Mathf.Abs(t - s) < Spacing * 0.8f)) continue;
            best = s;
            if (players.All(q => Hidden(lane, q, s))) break;
        }
        if (float.IsNaN(best)) best = Mathf.Clamp(from, 5f, end);
        taken.Add(best);
        return best;
    }

    private static bool Hidden(RaceRoute lane, Vector3 player, float s)
    {
        var p = PointAt(lane, s);
        float d = RaceRoute.Flat(p - player).Length();
        if (d >= 220f) return true;
        if (d < 110f) return false;
        var (ps, _) = Frame(lane, player, -1);
        var chord = RaceRoute.Flat(p - player).Normalized();
        float bend = 0f;
        for (float x = Mathf.Min(ps, s); x <= Mathf.Max(ps, s); x += 10f)
            bend = Mathf.Max(bend, Mathf.Abs(RaceRoute.Flat(PointAt(lane, x) - player).Cross(chord).Y));
        return bend > 15f;
    }

    // ====================================================================================
    // lane geometry
    // ====================================================================================

    public static Vector3 PointAt(RaceRoute lane, float s)
    {
        s = Mathf.Clamp(s, 0f, lane.Arc[^1]);
        int i = Mathf.Clamp(lane.NearestCentreIndexAt(s), 1, lane.Centre.Count - 1);
        float a = lane.Arc[i - 1], b = lane.Arc[i];
        return lane.Centre[i - 1].Lerp(lane.Centre[i], b > a ? (s - a) / (b - a) : 0f);
    }

    public static Vector3 TangentAt(RaceRoute lane, float s)
    {
        var t = RaceRoute.Flat(PointAt(lane, s + 2f) - PointAt(lane, s - 2f));
        return t.LengthSquared() > 1e-6f ? t.Normalized() : Vector3.Forward;
    }

    public static float WidthAt(RaceRoute lane, float s) => lane.Width[lane.NearestCentreIndexAt(s)];

    /// <summary>Where a point is in lane terms: arc and lateral offset (+ right). <paramref name="hint"/>: a centre index near it, or -1.</summary>
    public static (float S, float Lat) Frame(RaceRoute lane, Vector3 p, int hint)
    {
        int i = hint;
        if (i < 0) i = lane.NearestCentre(p);
        else
        {
            float best = float.MaxValue;
            for (int k = Mathf.Max(0, hint - 12); k < Mathf.Min(lane.Centre.Count, hint + 12); k++)
            {
                float d = RaceRoute.Flat(lane.Centre[k] - p).LengthSquared();
                if (d < best) { best = d; i = k; }
            }
        }
        float s0 = lane.Arc[i];
        var t = TangentAt(lane, s0);
        var d0 = RaceRoute.Flat(p - lane.Centre[i]);
        return (s0 + d0.Dot(t), d0.Dot(t.Cross(Vector3.Up)));
    }

    // ====================================================================================
    // the owner: driving it in
    // ====================================================================================

    private enum Step { Approach, Flick, Leg, Drift, Park, Hold }

    public readonly FootPlayer Me;
    public readonly RaceRoute Road;
    public readonly float Zero;
    public readonly ArrivalStyle Planned;
    public readonly int Variant;
    /// <summary>What it is actually doing: a turn with no room falls back to a simpler one.</summary>
    public ArrivalStyle Style { get; private set; }
    public System.Action<string>? Log;
    private static readonly bool Trace = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--trace") >= 0;

    private float _slotS, _slotLat;
    private Step _step = Step.Approach;
    private int _near = -1;
    private readonly float _dir;   // lane direction of travel while approaching: +1 from behind, -1 from ahead
    private float _spotS;           // where it turns round (from ahead)
    private Vector3 _spotCentre;    // the middle of the disc checked clear for it
    private bool _spotChecked;
    private float _t, _stepT, _blocked;
    private float? _bypass;
    private float _bypassUntil;
    // a leg of a turn: forward (+1) or back (-1), and the lock held
    private int _legDir = 1, _legs;
    private bool _legStopping;
    private float _rot, _lastYaw, _driftFrom;
    private int _parkDir;
    private int _shuffles;
    private float _flickAt;
    /// <summary>How far from the centreline a turn may put a corner: the tarmac, or the checked disc.</summary>
    private float _offRoad, _lastCorner;

    /// <summary>Seconds to GO once the grid is known.</summary>
    public double? GoIn { get; private set; }

    /// <summary>Reached its slot (the provisional one or the final one) and holds there.</summary>
    public bool Staged { get; private set; }
    public bool Settled => _step == Step.Hold;
    public string Phase => _step == Step.Leg ? (_legDir > 0 ? "turn-fwd" : "turn-back") : _step.ToString().ToLowerInvariant();

    // ---- what the check measures ----
    public float Travelled, Top, Reversed, Rotation;
    public float StagedAt = -1;
    public int Teleports;
    public int Legs => _legs;

    public NpcArrival(FootPlayer me, RaceRoute lane, float zero, ArrivalStyle style, int variant, int slot, int count)
    {
        Me = me;
        Road = lane;
        Zero = zero;
        Planned = Style = style;
        Variant = variant;
        (_slotS, _slotLat) = SlotOnLane(lane, zero, slot, count);
        _dir = style == ArrivalStyle.Behind ? 1f : -1f;
        _spotS = _slotS + 12f;
        _lastYaw = me.Motion.Yaw;
        // a flick on the way in, 15-20 m short of the slot
        _flickAt = style == ArrivalStyle.Behind && variant == 1 ? _slotS - 17f : float.NaN;
    }

    /// <summary>The grid is known: this slot, GO in <paramref name="countdown"/> s.</summary>
    public void SetSlot(Vector3 at, double countdown)
    {
        var (s, lat) = Frame(Road, at, -1);
        bool moved = Mathf.Abs(s - _slotS) > 0.3f || Mathf.Abs(lat - _slotLat) > 0.3f;
        _spotS += s - _slotS;
        (_slotS, _slotLat) = (s, lat);
        GoIn = countdown;
        if (moved && _step == Step.Hold) Begin(Step.Park);
    }

    /// <summary>Slot error: metres and degrees (to the race direction).</summary>
    public (float Metres, float Degrees) Error()
    {
        var target = PointAt(Road, _slotS) + TangentAt(Road, _slotS).Cross(Vector3.Up) * _slotLat;
        float m = RaceRoute.Flat(Me.GlobalPosition - target).Length();
        float deg = Mathf.RadToDeg(Mathf.Abs(RaceRoute.SignedAngle(TangentAt(Road, _slotS), Nose(Me.Motion.Yaw))));
        return (m, deg);
    }

    private static Vector3 Nose(float yaw) => new(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
    private static float Wrap(float a) => Mathf.Wrap(a, -Mathf.Pi, Mathf.Pi);

    private void Begin(Step step)
    {
        if (step != _step) Log?.Invoke($"{Phase} -> {(step == Step.Leg ? "turn" : step.ToString().ToLowerInvariant())} ({Style})");
        _step = step;
        _stepT = 0;
        _legStopping = false;
        if (step == Step.Park) { _parkDir = 0; }
    }

    /// <summary>One physics step: what the NPC's car is asked to do.</summary>
    public RideInput Drive(float dt, IReadOnlyList<Body> others)
    {
        if (Me.Vehicle is not Car car) return Handbrake(0f);
        var m = Me.Motion;
        var pos = Me.GlobalPosition;
        float u = m.Speed * Mathf.Cos(m.Slip);
        _t += dt;
        _stepT += dt;
        Travelled += m.Speed * dt;
        Top = Mathf.Max(Top, m.Speed);
        if (u < -0.3f) Reversed += -u * dt;
        float dyaw = Wrap(m.Yaw - _lastYaw);
        _lastYaw = m.Yaw;
        Rotation += Mathf.Abs(dyaw);
        _rot += dyaw;

        var (s, lat) = Frame(Road, pos, _near);
        _near = Road.NearestCentreIndexAt(s);
        var tan = TangentAt(Road, s);
        float hdg = RaceRoute.SignedAngle(tan, Nose(m.Yaw));   // 0: facing the race, ±π: facing back

        if (GoIn is { } go)
        {
            GoIn = go - dt;
            // not in by GO - 2 s: stop whatever it is doing and roll in
            if (GoIn < 2.0 && _step is Step.Approach or Step.Leg or Step.Drift or Step.Flick && Mathf.Abs(hdg) < 0.5f)
                Begin(Step.Park);
            // the last resort, at the line: into the slot by hand, logged
            var (em, ed) = Error();
            if (GoIn < 0.35 && (em > 1.5f || ed > 10f))
            {
                var at = PointAt(Road, _slotS) + TangentAt(Road, _slotS).Cross(Vector3.Up) * _slotLat;
                var f = TangentAt(Road, _slotS);
                Me.PlaceAt(at + Vector3.Up * 1.2f, Mathf.Atan2(-f.X, -f.Z));
                Teleports++;
                GD.Print($"[npc] {Me.Name} not in its slot at GO ({em:F1} m, {ed:F0}°, {Phase}): put there by hand");
                Begin(Step.Hold);
                Staged = true;
                return Handbrake(0f);
            }
        }

        switch (_step)
        {
            case Step.Approach: return Approach(dt, car, pos, s, lat, u, hdg, others);
            case Step.Flick:
                if (_stepT > 0.35f) Begin(Step.Approach);
                return new RideInput(0f, 0f, Variant % 2 == 0 ? 0.35f : -0.35f, false, Handbrake: true);
            case Step.Leg: return Leg(car, pos, s, lat, u, hdg, others);
            case Step.Drift: return Drift(car, pos, s, lat, u, hdg, others);
            case Step.Park: return Park(car, pos, s, lat, u, hdg, others);
            default:
                if (!Staged) { Staged = true; StagedAt = _t; Log?.Invoke($"in the slot after {_t:F1} s"); }
                return Handbrake(0f);
        }
    }

    private static RideInput Handbrake(float steer) => new(0f, 0f, steer, false, Handbrake: true);

    // ---------------------------------------------------------------- approach

    private RideInput Approach(float dt, Car car, Vector3 pos, float s, float lat, float u, float hdg, IReadOnlyList<Body> others)
    {
        float half = WidthAt(Road, s) * 0.5f;
        float roadLat = _dir * half * 0.45f;   // its own side of the road (the right)
        if (_dir > 0)
        {
            // from behind: into its column over the last 40 m, then settle
            float togo = _slotS - s;
            float want = togo < 40f ? _slotLat : roadLat;
            if (!float.IsNaN(_flickAt) && s >= _flickAt && u > 6f) { _flickAt = float.NaN; Begin(Step.Flick); return Handbrake(0f); }
            if (togo < 1.5f && Mathf.Abs(u) < 0.5f) { Begin(Step.Park); return Handbrake(0f); }
            float vmax = Variant == 2 ? 17f : 14f;
            float decel = Variant == 2 ? 4.5f : 2.5f;   // late on the brakes, or easy
            return Track(dt, car, pos, s, lat, u, +1, _slotS, want, vmax, decel, false, others, bypass: true);
        }

        // from ahead: to where it turns round
        float to = s - _spotS;
        if (!_spotChecked && to < 30f)
        {
            _spotChecked = true;
            ChooseSpot(s);
        }
        float speed = Style == ArrivalStyle.JTurn ? 8.5f : Style == ArrivalStyle.Donut ? 7f : 12f;
        // another car still turning round (or anything) on the spot: wait 22 m short of it
        var spot = PointAt(Road, _spotS);
        if (others.Any(b => RaceRoute.Flat(b.Position - spot).Length() < 12f))
        {
            if (to < 30f && _waitLogged < 0) { _waitLogged = _t; Log?.Invoke("waiting for the turning spot to clear"); }
            return Track(dt, car, pos, s, lat, u, -1, _spotS + 22f, roadLat, speed, 2.5f, false, others, bypass: false);
        }
        if (Style == ArrivalStyle.JTurn && _spotChecked && to < 0.5f && u > 6f)
        {
            _driftFrom = s;
            _rot = 0;
            _flicked = -1f;
            Begin(Step.Drift);
            return Drift(car, pos, s, lat, u, hdg, others);
        }
        if (to < 1f && Mathf.Abs(u) < 0.4f)
        {
            _rot = 0;
            _driftFrom = s;
            _flicked = -1f;
            if (Style == ArrivalStyle.Donut) Begin(Step.Drift);
            else if (Style == ArrivalStyle.JTurn) { Style = ArrivalStyle.KTurn; Log?.Invoke("too slow for the handbrake turn: three-point turn"); StartLegs(); }
            else StartLegs();
            return Handbrake(0f);
        }
        // a handbrake turn is entered at speed: aim past the spot so the profile does not brake for it
        float target = Style == ArrivalStyle.JTurn && _spotChecked ? _spotS - 25f : _spotS;
        return Track(dt, car, pos, s, lat, u, -1, target, roadLat, speed, 2.5f, false, others, bypass: true);
    }

    /// <summary>
    /// Where to turn round. A three-point turn fits any road: 12 m past its slot. A handbrake turn
    /// or a donut needs a clear disc — flat, no wall, trunk or drop — searched a little further on;
    /// none there, and it is a three-point turn after all.
    /// </summary>
    private void ChooseSpot(float s)
    {
        if (Style == ArrivalStyle.KTurn) return;
        float need = Style == ArrivalStyle.Donut ? DonutRoom : JTurnRoom;
        for (float x = _slotS + 16f; x <= Mathf.Min(_slotS + 60f, s - 4f); x += 4f)
        {
            // the disc sits over the side it turns towards: the car's left, the race's right
            var c = PointAt(Road, x) + TangentAt(Road, x).Cross(Vector3.Up) * 1.5f;
            if (!Clear(c, need)) continue;
            _spotS = x;
            _spotCentre = c;
            Log?.Invoke($"{Style}: room at {x - _slotS:F0} m past the slot");
            return;
        }
        Log?.Invoke($"{Style}: no room for it here — three-point turn");
        Style = ArrivalStyle.KTurn;
    }

    /// <summary>Radius of clear ground a donut and a handbrake turn need, m.</summary>
    public static float DonutRoom = 5.5f, JTurnRoom = 6.5f;

    /// <summary>The verge and obstacle rays: one query, reused (#221).</summary>
    private readonly Core.RayQuery _ray = new();

    /// <summary>
    /// The safe-verge test: a disc of <paramref name="r"/> round <paramref name="c"/> is clear if
    /// the ground stays within 0.9 m of the centre's height (no drop, no bank) and a ray at knee
    /// height out to its edge hits nothing (wall, trunk, car) in 16 directions.
    /// </summary>
    public bool Clear(Vector3 c, float r)
    {
        var terrain = Me.Terrain;
        if (terrain == null || !terrain.TryGetHeight(c, out float h0)) return false;
        var space = Me.GetWorld3D().DirectSpaceState;
        for (int k = 0; k < 16; k++)
        {
            float a = k * Mathf.Tau / 16f;
            var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            for (float x = 1f; x <= r + 0.5f; x += 1f)
                if (!terrain.TryGetHeight(c + d * x, out float h) || Mathf.Abs(h - h0) > 0.9f) return false;
            var from = c with { Y = h0 + 0.7f };
            var hit = _ray.Cast(space, from, from + d * (r + 0.5f), 0xFFFFFFFF, Me.SelfExclude);
            if (hit.Count > 0 && Mathf.Abs(hit["normal"].AsVector3().Y) < 0.75f) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- three-point turn

    private void StartLegs()
    {
        _legDir = 1;
        Begin(Step.Leg);
    }

    /// <summary>
    /// A leg of a three-point turn: forward on full left lock across the road, back on full right
    /// lock, until the nose is within 25° of the race direction — then it parks. Each leg ends at
    /// the tarmac's edge (a bumper 0.3 m over it), at a wall or a body, stopped, the wheel turned
    /// the other way before it moves again (the wheel is turned at a standstill, as a driver would).
    /// </summary>
    private RideInput Leg(Car car, Vector3 pos, float s, float lat, float u, float hdg, IReadOnlyList<Body> others)
    {
        float steer = _legDir > 0 ? -1f : 1f;   // both turn it anticlockwise
        if (Mathf.Abs(hdg) < 0.44f)
        {
            if (Mathf.Abs(u) > 0.3f) return Handbrake(0f);
            Begin(Step.Park);
            return Handbrake(0f);
        }
        if (_stepT < 0.45f) return Handbrake(steer);   // wind the lock on first
        var nose = Nose(Me.Motion.Yaw);
        var move = nose * _legDir;
        var bumper = pos + move * 2.1f;
        var perp = new Vector3(move.Z, 0, -move.X) * 0.85f;
        var (bs, l1) = Frame(Road, bumper + perp, _near);
        var (_, l2) = Frame(Road, bumper - perp, _near);
        float half = WidthAt(Road, bs) * 0.5f;
        float stopping = u * u / 7f + 0.25f;
        // the leading corners against the tarmac's edge (0.3 m over it allowed), and anything in front
        // beyond the tarmac only on ground the turn's site was checked clear for (a donut's disc)
        float corner = Mathf.Max(Mathf.Abs(l1), Mathf.Abs(l2));
        float edge = Mathf.Max(half + 0.3f, _offRoad) - corner;
        bool outward = corner > _lastCorner;
        _lastCorner = corner;
        bool blocked = Obstacle(pos, move, 2.1f + stopping + 0.6f, others);
        if (Trace && (int)(_stepT * 2) != (int)((_stepT - 1f / 60f) * 2))
            Log?.Invoke($"  leg t={_stepT:F1} u={u:F2} gear={car.Gear} edge={edge:F2} l=({l1:F1},{l2:F1}) half={half:F1} lat={lat:F1} hdg={Mathf.RadToDeg(hdg):F0} blocked={blocked} stop={_legStopping} floor={Me.IsOnFloor()} th={car.Throttle:F2} rpm={car.Rpm:F0} v={Me.Motion.Speed:F2} real={Me.GetRealVelocity().Length():F2}");
        if (_legStopping || (edge < stopping && outward && _stepT > 0.6f) || blocked || _stepT > 9f)
        {
            _legStopping = true;
            if (Mathf.Abs(u) > 0.15f) return Handbrake(steer);
            _legDir = -_legDir;
            _legs++;
            if (_legs > 9) { Log?.Invoke("turn not coming round: parking as it is"); Begin(Step.Park); return Handbrake(0f); }
            Log?.Invoke($"leg {_legs}: {(blocked ? "blocked" : "edge")} at heading {Mathf.RadToDeg(hdg):F0}°, now {(_legDir > 0 ? "forward" : "back")}");
            _stepT = 0;
            _legStopping = false;
            return Handbrake(-steer);
        }
        return Pedals(car, _legDir * 2.0f, u, steer);
    }

    /// <summary>Something solid in front of the moving end: a body, or a wall or trunk at knee height.</summary>
    private bool Obstacle(Vector3 pos, Vector3 move, float reach, IReadOnlyList<Body> others)
    {
        foreach (var b in others)
        {
            var rel = RaceRoute.Flat(b.Position - pos);
            float along = rel.Dot(move);
            if (along > 0.5f && along < reach + 1.5f && Mathf.Abs(rel.Cross(move).Y) < 2.4f) return true;
        }
        var space = Me.GetWorld3D().DirectSpaceState;
        var from = pos + Vector3.Up * 0.1f;
        var hit = _ray.Cast(space, from, from + move * reach, 0xFFFFFFFF, Me.SelfExclude);
        return hit.Count > 0 && Mathf.Abs(hit["normal"].AsVector3().Y) < 0.75f;
    }

    // ---------------------------------------------------------------- handbrake turn, donut

    /// <summary>
    /// A handbrake turn: full left lock and the handbrake at ~10 m/s swings the tail round; the
    /// gas keeps it rotating until the nose is back round to the race direction. A donut: from a
    /// crawl, lock, a handbrake flick and the gas held — the car spins about its front wheels —
    /// one and a half turns, the gas eased whenever it wanders from its disc. Either ends stopped
    /// and, if not straight enough, carries on as a three-point turn.
    /// </summary>
    private RideInput Drift(Car car, Vector3 pos, float s, float lat, float u, float hdg, IReadOnlyList<Body> others)
    {
        bool donut = Style == ArrivalStyle.Donut;
        if (Trace && (int)(_stepT * 4) != (int)((_stepT - 1f / 60f) * 4))
            Log?.Invoke($"  drift t={_stepT:F2} v={Me.Motion.Speed:F1} u={u:F1} slip={Mathf.RadToDeg(Wrap(Me.Motion.Slip)):F0} r={Me.Motion.YawRate:F2} rot={Mathf.RadToDeg(_rot):F0} gear={car.Gear} floor={Me.IsOnFloor()}");
        float target = donut ? 3f * Mathf.Pi : Mathf.Pi;   // radians of rotation
        float lead = donut ? 0.9f : 0.6f;                   // let go early: it keeps turning
        var centre = _spotCentre;
        float wander = RaceRoute.Flat(pos - centre).Length();
        // out of its checked ground: across the tarmac's far edge, past the disc's, or run on down the road
        float room = donut ? DonutRoom : JTurnRoom;
        bool strayed = lat < -(WidthAt(Road, s) * 0.5f + 0.3f) || lat > 1.5f + room - 0.5f || Mathf.Abs(s - _spotS) > room + 4f;
        bool done = _rot >= target - lead || _stepT > (donut ? 14f : 6f) || strayed;
        if (done)
        {
            if (Mathf.Abs(u) > 0.3f || Mathf.Abs(Me.Motion.YawRate) > 0.3f) return new RideInput(0f, 0f, 0f, false, Handbrake: true);
            Log?.Invoke($"{Style}: {Mathf.RadToDeg(_rot):F0}° of rotation, {wander:F1} m from its start");
            _offRoad = donut ? DonutRoom : JTurnRoom;
            if (Mathf.Abs(hdg) < (Mathf.Abs(_slotS - s) > 12f ? 1.0f : 0.44f)) Begin(Step.Park);
            else { Style = ArrivalStyle.KTurn; StartLegs(); }
            return Handbrake(0f);
        }
        if (!donut)
            // the handbrake swings the tail, then the gas on full lock carries the nose round
            return _stepT < 0.6f ? new RideInput(0f, 0f, -1f, false, Handbrake: true)
                                 : new RideInput(Me.Motion.Speed > 4f ? 0.2f : 0.7f, 0f, -1f, false);
        // donut: wind up a tight circle on full lock, flick the handbrake to break the rear
        // loose, then the gas holds it sliding round; less gas when it wanders off its disc
        if (_flicked < 0f)
        {
            if (Me.Motion.Speed < 5.5f) return new RideInput(1f, 0f, -1f, false);
            _flicked = _stepT;
        }
        if (_stepT - _flicked < 0.35f) return new RideInput(0f, 0f, -1f, false, Handbrake: true);
        // the Game profile's catch straightens it within a second: flick again as it grips, and
        // hold the pace near 5 m/s — faster and the circle grows off its disc
        float v = Me.Motion.Speed;
        if (Mathf.Abs(Wrap(Me.Motion.Slip)) < 0.2f && v > 4.5f && _stepT - _flicked > 1.2f) _flicked = _stepT;
        float gas = Mathf.Clamp((5.5f - v) * 0.4f + 0.55f, 0.2f, 1f) * (wander > DonutRoom - 2f ? 0.5f : 1f);
        return new RideInput(gas, 0f, -1f, false);
    }

    // ---------------------------------------------------------------- park

    /// <summary>
    /// Into the slot along its column: forward if the slot is ahead, back if it is behind. Stopped
    /// in it, it holds if it is within 0.7 m and 6° — else (twice at most) it pulls forward 6 m and
    /// comes back, which straightens a car the way a driver does it.
    /// </summary>
    private RideInput Park(Car car, Vector3 pos, float s, float lat, float u, float hdg, IReadOnlyList<Body> others)
    {
        if (_shuffleTo is { } to)
        {
            if (s < to - 0.2f) return Track(0f, car, pos, s, lat, u, +1, to, _slotLat, 3f, 2f, false, others, bypass: false);
            if (Mathf.Abs(u) > 0.2f) return Handbrake(0f);
            _shuffleTo = null;
            _parkDir = -1;
        }
        float err = _slotS - s;
        if (_parkDir == 0) _parkDir = err >= 0 ? 1 : -1;
        if (err * _parkDir < 0.15f)
        {
            if (Mathf.Abs(u) > 0.2f) return Handbrake(0f);
            var (em, ed) = Error();
            if ((em > 0.7f || ed > 6f) && _shuffles < 2 && !(GoIn < 2.5))
            {
                _shuffles++;
                Log?.Invoke($"off by {em:F1} m / {ed:F0}°: straightening ({_shuffles})");
                _shuffleTo = s + 6f;
                return Handbrake(0f);
            }
            Begin(Step.Hold);
            return Handbrake(0f);
        }
        return _parkDir > 0
            ? Track(0f, car, pos, s, lat, u, +1, _slotS, _slotLat, 6f, 2.2f, false, others, bypass: false)
            : Track(0f, car, pos, s, lat, u, -1, _slotS, _slotLat, 3.5f, 1.8f, true, others, bypass: false);
    }

    private float? _shuffleTo;
    private float _flicked = -1f, _waitLogged = -1f;

    // ---------------------------------------------------------------- following the lane

    /// <summary>
    /// Along the lane in direction <paramref name="dir"/> to <paramref name="toS"/>, holding
    /// <paramref name="wantLat"/>, at most <paramref name="vmax"/>, stopping there with
    /// <paramref name="decel"/>. Pure pursuit on the travel (the tail when reversing). A body in the
    /// way is waited for; a parked one that is not an NPC is gone round if the road leaves room.
    /// </summary>
    private RideInput Track(float dt, Car car, Vector3 pos, float s, float lat, float u, int dir, float toS, float wantLat,
        float vmax, float decel, bool reverse, IReadOnlyList<Body> others, bool bypass)
    {
        float half = WidthAt(Road, s) * 0.5f;
        if (_bypass is { } by && (s - _bypassUntil) * dir < 0) wantLat = by;
        else _bypass = null;

        float togo = (toS - s) * dir;
        float v = Mathf.Abs(u);
        float look = reverse ? Mathf.Clamp(v * 0.8f + 3.5f, 3.5f, 7f) : Mathf.Clamp(v * 0.9f + 4f, 5f, 16f);
        float aimS = s + dir * look;
        var aim = PointAt(Road, aimS) + TangentAt(Road, aimS).Cross(Vector3.Up) * wantLat;
        var nose = Nose(Me.Motion.Yaw);
        var move = reverse ? -nose : nose;
        float ang = RaceRoute.SignedAngle(move, RaceRoute.Flat(aim - pos));
        float steer = reverse ? Mathf.Clamp(ang * 1.8f, -1f, 1f)
                              : Mathf.Clamp(-ang * 2.2f * Mathf.Clamp(8f / Mathf.Max(v, 1f), 0.4f, 1f), -1f, 1f);

        float want = Mathf.Min(vmax, Mathf.Sqrt(2f * decel * Mathf.Max(togo - 0.1f, 0f)));
        if (togo < 0.15f) want = 0f;

        // whatever is in the way along the lane: stop 5.5 m short of it
        float cap = float.MaxValue;
        Body? blocker = null;
        foreach (var b in others)
        {
            var (bs, bl) = Frame(Road, b.Position, _near);
            float along = (bs - s) * dir;
            if (along < 0.5f || along > 40f) continue;
            if (Mathf.Abs(bl - wantLat) > 2.3f && Mathf.Abs(bl - lat) > 2.3f) continue;
            // a car it is closing on only needs to be followed; a clear line ahead just crawls past
            // one that is turning round (moving across the road, or back at it) gets room to finish
            float with = b.Velocity.Dot(TangentAt(Road, bs) * dir);
            bool turning = b.Velocity.Length() > 0.5f && with < 0.8f * b.Velocity.Length();
            // and a parked player leaves room to steer round
            float gap = along - (turning ? 12f : !b.Npc && b.Velocity.Length() < 0.5f ? 9f : 5.5f);
            float c = Mathf.Sqrt(2f * 3f * Mathf.Max(gap, 0f)) + (Mathf.Abs(bl - wantLat) > 2.3f ? 1.5f : 0f);
            if (c < cap) { cap = c; blocker = b; }
        }
        want = Mathf.Min(want, cap);
        _blocked = cap < 0.3f && want < 0.3f && togo > 1f ? _blocked + dt : 0f;
        if (bypass && _blocked > 1.5f && blocker is { } q && !q.Npc && q.Velocity.Length() < 0.5f)
        {
            // parked in the way (a player at the start, a wreck): round it, if the road has room
            var (qs, ql) = Frame(Road, q.Position, _near);
            float room = half - 1.0f;
            float left = ql - 2.9f, right = ql + 2.9f;
            float pick = Mathf.Abs(left) <= room && (Mathf.Abs(right) > room || Mathf.Abs(left - lat) < Mathf.Abs(right - lat)) ? left
                : Mathf.Abs(right) <= room ? right : float.NaN;
            if (!float.IsNaN(pick))
            {
                _bypass = pick;
                _bypassUntil = qs + dir * 7f;
                _blocked = 0;
                Log?.Invoke($"going round a parked {(q.Npc ? "NPC" : "player")} at {pick:F1} m");
            }
        }
        return Pedals(car, reverse ? -want : want, u, steer);
    }

    /// <summary>
    /// Pedals for a signed target speed (− is reversing). Above walking pace the brake stops it;
    /// below, the handbrake does — at a standstill the brake pedal selects reverse and then drives
    /// it, and in reverse the throttle is the brake.
    /// </summary>
    private static RideInput Pedals(Car car, float want, float u, float steer)
    {
        if (Mathf.Abs(want) < 0.05f)
        {
            if (u > 1.2f && car.Gear > 0) return new RideInput(0f, Mathf.Clamp(u * 0.35f, 0.3f, 1f), steer, false);
            if (u < -1.2f && car.Gear < 0) return new RideInput(Mathf.Clamp(-u * 0.35f, 0.3f, 1f), 0f, steer, false);
            return Handbrake(steer);
        }
        if (want > 0)
        {
            if (u < -0.6f) return Handbrake(steer);   // still rolling back: stop first
            float err = want - u;
            if (err < -1f && u > 1.2f) return new RideInput(0f, Mathf.Clamp(-err * 0.35f, 0f, 1f), steer, false);
            if (err < -0.3f) return u > 1.2f ? new RideInput(0f, 0f, steer, false) : Handbrake(steer);
            // generous at a crawl: on full lock the fronts scrub (the tyre model sees their slip
            // angle at walking pace), and 0.45 of throttle did not move the car at all
            float th = Mathf.Clamp(err * 0.5f + 0.15f + 0.35f * Mathf.Abs(steer), 0f, 1f);
            if (car.Gear < 0) th = Mathf.Max(th, 0.35f);   // selects first
            return new RideInput(th, 0f, steer, false);
        }
        float back = -u, wantBack = -want;
        if (back < -0.6f) return Handbrake(steer);   // still rolling forward: stop first
        float e = wantBack - back;
        if (e < -1f && back > 1.2f && car.Gear < 0) return new RideInput(Mathf.Clamp(-e * 0.35f, 0f, 1f), 0f, steer, false);
        if (e < -0.3f) return back > 1.2f ? new RideInput(0f, 0f, steer, false) : Handbrake(steer);
        float pedal = Mathf.Clamp(e * 0.5f + 0.15f + 0.35f * Mathf.Abs(steer), 0f, 1f);
        if (car.Gear > 0) pedal = Mathf.Max(pedal, 0.35f);   // selects reverse
        return new RideInput(0f, pedal, steer, false);
    }
}
