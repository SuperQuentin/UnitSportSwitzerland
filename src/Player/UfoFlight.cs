using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// The flying saucer's motion (#861), pure maths so tier 0 tests it (tests/…/UfoFlightTests). An
/// arcade hover craft, not an aircraft: no lift, no stall, no gravity while it flies. It holds its
/// height when the climb keys are let go, moves the same in every direction (the stick is a wish
/// for a horizontal velocity, forward and sideways alike), turns on the spot to where the camera
/// looks, and tilts into its acceleration on a lightly damped spring, so it wobbles when it sets off
/// and when it stops. It lands on its legs: they come down near the ground, the craft settles on
/// them, and the ramp opens once it stands still.
/// </summary>
public static class UfoFlight
{
    /// <param name="Stick">x right, y back (forward is −1), up to length 1: where to go, in the craft's heading frame.</param>
    /// <param name="Up">Climb, 0..1 (Space / RT).</param>
    /// <param name="Down">Descend, 0..1 (Ctrl / LT).</param>
    /// <param name="Boost">Shift / L3: the fast setting.</param>
    /// <param name="ViewYaw">Where the camera looks: the heading turns to it.</param>
    /// <param name="Hatch">Pressed this step (edge): on the ground the ramp opens or shuts, in the air the legs fold or come down.</param>
    /// <param name="Beam">Pressed this step (edge): the tractor beam on or off.</param>
    /// <param name="Lights">Pressed this step (edge): the rim lamps on or off.</param>
    public readonly record struct Controls(Vector2 Stick, float Up, float Down, bool Boost, float ViewYaw, bool Hatch = false,
        bool Beam = false, bool Lights = false);

    /// <param name="OnFloor">The body stands on something.</param>
    /// <param name="Below">Free height under the legs' feet, m (a ray down: terrain, roofs, the flat test world); large when nothing is near.</param>
    public readonly record struct Env(bool OnFloor, float Below);

    public struct State
    {
        public Vector3 Velocity;
        /// <summary>Heading about +Y; the front (the ramp side) is (−sin, 0, −cos), as for every craft.</summary>
        public float Yaw;
        /// <summary>Nose-down (+) pitch and right-down (+) roll, radians: the wobble spring's position.</summary>
        public Vector2 Tilt;
        /// <summary>The spring's velocity, rad/s.</summary>
        public Vector2 TiltRate;
        /// <summary>Landing legs, 0 folded .. 1 down.</summary>
        public float Legs;
        /// <summary>The hatch's ramp, 0 shut .. 1 open on the ground.</summary>
        public float Ramp;
        /// <summary>The engine's hum, 0..1: sound pitch and the underside's glow.</summary>
        public float Hum;
        /// <summary>Standing on its legs.</summary>
        public bool Landed;
        /// <summary>Seconds landed and still: the ramp opens after <see cref="RampDelay"/>.</summary>
        public float Settled;
        /// <summary>The pilot's choice for the ramp on the ground (Hatch), or null for automatic.</summary>
        public bool? RampWanted;
        /// <summary>The pilot's choice for the legs in the air (Hatch), or null for automatic.</summary>
        public bool? LegsWanted;
        /// <summary>The previous step's horizontal velocity, for the acceleration that tilts it.</summary>
        public Vector3 LastFlat;
        /// <summary>The tractor beam is on (<see cref="TractorBeamMath"/>).</summary>
        public bool Beam;
        /// <summary>The rim lamps chase.</summary>
        public bool Lights;
    }

