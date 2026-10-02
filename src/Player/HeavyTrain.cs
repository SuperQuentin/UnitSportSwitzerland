using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>What the driver and the driveline ask of a <see cref="HeavyTrain"/> for one step.</summary>
public struct TrainControls
{
    /// <summary>Front road-wheel angle, rad, + left.</summary>
    public float Steer;
    /// <summary>Engine force at the driven wheels' contact patches, N, + forward.</summary>
    public float Drive;
    /// <summary>Retarder and engine brake at the driven wheels, N (a magnitude: it opposes the rolling).</summary>
    public float Retard;
    /// <summary>Service brake pressure in the chambers, 0..1.</summary>
    public float Brake;
    /// <summary>Spring brakes on: the drive axles and every trailer axle locked.</summary>
    public bool Spring;
    /// <summary>Extra braking on the trailing sections only, 0..1: Game's anti-jackknife stretch braking.</summary>
    public float Stretch;
    /// <summary>Peak tyre friction on the ground under the train.</summary>
    public float Grip;
    /// <summary>Rise per metre along the first section's heading, + uphill.</summary>
    public float Grade;
    public float Draft;
    public bool OnFloor;
    /// <summary>Share of each tyre's longitudinal grip ABS lets the brakes take (the rest stays for cornering).</summary>
    public float AbsLimit;
    /// <summary>Game: a yaw moment on the first section toward the steady turn the wheels ask for, N·m per rad/s of excess.</summary>
    public float YawAssist;
    /// <summary>Game: damping on a pivot while it folds at speed, N·m·s/rad (an anti-jackknife damper).</summary>
    public float FoldDamping;
}

/// <summary>A contact a section's own body found in the world, fed back into the next step.</summary>
/// <param name="Section">Which section.</param>
/// <param name="Lever">From that section's centre of mass to the contact, in the train's frame, m.</param>
/// <param name="Normal">Out of the obstacle, in the train's frame (unit).</param>
public readonly record struct TrainContact(int Section, Vector2 Lever, Vector2 Normal);

/// <summary>
/// A heavy vehicle and whatever it pulls, as planar rigid bodies joined by pins (#70): a tractor
/// and its semi-trailer on the fifth wheel, a rigid truck, the dolly on its drawbar and the body on
/// the dolly's turntable, the two halves of an articulated bus on their damped joint.
///
/// <para>
/// Each section is a body with its own mass, yaw inertia and axles; every axle makes its own
/// tyre force from its own slip angle (the car's curve, <c>sin(C·atan(B·α))</c>, with a truck
/// tyre's shallower slope), its own friction circle and its own share of the brakes. Nothing about
/// a train is scripted: the trailer's axles cutting inside the tractor's path (off-tracking), a
/// drive axle that loses its side grip under a retarder on snow and lets the trailer fold it round
/// (jackknife), a reversing trailer that runs away to one side — all of it is the pins and the
/// tyres. A tri-axle's tyres scrub in a tight turn because three fixed axles cannot all roll round
/// one centre, which is also true.
/// </para>
///
/// <para>
/// The pins are solved as velocity constraints (sequential impulses, a 2x2 block per pin, a few
/// iterations) with a little positional correction, plus a one-sided stop at each pivot's
/// articulation limit (a semi's front corner meeting the cab). Contacts the sections' own collision
/// bodies found last frame go into the same solve, so a trailer jammed against a bollard holds the
/// tractor back through the kingpin.
/// </para>
///
/// <para>
/// The frame is the first section's at the start of the step: origin at its centre of mass,
/// x forward, y to the left, yaw + to the left — the car's (u, w, r). Positions of the trailing
/// sections are rebuilt from the articulation angles at every step, so they never drift off the pins.
/// </para>
/// </summary>
public sealed class HeavyTrain
{
    public const float Gravity = 9.81f;
    private const float AirDensity = 1.2f;
    /// <summary>Truck tyres roll easier than a car's.</summary>
    private const float RollingResistance = 0.006f;
    /// <summary>Tyre curve, sin(C·atan(B·α)): a truck tyre's peak comes later and its slope is shallower than a car's.</summary>
    private const float TyreB = 8f, SimTyreC = 1.45f, ArcadeTyreC = 1.35f;
    private const int Iterations = 12;
    /// <summary>Share of a pin's position error corrected per substep.</summary>
    private const float Baumgarte = 0.2f;

