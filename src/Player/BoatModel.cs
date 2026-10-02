using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

// The boat model (#302): plain C# over Godot's managed maths only (no engine calls), so it is
// tier-0 tested (tests/UnitSportSwitzerland.Tests/BoatTests.cs) on the real wave spectrum.
//
// Frames. The HULL frame is a boat's own: X to starboard (right), Y up, -Z to the bow, its origin on
// the keel straight under the centre of mass. The body that carries a boat in the world (a
// FootPlayer's capsule, a parked VehicleBody's box) stands upright and yaw-only with its origin at
// that keel point at rest; the hull's attitude (pitch, roll, a flip) is drawn about the centre of
// mass (Boat.Pose), as a Flyer's is.

/// <summary>
/// One column of a hull: a vertical prism standing on <see cref="Foot"/> (hull frame, the bottom of
/// the hull there), <see cref="Width"/> across, <see cref="Length"/> fore and aft and
/// <see cref="Height"/> up to the gunwale or deck. A hull is a set of these (the "coarse voxel set"
/// of #302): buoyancy is the water each one displaces, the slam and the sideways bite are each
/// one's own, so a wave under the bow lifts the bow. Data, so a 70 m paddle steamer (#303) is more
/// columns, not new code.
/// </summary>
public readonly record struct HullColumn(Vector3 Foot, float Width, float Length, float Height)
{
    public float Area => Width * Length;
    public float Volume => Area * Height;
}

/// <summary>What pushes a boat: a propeller with a rudder behind it, or a steerable water jet.</summary>
public enum BoatDrive { Propeller, Jet }

/// <summary>
/// A boat as numbers: its hull, its mass, its drive and how it planes. Everything the model does
/// comes from here (#302); the speedboat, the jetski and later the steamer (#303) are three of these.
/// </summary>
public sealed record BoatSpec
{
    public string Name { get; init; } = "";
    /// <summary>Overall length, beam and depth (keel to gunwale amidships), m.</summary>
    public float Length { get; init; }
    public float Beam { get; init; }
    public float Depth { get; init; }
    /// <summary>Ready to go with one person aboard, kg.</summary>
    public float Mass { get; init; }
    /// <summary>The centre of mass above the keel, m: the hull frame's origin is the keel under it.</summary>
    public float CentreHeight { get; init; }
    /// <summary>Moments of inertia about the centre of mass, kg m², about X (pitch), Y (yaw), Z (roll). Zero: from the box.</summary>
    public Vector3 Inertia { get; init; }
    public HullColumn[] Columns { get; init; } = System.Array.Empty<HullColumn>();

    public BoatDrive Drive { get; init; }
    /// <summary>Engine power at the shaft, kW, and the share of it the prop or jet turns into thrust at speed.</summary>
    public float PowerKw { get; init; }
    public float Efficiency { get; init; } = 0.55f;
    /// <summary>Thrust at a standstill, full throttle, N (power over speed caps it above a few m/s).</summary>
    public float StaticThrust { get; init; }
    /// <summary>Reverse thrust as a share of forward.</summary>
    public float ReverseShare { get; init; } = 0.4f;
    /// <summary>Where the prop or the jet's nozzle pushes, hull frame: at the stern, under water.</summary>
    public Vector3 ThrustAt { get; init; }
    /// <summary>The prop disc's (or the jet's intake's) area, m²: its wash over the rudder.</summary>
    public float DiscArea { get; init; } = 0.1f;
    /// <summary>Full helm, rad (rudder or nozzle), and how fast the helm gets there, rad/s.</summary>
    public float MaxSteer { get; init; } = 0.5f;
    public float SteerRate { get; init; } = 2.5f;
    /// <summary>A propeller boat's rudder, m²: it works with the flow past it (speed and prop wash), nothing else.</summary>
    public float RudderArea { get; init; }