    /// <summary>Horizontal speeds, m/s: cruising and boosted (108 and 252 km/h).</summary>
    public const float Cruise = 30f, Dash = 70f;
    /// <summary>Vertical speeds, m/s, and the climb with boost.</summary>
    public const float Climb = 10f, DashClimb = 20f;
    /// <summary>Horizontal acceleration, m/s², cruising and boosted; a stop is quicker than a start.</summary>
    public const float Accel = 14f, DashAccel = 28f, StopAccel = 22f, VerticalAccel = 16f;
    /// <summary>How fast the heading swings to the look, rad/s: brisk, a saucer has no nose to swing.</summary>
    public const float YawRate = 2.4f;
    /// <summary>The tilt spring: natural frequency (rad/s) and damping ratio. Under-damped: a wobble.</summary>
    public const float WobbleOmega = 5.5f, WobbleDamping = 0.28f;
    /// <summary>Tilt per m/s² of acceleration, and the most it ever tilts, rad.</summary>
    public const float TiltPerAccel = 0.022f, MaxTilt = 0.38f;
    /// <summary>Legs come down under this much free height while descending or slow, and fold above <see cref="LegsUp"/>.</summary>
    public const float LegsDown = 7f, LegsUp = 11f;
    /// <summary>Legs and ramp travel, per second (0..1).</summary>
    public const float LegsSpeed = 0.9f, RampSpeed = 0.6f;
    /// <summary>Seconds landed and still before the ramp opens by itself.</summary>
    public const float RampDelay = 0.8f;
    /// <summary>The flare: the fastest sink at the ground, m/s, and how much more each metre above it allows.</summary>
    public const float FlareSink = 2f, FlarePerMetre = 0.9f;
    /// <summary>Under this free height, slow and with nobody climbing, it settles onto its legs, m.</summary>
    public const float SettleHeight = 4f;
    /// <summary>Under this ground speed a touchdown is a landing; faster it slides on (and the body may call it a crash).</summary>
    public const float LandSpeed = 6f;