    public sealed class Body
    {
        public readonly SectionSpec Spec;
        public float Payload;
        public float Mass, Inertia, CgAt, CgHeight;
        /// <summary>Where the pin to the section ahead and the hitch for the one behind are, + forward of the CG, m.</summary>
        public float PivotZ, HitchZ;
        public readonly float[] AxleZ, StaticLoad, Load;
        /// <summary>Per axle, last step: forward speed at the axle (for the wheels' spin) and how hard it slid, 0..1.</summary>
        public readonly float[] AxleSpeed, AxleSlide;
        /// <summary>Per axle, last substep: side force, N, + left, and slip angle, rad — a steering wheel's feel.</summary>
        public readonly float[] AxleFy, AxleAlpha;
        public Vector2 P, V;
        public float Psi, W;
        /// <summary>Acceleration of the centre of mass in the body's frame, last step (+x forward, +y left), m/s².</summary>
        public Vector2 Accel;
        /// <summary>The liquid's free surface: a mass on a spring in the body's frame.</summary>
        public float SloshMass;
        public Vector2 Slosh, SloshVel;
        /// <summary>Lateral acceleration it can take before it rolls, in g (static rollover threshold).</summary>
        public float Srt;
        /// <summary>Lateral acceleration as the body's roll has followed it, g.</summary>
        public float RollLat;

        public Body(SectionSpec spec, float payload)
        {
            Spec = spec;
            AxleZ = new float[spec.Axles.Length];
            StaticLoad = new float[spec.Axles.Length];
            Load = new float[spec.Axles.Length];
            AxleSpeed = new float[spec.Axles.Length];
            AxleSlide = new float[spec.Axles.Length];
            AxleFy = new float[spec.Axles.Length];
            AxleAlpha = new float[spec.Axles.Length];
            SetPayload(payload);
        }

        public void SetPayload(float kg)
        {
            Payload = Mathf.Clamp(kg, 0f, Spec.PayloadMax);
            Mass = Spec.Mass + Payload;
            CgAt = (Spec.Mass * Spec.CgAt + Payload * Spec.PayloadAt) / Mass;
            CgHeight = (Spec.Mass * Spec.CgHeight + Payload * Spec.PayloadHeight) / Mass;
            // a box of the section's size, the load spread along the floor like the body
            Inertia = Mass * (Spec.Length * Spec.Length + Spec.Width * Spec.Width) / 12f;
            PivotZ = float.IsNaN(Spec.PivotAt) ? 0f : CgAt - Spec.PivotAt;
            HitchZ = float.IsNaN(Spec.HitchAt) ? 0f : CgAt - Spec.HitchAt;
            for (int i = 0; i < AxleZ.Length; i++) AxleZ[i] = CgAt - Spec.Axles[i].At;
            // a part-full tank sloshes most; full or empty, the liquid has nowhere to go
            float fill = Spec.PayloadMax > 0f ? Payload / Spec.PayloadMax : 0f;
            SloshMass = Spec.Liquid ? Payload * 0.8f * 4f * fill * (1f - fill) : 0f;
            // how far the outer wheels are from the CG over how high it is, less what the springs
            // and tyres give up as the body rolls: ~0.35 g for a full tanker, ~0.7 g empty
            Srt = Spec.Track * 0.5f / Mathf.Max(CgHeight, 0.3f) * 0.78f;
        }

        public Vector2 Forward => new(Mathf.Cos(Psi), Mathf.Sin(Psi));
        public Vector2 Left => new(-Mathf.Sin(Psi), Mathf.Cos(Psi));
    }

