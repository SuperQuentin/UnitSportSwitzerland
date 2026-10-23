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
    /// <summary>The dance's CD has a vocal analysis: the lips move at a voice's speed.</summary>
    private bool _singing;

    /// <summary>Past this from the camera a face stops moving, m: a few pixels nobody reads.</summary>
    public const float FaceDrawDistance = 30f;
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
        : PhoneState == PhoneSelfieState ? SelfieFace()
        : FaceExpression.Neutral;

    /// <summary>A selfie's faces (#889), one after the other, a few seconds each.</summary>
    private static readonly FaceExpression[] SelfieFaces = { FaceExpression.Happy, FaceExpression.Joy, FaceExpression.Wink, FaceExpression.Love };

    /// <summary>The selfie face now: off the shared clock, so every peer shows the same one at the same moment.</summary>
    private static FaceExpression SelfieFace() =>
        SelfieFaces[(int)(Math.Max(0.0, Net.ClockSync.ServerNow) / 2.5) % SelfieFaces.Length];

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
        // a selfie: into the phone's lens
        if (PhoneState == PhoneSelfieState && _phoneModel is { Visible: true } phone)
            look = phone.GlobalTransform * Phone.PhoneModel.LensPoint;
        if (DanceFaceNow() is { } face) _face.Step(walker, face, look, dt, ease: _singing ? FaceAnimator.LipEase : 9f);
        else _face.Step(walker, FaceMood, look, dt);
    }

    /// <summary>
    /// Dancing to music (#728), the radio's dance or one picked on the wheel (#802): the face follows
    /// the CD's analysis on the shared clock (<see cref="DanceFace"/>), the same on every peer. Null
    /// otherwise, and for the wheel's gestures.
    /// </summary>
    private FaceState? DanceFaceNow()
    {
        if (DanceId == 0 || (Emote >= 0 && !FaceExpressions.FollowsMusic(Emote)) || Down == 1 || Downed || DrawnDance is not { Weight: > 0.5f } d
            || _danceMusic is not { } m || !IsInstanceValid(m.Source)) return null;
        var g = Items.RadioGroove.Of(m.CdId, m.StartedAt, Net.ClockSync.ServerNow);
        if (!g.Beating) return null;
        // where a break set is: footwork and power moves concentrate, the freeze is cool
        float beats = (d.Bar & 1) * 4f + (d.BarPhase - Mathf.Floor(d.BarPhase)) * 4f;
        int floor = d.Move == Avatar.HumanMeshBuilder.BreakDown ? (beats >= 5f ? 1 : 0)
            : Avatar.HumanMeshBuilder.IsBreakPower(d.Move) ? (beats < 3.7f ? 1 : beats < 7f ? 2 : 0)
            : 0;
        var hearing = new DanceHearing((int)d.Style, (int)g.Section, g.Level, g.Kick, g.BarKick, g.Burst, g.Beat, d.Bar, floor,
            g.Voice, g.Syllable, g.Vowel);
        _singing = g.Voice >= 0f;
        return DanceFace.Target(hearing, DanceSeed());
    }
}
