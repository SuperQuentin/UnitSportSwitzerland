using Godot;
using UnitSport.Audio.Cd;
using static UnitSport.Interiors.InteriorMeshBuilder;

namespace UnitSport.Interiors;

/// <summary>
/// The intro (#370): the chess type beat opens on five hits before the trumpet, the rat strikes a
/// pose on each (<see cref="ChurchStage.RatIntro"/>), and every player in the church watches it
/// through a camera that cuts to a new angle on every hit; on the trumpet their own camera, and
/// their controls, come back. Only when the play is seen to start inside the intro: someone who
/// walks in, or joins, later goes straight to the dance.
/// </summary>
public partial class ChurchStage
{
    private Camera3D? _introCam;
    private Camera3D? _ownCam;

    private void StartIntro(CdInfo cd, double t)
    {
        if (t >= IntroEndOf(cd) - 0.1 || _rat < 0) return;
        if (!LocalHere() || XR.XrSession.Active) return;
        var own = GetViewport().GetCamera3D();
        if (own == null) return;
        _ownCam = own;
        _introCam = new Camera3D
        {
            Name = "IntroCamera", Fov = 60f, Near = 0.05f, Far = own.Far,
            Environment = own.Environment, Attributes = own.Attributes, CullMask = own.CullMask,
        };
        AddChild(_introCam);
        _introCam.MakeCurrent();
        // the panel the player pressed Play on gets out of the shot; the controls wait for the trumpet
        Items.RadioUi.Instance?.Close();
        Core.UiFocus.Set(this, true);
    }

    private void StepIntro(float t, bool intro, CdInfo cd)
    {
        if (_introCam == null) return;
        // the last shot holds through the jump on the trumpet, then the player has their camera back
        if (!intro && t >= IntroEndOf(cd) + HopTime || !IsInstanceValid(_ownCam)) { StopIntro(); return; }
        int k = Math.Max(0, HitAt(cd, t));
        var rat = _figures[_rat];
        float headY = rat.Parts[RatParts.Head].Pivot.Y + 0.14f;
        float side = AwayFromAltar(rat);
        // the shots, in the rat's frame (it faces +Z, toward the pews)
        // the head as posed this frame, in the rat's frame: the shots follow the crouch and the lean
        var headNow = rat.Frame.AffineInverse() * ToLocal(_parts[_rat][RatParts.Head].GlobalPosition) + new Vector3(0, 0.12f, 0);
        var (eye, look, fov) = k switch
        {
            0 => (new Vector3(0, 0.5f, 4.5f), new Vector3(0, headY * 0.8f, 0), 60f),           // low and wide, from the aisle
            1 => (new Vector3(0.12f, headY, 0.95f), headNow, 45f),                           // the face
            2 => (new Vector3(0.6f * side, 2.6f, -1.1f), new Vector3(0, headY * 0.6f, 0.2f), 60f), // high, from behind the altar
            // three-quarters from the front, on the side away from the altar: clear of the radio stand too
            3 => (new Vector3(0.9f * side, headY * 0.75f, 1.6f), headNow - new Vector3(0, 0.25f, 0), 50f),
            // low and close, looking up at the crouch about to spring: the whole rat, mitre to feet
            // the crouch, then the jump: wide and low, the camera still so the jump reads, room above the mitre
            _ => (new Vector3(0.25f * side, 0.35f, 2.8f), new Vector3(0, headY * 0.75f, 0), 60f),
        };
        var frame = rat.Frame;
        var at = frame * eye;
        _introCam.Position = at;
        _introCam.Fov = fov;
        var target = frame * look;
        if (!at.IsEqualApprox(target)) _introCam.LookAt(ToGlobal(target), Vector3.Up);
    }

    private void StopIntro()
    {
        if (_introCam == null) return;
        if (_ownCam != null && IsInstanceValid(_ownCam)) _ownCam.MakeCurrent();
        _introCam.QueueFree();
        _introCam = null;
        _ownCam = null;
        Core.UiFocus.Set(this, false);
    }

    /// <summary>The church a probe stands in with no <see cref="InteriorManager"/> (<c>ChurchStageProbe</c>).</summary>
    internal static string? ProbePlan;

    /// <summary>Whether the local player is in this church.</summary>
    private bool LocalHere() => (InteriorManager.Instance?.Current?.Key ?? ProbePlan) == _plan;

    /// <summary>+1 or -1: the rat's side (its local X) away from the altar, for the profile shot.</summary>
    private float AwayFromAltar(Figure rat)
    {
        foreach (var f in _interior.Layout.Furniture)
        {
            if (f.Type != FurnitureType.Altar) continue;
            var local = rat.Frame.AffineInverse() * new Vector3(f.X, rat.Frame.Origin.Y, f.Z);
            return local.X > 0 ? -1f : 1f;
        }
        return 1f;
    }
}