    public readonly List<Body> Bodies = new();

    /// <summary>Contacts to honour in the next step (the sections' collision bodies, last frame).</summary>
    public readonly List<TrainContact> Contacts = new();

    /// <summary>How hard any tyre slid last step, 0..1, for the squeal.</summary>
    public float TyreSlide { get; private set; }
    /// <summary>The section whose lateral acceleration has been past its rollover threshold for a moment, or −1.</summary>
    public int Rolling { get; private set; } = -1;
    private float _overSrt;
    /// <summary>Last step's lateral acceleration against the threshold of the section nearest to rolling (1 = on the point of it).</summary>
    public float WorstRoll { get; private set; }

    /// <summary>Game's gentler tyre curve past the peak.</summary>
    public bool Arcade { get; set; }

    public int Count => Bodies.Count;

    /// <summary>The load on the driven axles, N: what the engine and the retarder have to grip with.</summary>
    public float DrivenLoad
    {
        get
        {
            float n = 0f;
            foreach (var b in Bodies)
                for (int i = 0; i < b.Load.Length; i++)
                    if (b.Spec.Axles[i].Driven) n += b.Load[i];
            return n;
        }
    }

    public void Clear() => Bodies.Clear();

    public void Add(SectionSpec spec, float payload) { Bodies.Add(new Body(spec, payload)); ComputeLoads(); }

    /// <summary>The train's mass, kg.</summary>
    public float Mass { get { float m = 0; foreach (var b in Bodies) m += b.Mass; return m; } }

    /// <summary>Does the pivot of this section carry weight (a fifth wheel, a turntable, a bus joint) or only pull (a drawbar)?</summary>
    private static bool Carries(Coupling c) => c is Coupling.FifthWheel or Coupling.Turntable or Coupling.BusJoint;

    /// <summary>
    /// Static axle loads, from the last section forward: each section rests on its axle groups and,
    /// if its pivot carries weight, on the section ahead, whose hitch then carries that share.
    /// </summary>
    public void ComputeLoads()
    {
        float passed = 0f;   // weight the section behind puts on this one's hitch, N
        for (int k = Bodies.Count - 1; k >= 0; k--)
        {
            var b = Bodies[k];
            var s = b.Spec;
            float weight = b.Mass * Gravity;
            float total = weight + passed;
            float moment = weight * b.CgAt + passed * (float.IsNaN(s.HitchAt) ? b.CgAt : s.HitchAt);   // about the front, N·m
            float at = moment / Mathf.Max(total, 1f);

            // the supports: the pivot (if it carries) and the axle groups, by their mean position
            float g0 = 0, g1 = 0; int n0 = 0, n1 = 0;
            foreach (var a in s.Axles) { if (a.Group == 0) { g0 += a.At; n0++; } else { g1 += a.At; n1++; } }
            bool pivot = k > 0 && Carries(s.Pivot);
            float front, rear;
            float frontAt, rearAt;
            if (pivot)
            {
                // pivot + all the axles as one group
                frontAt = s.PivotAt;
                rearAt = (g0 + g1) / Mathf.Max(1, n0 + n1);
            }
            else if (n0 > 0 && n1 > 0)
            {
                frontAt = g0 / n0;
                rearAt = g1 / n1;
            }
            else { frontAt = rearAt = (g0 + g1) / Mathf.Max(1, n0 + n1); }

            if (Mathf.Abs(rearAt - frontAt) < 0.05f) { front = 0f; rear = total; }
            else
            {
                front = total * (rearAt - at) / (rearAt - frontAt);
                rear = total - front;
            }
            front = Mathf.Max(front, 0.05f * total);
            rear = Mathf.Max(rear, 0.05f * total);

            if (pivot)
            {
                passed = front;
                int n = Mathf.Max(1, n0 + n1);
                for (int i = 0; i < s.Axles.Length; i++) b.StaticLoad[i] = rear / n;
            }
            else
            {
                passed = 0f;
                for (int i = 0; i < s.Axles.Length; i++)
                {
                    bool frontGroup = n0 > 0 && n1 > 0 ? s.Axles[i].Group == 0 : false;
                    b.StaticLoad[i] = n0 > 0 && n1 > 0
                        ? (frontGroup ? front / n0 : rear / n1)
                        : total / Mathf.Max(1, s.Axles.Length);
                }
            }
            System.Array.Copy(b.StaticLoad, b.Load, b.Load.Length);
        }
    }

