using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Creative mode (#1002, docs/notes/player/creative-mode.md): the body keeps its inventory and
/// tools, takes no damage (<see cref="TakeDamage(float, long, DamageCause)"/>), and flies like
/// Minecraft's creative flight, without speed lines or shake (<see cref="PlayerFeel"/>), the view free
/// to look straight up and down (<see cref="ClampPitch"/>). Jump twice quickly to take off or stop; while flying the stick moves
/// on the ground plane from the view's yaw, Jump rises, Shift (a pad's B) sinks, <see cref="PlayerInput.FlyBoost"/>
/// goes fast; touching down ends the flight. <see cref="GameSettings.CreativeNoclip"/> flies
/// through the ground and walls instead. T in <c>ClientWorld</c> switches it; local to the owner.
/// </summary>
public partial class FootPlayer
{
    /// <summary>Creative mode: no damage, and a double jump takes off. The owner's; not replicated.</summary>
    public bool Creative
    {
        get => _creative;
        set
        {
            _creative = value;
            if (!value) CreativeFlying = false;
        }
    }
    private bool _creative;

    /// <summary>Flying in creative mode (<see cref="Creative"/>): the view looks straight up and down while it lasts.</summary>
    public bool CreativeFlying
    {
        get => _creativeFlying;
        private set
        {
            _creativeFlying = value;
            // back on the ground, the third-person lens keeps out of it again
            if (!value) _pitch = ClampPitch(_pitch);
        }
    }
    private bool _creativeFlying;

    public const float CreativeFlySpeed = 12f;
    public const float CreativeFlyClimb = 8f;
    public const float CreativeBoost = 5f;
    /// <summary>Two jump presses this close (seconds) are a double tap.</summary>
    private const float CreativeDoubleTap = 0.3f;

    private double _creativeLastPress = -1;
    private bool _creativeJumpHeld;

    /// <summary>
    /// The creative flight in place of the walk, and the double tap that starts and stops it. False
    /// when not flying: the walk runs as usual (the first tap of a take-off is an ordinary jump).
    /// </summary>
    private bool CreativePhysics(float dt, bool onFloor)
    {
        if (!_creative || Npc) return false;

        bool jumpDown = PlayerInput.Held(PlayerInput.Jump);
        bool pressed = jumpDown && !_creativeJumpHeld;
        _creativeJumpHeld = jumpDown;
        if (pressed)
        {
            double now = GameClock.Now;
            if (_creativeLastPress >= 0 && now - _creativeLastPress <= CreativeDoubleTap)
            {
                CreativeFlying = !CreativeFlying;
                _creativeLastPress = -1;
                // the walk must not see this press as a fresh one (a wall jump, the wingsuit)
                _jumpHeld = true;
                if (CreativeFlying) { _jumpBuffer = 0; _fallSpeed = 0; }
            }
            else _creativeLastPress = now;
        }
        if (!CreativeFlying) return false;

        // a pad's L3 is the boost, so it sinks on B (crouch); a keyboard's Shift, as in Minecraft
        bool pad = PlayerInput.LastDevice == InputDevice.Gamepad;
        bool down = pad ? PlayerInput.Held(PlayerInput.CrouchSlide) : PlayerInput.Held(PlayerInput.Sprint);
        float boost = PlayerInput.Held(PlayerInput.FlyBoost) ? CreativeBoost : 1f;

        var input = PlayerInput.Move;
        var flat = new Basis(Vector3.Up, _viewYaw) * new Vector3(input.X, 0, input.Y);
        // a script's walk (a check) steers the flight as it steers the walk
        if (WalkControls?.Invoke() is { } walk) flat = walk.Wish with { Y = 0 };
        if (flat.LengthSquared() > 1f) flat = flat.Normalized();
        float climb = (jumpDown ? 1f : 0f) - (down ? 1f : 0f);
        var wish = flat * CreativeFlySpeed * boost + Vector3.Up * climb * CreativeFlyClimb * boost;

        // a little inertia, as Minecraft's: it eases into speed and glides to a stop
        var velocity = Velocity.Lerp(wish, MathX.Damp(8f, dt));
        _fallSpeed = 0;
        Velocity = velocity;
        if (GameSettings.Current.CreativeNoclip) GlobalPosition += velocity * dt;
        else
        {
            MoveAndSlide();
            // sinking onto the ground lands, as in Minecraft
            if (IsOnFloor() && climb < 0f) CreativeFlying = false;
        }
        // third person, the body turns to where it flies, as the walk's does: Superman faces his way (#1219)
        if (_thirdPerson) FaceTravel(dt, flat);
        // the first-person eye takes its pitch here, as the walk and the swim do: without it the view stays level
        UpdateCameraFeel(dt, false, false);
        return true;
    }

    /// <summary>Creative flight's figure (#1219): <c>Anim.X</c> is the lean, the tilt goes in <see cref="BodyPose"/>.</summary>
    public const int PoseFly = 8;

    /// <summary>Flying this fast forward (m/s) the figure lies flat out, Superman; slower it stands up toward the hover.</summary>
    private const float FlyFullLean = 9f;
    /// <summary>How far the body tips at full lean (radians, 80°), about its middle.</summary>
    private const float FlyTilt = 1.4f;

    private float _flyLean, _flyBank;

    /// <summary>
    /// Creative flight's pose (#1219): leaning into the flight with its forward speed, Superman when
    /// fast, upright with a slow bob when slowing to a hover; back a little flying backwards, banked
    /// a little sideways, head down a little diving. Eased, so a stop stands the body up gently.
    /// </summary>
    private void PublishFlyPose(float dt)
    {
        _airTime = 0f;
        // the flight as the body sees it: a node faces -Z
        var local = GlobalBasis.Inverse() * Velocity;
        float lean = Mathf.Clamp(-local.Z / FlyFullLean, -0.25f, 1f);
        float bank = Mathf.Clamp(local.X / FlyFullLean, -1f, 1f);
        _flyLean = Mathf.Lerp(_flyLean, lean, MathX.Damp(4f, dt));
        _flyBank = Mathf.Lerp(_flyBank, bank, MathX.Damp(4f, dt));
        PoseKind = PoseFly;
        Anim = new Vector4(Mathf.Max(_flyLean, 0f), 0f, 0f, 0f);

        float forward = Mathf.Max(_flyLean, 0f);
        // diving tips the head further down, climbing lifts it, only as far as it leans
        float dive = Mathf.Clamp(-Velocity.Y / FlyFullLean, -1f, 1f) * 0.3f * forward;
        // negative about X tips the head ahead (-Z); flying right puts the right side down
        var tilt = new Basis(Vector3.Back, -_flyBank * 0.4f) * new Basis(Vector3.Right, -(_flyLean * FlyTilt + dive));
        // hovering, a slow bob; gone once it flies
        float bob = Mathf.Sin((float)GameClock.Now * Mathf.Tau / 2.4f) * 0.04f * (1f - forward);
        var middle = new Vector3(0, 1f, 0);
        BodyPose = new Transform3D(tilt, middle - tilt * middle + new Vector3(0, bob, 0));
    }
}
