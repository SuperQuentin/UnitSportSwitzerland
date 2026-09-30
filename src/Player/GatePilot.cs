using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Flies a craft gate to gate for air races with <c>--raceauto</c>, through the real input
/// actions — the path <c>--flycheck</c> scripts, so the pilot flies the same model a player does
/// and <see cref="FootPlayer"/> needs no second input seam.
///
/// <para>
/// Plane: bank toward the gate (a coordinated turn), pitch for a climb rate toward the higher of
/// the gate and the terrain ahead, throttle lever full. Helicopter: the look turns it, the stick
/// flies it forward, Space/Ctrl hold the altitude. Paraglider and wingsuit: steer, and trade
/// glide for sink (brakes / dive) when the next gate is below the glide slope. A wingsuit starts
/// as a base jump: fall, then Jump.
/// </para>
/// </summary>
public sealed class GatePilot
{
    private readonly FootPlayer _p;
    private bool _jumped, _canopy;
    private static readonly string[] Actions =
    {
        PlayerInput.MoveLeft, PlayerInput.MoveRight, PlayerInput.MoveForward, PlayerInput.MoveBack,
        PlayerInput.LookLeft, PlayerInput.LookRight, PlayerInput.Jump, PlayerInput.CrouchSlide,
        PlayerInput.Sprint, PlayerInput.TuckBoost,
    };

    public GatePilot(FootPlayer player) => _p = player;

    /// <summary>On the grid, before GO: only the throttle lever, so the plane leaves at full power.</summary>
    public void Prepare()
    {
        Release();
        if (_p.Vehicle is Plane) Hold(PlayerInput.Sprint, _p.Flight.Control < 0.99f);
    }

    /// <summary>
    /// One frame toward <paramref name="gate"/>. <paramref name="finished"/>: past the line —
    /// fly on level (plane), stop (helicopter), open the canopy (wingsuit).
    /// </summary>
    public void Fly(Vector3 gate, bool finished)
    {
        Release();
        var pos = _p.GlobalPosition;
        var to = RaceRoute.Flat(gate - pos);
        float yaw = _p.IsFlying ? _p.Flight.Yaw : _p.Rotation.Y;
        // + = the gate is to the left
        float err = finished || to.LengthSquared() < 1f ? 0f
            : Mathf.Wrap(Mathf.Atan2(-to.X, -to.Z) - yaw, -Mathf.Pi, Mathf.Pi);
        float flat = Mathf.Max(to.Length(), 1f);

        switch (_p.Vehicle)
        {
            case Plane: FlyPlane(pos, gate, err, finished); break;
            case Helicopter: FlyHeli(pos, gate, err, flat, finished); break;
            case Canopy c when c.Kind == RideKind.Paraglider:
                float need = (pos.Y - gate.Y) / flat;   // slope down to the gate
                // trim (0.11) is the flattest glide; the bar (0.13) is faster; the brakes (0.21) steepest
                float y = finished || need <= 0.11f ? 0f : need <= 0.15f ? -Mathf.Clamp((need - 0.11f) / 0.02f, 0f, 1f) : 1f;
                Stick(new Vector2(Mathf.Clamp(-err * 1.5f, -1f, 1f), y));
                break;
            case Wingsuit:
                if (finished && !_canopy) { _canopy = true; Hold(PlayerInput.Jump, true); break; }
                float slope = (pos.Y - gate.Y) / flat;   // the suit's own glide is ~0.37
                float dive = slope > 0.40f ? -Mathf.Clamp((slope - 0.37f) * 3f, 0f, 1f)
                    : slope < 0.33f ? Mathf.Clamp((0.37f - slope) * 2f, 0f, 0.5f) : 0f;
                Stick(new Vector2(Mathf.Clamp(-err * 1.5f, -1f, 1f), dive));
                break;
            case null when !_jumped && _p.Velocity.Y < -3f:
                // a base jump: falling, so Jump deploys the suit
                _jumped = true;
                Hold(PlayerInput.Jump, true);
                break;
        }
    }

