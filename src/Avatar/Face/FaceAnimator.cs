using Godot;

namespace UnitSport.Avatar.Face;

/// <summary>
/// Drives one figure's face (#657): eases its <see cref="FaceState"/> towards an expression, adds
/// a hit's grimace and where the eyes look, and hands the shader the result as instance uniforms
/// (<c>shaders/body/face.gdshaderinc</c>) only when it changed, on the figure's own node, so the
/// shared figure material stays shared and no mesh is rebuilt. Blinking and idle glances are the
/// shader's: a figure nobody drives (a bus passenger) is still alive.
/// Later drivers (mic #659, webcam #660, VR #661) write into the same state.
/// </summary>
public sealed class FaceAnimator
{
    private static readonly StringName UEyes = "face_eyes", UMouth = "face_mouth", UMood = "face_mood";

    /// <summary>The figure material's switch for the shader's own blink and glances (0 for stills).</summary>
    public static readonly StringName IdleParam = "face_idle";

    /// <summary>How fast a face follows its expression, 1/s.</summary>
    private const float Ease = 9f;
    /// <summary>The eyes follow a target this far off the nose at most, rad (gaze 1).</summary>
    private const float GazeReach = 0.7f;

    private FaceState _state = FaceState.Neutral;
    private static readonly Vector4 Unsent = new(float.NaN, 0f, 0f, 0f);
    private Vector4 _sentEyes = Unsent, _sentMouth = Unsent, _sentMood = Unsent;
    private float _pain;

    /// <summary>The state drawn last, for probes.</summary>
    public FaceState State => _state;

    /// <summary>Grimace for <see cref="FaceExpressions.PainSeconds"/>, over whatever the face was doing.</summary>
    public void Hurt() => _pain = FaceExpressions.PainSeconds;

    /// <summary>
    /// One frame: towards <paramref name="expression"/>, looking at <paramref name="look"/> (global;
    /// null: ahead) from a head <paramref name="headHeight"/> up the figure, which faces −Z.
    /// </summary>
    public void Step(GeometryInstance3D node, FaceExpression expression, Vector3? look, float dt, float headHeight = 1.62f)
    {
        var target = FaceExpressions.Of(_pain > 0f ? FaceExpression.Pain : expression);
        _pain = Mathf.Max(0f, _pain - dt);
        if (look is { } at)
        {
            var dir = node.GlobalTransform.AffineInverse() * at - new Vector3(0f, headHeight, 0f);
            float yaw = Mathf.Atan2(-dir.X, -dir.Z);
            // behind the head, the eyes give up rather than roll back
            if (Mathf.Abs(yaw) < 1.6f)
            {
                float pitch = Mathf.Atan2(-dir.Y, new Vector2(dir.X, dir.Z).Length());
                target.GazeX = Mathf.Clamp(target.GazeX + yaw / GazeReach, -1f, 1f);
                target.GazeY = Mathf.Clamp(target.GazeY + pitch / GazeReach, -1f, 1f);
            }
        }
        _state = FaceState.Lerp(_state, target, 1f - Mathf.Exp(-Ease * dt));
        // the special eyes switch at once, not halfway through the ease
        _state.Special = target.Special;
        Send(node);
    }

    /// <summary>Sets <paramref name="state"/> on <paramref name="node"/> at once (stills, probes).</summary>
    public static void Apply(GeometryInstance3D node, in FaceState state)
    {
        var (a, b, c, d) = state.Eyes;
        node.SetInstanceShaderParameter(UEyes, new Vector4(a, b, c, d));
        (a, b, c, d) = state.Mouth;
        node.SetInstanceShaderParameter(UMouth, new Vector4(a, b, c, d));
        (a, b, c, d) = state.Mood;
        node.SetInstanceShaderParameter(UMood, new Vector4(a, b, c, d));
    }

    // only what changed by more than a pixel's worth, each uniform on its own
    private void Send(GeometryInstance3D node)
    {
        var (a, b, c, d) = _state.Eyes;
        var eyes = Snap(new Vector4(a, b, c, d));
        (a, b, c, d) = _state.Mouth;
        var mouth = Snap(new Vector4(a, b, c, d));
        (a, b, c, d) = _state.Mood;
        var mood = Snap(new Vector4(a, b, c, d));
        if (eyes != _sentEyes) node.SetInstanceShaderParameter(UEyes, _sentEyes = eyes);
        if (mouth != _sentMouth) node.SetInstanceShaderParameter(UMouth, _sentMouth = mouth);
        if (mood != _sentMood) node.SetInstanceShaderParameter(UMood, _sentMood = mood);
    }

    private static Vector4 Snap(Vector4 v) => (v * 32f).Round() / 32f;

    /// <summary>Forget what was sent: the node was rebuilt and lost its uniforms.</summary>
    public void Reset() => _sentEyes = _sentMouth = _sentMood = Unsent;
}