    public static Vector3 Heading(float yaw) => new(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
    public static Vector3 Right(float yaw) => Heading(yaw).Cross(Vector3.Up);

    /// <summary>A craft at rest on its legs, ramp shut (it opens by itself once settled).</summary>
    public static State Parked(float yaw) => new() { Yaw = yaw, Legs = 1f, Landed = true, Hum = 0.15f, Lights = true };

    public static State Step(State s, in Controls c, in Env env, float dt)
    {
        float lift = c.Up - c.Down;
        var flat = MathX.Flat(s.Velocity);
        if (c.Beam) s.Beam = !s.Beam;
        if (c.Lights) s.Lights = !s.Lights;

        // ---- the ground: standing on its legs until the pilot climbs -----------------------
        bool touching = env.OnFloor && s.Velocity.Y <= 0.5f && lift <= 0.05f;
        if (touching && flat.Length() < LandSpeed)
        {
            if (!s.Landed) { s.Landed = true; s.Settled = 0f; s.LegsWanted = null; }
            s.Settled += dt;
            // the legs' pads grip: a short slide, then still
            flat = flat.MoveToward(Vector3.Zero, StopAccel * 1.5f * dt);
            // turning on the spot works on the ground too: it is a saucer
            s.Yaw += TurnStep(s.Yaw, c.ViewYaw, dt);
            if (c.Hatch) s.RampWanted = !(s.RampWanted ?? s.Ramp > 0.5f);
            bool open = s.RampWanted ?? s.Settled > RampDelay;
            s.Ramp = Mathf.MoveToward(s.Ramp, open ? 1f : 0f, RampSpeed * dt);
            s.Legs = Mathf.MoveToward(s.Legs, 1f, LegsSpeed * dt);
            s.Hum = Approach(s.Hum, 0.15f, 2f, dt);
            s.Velocity = new Vector3(flat.X, -0.4f, flat.Z);   // pressed onto the ground, not falling into it
            Wobble(ref s, Vector2.Zero, dt);
            s.LastFlat = flat;
            return s;
        }

        // ---- flying ---------------------------------------------------------------------
        if (s.Landed)
        {
            // lifting off: the ramp must be up first, the pilot's choice for it forgotten
            s.Landed = false;
            s.RampWanted = null;
        }
        s.Settled = 0f;
        s.Ramp = Mathf.MoveToward(s.Ramp, 0f, RampSpeed * 2f * dt);
        // the ramp still down: a gentle climb until it is in, so nothing scrapes along the field
        float rampHold = 1f - Mathf.Clamp(s.Ramp * 3f, 0f, 1f);

        s.Yaw += TurnStep(s.Yaw, c.ViewYaw, dt);
        var fwd = Heading(s.Yaw);
        var right = Right(s.Yaw);
        var stick = c.Stick.LengthSquared() > 1f ? c.Stick.Normalized() : c.Stick;
        float top = c.Boost ? Dash : Cruise;
        var wish = (fwd * -stick.Y + right * stick.X) * top * rampHold;
        // accelerating toward the wish, braking harder when it asks for less
        float rate = wish.LengthSquared() < flat.LengthSquared() * 0.81f ? StopAccel : c.Boost ? DashAccel : Accel;
        flat = flat.MoveToward(wish, rate * dt);

        float vyWish = lift * (c.Boost ? DashClimb : Climb);
        // near the ground it flares: never faster down than it can stop on its legs
        vyWish = Mathf.Max(vyWish, -(FlareSink + env.Below * FlarePerMetre));
        // a hand's breadth over the ground and nobody climbing: it settles onto its legs (a saucer
        // that holds its height would otherwise hover 2 m up for ever after a slow descent)
        if (lift <= 0.05f && env.Below < SettleHeight && flat.Length() < LandSpeed) vyWish = Mathf.Min(vyWish, -FlareSink);
        if (rampHold < 1f) vyWish = Mathf.Max(vyWish, 0f) * 0.3f + 0.6f;
        float vy = Mathf.MoveToward(s.Velocity.Y, vyWish, VerticalAccel * dt);
        s.Velocity = new Vector3(flat.X, vy, flat.Z);

        // the legs: down near the ground (descending or slow), folded up high, unless the pilot chose
        if (c.Hatch) s.LegsWanted = !(s.LegsWanted ?? s.Legs > 0.5f);
        bool low = env.Below < LegsDown && (vy < 0.5f || flat.Length() < 8f);
        bool high = env.Below > LegsUp;
        bool legs = s.LegsWanted ?? (low || (!high && s.Legs > 0.5f));
        // whatever the pilot chose, the legs are down for a touchdown
        if (env.Below < 2.5f && vy < 0f) legs = true;
        s.Legs = Mathf.MoveToward(s.Legs, legs ? 1f : 0f, LegsSpeed * dt);

        // tilt into the acceleration (nose down to speed up, up to stop, into a sideslip): the wobble
        var accel = (flat - s.LastFlat) / Mathf.Max(dt, 1e-4f);
        var target = new Vector2(accel.Dot(fwd), accel.Dot(right)) * TiltPerAccel;
        // and a steady lean with the speed itself, as a hovering thing leans into where it goes
        target += new Vector2(flat.Dot(fwd), flat.Dot(right)) / Dash * 0.12f;
        target = target.LimitLength(MaxTilt);
        Wobble(ref s, target, dt);
        s.LastFlat = flat;

        float speed01 = Mathf.Clamp(s.Velocity.Length() / Dash, 0f, 1f);
        s.Hum = Approach(s.Hum, 0.3f + 0.7f * speed01, 3f, dt);
        return s;
    }

    /// <summary>One step of the heading toward the look, at <see cref="YawRate"/>.</summary>
    public static float TurnStep(float yaw, float viewYaw, float dt) =>
        Mathf.Clamp(MathX.WrapAngle(viewYaw - yaw), -YawRate * dt, YawRate * dt);

    /// <summary>The tilt spring, a semi-implicit Euler step toward <paramref name="target"/>.</summary>
    private static void Wobble(ref State s, Vector2 target, float dt)
    {
        var acc = (target - s.Tilt) * (WobbleOmega * WobbleOmega) - s.TiltRate * (2f * WobbleDamping * WobbleOmega);
        s.TiltRate += acc * dt;
        s.Tilt += s.TiltRate * dt;
        s.Tilt = s.Tilt.LimitLength(MaxTilt * 1.3f);
    }

    /// <summary>The craft's orientation: the heading, then the tilt (nose down +, right down +).</summary>
    public static Basis Attitude(in State s) =>
        new Basis(Vector3.Up, s.Yaw) * new Basis(Vector3.Right, -s.Tilt.X) * new Basis(Vector3.Back, -s.Tilt.Y);

    private static float Approach(float value, float target, float rate, float dt) =>
        value + (target - value) * MathX.Damp(rate, dt);
}