    /// <summary>
    /// Lays the train out in the first section's frame from its articulation angles (section k's yaw
    /// minus section k−1's), and sets the velocities from the first section's (u forward, w left, r)
    /// and the articulation rates.
    /// </summary>
    public void Pose(float u, float w, float r, float[] gamma, float[] gammaRate)
    {
        if (Bodies.Count == 0) return;
        var b0 = Bodies[0];
        b0.P = Vector2.Zero;
        b0.Psi = 0f;
        b0.V = new Vector2(u, w);
        b0.W = r;
        for (int k = 1; k < Bodies.Count; k++)
        {
            var p = Bodies[k - 1];
            var c = Bodies[k];
            var joint = p.P + p.Forward * p.HitchZ;
            var jointVel = p.V + Perp(p.Forward * p.HitchZ) * p.W;
            c.Psi = p.Psi + (k - 1 < gamma.Length ? gamma[k - 1] : 0f);
            c.W = p.W + (k - 1 < gammaRate.Length ? gammaRate[k - 1] : 0f);
            var lever = -c.Forward * c.PivotZ;   // joint to CG
            c.P = joint + lever;
            c.V = jointVel + Perp(lever) * c.W;
        }
    }

    /// <summary>Articulation angles and rates after a step (k−1 is the pin in front of section k).</summary>
    public void Read(float[] gamma, float[] gammaRate)
    {
        for (int k = 1; k < Bodies.Count && k - 1 < gamma.Length; k++)
        {
            gamma[k - 1] = MathX.WrapAngle(Bodies[k].Psi - Bodies[k - 1].Psi);
            gammaRate[k - 1] = Bodies[k].W - Bodies[k - 1].W;
        }
    }

    /// <summary>Advances the whole train by <paramref name="dt"/> in <paramref name="substeps"/> substeps.</summary>
    public void Step(in TrainControls c, float dt, int substeps)
    {
        float h = dt / substeps;
        float slide = 0f;
        var before = new Vector2[Bodies.Count];
        for (int k = 0; k < Bodies.Count; k++) before[k] = Bodies[k].V;
        for (int s = 0; s < substeps; s++) slide = Mathf.Max(slide, Substep(c, h));
        TyreSlide = c.OnFloor ? slide : 0f;
        Contacts.Clear();

        // rollover: the lateral acceleration of each section's mass (the tyres' side forces plus
        // what the pin passes on) against what its track and CG height allow, for a moment
        int worst = -1;
        float worstOver = 0f;
        for (int k = 0; k < Bodies.Count; k++)
        {
            var b = Bodies[k];
            // the steady part of it, speed times yaw rate: the pin's jolts on a light tractor are not
            // what tips anything over
            float lat = Mathf.Abs(b.V.Dot(b.Forward) * b.W) / Gravity;
            // a body takes a moment to roll onto its outer springs: a flick of the wheel is not a
            // sustained lean (roll lag ~0.35 s)
            b.RollLat += (lat - b.RollLat) * (1f - Mathf.Exp(-dt / 0.35f));
            lat = b.RollLat;
            // the liquid thrown to the outside raises it further
            if (b.SloshMass > 0f) lat += b.SloshMass / b.Mass * Mathf.Abs(b.Slosh.Y) / Mathf.Max(b.CgHeight, 0.5f);
            float over = lat / Mathf.Max(b.Srt * (Arcade ? 1.6f : 1f), 0.05f);
            if (over > worstOver) { worstOver = over; worst = k; }
        }
        _overSrt = c.OnFloor && worstOver > 1f ? _overSrt + dt : 0f;
        Rolling = _overSrt > 0.3f ? worst : -1;
        WorstRoll = worstOver;
    }

