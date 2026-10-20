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
    private float _snowFriction = 1f;

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
            if (onFloor) { Slope = null; _snowFriction = 1f; }
            return;
        }
        Slope = slope;
        SlopeSnow = state;
        _snowFriction = Mathf.Lerp(1f, state.FrictionScale, mask);
    }
}