    private void FlyPlane(Vector3 pos, Vector3 gate, float err, bool finished)
    {
        var f = _p.Flight;
        Hold(PlayerInput.Sprint, f.Control < 0.99f);
        float bankWanted = Mathf.Clamp(-err * 1.2f, -0.7f, 0.7f);   // + right wing down
        float x = Mathf.Clamp((bankWanted - f.Bank) * 2.5f, -1f, 1f);

        var nose = -f.Attitude.Orthonormalized().Z;
        float height = finished ? pos.Y : Mathf.Max(gate.Y, TerrainAhead(pos, nose) + 50f);
        float climb = Mathf.Clamp((height - pos.Y) * 0.25f, -8f, 10f);
        float wanted = Mathf.Asin(Mathf.Clamp(climb / Mathf.Max(f.Airspeed, 20f), -0.5f, 0.5f));
        float y = Mathf.Clamp((wanted - Mathf.Asin(Mathf.Clamp(nose.Y, -1f, 1f))) * 3f, -1f, 1f);
        if (f.Airspeed < 30f) y = Mathf.Min(y, 0f);   // never pull toward a stall
        Stick(new Vector2(x, y));
    }

    private void FlyHeli(Vector3 pos, Vector3 gate, float err, float flat, bool finished)
    {
        // The look sets where the nose goes and the nose follows it at 1.8 rad/s. Turning the
        // look at a rate proportional to the nose's error keeps the two together (the nose is
        // the faster of them), so this settles without knowing the look sensitivity.
        Look(Mathf.Clamp(err * 1.5f, -1.5f, 1.5f));
        if (!finished)
        {
            Stick(new Vector2(0f, Mathf.Abs(err) < 0.5f ? -1f : -0.3f));
            Hold(PlayerInput.TuckBoost, Mathf.Abs(err) < 0.2f && flat > 250f);
        }
        float height = finished ? pos.Y : Mathf.Max(gate.Y, TerrainAhead(pos, -_p.Flight.Attitude.Z) + 40f);
        Hold(PlayerInput.Jump, pos.Y < height - 2f);
        Hold(PlayerInput.CrouchSlide, pos.Y > height + 2f);
    }

    /// <summary>The highest terrain along the next ~600 m of the nose, from what this client has loaded.</summary>
    private float TerrainAhead(Vector3 pos, Vector3 nose)
    {
        var f = RaceRoute.Flat(nose);
        f = f.LengthSquared() > 1e-4f ? f.Normalized() : Vector3.Zero;
        float top = float.MinValue;
        foreach (float d in new[] { 0f, 150f, 300f, 600f })
            if (_p.Terrain != null && _p.Terrain.TryGetHeight(pos + f * d, out float g)) top = Mathf.Max(top, g);
        return top;
    }

    /// <summary>
    /// The left stick as <see cref="PlayerInput.Move"/> will read it. <c>GetVector</c> rescales past
    /// the deadzone, so the raw press is lengthened by it to come out at the value asked for.
    /// </summary>
    private static void Stick(Vector2 v)
    {
        float m = Mathf.Min(v.Length(), 1f);
        if (m < 1e-3f) return;
        float dz = InputMap.ActionGetDeadzone(PlayerInput.MoveLeft);
        var raw = v.Normalized() * (dz + m * (1f - dz));
        Press(raw.X < 0 ? PlayerInput.MoveLeft : PlayerInput.MoveRight, Mathf.Abs(raw.X));
        Press(raw.Y < 0 ? PlayerInput.MoveForward : PlayerInput.MoveBack, Mathf.Abs(raw.Y));
    }

    /// <summary>A look rate, rad/s, + = turn left. <see cref="PlayerInput.LookRate"/> squares the stick.</summary>
    private static void Look(float rate)
    {
        float m = Mathf.Sqrt(Mathf.Abs(rate) / (PlayerInput.StickTurnRate * GameSettings.Current.StickSensitivity));
        if (m < 0.02f) return;
        float dz = InputMap.ActionGetDeadzone(PlayerInput.LookLeft);
        Press(rate > 0 ? PlayerInput.LookLeft : PlayerInput.LookRight, dz + Mathf.Min(m, 1f) * (1f - dz));
    }

    private static void Press(string action, float strength)
    {
        if (strength > 0.001f) Input.ActionPress(action, Mathf.Min(strength, 1f));
    }

    private static void Hold(string action, bool on)
    {
        if (on) Input.ActionPress(action);
    }

    /// <summary>Lets go of everything; each frame presses afresh (Jump is edge-triggered, so a press lasts one frame).</summary>
    public static void Release()
    {
        foreach (var a in Actions) Input.ActionRelease(a);
    }
}
