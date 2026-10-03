using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// The playable pigeon's motion (#217), pure maths so tier 0 tests it (tests/…/PigeonFlightTests).
/// Three modes: in the air (flap to climb, glide when you let go), on the ground (walk, flap to
/// take off) and perched (still, flap or push forward to leave). Arcade numbers from a feral
/// pigeon: ~16 m/s cruising flap, ~11 m/s glide at ~6:1, a steep dive past 25 m/s.
/// </summary>
public static class PigeonFlight
{
    public enum Mode : byte { Air, Ground, Perched }

    /// <param name="Stick">x right, y back (forward is −1).</param>
    /// <param name="Flap">Jump / A held: flap (climb in the air, take off from the ground or a perch).</param>
    /// <param name="Dive">Crouch / B held: fold the wings and drop.</param>
    /// <param name="Effort">Shift / L3: flap harder, walk faster.</param>
    public readonly record struct Controls(Vector2 Stick, bool Flap, bool Dive, bool Effort);

    public struct State
    {
        public Vector3 Velocity;
        /// <summary>Heading about +Y; the nose is (−sin, 0, −cos), as for every craft.</summary>
        public float Yaw;
        public float Bank;
        /// <summary>0..1, how hard the wings beat (drawn, and sent to the others).</summary>
        public float Flap;
        public Mode Mode;
    }

    public const float CruiseFlap = 16f, FastFlap = 21f, Glide = 11f, GlideSink = 1.8f, ClimbRate = 3.5f;
    public const float TurnRate = 2.4f, WalkSpeed = 1.1f, RunSpeed = 2.4f;
    /// <summary>A perch catches a bird gliding or braking within this distance, below this speed.</summary>
    public const float PerchReach = 1.3f, PerchSpeed = 9f;

    public static Vector3 Heading(float yaw) => new(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));

    public static State Step(State s, in Controls c, bool onFloor, float dt)
    {
        var h = Heading(s.Yaw);
        switch (s.Mode)
        {
            case Mode.Perched:
                s.Yaw -= c.Stick.X * TurnRate * dt;
                s.Velocity = Vector3.Zero;
                s.Bank = 0f;
                s.Flap = Approach(s.Flap, 0f, 6f, dt);
                if (c.Flap || c.Stick.Y < -0.5f) return TakeOff(s, 2.5f);
                return s;
            case Mode.Ground when onFloor:
            case Mode.Air when onFloor && s.Velocity.Y <= 0.5f:
                s.Mode = Mode.Ground;
                if (c.Flap) return TakeOff(s, 4.5f);
                s.Yaw -= c.Stick.X * TurnRate * dt;
                h = Heading(s.Yaw);
                float walk = -c.Stick.Y * (c.Effort ? RunSpeed : WalkSpeed);
                if (walk < 0f) walk *= 0.4f;   // a few steps back, not a reverse gear
                var flat = MathX.Flat(s.Velocity).MoveToward(h * walk, 8f * dt);
                // pressed onto the ground, not accelerating into it: a steady push follows slopes and steps down
                s.Velocity = new Vector3(flat.X, -1.5f, flat.Z);
                s.Bank = 0f;
                s.Flap = Approach(s.Flap, 0f, 6f, dt);
                return s;
        }

        s.Mode = Mode.Air;
        float fwd = Mathf.Max(0f, -c.Stick.Y), back = Mathf.Max(0f, c.Stick.Y);
        s.Yaw -= c.Stick.X * TurnRate * dt;
        h = Heading(s.Yaw);
        float speed = (c.Flap ? (c.Effort ? FastFlap : CruiseFlap) : Glide) + fwd * 8f - back * 5f;
        float vy = c.Dive ? -14f
            : c.Flap ? ClimbRate - fwd * 4f + back * 1.5f
            : -GlideSink - fwd * 6f + back * 0.8f;
        var target = h * Mathf.Max(speed, 5f) + Vector3.Up * vy;
        s.Velocity = s.Velocity.Lerp(target, MathX.Damp(c.Dive ? 1.2f : 1.8f, dt));
        s.Bank = Approach(s.Bank, c.Stick.X * 0.7f, 4f, dt);
        s.Flap = Approach(s.Flap, c.Flap ? 1f : 0f, 5f, dt);
        return s;
    }

    /// <summary>Lands on a perch if the bird is in the air, not flapping, slow enough and close.</summary>
    public static bool Catches(in State s, in Controls c, Vector3 at, Vector3 perch) =>
        s.Mode == Mode.Air && !c.Flap && s.Velocity.Length() < PerchSpeed && at.DistanceTo(perch) < PerchReach;

    public static State Perch(State s)
    {
        s.Mode = Mode.Perched;
        s.Velocity = Vector3.Zero;
        s.Bank = 0f;
        return s;
    }

    private static State TakeOff(State s, float up)
    {
        s.Mode = Mode.Air;
        s.Velocity = Heading(s.Yaw) * 3.5f + Vector3.Up * up;
        s.Flap = 1f;
        return s;
    }

    private static float Approach(float value, float target, float rate, float dt) =>
        value + (target - value) * MathX.Damp(rate, dt);
}