    /// <summary>
    /// Displacement versus planing. Below <see cref="HumpSpeed"/> the hull pushes water (its drag
    /// peaks there: the hump, bow up); by <see cref="PlaneSpeed"/> the bottom's lift carries
    /// <see cref="LiftShare"/> of the weight and the hull rides up onto the plane. A displacement
    /// ship (the steamer) has no lift.
    /// </summary>
    public float HumpSpeed { get; init; } = 5f;
    public float PlaneSpeed { get; init; } = 9f;
    public float LiftShare { get; init; }
    /// <summary>Drag at the hump as a share of the weight.</summary>
    public float HumpDrag { get; init; } = 0.2f;
    /// <summary>
    /// Bow-up trim at the hump and on the plane, rad: the hull's own wave system (the bow wave up
    /// forward, the stern down in its trough) tilts the water it floats on by this much, so the
    /// trim comes out of the buoyancy and the lift, not from a torque that fights them.
    /// </summary>
    public float HumpTrim { get; init; }
    public float PlaneTrim { get; init; }
    /// <summary>A fixed fin at the stern (a skeg, a jet's pump housing and ride plate), m²: it keeps the stern behind the bow.</summary>
    public float SkegArea { get; init; }
    /// <summary>Top speed in flat calm at full throttle, m/s: the drag is calibrated to it.</summary>
    public float TopSpeed { get; init; } = 15f;

    /// <summary>How hard the hull resists going sideways (its keel and chines): a drag coefficient on its side area.</summary>
    public float LateralDrag { get; init; } = 1.2f;
    /// <summary>Heave, pitch and roll damping: linear (× ρ per m² of column) and the slam's quadratic coefficient.</summary>
    public float HeaveDamping { get; init; } = 0.8f;
    public float SlamDrag { get; init; } = 0.6f;
    /// <summary>Leaning into a turn: the bank per g of turn and its limit, rad.</summary>
    public float BankPerG { get; init; } = 0.5f;
    public float MaxBank { get; init; } = 0.35f;
    /// <summary>The side the wind catches, m² (#304: wind and sails).</summary>
    public float WindArea { get; init; }

    public float IdleRpm { get; init; } = 700f;
    public float MaxRpm { get; init; } = 5000f;
    /// <summary>How fast the engine follows the lever, 1/s.</summary>
    public float SpoolRate { get; init; } = 2.5f;

    /// <summary>
    /// A sit-on machine (the jetski) throws its rider off a landing harder than <see cref="ThrowLanding"/>
    /// m/s into the water, a crooked one (<see cref="ThrowTilt"/> rad off upright) faster than a
    /// third of it, or when it rolls past <see cref="FlipAngle"/>. A boat you sit in keeps you.
    /// </summary>
    public bool ThrowsRider { get; init; }
    public float ThrowLanding { get; init; } = 7f;
    public float ThrowTilt { get; init; } = 0.75f;
    public float FlipAngle { get; init; } = 1.6f;

    // ---- derived, once --------------------------------------------------------------------

    public const float Rho = 1000f, RhoAir = 1.2f, G = 9.81f;

    /// <summary>The water it displaces floating at rest, m³.</summary>
    public float RestVolume => Mass / Rho;

    public float Weight => Mass * G;

    /// <summary>The hull frame's centre of mass.</summary>
    public Vector3 Centre => new(0, CentreHeight, 0);

    public Vector3 InertiaOrBox => Inertia != Vector3.Zero ? Inertia : new Vector3(
        Mass * (Length * Length + Depth * Depth) / 12f * 0.6f,
        Mass * (Length * Length + Beam * Beam) / 12f * 0.6f,
        Mass * (Beam * Beam + Depth * Depth) / 12f * 0.6f);

    /// <summary>
    /// The hump in the drag: x² e^(1−x²) of the speed over <see cref="HumpSpeed"/>, 0 at rest, 1 at
    /// the hump, gone by three times it.
    /// </summary>
    public static float Hump(float x) => x * x * Mathf.Exp(1f - x * x);