    private float Substep(in TrainControls c, float h)
    {
        int n = Bodies.Count;
        float tyreC = Arcade ? ArcadeTyreC : SimTyreC;
        float slide = 0f;

        // the driven axles share the engine's force by their load, as a locked-up tandem's diff roughly does
        float drivenLoad = 0f;
        foreach (var b in Bodies)
            for (int i = 0; i < b.AxleZ.Length; i++)
                if (b.Spec.Axles[i].Driven) drivenLoad += b.Load[i];
        drivenLoad = Mathf.Max(drivenLoad, 1f);

        var forces = new Vector2[n];
        var torques = new float[n];
        var b0 = Bodies[0];
        for (int k = 0; k < n; k++)
        {
            var b = Bodies[k];
            var f = b.Forward;
            var l = b.Left;
            var F = Vector2.Zero;
            float T = 0f;

            // longitudinal load transfer between the axle groups, from last substep's acceleration
            TransferLoad(b);

            // gravity along the slope this section is on (it points roughly where the first one does)
            float grade = c.Grade * Mathf.Cos(b.Psi - b0.Psi);
            if (c.OnFloor) F += f * (b.Mass * -Gravity * grade / Mathf.Sqrt(1f + grade * grade));

            float u = b.V.Dot(f);
            F -= f * (0.5f * AirDensity * b.Spec.DragArea * u * Mathf.Abs(u) * (1f - c.Draft));

            if (c.OnFloor)
                for (int i = 0; i < b.AxleZ.Length; i++)
                {
                    var axle = b.Spec.Axles[i];
                    var r = f * b.AxleZ[i];
                    var va = b.V + Perp(r) * b.W;
                    float delta = c.Steer * axle.Steer;
                    var fa = f.Rotated(delta);
                    var la = l.Rotated(delta);
                    float vx = va.Dot(fa), vy = va.Dot(la);
                    float N = b.Load[i];
                    float m = N / Gravity;
                    float cap = c.Grip * N;

                    float fx = 0f, latCap;
                    bool locked = c.Spring && (axle.Driven || k > 0);
                    if (locked)
                    {
                        // spring brakes: the wheel does not turn; it slides, or holds a parked train
                        float hold = 0.75f * cap;
                        fx = -Mathf.Clamp(vx * m / h, -hold, hold);
                        latCap = Mathf.Abs(vx) > 0.5f ? 0.3f * cap : cap;
                        if (Mathf.Abs(vx) > 0.5f) slide = Mathf.Max(slide, 0.8f);
                    }
                    else
                    {
                        // EBS: each axle braked in proportion to the load on it, to the design deceleration
                        float brake = c.Brake * N * BrakeDesign + (k > 0 ? c.Stretch * 0.25f * cap : 0f);
                        float retard = axle.Driven ? c.Retard * N / drivenLoad : 0f;
                        // ABS (and the retarder's own slip control) never let the brakes take all of it
                        float stop = Mathf.Min(brake + retard, c.AbsLimit * cap) + RollingResistance * N;
                        float drive = axle.Driven ? c.Drive * N / drivenLoad : 0f;
                        fx = drive - Mathf.Clamp(vx * m / h + drive, -stop, stop);
                        if (Mathf.Abs(fx) > cap) slide = Mathf.Max(slide, Mathf.Clamp(Mathf.Abs(fx) / cap - 1f, 0f, 1f));
                        fx = Mathf.Clamp(fx, -cap, cap);
                        latCap = Mathf.Sqrt(Mathf.Max(cap * cap - fx * fx, 0.02f * cap * cap));
                    }

                    // side force from the slip angle, never more than it takes to stop the axle
                    // sliding sideways this substep (at a crawl the slip angle is all noise)
                    float alpha = Mathf.Atan2(vy, Mathf.Max(Mathf.Abs(vx), 0.8f));
                    float fy = -latCap * Mathf.Sin(tyreC * Mathf.Atan(TyreB * alpha));
                    float stopY = Mathf.Abs(vy) * m / h * 0.5f;
                    if (Mathf.Abs(fy) > stopY) fy = -Mathf.Sign(vy) * stopY;
                    b.AxleSpeed[i] = vx;
                    b.AxleFy[i] = fy;
                    b.AxleAlpha[i] = alpha;
                    b.AxleSlide[i] = Mathf.Clamp((Mathf.Abs(alpha) - 0.12f) / 0.2f, 0f, 1f) * Mathf.Clamp(Mathf.Abs(vx) / 4f, 0f, 1f);
                    slide = Mathf.Max(slide, b.AxleSlide[i]);

                    var fAxle = fa * fx + la * fy;
                    F += fAxle;
                    T += Cross(r, fAxle);
                }

            // the liquid: a mass on a spring in the tank's frame, driven by the tank's acceleration
            if (b.SloshMass > 0f)
            {
                const float wx = Mathf.Tau * 0.6f, wy = Mathf.Tau * 0.5f, zeta = 0.08f;
                var sf = new Vector2(wx * wx * b.Slosh.X + 2f * zeta * wx * b.SloshVel.X,
                                     wy * wy * b.Slosh.Y + 2f * zeta * wy * b.SloshVel.Y);
                b.SloshVel += (-b.Accel - sf) * h;
                b.Slosh += b.SloshVel * h;
                b.Slosh = b.Slosh.Clamp(new Vector2(-1.5f, -0.6f), new Vector2(1.5f, 0.6f));
                var force = sf * b.SloshMass;
                F += f * force.X + l * force.Y;
            }

            forces[k] = F;
            torques[k] = T;
        }

        // a pusher bus's joint control: damping on the articulation rate; in Game, a damper that
        // only works on a pivot folding further at speed, so a plain turn is left alone
        float u0f = Mathf.Abs(b0.V.Dot(b0.Forward));
        for (int k = 1; k < n; k++)
        {
            float damp = Bodies[k].Spec.JointDamping;
            float gamma = MathX.WrapAngle(Bodies[k].Psi - Bodies[k - 1].Psi);
            float rate = Bodies[k].W - Bodies[k - 1].W;
            if (c.FoldDamping > 0f && gamma * rate > 0f && Mathf.Abs(gamma) > 0.12f)
                damp += c.FoldDamping * Mathf.Clamp((u0f - 4f) / 6f, 0f, 1f);
            if (damp <= 0f) continue;
            float tau = -damp * (Bodies[k].W - Bodies[k - 1].W);
            torques[k] += tau;
            torques[k - 1] -= tau;
        }

        // Game: a yaw moment on the first section toward the turn its front wheels ask for (the
        // electronic stability control every modern truck has, generous)
        if (c.YawAssist > 0f && c.OnFloor)
        {
            float u0 = b0.V.Dot(b0.Forward);
            float wheelbase = Wheelbase(b0);
            float want = u0 * Mathf.Tan(c.Steer) / Mathf.Max(wheelbase, 1f);
            torques[0] -= c.YawAssist * b0.Inertia * (b0.W - want) * Mathf.Clamp(Mathf.Abs(u0) / 5f, 0f, 1f);
        }

        var vBefore = new Vector2[n];
        for (int k = 0; k < n; k++)
        {
            var b = Bodies[k];
            vBefore[k] = b.V;
            float rigid = b.Mass - b.SloshMass;
            b.V += forces[k] / rigid * h;
            b.W += torques[k] / b.Inertia * h;
        }

        SolvePins(h);

        for (int k = 0; k < n; k++)
        {
            var b = Bodies[k];
            var a = (b.V - vBefore[k]) / h;
            b.Accel = new Vector2(a.Dot(b.Forward), a.Dot(b.Left));
            b.P += b.V * h;
            b.Psi += b.W * h;
        }
        return slide;
    }

