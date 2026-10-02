using Godot;
using UnitSport.Core;

namespace UnitSport.Avatar;

/// <summary>
/// The air a figure feels, measured from how its own node moves (#251): no speed is replicated for
/// it, so the owner's figure and every remote copy of it agree. Returned in the figure's author
/// space (+Z forward, what <see cref="HumanPalette.Wind"/> takes), eased so a bump in the road or a
/// network correction does not flick a skirt.
/// </summary>
public sealed class FigureWind
{
    private Vector3 _last;
    private bool _started;

    /// <summary>The smoothed wind, m/s, author space.</summary>
    public Vector3 Wind { get; private set; }

    /// <summary>
    /// Measures one frame. <paramref name="node"/> is the figure's own node: a mesh built by
    /// <see cref="MeshScratch"/>, so facing −Z.
    /// </summary>
    public Vector3 Update(Node3D node, float dt)
    {
        if (!node.IsInsideTree() || dt <= 0f) return Wind;
        var at = node.GlobalPosition;
        if (!_started)
        {
            _last = at;
            _started = true;
            return Wind;
        }
        var velocity = (at - _last) / dt;
        _last = at;
        // a teleport or a respawn, not a gale
        if (velocity.LengthSquared() > 90f * 90f) velocity = Vector3.Zero;
        var local = node.GlobalBasis.Orthonormalized().Inverse() * velocity;
        // node space faces −Z; author space faces +Z (the half turn MeshScratch.Build makes)
        var air = -new Vector3(-local.X, local.Y, -local.Z);
        Wind = Wind.Lerp(air, MathX.Damp(5f, dt));
        return Wind;
    }
}