    /// <summary>How far onto the plane at a forward water speed: 0 displacement .. 1 planing.</summary>
    public float Planing(float u) => LiftShare <= 0f ? 0f : Mathf.SmoothStep(HumpSpeed, PlaneSpeed, u);

    private float _drag2 = float.NaN;

    /// <summary>
    /// The quadratic drag coefficient, N/(m/s)², solved so that full thrust at <see cref="TopSpeed"/>
    /// equals the drag there: the top speed is a published figure, the drag is not.
    /// </summary>
    public float Drag2
    {
        get
        {
            if (!float.IsNaN(_drag2)) return _drag2;
            float v = Mathf.Max(TopSpeed, 1f);
            float thrust = Mathf.Min(StaticThrust, PowerKw * 1000f * Efficiency / v);
            // the air takes its share of the thrust too (WindArea, the same term the step applies)
            // and the plane's lift, normal to a bottom trimmed bow-up, leans back against it
            float rest = Weight * HumpDrag * Hump(v / HumpSpeed) + Drag1 * v + 0.5f * RhoAir * WindArea * v * v
                + Weight * LiftShare * Planing(v) * Mathf.Sin(PlaneTrim);
            return _drag2 = Mathf.Max(1f, (thrust - rest) / (v * v));
        }
    }

    /// <summary>A little drag linear in speed, so a slow drift dies out instead of lasting for ever.</summary>
    public float Drag1 => Mass * 0.05f;

    /// <summary>The water's drag on the hull at a forward water speed (either way), N.</summary>
    public float Resistance(float u)
    {
        float a = Mathf.Abs(u);
        return Weight * HumpDrag * Hump(a / HumpSpeed) + Drag1 * a + Drag2 * a * a;
    }

    /// <summary>Thrust at full throttle at forward water speed u, N: the static figure, capped by the power.</summary>
    public float FullThrust(float u) => Mathf.Min(StaticThrust, PowerKw * 1000f * Efficiency / Mathf.Max(Mathf.Abs(u), 1f));

    private float _rollStiffness = float.NaN;

    /// <summary>
    /// The hull's hydrostatic roll stiffness at rest, N·m/rad: ρ g times the columns' second moment
    /// of area about the centreline. The bank into a turn is a torque this size times the bank wanted.
    /// </summary>
    public float RollStiffness
    {
        get
        {
            if (!float.IsNaN(_rollStiffness)) return _rollStiffness;
            float i = 0f;
            foreach (var c in Columns) i += c.Area * c.Foot.X * c.Foot.X;
            return _rollStiffness = Mathf.Max(Rho * G * i, 1f);
        }
    }

    /// <summary>The hull's lines (a planing hull's), shared by its columns and its drawn mesh.</summary>
    public HullShape Shape { get; init; }

    /// <summary>
    /// Columns for a planing hull of these dimensions and <paramref name="shape"/>:
    /// <see cref="HullShape.Stations"/> fore and aft by <see cref="HullShape.Across"/> athwart.
    /// </summary>
    public static HullColumn[] PlaningHull(float length, float beam, float depth, HullShape shape)
    {
        int stations = shape.Stations, across = shape.Across;
        var cols = new HullColumn[stations * across];
        float dz = length / stations;
        int n = 0;
        for (int k = 0; k < stations; k++)
        {
            float t = (k + 0.5f) / stations;          // 0 at the transom .. 1 at the stem
            float z = shape.SternZ - (k + 0.5f) * dz;
            float half = beam * 0.5f * HalfBeam(t);
            float keel = shape.Keel(t);
            float top = shape.Sheer(depth, t);
            for (int j = 0; j < across; j++)
            {
                float x = (-1f + (2f * j + 1f) / across) * half;
                float foot = keel + shape.Deadrise * Mathf.Abs(x) / (beam * 0.5f);
                cols[n++] = new HullColumn(new Vector3(x, foot, z), 2f * half / across, dz, Mathf.Max(0.1f, top - foot));
            }
        }
        return cols;
    }

