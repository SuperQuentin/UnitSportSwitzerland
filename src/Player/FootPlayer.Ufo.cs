using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// Flying the saucer (#861, docs/notes/player/ufo.md): the switches pressed (ramp / legs, beam,
/// lamps) become edges of the <see cref="FlightInput"/> intent, so a script or a bot flies it
/// through <see cref="FlyControls"/> the same way; the free height under the legs; getting out at
/// the foot of the ramp. The flight is <see cref="Ufo"/>, the beam <see cref="TractorBeams"/>.
/// </summary>
public partial class FootPlayer
{
    private const int UfoPressHatch = 1, UfoPressBeam = 2, UfoPressLights = 4;
    /// <summary>Switches pressed since the last flight step (bits), consumed by it.</summary>
    private int _ufoPress;
    private readonly RayQuery _belowRay = new();

    /// <summary>G / X, H / D-pad ←, L / D-pad → in the saucer's seat: remembered for the next step.</summary>
    private bool HandleUfoInput(InputEvent e)
    {
        if (!e.IsPressed() || e.IsEcho()) return false;
        if (e.IsActionPressed(PlayerInput.CarDoor)) _ufoPress |= UfoPressHatch;
        else if (e.IsActionPressed(PlayerInput.TractorBeam)) _ufoPress |= UfoPressBeam;
        else if (e.IsActionPressed(PlayerInput.LightsToggle)) _ufoPress |= UfoPressLights;
        else return false;
        return true;
    }

    /// <summary>Whether <paramref name="bit"/> was pressed since the last step; clears it.</summary>
    private bool TakeUfoPress(int bit)
    {
        bool pressed = (_ufoPress & bit) != 0;
        _ufoPress &= ~bit;
        return pressed;
    }

    /// <summary>Before the saucer's step: the free height under its legs, by a ray (terrain, roofs, the flat test world).</summary>
    private void UfoStep(Ufo ufo)
    {
        const float Look = 40f;
        var from = GlobalPosition + Vector3.Up * 0.5f;
        var hit = _belowRay.Cast(GetWorld3D().DirectSpaceState, from, from + Vector3.Down * Look, CollisionMask, SelfExclude);
        ufo.Below = hit.Count > 0 ? from.Y - 0.5f - hit["position"].AsVector3().Y : 999f;
        // no terrain to measure from (the flat test world): the HUD's height is the ray's
        if (Clearance >= 999f && hit.Count > 0) Clearance = ufo.Below;
    }

    /// <summary>Feet at <paramref name="feet"/> (put on the terrain when under it) if a person stands there clear; null if not.</summary>
    private Vector3? ClearStandAt(Vector3 feet)
    {
        if (!Indoors && Terrain != null && Terrain.TryGetHeight(feet, out float g) && Mathf.Abs(g - feet.Y) < 1.5f)
            feet = feet with { Y = g };
        _standProbe ??= new CapsuleShape3D { Radius = BodyRadius - 0.03f, Height = StandHeight };
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = _standProbe,
            Transform = new Transform3D(Basis.Identity, feet + Vector3.Up * (StandHeight * 0.5f + 0.1f)),
            CollisionMask = CollisionMask,
            Exclude = SelfExclude,
        };
        return GetWorld3D().DirectSpaceState.IntersectShape(query, 1).Count == 0 ? feet + Vector3.Up * 0.1f : null;
    }

    /// <summary>The saucer this player flies, or null.</summary>
    public Ufo? Saucer => _ride as Ufo;
}
