using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// A car, motorbike or truck driven into water deeper than it can wade (#299): it floats a moment,
/// fills and sinks, and is lost; the driver comes out on foot (swimming is #301). Keyed on the real
/// depth (<see cref="WaterField"/>): a legacy tile's lake is 0.12 m deep, so cars still drive on it
/// as they always did; only where a bed exists (the fixture lake, #298's lakes) does a lake take a car.
/// </summary>
public partial class FootPlayer
{
    /// <summary>Seconds afloat before it starts to go down, then seconds going down before it is lost.</summary>
    private const float FloatSeconds = 2.5f, SinkSeconds = 4f;

    /// <summary>How fast it goes down once it has filled, m/s.</summary>
    private const float SinkSpeed = 0.9f;

    /// <summary>Seconds this vehicle has been in water over its wading depth; 0 on dry land or in the shallows.</summary>
    public float Sinking { get; private set; }

    /// <summary>
    /// How deep a ground vehicle can stand in water, metres of water over the ground it stands on:
    /// a truck up to its axles' seals, a car to its sills, a motorbike to its engine's air box.
    /// </summary>
    public float WadingDepth => _ride switch
    {
        Truck => 1.0f,
        Motorbike => 0.45f,
        Car => 0.55f,
        _ => float.PositiveInfinity,
    };

    /// <summary>
    /// The water's turn, before the ride step: true when it has this vehicle this step (afloat or
    /// going down), and the vehicle model must not drive it. Ends with <see cref="Drowned"/>.
    /// </summary>
    private bool WaterPhysics(float dt)
    {
        if (_ride is not (Car or Motorbike or Truck)) { Sinking = 0; return false; }
        if (!WaterField.TryLevelAt(GlobalPosition, out float level)) { Sinking = 0; return false; }
        float depth = level - GlobalPosition.Y;
        if (Sinking <= 0f && depth <= WadingDepth) return false;
        // back out of it on its own (a wave let go of it before it filled): only while still afloat
        if (Sinking < FloatSeconds && depth < 0.1f && IsOnFloor()) { Sinking = 0; return false; }

        Sinking += dt;
        var v = Velocity;
        // the hull ploughs: horizontal speed bleeds away, whatever the throttle says
        float drag = Mathf.Exp(-1.2f * dt);
        v.X *= drag;
        v.Z *= drag;
        _motion.Speed *= drag;
        if (Sinking < FloatSeconds)
        {
            // afloat, low in the water, riding the waves
            float draft = _ride.BodyHeight * 0.55f;
            float rise = (level - draft - GlobalPosition.Y) * 2.5f + WaterField.Velocity(GlobalPosition.X, GlobalPosition.Z, WaterField.Now).Y;
            v.Y = Mathf.Lerp(v.Y, rise, MathX.Damp(5f, dt));
        }
        else v.Y = Mathf.MoveToward(v.Y, -SinkSpeed, 3f * dt);
        Velocity = v;
        MoveAndSlide();

        if (Sinking >= FloatSeconds + SinkSeconds) Drowned(level);
        return true;
    }

    /// <summary>Lost to the water: wrecked where it went down (no fire, no blast under water), the driver out at the surface.</summary>
    private void Drowned(float level)
    {
        Sinking = 0;
        var state = CaptureVehicle(wrecked: true);
        Announced?.Invoke("SUNK!", false);
        PlayerInput.Rumble(0.6f, 0.4f, 0.5f);
        ApplyRide(RideKind.OnFoot, Vector3.Up * 1.5f);
        var at = Origin!.ToWorld(state.Position);
        GlobalPosition = at with { Y = Mathf.Max(at.Y, level - 0.2f) };
        // everyone aboard goes out with the driver
        if (OnlineSeats && (SeatIndex > 0 || Riders.Any())) PassengerService.Instance!.Wrecked(Vector3.Zero);
        SeatIndex = 0;
        Vehicles?.Park(state);
        GD.Print($"[water] {Name} sank at {at.X:F0},{at.Z:F0}, wrecked");
    }
}
