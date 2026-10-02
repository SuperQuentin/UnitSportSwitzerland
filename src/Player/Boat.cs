using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;
using UnitSport.Terrain;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// A boat (#302): the jetski and the speedboat, later the steamer (#303). Like a <see cref="Flyer"/>
/// it owns a full 3D motion (<see cref="State"/>, stepped by <see cref="BoatDynamics"/> against the
/// shared wave field) posed on the yaw-only body that carries it; <see cref="Rideable.Step"/> is
/// unused. Not a Flyer: it has seats, a hull that collides as drawn, and it is left in the world
/// floating. Driven by <c>FootPlayer.BoatPhysics</c>, left by <c>VehicleBody.StepBoat</c>.
/// </summary>
public class Boat : Rideable, IEngined
{
    public BoatSpec Spec { get; }
    private readonly RideKind _kind;

    public Boat(RideKind kind, BoatSpec spec)
    {
        _kind = kind;
        Spec = spec;
    }

    /// <summary>A fresh boat of this kind, or null when the kind is not a boat.</summary>
    public static Boat? For(RideKind kind) => kind == RideKind.Steamer ? new Steamer()
        : BoatCatalog.For((int)kind) is { } spec ? new Boat(kind, spec) : null;

    public static bool IsBoat(RideKind kind) => BoatCatalog.For((int)kind) != null;

    /// <summary>The live motion: the centre of mass, velocity, spin, attitude, engine.</summary>
    public BoatState State;

    /// <summary>The helm as last applied, for the sound and the rig.</summary>
    public BoatControls Controls;

    /// <summary>
    /// How far the body (the keel under the centre of mass) is above the mean surface under the
    /// hull (<see cref="TrySurface"/>), m; <see cref="NoHeave"/> out of the water. What a remote copy
    /// adds to its OWN copy of the waves at its own time, so it draws the boat on the waves it draws.
    /// </summary>
    public float Heave = NoHeave;
    public const float NoHeave = 99f;

    protected bool Jet => Spec.Drive == BoatDrive.Jet;

    public override RideKind Kind => _kind;
    public override string Label => Spec.Name;
    public override string Blurb => Jet
        ? "{move_forward} throttle, {move_back} astern; it only steers under power, leans into turns"
        : "{move_forward} throttle, {move_back} astern, {move_left}/{move_right} the wheel (the rudder needs way on)";
    public override bool IsVehicle => true;
    public override bool HasEngine => true;
    public override bool CanHop => false;
    public override bool Driverless => true;
    public override float MaxHealth => Jet ? 100f : 160f;
    public override float BodyRadius => Jet ? 0.42f : 0.9f;
    public override float BodyHeight => Jet ? 1.2f : 1.8f;
    /// <summary>The hull boxes start at about the waterline: below it the boat meets nothing but water and the bed.</summary>
    public override float HullLift => Jet ? 0.28f : 0.32f;
    public override float ChaseDistance => Jet ? 4.8f : 8.5f;
    public override float ChaseHeight => Jet ? 1.9f : 2.8f;
    public override float ChasePitch => -0.08f;
    public override float BaseFov => 70f;
    public override float MaxFov => 90f;
    public override float FovSpeed => Jet ? 25f : 22f;
    public override float DismountSpeed => 3f;
    public override float EyeHeight => Jet ? 1.7f : 1.5f;

    public override Vector3 FirstPersonEye
    {
        get
        {
            var seat = Seats[0];
            return SeatedFigure.Eye(seat) + new Vector3(0, 0.02f, -0.05f);
        }
    }

    public override SeatAnchor[] Seats => SeatsOf(Kind, () => Jet ? BoatMeshBuilder.JetskiSeats() : BoatMeshBuilder.RunaboutSeats());

    /// <summary>Measured from the parked mesh, the flag's staff left out (nothing rests on it).</summary>
    public override (Vector3 Centre, Vector3 Size) ParkedBox => Measured(Kind, BuildParkedVisual, "Flag");