    /// <summary>The half-beam at a station as a share of the widest: full aft, a rounded taper to the bow.</summary>
    public static float HalfBeam(float t) => t <= 0.5f ? 1f - 0.06f * (0.5f - t) / 0.5f
        : Mathf.Sqrt(Mathf.Max(0.02f, 1f - 0.85f * Mathf.Pow((t - 0.5f) / 0.5f, 2f)));
}

/// <summary>
/// A planing hull's lines: the transom <see cref="SternZ"/> behind the centre of mass (hull frame
/// +Z), a vee bottom <see cref="Deadrise"/> m up at the chine, the keel sweeping up
/// <see cref="BowRise"/> m to the stem over the forward 45 %, the sheer rising <see cref="SheerRise"/>
/// m to the bow; the beam full aft and tapering forward (<see cref="BoatSpec.HalfBeam"/>). The
/// columns the boat floats on and the mesh it is drawn as are both made from these, so what is
/// drawn is what floats.
/// </summary>
public readonly record struct HullShape(float SternZ, float Deadrise, float BowRise, float SheerRise, int Stations, int Across)
{
    /// <summary>The keel's height above the lowest point at <paramref name="t"/> (0 transom .. 1 stem).</summary>
    public float Keel(float t) => t > 0.55f ? BowRise * Mathf.Pow((t - 0.55f) / 0.45f, 2f) : 0f;

    /// <summary>The gunwale's height at <paramref name="t"/>.</summary>
    public float Sheer(float depth, float t) => depth + SheerRise * t;
}

/// <summary>The helm as the boat sees it: throttle forward, throttle astern, steer (−1 port .. +1 starboard).</summary>
public readonly record struct BoatControls(float Throttle, float Reverse, float Steer);

/// <summary>What a boat stands in: the water and the ground under it, and the wind (#304).</summary>
public interface IBoatWater
{
    /// <summary>The surface's altitude over (x, z) now, waves included, and the water's velocity there; false where dry.</summary>
    bool Surface(float x, float z, out float level, out Vector3 flow);

    /// <summary>The ground's altitude under (x, z) (the lake bed, a beach); NaN where unknown.</summary>
    float Bed(float x, float z);

    /// <summary>The wind, m/s, world. Zero until #304 brings wind.</summary>
    Vector3 Wind { get; }
}

/// <summary>What a step of the boat asks its rider to do.</summary>
public enum BoatEvent { None, Thrown }

/// <summary>
/// A boat's state between steps: a rigid body in full 3D, like a <see cref="FlightMotion"/> but
/// with its angular velocity (waves torque it, inertia carries it on).
/// </summary>
public struct BoatState
{
    /// <summary>The centre of mass, world.</summary>
    public Vector3 Position;
    public Vector3 Velocity;
    /// <summary>Angular velocity, world, rad/s.</summary>
    public Vector3 Spin;
    /// <summary>Hull frame to world.</summary>
    public Quaternion Attitude;
    /// <summary>The engine, 0 idle .. 1 full, and the lever's sign (1 ahead, −1 astern).</summary>
    public float Rpm01;
    public int Gear;
    /// <summary>Rudder or nozzle now, rad, + to starboard.</summary>
    public float Helm;
    /// <summary>Water displaced over the water displaced at rest: 1 floating still, ~0.3 on the plane, 0 in the air.</summary>
    public float Immersion;
    /// <summary>0..1, how much the hull has the water: what drag, lift and steering scale with.</summary>
    public float Wet;
    /// <summary>Seconds out of the water (a jump), 0 in it.</summary>
    public float Airborne;
    /// <summary>Forward speed through the water, m/s (negative astern).</summary>
    public float WaterSpeed;
    /// <summary>The thrust pushing now, N, and whether the prop or intake had water.</summary>
    public float Thrust;
    public bool Biting;
    /// <summary>Some of the hull rests on the ground (beached, aground).</summary>
    public bool Grounded;
    /// <summary>The last landing's speed into the water, m/s, and seconds since the hull went past <see cref="BoatSpec.FlipAngle"/>.</summary>
    public float LastLanding;
    public float Capsized;
    /// <summary>Each column's metres under water last step, for the slam (water rising up it).</summary>
    public float[]? Submerged;