    /// <summary>The service brakes' design deceleration at full pressure, in g, set by the vehicle.</summary>
    public float BrakeDesign { get; set; } = 0.65f;

    /// <summary>A braking or accelerating train moves weight between a section's front and rear axle groups.</summary>
    private static void TransferLoad(Body b)
    {
        var axles = b.Spec.Axles;
        float g0 = 0, g1 = 0; int n0 = 0, n1 = 0;
        foreach (var a in axles) { if (a.Group == 0) { g0 += a.At; n0++; } else { g1 += a.At; n1++; } }
        if (n0 == 0 || n1 == 0)
        {
            System.Array.Copy(b.StaticLoad, b.Load, b.Load.Length);
            return;
        }
        float span = Mathf.Max(g1 / n1 - g0 / n0, 0.5f);
        float shift = -b.Mass * b.Accel.X * b.CgHeight / span;   // braking (a < 0) moves load forward
        for (int i = 0; i < axles.Length; i++)
        {
            float s = axles[i].Group == 0 ? shift / n0 : -shift / n1;
            b.Load[i] = Mathf.Max(b.StaticLoad[i] + s, 0.1f * b.StaticLoad[i]);
        }
    }

    /// <summary>The first section's wheelbase (front axle to the rear group's middle), m.</summary>
    public static float Wheelbase(Body b)
    {
        float g0 = 0, g1 = 0; int n0 = 0, n1 = 0;
        foreach (var a in b.Spec.Axles) { if (a.Group == 0) { g0 += a.At; n0++; } else { g1 += a.At; n1++; } }
        return n0 > 0 && n1 > 0 ? g1 / n1 - g0 / n0 : 3f;
    }

