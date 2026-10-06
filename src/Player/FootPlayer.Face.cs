using Godot;
using UnitSport.Avatar.Face;

namespace UnitSport.Player;

/// <summary>
/// The walker's face (#657, docs/notes/avatar/procedural-faces.md): an expression from state every
/// peer already has (the emote or dance, being down, a blow's flinch), the eyes on a camera close
/// by. Nothing new on the network: each peer drives its own copy. Blinking and idle glances are
/// the shader's own.
/// </summary>
public partial class FootPlayer
{
    private readonly FaceAnimator _face = new();

    /// <summary>Past this from the camera a face stops moving, m: a few pixels nobody reads.</summary>
    private const float FaceDrawDistance = 30f;
    /// <summary>Within this a face looks at the camera, m.</summary>
    private const float FaceLookDistance = 8f;

    /// <summary>The face's expression, the state drawn last, for probes.</summary>
    public FaceState DrawnFace => _face.State;

    /// <summary>What the face shows, from replicated state only, so every peer agrees.</summary>
    public FaceExpression FaceMood =>
        Down == 1 ? FaceExpression.Out
        : Downed ? FaceExpression.Pain
        : Emote >= 0 ? FaceExpressions.ForEmote(Emote)
        : DanceId != 0 ? FaceExpression.Happy
        : FaceExpression.Neutral;

    private void StepFace(float dt)
    {
        if (_walker is not { Visible: true } walker) return;
        Vector3? look = null;
        if (GetViewport()?.GetCamera3D() is { } cam)
        {
            float dist = cam.GlobalPosition.DistanceTo(walker.GlobalPosition);
            if (dist > FaceDrawDistance) return;
            if (dist < FaceLookDistance) look = cam.GlobalPosition;
        }
        _face.Step(walker, FaceMood, look, dt);
    }
}