    public override Node3D BuildVisual(int riderIndex, Outfit outfit = default) =>
        BoatRig.Create(Spec, (int)Kind, HumanPalette.ForRider(riderIndex) with { Outfit = outfit }, riderIndex);

    public override Node3D BuildParkedVisual(int riderIndex) => BoatRig.Create(Spec, (int)Kind, null, riderIndex);

    /// <summary>A boat's motion is its own (<see cref="BoatDynamics"/>), not a speed along a heading.</summary>
    public override void Step(in RideInput input, in RideGround ground, float dt, ref RideMotion motion) { }

    // ---- the engine, for the sound and the rev counter --------------------------------------

    public float Rpm => Spec.IdleRpm + (Spec.MaxRpm - Spec.IdleRpm) * Mathf.Clamp(State.Rpm01, 0f, 1.15f);
    public float Rpm01 => Mathf.Clamp(State.Rpm01, 0f, 1.1f);
    public int Gear => State.Gear < 0 ? -1 : 1;
    public float Throttle => Mathf.Max(Controls.Throttle, Controls.Reverse);

    /// <summary>A PWC's 1.6 L triple on its short wet exhaust; a runabout's big V8 burbling through the water.</summary>
    public virtual EngineProfile Sound => _sound ??= Jet
        ? EngineProfile.Inline4Na with { Cylinders = 3, IdleRpm = Spec.IdleRpm, MaxRpm = Spec.MaxRpm, PipeM = 0.55f, Unevenness = 0.6f }
        : EngineProfile.For(EngineLayout.V8, Spec.IdleRpm, Spec.MaxRpm) with { PipeM = 1.7f, Unevenness = 2.4f };
    private EngineProfile? _sound;

    // ---- the pose --------------------------------------------------------------------------

    /// <summary>The centre of mass, in the drawn boat's frame: the attitude turns about it.</summary>
    public Vector3 Pivot => new(0, Spec.CentreHeight, 0);

    /// <summary>Puts the drawn boat at <paramref name="attitude"/>, about its centre of mass, on a yaw-only body.</summary>
    public void Pose(Node3D visual, float bodyYaw, Quaternion attitude)
    {
        var q = attitude == default ? Quaternion.Identity : attitude;
        var local = new Basis(Vector3.Up, -bodyYaw) * new Basis(q);
        visual.Transform = new Transform3D(local, Pivot - local * Pivot);
    }

    /// <summary>The body's position (the keel under the centre of mass, upright) for a centre of mass.</summary>
    public Vector3 BodyAt(Vector3 centre) => centre - Pivot;