    private void SolvePins(float h)
    {
        int n = Bodies.Count;
        for (int it = 0; it < Iterations; it++)
        {
            for (int k = 1; k < n; k++)
            {
                var p = Bodies[k - 1];
                var c = Bodies[k];
                var rp = p.Forward * p.HitchZ;
                var rc = c.Forward * c.PivotZ;
                var vp = p.V + Perp(rp) * p.W;
                var vc = c.V + Perp(rc) * c.W;
                var err = c.P + rc - (p.P + rp);
                var rhs = -(vc - vp + err * (Baumgarte / h));
                float mp = 1f / p.Mass, mc = 1f / c.Mass, ip = 1f / p.Inertia, ic = 1f / c.Inertia;
                float k11 = mp + mc + rp.Y * rp.Y * ip + rc.Y * rc.Y * ic;
                float k12 = -rp.X * rp.Y * ip - rc.X * rc.Y * ic;
                float k22 = mp + mc + rp.X * rp.X * ip + rc.X * rc.X * ic;
                float det = k11 * k22 - k12 * k12;
                if (Mathf.Abs(det) < 1e-12f) continue;
                var lambda = new Vector2((k22 * rhs.X - k12 * rhs.Y) / det, (k11 * rhs.Y - k12 * rhs.X) / det);
                c.V += lambda * mc;
                c.W += Cross(rc, lambda) * ic;
                p.V -= lambda * mp;
                p.W -= Cross(rp, lambda) * ip;

                // the stop: the trailer's front corner against the cab, the dolly against the frame
                float gamma = MathX.WrapAngle(c.Psi - p.Psi);
                float max = c.Spec.MaxArticulation;
                if (Mathf.Abs(gamma) > max - 0.02f)
                {
                    float sgn = Mathf.Sign(gamma);
                    float rel = c.W - p.W;
                    float pen = Mathf.Abs(gamma) - max;
                    float target = pen > 0f ? -pen * Baumgarte / h : 0f;
                    if (sgn * rel > target)
                    {
                        float l = (sgn * rel - target) / (ic + ip);
                        c.W -= sgn * l * ic;
                        p.W += sgn * l * ip;
                    }
                }
            }

            foreach (var contact in Contacts)
            {
                if (contact.Section < 0 || contact.Section >= n) continue;
                var b = Bodies[contact.Section];
                var r = contact.Lever;
                var nrm = contact.Normal;
                float vn = (b.V + Perp(r) * b.W).Dot(nrm);
                if (vn >= 0f) continue;
                float rn = Cross(r, nrm);
                float kn = 1f / b.Mass + rn * rn / b.Inertia;
                float lambda = -vn / kn;
                b.V += nrm * (lambda / b.Mass);
                b.W += rn * lambda / b.Inertia;
            }
        }
    }

