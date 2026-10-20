using Godot;
using UnitSport.Terrain.Format;
using UnitSport.World.Snow;

namespace UnitSport.Player;

/// <summary>
/// On skis, the slope underfoot and its snow (#887): the friction the skis feel and what the HUD
/// shows. Looked up every <see cref="SnowLookTicks"/> physics ticks, not every one: the snow
/// changes over in-game minutes, and a run crosses a slope's edge in seconds, not frames.
/// </summary>
public partial class FootPlayer
{
    private const int SnowLookTicks = 15;

    private int _snowTick;
    private float _snowFriction = 1f, _snowGrip = 1f;
    private Vector3 _rideAirVelocity;

    /// <summary>The slope under these skis, or null (off a slope, or not on skis).</summary>
    public PisteRecord? Slope { get; private set; }

    /// <summary>The snow on <see cref="Slope"/> here and now.</summary>
    public SnowState SlopeSnow { get; private set; }

    private void LookAtSnow(bool onFloor)
    {
        if (++_snowTick < SnowLookTicks) return;
        _snowTick = 0;
        if (!onFloor || SnowService.Instance is not { } snow || !snow.TryAt(GlobalPosition, out var slope, out var state, out float mask))
        {
            // in the air the last slope still counts; off any slope the skis keep their old snow friction
            if (onFloor) { Slope = null; _snowFriction = 1f; _snowGrip = 1f; }
            return;
        }
        Slope = slope;
        SlopeSnow = state;
        _snowFriction = Mathf.Lerp(1f, state.FrictionScale, mask);
        _snowGrip = Mathf.Lerp(1f, state.GripScale, mask);
    }

    /// <summary>
    /// A landing on skis by its equivalent fall height (#899): the speed into the slope, not the
    /// drop. Down a steep landing the same jump is soft; onto the flat it is hard. A stumble costs a
    /// quarter of the speed, a fall is the bail every ride has.
    /// </summary>
    private void LandSkis()
    {
        float into = Mathf.Max(0f, -_rideAirVelocity.Dot(GetFloorNormal()));
        switch (SkiDynamics.Landing(into))
        {
            case 1:
                float lost = _motion.Speed * 0.25f;
                _motion.Speed -= lost;
                Impacted?.Invoke(lost);
                Announced?.Invoke("STUMBLE", false);
                break;
            case 2:
                Impacted?.Invoke(Mathf.Max(_motion.Speed, 6f));
                _motion.Speed = 0f;
                _bailTimer = 1.2f;
                Announced?.Invoke("BAIL!", false);
                break;
        }
    }
}