    /// <summary>
    /// The mean water surface under the hull's centreline (bow, middle and stern thirds) at wave time
    /// <paramref name="t"/>: what a hull this long floats on, short of waves too small to move it.
    /// False with no water under any of the three.
    /// </summary>
    public bool TrySurface(Vector3 body, float yaw, double t, out float surface)
    {
        var along = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)) * (Spec.Length / 3f);
        float sum = 0f;
        int n = 0;
        for (int i = -1; i <= 1; i++)
            if (WaterField.TryLevelAt(body + along * i, t, out float level)) { sum += level; n++; }
        surface = n > 0 ? sum / n : 0f;
        return n > 0;
    }

    /// <summary>Measures <see cref="Heave"/> for a body at <paramref name="body"/>, heading <paramref name="yaw"/>, now.</summary>
    public void MeasureHeave(Vector3 body, float yaw) =>
        Heave = State.Airborne <= 0.05f && TrySurface(body, yaw, WaterField.Now, out float s) ? body.Y - s : NoHeave;

    /// <summary>
    /// A remote copy's height: its own copy of the waves at its own time plus the owner's
    /// <see cref="Heave"/> (sent in the pose), so the boat rides the waves this peer draws however
    /// far behind its position stream is; the stream's own height out of the water.
    /// </summary>
    public float RemoteY(Vector3 body, float yaw, float heave) =>
        heave < NoHeave * 0.5f && TrySurface(body, yaw, WaterField.Now, out float s) ? s + heave : body.Y;

    // ---- what other players see ------------------------------------------------------------

    /// <summary>Engine, thrust share, <see cref="Heave"/>, and how much the hull has the water (+2 airborne).</summary>
    public override Vector4 WritePose(Node3D visual, in RideMotion motion, in FlightMotion flight) =>
        new(State.Rpm01, Mathf.Clamp(Mathf.Abs(State.Thrust) / Mathf.Max(1f, Spec.StaticThrust), 0f, 1f), Heave,
            State.Wet + (State.Airborne > 0.1f ? 2f : 0f));

    private Vector3 _lastAt;
    private bool _hasLast;
    private float _speed;

    public override void AnimateRemote(Node3D visual, Vector4 pose, float dt)
    {
        // the speed it is drawn at, from where it is drawn (the copy's own velocity is not sent here)
        var at = visual.GlobalPosition;
        if (_hasLast && dt > 0f)
            _speed = Mathf.Lerp(_speed, new Vector2(at.X - _lastAt.X, at.Z - _lastAt.Z).Length() / dt, 1f - Mathf.Exp(-6f * dt));
        _lastAt = at;
        _hasLast = true;
        if (visual is BoatRig rig) rig.Water(_speed, pose.W % 2f, pose.Y, pose.W < 2f);
    }

    /// <summary>Owner, each frame: the wake and spray from the live state.</summary>
    public override void Animate(Node3D visual, in RideMotion motion, float dt)
    {
        if (visual is BoatRig rig)
            rig.Water(Mathf.Abs(State.WaterSpeed), State.Wet, Mathf.Abs(State.Thrust) / Mathf.Max(1f, Spec.StaticThrust), State.Airborne <= 0.1f);
    }
}

/// <summary>
/// The water a boat floats in, for <see cref="BoatDynamics"/>: <see cref="WaterField"/>'s surface at
/// one wave time per step, its flow (the waves' orbital velocity and their Stokes drift, taken once
/// at the hull per step), the ground under it, no wind yet (#304). One per boat body, reused.
/// </summary>
public sealed class BoatWater : IBoatWater
{
    public ChunkManager? Terrain;
    private double _t;
    private Vector3 _flow;

    /// <summary>Sets the wave time and the flow at the hull for the next step.</summary>
    public void Begin(Vector3 at, double t)
    {
        _t = t;
        _flow = WaterField.TryGetStill(at, out _, out float scale)
            ? WaterField.Velocity(at.X, at.Z, t) + Drift(scale)
            : Vector3.Zero;
    }

    public bool Surface(float x, float z, out float level, out Vector3 flow)
    {
        flow = _flow;
        return WaterField.TryLevelAt(new Vector3(x, 0, z), _t, out level);
    }

    public float Bed(float x, float z) =>
        Core.TestWorld.TryGround(Terrain, new Vector3(x, 0, z), out float y) ? y : float.NaN;

    /// <summary>#304: the wind.</summary>
    public Vector3 Wind => Vector3.Zero;

    /// <summary>
    /// The waves' Stokes drift at the surface, Σ ω k a² along each wave: the slow downwave creep that
    /// carries a boat left alone (gamey: ~4 cm/s; nothing in a calm).
    /// </summary>
    public static Vector3 Drift(float scale)
    {
        var amp = WaterField.Amplitudes;
        var d = Vector3.Zero;
        for (int i = 0; i < WaveSpectrum.Count; i++)
        {
            var w = WaveSpectrum.Waves[i];
            double a = amp[i] * scale, k = w.K;
            double u = w.Omega * k * a * a;
            d += new Vector3((float)(u * w.Kx / k), 0, (float)(u * w.Kz / k));
        }
        return d;
    }
}