    /// <summary>A vector turned 90° to the left: ω × r in the plane is ω·Perp(r).</summary>
    public static Vector2 Perp(Vector2 v) => new(-v.Y, v.X);
    public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    // ---- geometry: off-tracking and the swept path, for the road-width check ----

    /// <summary>
    /// Steady low-speed turn: the radius each section's axle-group centre runs on, starting from the
    /// first section's front axle at <paramref name="frontRadius"/>. The classic chain: a rear axle
    /// runs on √(R² − L²) of the point ahead of it, and a pin ahead of an axle group by c on √(R² + c²).
    /// </summary>
    public static float[] SteadyRadii(IReadOnlyList<SectionSpec> sections, float frontRadius)
    {
        var radii = new float[sections.Count];
        float R = frontRadius;
        for (int k = 0; k < sections.Count; k++)
        {
            var s = sections[k];
            float front = k == 0 ? FrontAxleAt(s) : s.PivotAt;
            float rear = RearGroupAt(s);
            float L = rear - front;
            float rearR = Mathf.Sqrt(Mathf.Max(R * R - L * L, 0.01f));
            radii[k] = rearR;
            if (float.IsNaN(s.HitchAt)) break;
            float c = rear - s.HitchAt;   // the hitch ahead (+) or behind (−) the rear group
            R = Mathf.Sqrt(rearR * rearR + c * c);
        }
        return radii;
    }

    /// <summary>Front steered axle position, metres behind the front.</summary>
    public static float FrontAxleAt(SectionSpec s)
    {
        foreach (var a in s.Axles) if (a.Group == 0) return a.At;
        return s.Axles.Length > 0 ? s.Axles[0].At : 0f;
    }

    /// <summary>Middle of the rear axle group (the fixed axles a section turns about), metres behind the front.</summary>
    public static float RearGroupAt(SectionSpec s)
    {
        float sum = 0; int n = 0;
        foreach (var a in s.Axles) if (a.Group == 1) { sum += a.At; n++; }
        if (n == 0) foreach (var a in s.Axles) { sum += a.At; n++; }
        return n > 0 ? sum / n : s.Length * 0.5f;
    }

    /// <summary>
    /// The width of road a train sweeps in a steady turn whose outer front corner runs on
    /// <paramref name="outerRadius"/>: from that corner in to the innermost wheel of the last section.
    /// </summary>
    public static float SweptWidth(IReadOnlyList<SectionSpec> sections, float outerRadius)
    {
        var s0 = sections[0];
        float halfW = s0.Width * 0.5f;
        float overhang = FrontAxleAt(s0);
        // the front axle's centre radius that puts the outer front corner on outerRadius
        float lo = 1f, hi = outerRadius;
        for (int i = 0; i < 40; i++)
        {
            float mid = (lo + hi) * 0.5f;
            float rearR = Mathf.Sqrt(Mathf.Max(mid * mid - Sq(RearGroupAt(s0) - overhang), 0.01f));
            float corner = Mathf.Sqrt(Sq(rearR + halfW) + Sq(RearGroupAt(s0)));
            if (corner > outerRadius) hi = mid; else lo = mid;
        }
        var radii = SteadyRadii(sections, lo);
        float inner = float.MaxValue;
        for (int k = 0; k < radii.Length; k++) inner = Mathf.Min(inner, radii[k] - sections[k].Width * 0.5f);
        return outerRadius - inner;
    }

    private static float Sq(float x) => x * x;
}