    public static BoatState At(Vector3 centre, float yaw, Vector3 velocity) => new()
    {
        Position = centre,
        Velocity = velocity,
        Attitude = new Quaternion(Vector3.Up, yaw),
        Immersion = 1f,
        Wet = 1f,
    };

    public readonly Basis Basis => new(Attitude);
    /// <summary>Heading about +Y (Godot's yaw: 0 faces −Z), from the bow's direction; held when the bow points straight up or down.</summary>
    public readonly float Yaw(float fallback)
    {
        var f = -Basis.Z;
        return f.X * f.X + f.Z * f.Z > 1e-4f ? Mathf.Atan2(-f.X, -f.Z) : fallback;
    }

    /// <summary>The bow's angle above the horizon, rad.</summary>
    public readonly float Pitch => Mathf.Asin(Mathf.Clamp(-Basis.Z.Y, -1f, 1f));

    /// <summary>Roll, rad, + with the starboard side down.</summary>
    public readonly float Roll => -Mathf.Asin(Mathf.Clamp(Basis.X.Y, -1f, 1f));
}

/// <summary>The boat model's step (#302). See <see cref="BoatSpec"/> for the numbers and the frames.</summary>
public static class BoatDynamics
{
    private const float Rho = BoatSpec.Rho, G = BoatSpec.G;

    /// <summary>A hull on gravel or sand, sliding: its friction coefficient.</summary>
    public const float GroundFriction = 0.5f;

    /// <summary>
    /// The body that carries the boat stands on the ground (its keel is on a beach, a slipway):
    /// the hull drags on it, slowing along the ground at μ g whatever the hull's columns felt.
    /// </summary>
    public static void Beached(ref BoatState b, float dt)
    {
        var flat = new Vector3(b.Velocity.X, 0, b.Velocity.Z).MoveToward(Vector3.Zero, GroundFriction * G * dt);
        b.Velocity = new Vector3(flat.X, Mathf.Min(b.Velocity.Y, 0f), flat.Z);
        b.Spin *= Mathf.Exp(-3f * dt);
        b.Grounded = true;
    }

    /// <summary>
    /// Advances a boat by <paramref name="dt"/> in <paramref name="substeps"/> steps: buoyancy and
    /// slam per column against the water's surface, the hull's sideways bite, the drag along it,
    /// planing lift and trim, thrust and steering (a rudder only with flow past it, a jet only with
    /// thrust), leaning into a turn, the ground where it touches, the wind, gravity. Returns
    /// <see cref="BoatEvent.Thrown"/> when a sit-on machine throws its rider (a hard landing, a flip).
    /// </summary>
    public static BoatEvent Step(BoatSpec s, ref BoatState b, in BoatControls c, IBoatWater water, float dt, int substeps = 2)
    {
        if (b.Attitude == default) b.Attitude = Quaternion.Identity;
        int n = s.Columns.Length;
        bool fresh = b.Submerged == null || b.Submerged.Length != n;
        if (fresh) b.Submerged = new float[n];
        var ev = BoatEvent.None;
        float h = dt / Mathf.Max(1, substeps);
        for (int k = 0; k < Mathf.Max(1, substeps); k++)
        {
            if (Sub(s, ref b, c, water, h, fresh && k == 0)) ev = BoatEvent.Thrown;
        }
        return ev;
    }

