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
        // the first-person eye takes its pitch here, as the walk and the swim do: without it the view stays level
        UpdateCameraFeel(dt, false, false);
        return true;
    }
}
