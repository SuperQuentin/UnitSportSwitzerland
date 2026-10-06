namespace UnitSport.Avatar.Face;

// Plain C#, no Godot: linked into the unit tests (tests/UnitSportSwitzerland.Tests).

/// <summary>Eyes drawn as something else: crosses, hearts, spirals, closed happy arcs, a wink.</summary>
public enum FaceEyes { Normal, Cross, Hearts, Spirals, Happy, Wink }

/// <summary>A face's mood: a <see cref="FaceState"/> to ease towards (<see cref="FaceExpressions.Of"/>).</summary>
public enum FaceExpression { Neutral, Happy, Joy, Sad, Angry, Surprised, Pain, Sleepy, Dizzy, Love, Wink, Focused, Unsure, Rock, Out }

/// <summary>
/// What a face is doing (#657): a small expression vector shaped like the ARKit / VRM blendshapes
/// it will one day be fed from (mic, webcam, VR face tracking), so any of them maps onto it. The
/// shader reads it as three instance uniforms (<see cref="Eyes"/>, <see cref="Mouth"/>,
/// <see cref="Mood"/>); blinking and glances it adds by itself on top.
/// </summary>
public struct FaceState
{
    /// <summary>Each eye 0 shut .. 1 open (the viewer's left, then right), times the shader's own blink.</summary>
    public float OpenL, OpenR;
    /// <summary>Where the eyes look, −1..1 (x the viewer's right, y down), plus the shader's glances.</summary>
    public float GazeX, GazeY;
    /// <summary>−1 a frown .. 1 a smile.</summary>
    public float Smile;
    /// <summary>0 shut .. 1 wide open.</summary>
    public float Jaw;
    /// <summary>−1 pursed (an "oo") .. 1 stretched wide (an "ee").</summary>
    public float Wide;
    /// <summary>−1 knitted down (anger) .. 1 raised (surprise).</summary>
    public float Brow;
    /// <summary>0..1 blush over the face's own.</summary>
    public float Blush;
    /// <summary>0..1: the eyes narrowed.</summary>
    public float Squint;
    public FaceEyes Special;

    public static FaceState Neutral => new() { OpenL = 1f, OpenR = 1f };

    /// <summary>From <paramref name="a"/> to <paramref name="b"/> by <paramref name="t"/>; the special eyes switch halfway.</summary>
    public static FaceState Lerp(in FaceState a, in FaceState b, float t) => new()
    {
        OpenL = L(a.OpenL, b.OpenL, t), OpenR = L(a.OpenR, b.OpenR, t),
        GazeX = L(a.GazeX, b.GazeX, t), GazeY = L(a.GazeY, b.GazeY, t),
        Smile = L(a.Smile, b.Smile, t), Jaw = L(a.Jaw, b.Jaw, t), Wide = L(a.Wide, b.Wide, t),
        Brow = L(a.Brow, b.Brow, t), Blush = L(a.Blush, b.Blush, t), Squint = L(a.Squint, b.Squint, t),
        Special = t < 0.5f ? a.Special : b.Special,
    };

    private static float L(float a, float b, float t) => a + (b - a) * t;

    /// <summary>The three uniforms' values, each a vec4: eyes (open L, open R, gaze x, gaze y).</summary>
    public (float, float, float, float) Eyes => (OpenL, OpenR, GazeX, GazeY);

    /// <summary>Mouth and brow: (smile, jaw, wide, brow).</summary>
    public (float, float, float, float) Mouth => (Smile, Jaw, Wide, Brow);

    /// <summary>Mood: (blush, squint, special eyes, the blink seed's offset, here 0).</summary>
    public (float, float, float, float) Mood => (Blush, Squint, (float)Special, 0f);
}

/// <summary>The expressions, and which an emote or a state shows.</summary>
public static class FaceExpressions
{
    /// <summary>The face <paramref name="e"/> eases towards; gaze stays where the caller puts it.</summary>
    public static FaceState Of(FaceExpression e)
    {
        var s = FaceState.Neutral;
        switch (e)
        {
            case FaceExpression.Happy: s.Smile = 0.7f; s.Brow = 0.2f; break;
            case FaceExpression.Joy: s.Smile = 1f; s.Jaw = 0.55f; s.Brow = 0.5f; s.Special = FaceEyes.Happy; s.Blush = 0.4f; break;
            case FaceExpression.Sad: s.Smile = -0.7f; s.Brow = 0.4f; s.OpenL = s.OpenR = 0.7f; s.GazeY = 0.5f; break;
            case FaceExpression.Angry: s.Smile = -0.5f; s.Brow = -1f; s.Squint = 0.5f; s.Wide = 0.3f; break;
            case FaceExpression.Surprised: s.Jaw = 0.6f; s.Wide = -0.6f; s.Brow = 1f; break;
            case FaceExpression.Pain: s.Smile = -0.6f; s.Jaw = 0.3f; s.Wide = 0.8f; s.Brow = -0.6f; s.Squint = 1f; break;
            case FaceExpression.Sleepy: s.OpenL = s.OpenR = 0.25f; s.Brow = -0.1f; break;
            case FaceExpression.Dizzy: s.Special = FaceEyes.Spirals; s.Jaw = 0.25f; s.Smile = -0.2f; break;
            case FaceExpression.Love: s.Special = FaceEyes.Hearts; s.Smile = 0.8f; s.Blush = 1f; break;
            case FaceExpression.Wink: s.Special = FaceEyes.Wink; s.Smile = 0.6f; s.Brow = 0.2f; break;
            case FaceExpression.Focused: s.Squint = 0.4f; s.Brow = -0.3f; s.Smile = 0.1f; break;
            case FaceExpression.Unsure: s.Smile = -0.25f; s.Brow = 0.8f; s.Wide = -0.3f; s.GazeY = -0.3f; break;
            case FaceExpression.Rock: s.Jaw = 0.8f; s.Wide = 0.4f; s.Brow = -0.7f; s.Squint = 0.8f; break;
            case FaceExpression.Out: s.Special = FaceEyes.Cross; s.Jaw = 0.35f; s.Smile = -0.3f; break;
        }
        return s;
    }

    /// <summary>
    /// What emote <paramref name="index"/> of the wheel's catalog (<c>HumanMeshBuilder.EmoteTable</c>,
    /// append only) shows: the gestures by name, the dances happy or joyful in turn.
    /// </summary>
    public static FaceExpression ForEmote(int index) => index switch
    {
        0 => FaceExpression.Happy,      // wave
        1 => FaceExpression.Joy,        // cheer
        2 => FaceExpression.Focused,    // salute
        3 => FaceExpression.Unsure,     // shrug
        4 => FaceExpression.Happy,      // clap
        5 => FaceExpression.Wink,       // dab
        6 => FaceExpression.Joy,        // fist pump
        7 or 8 => FaceExpression.Rock,  // headbang, air guitar
        9 => FaceExpression.Happy,      // arm wave
        29 => FaceExpression.Love,      // rat dance
        < 0 => FaceExpression.Neutral,
        _ => index % 3 == 0 ? FaceExpression.Joy : index % 3 == 1 ? FaceExpression.Happy : FaceExpression.Wink,
    };

    /// <summary>How long a hit's grimace lasts, s.</summary>
    public const float PainSeconds = 0.7f;
}