    private static bool Sub(BoatSpec s, ref BoatState b, in BoatControls c, IBoatWater water, float h, bool fresh)
    {
        var R = b.Basis.Orthonormalized();
        var right = R.X;
        var up = R.Y;
        var fwd = -R.Z;
        var P = b.Position;
        var V = b.Velocity;
        var W = b.Spin;
        float weight = s.Weight;
        int n = s.Columns.Length;

        var force = new Vector3(0, -weight, 0);
        var torque = Vector3.Zero;
        float displaced = 0f;
        var flowSum = Vector3.Zero;
        bool grounded = false;
        float slamIn = 0f;
        // the ground's contact stiffness: each column's share of the weight pushes it 4 cm in
        float kGround = weight / Mathf.Max(1, n) / 0.04f;
        // the hull's own waves: the water under it tilts bow-up over the hump and on the plane
        float trimSlope = s.HumpTrim * BoatSpec.Hump(Mathf.Max(b.WaterSpeed, 0f) / s.HumpSpeed) + s.PlaneTrim * s.Planing(b.WaterSpeed);
        float cGround = 2f * Mathf.Sqrt(kGround * s.Mass / Mathf.Max(1, n));

        for (int i = 0; i < n; i++)
        {
            var col = s.Columns[i];
            var foot = P + R * (col.Foot - s.Centre);
            var top = foot + up * col.Height;
            float sub = 0f;
            var flow = Vector3.Zero;
            var cb = foot;
            if (water.Surface(foot.X, foot.Z, out float level, out flow))
            {
                level -= trimSlope * col.Foot.Z * b.Wet;
                bool footLow = foot.Y <= top.Y;
                float lo = footLow ? foot.Y : top.Y, hi = footLow ? top.Y : foot.Y, span = hi - lo;
                float frac = span > 1e-3f ? Mathf.Clamp((level - lo) / span, 0f, 1f) : level > lo ? 1f : 0f;
                sub = frac * col.Height;
                if (frac > 0f)
                {
                    var lower = footLow ? foot : top;
                    var upper = footLow ? top : foot;
                    cb = lower + (upper - lower) * (frac * 0.5f);
                }
            }
            float before = fresh ? sub : b.Submerged![i];
            b.Submerged![i] = sub;
            if (sub > 0f)
            {
                float vol = col.Area * sub;
                displaced += vol;
                flowSum += flow * vol;
                var r = cb - P;
                var rel = V + W.Cross(r) - flow;
                // buoyancy: the water it displaces, straight up
                var f = new Vector3(0, Rho * G * vol, 0);
                // the water rising up the column (the hull dropping into it, a wave face met at
                // speed) pushes it out; falling away it pulls a little: heave, pitch and roll damping,
                // and the slam of a landing
                // (capped: a hull set down or turned by hand would otherwise "slam" at 60 m/s)
                float rate = Mathf.Clamp((sub - before) / h, -10f, 10f);
                float slam = rate > 0f ? rate * rate : -0.3f * rate * rate;
                f += up * (col.Area * (Rho * s.HeaveDamping * rate + 0.5f * Rho * s.SlamDrag * slam));
                if (rate > slamIn) slamIn = rate;
                // the hull's side bites the water: it goes where it points
                float lat = rel.Dot(right);
                float side = sub * col.Length;
                f -= right * (0.5f * Rho * s.LateralDrag * side * lat * Mathf.Abs(lat) + Rho * 0.3f * side * lat);
                force += f;
                torque += r.Cross(f);
            }

            // the ground: a stiff damped contact under the column's foot, and friction along it
            float bed = water.Bed(foot.X, foot.Z);
            if (!float.IsNaN(bed) && foot.Y < bed)
            {
                grounded = true;
                var r = foot - P;
                var vp = V + W.Cross(r);
                float normal = Mathf.Max(0f, kGround * (bed - foot.Y) - cGround * Mathf.Min(vp.Y, 0f));
                var f = new Vector3(0, normal, 0);
                var slide = new Vector3(vp.X, 0, vp.Z);
                float sp = slide.Length();
                // gravel and sand hold a hull: never more than stops its share of the boat this step
                if (sp > 1e-4f) f -= slide / sp * Mathf.Min(0.6f * normal, sp * s.Mass / Mathf.Max(1, n) / h);
                force += f;
                torque += r.Cross(f);
            }
        }

        float immersion = displaced / s.RestVolume;
        float wet = Mathf.Clamp(immersion / 0.12f, 0f, 1f);
        var flowMean = displaced > 1e-6f ? flowSum / displaced : Vector3.Zero;
        float u = (V - flowMean).Dot(fwd);

        // the drag along the hull, the hump in it, and the lift of the plane
        if (wet > 0f)
        {
            force -= fwd * (Mathf.Sign(u) * s.Resistance(u) * wet);
            float plane = s.Planing(u);
            // The bottom's dynamic lift, shared over the columns by the water each displaces: it
            // acts where the hull meets the water, so a bow dipping into a wave meets more of it
            // and is pushed back up (lift at the centre of mass alone left the plane with no pitch
            // stiffness, and the bow dug in).
            float lift = weight * s.LiftShare * plane * wet;
            if (lift > 0f && displaced > 1e-6f)
                for (int i = 0; i < n; i++)
                {
                    float sub = b.Submerged![i];
                    if (sub <= 0f) continue;
                    var col = s.Columns[i];
                    var foot = P + R * (col.Foot - s.Centre);
                    // normal to the bottom: banked into a turn it pulls the boat round, like a carving ski
                    var f = up * (lift * col.Area * sub / displaced);
                    force += f;
                    torque += (foot - P).Cross(f);
                }
            // Leaning into a turn: the rider's weight and the vee's bite bank the hull toward the
            // inside (− yaw rate is a turn to starboard), held as a damped spring on the roll
            // so neither the hull's outward heel nor an over-bank wins.
            float yawRate = W.Dot(up);
            float bank = Mathf.Clamp(s.BankPerG * Mathf.Max(u, 0f) * -yawRate / G, -s.MaxBank, s.MaxBank);
            float roll = -Mathf.Asin(Mathf.Clamp(right.Y, -1f, 1f));
            float rollRate = W.Dot(fwd);   // + starboard going down
            float k = s.RollStiffness * 1.5f;
            float d = 2f * Mathf.Sqrt(k * s.InertiaOrBox.Z) * 0.6f;
            if (Mathf.Abs(roll) < 1.2f) torque += fwd * ((k * (bank - roll) - d * rollRate) * wet);
        }

        // the engine follows the lever; ahead or astern
        float lever = c.Throttle > 0.02f ? c.Throttle : c.Reverse > 0.02f ? -c.Reverse : 0f;
        if (lever > 0f) b.Gear = 1;
        else if (lever < 0f) b.Gear = -1;
        var thrustAt = P + R * (s.ThrustAt - s.Centre);
        b.Biting = water.Surface(thrustAt.X, thrustAt.Z, out float atStern, out _) && thrustAt.Y < atStern + 0.05f;
        // out of the water the prop or the impeller spins free: the engine races
        float wantRpm = Mathf.Abs(lever) * (b.Biting ? 1f : 1.15f);
        b.Rpm01 += (wantRpm - b.Rpm01) * MathX.Damp(s.SpoolRate * (b.Biting ? 1f : 3f), h);
        float thrust = 0f;
        if (b.Biting && lever != 0f)
            thrust = s.FullThrust(u) * b.Rpm01 * (b.Gear < 0 ? -s.ReverseShare : 1f);
        b.Thrust = thrust;

        b.Helm = Mathf.MoveToward(b.Helm, Mathf.Clamp(c.Steer, -1f, 1f) * s.MaxSteer, s.SteerRate * h);
        var thrustDir = fwd;
        if (s.Drive == BoatDrive.Jet)
            // the nozzle turns the jet: no thrust, no steering
            thrustDir = (fwd * Mathf.Cos(b.Helm) - right * Mathf.Sin(b.Helm)).Normalized();
        if (thrust != 0f)
        {
            var f = thrustDir * thrust;
            force += f;
            torque += (thrustAt - P).Cross(f);
        }
        // The rudder and the skeg: fins at the stern, lifting with the water past them (the boat's
        // way and, over a rudder, the prop's wash) at their angle to it (the helm plus the stern's
        // sideslip). A jet has no rudder (its nozzle steers) but its pump housing is a skeg.
        float fin = s.SkegArea + (s.Drive == BoatDrive.Propeller ? s.RudderArea : 0f);
        if (fin > 0f && b.Biting)
        {
            var rs = thrustAt - P;
            var vs = V + W.Cross(rs) - flowMean;
            float us = vs.Dot(fwd), ls = vs.Dot(right);
            float slip = Mathf.Atan2(ls, Mathf.Abs(us) + 0.5f);
            float wash = s.Drive == BoatDrive.Propeller ? Mathf.Max(thrust, 0f) / (Rho * s.DiscArea) : 0f;
            float qs = us * us + wash;
            float rudder = s.Drive == BoatDrive.Propeller ? s.RudderArea * Mathf.Sign(us) * b.Helm : 0f;
            float lift = 0.5f * Rho * 3f * qs * (fin * Mathf.Sin(Mathf.Clamp(slip, -0.6f, 0.6f)) + rudder);
            var f = -right * lift - fwd * (Mathf.Abs(lift) * 0.1f * Mathf.Sign(us));
            force += f;
            torque += rs.Cross(f);
        }

        // the wind on what stands out of the water (#304), and air on the rest
        var air = water.Wind - V;
        force += air * (0.5f * BoatSpec.RhoAir * 1.0f * s.WindArea * air.Length());

        // ---- integrate: semi-implicit Euler, the rotation in the body frame ----
        V += force / s.Mass * h;
        var I = s.InertiaOrBox;
        var Rt = R.Transposed();
        var wb = Rt * W;
        var tb = Rt * torque;
        var Iw = new Vector3(I.X * wb.X, I.Y * wb.Y, I.Z * wb.Z);
        var dw = tb - wb.Cross(Iw);
        wb += new Vector3(dw.X / I.X, dw.Y / I.Y, dw.Z / I.Z) * h;
        // out of the water nothing but the air slows a tumble
        if (wet <= 0f) wb *= Mathf.Exp(-0.4f * h);
        if (wb.LengthSquared() > 400f) wb = wb.Normalized() * 20f;
        W = R * wb;
        P += V * h;
        var q = b.Attitude;
        q += q * new Quaternion(wb.X, wb.Y, wb.Z, 0f) * (0.5f * h);
        b.Attitude = q.Normalized();

        b.Position = P;
        b.Velocity = V;
        b.Spin = W;
        b.Immersion = immersion;
        b.Wet = wet;
        b.WaterSpeed = u;
        b.Grounded = grounded;

        // ---- the rider: a hard landing or a flip throws them off a sit-on machine ----
        bool thrown = false;
        if (wet <= 0f && !grounded) b.Airborne += h;
        else
        {
            if (b.Airborne > 0.25f)
            {
                // the speed it met the water at, and how crooked it came down
                b.LastLanding = Mathf.Max(slamIn, -V.Y);
                float tilt = Mathf.Acos(Mathf.Clamp(new Basis(b.Attitude).Y.Y, -1f, 1f));
                if (s.ThrowsRider && (b.LastLanding > s.ThrowLanding || (tilt > s.ThrowTilt && b.LastLanding > s.ThrowLanding / 3f)))
                    thrown = true;
            }
            b.Airborne = 0f;
        }
        float upY = new Basis(b.Attitude).Y.Y;
        b.Capsized = upY < Mathf.Cos(s.FlipAngle) ? b.Capsized + h : 0f;
        if (s.ThrowsRider && b.Capsized > 0.3f) thrown = true;
        return thrown;
    }
}
