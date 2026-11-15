using System;
using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// <c>--moonshowcase</c> (a film of the Moon's features, dev): at each site of <c>--moonsites</c>, once its ground and
/// pieces are in, one glide of the camera filmed as its own clip (<c>test_output/clips/show_NN_&lt;id&gt;</c>, through
/// <see cref="ClipRecorder.Film"/>): round the site's own view by <see cref="Swing"/> degrees, closing in a little, over
/// <see cref="GlideSeconds"/>, eased at both ends. Inside a module it turns on the spot round the room's middle. A film tool:
/// run windowed with <c>--resolution 1920x1080 --fixed-fps 30</c> before <c>--</c> and <c>--clip-hq</c>, so no frame is lost.
/// The EVA colonist's run films its own parts (<c>--colonisteva --moonshowcase</c>: the airlock out, the drive, the way back
/// in) with a chase camera eased after the colonist.
/// </summary>
public partial class MoonSitesCheck
{
    public static bool Showcase => CmdArgs.Has("--moonshowcase");

    /// <summary>A site's glide, seconds (<c>--showseconds</c> to change it).</summary>
    private static readonly double GlideSeconds = CmdArgs.Value("--showseconds") is { } v
        && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 6.5;

    /// <summary>How far round its own view a site's glide turns, degrees.</summary>
    private const float Swing = 36;

    private double _showT = -1;
    private bool _showCut;
    private Vector3 _showEye, _showLook;
    private Camera3D? _showCam;

    /// <summary>The site's view, as the visit set it: the glide turns round it.</summary>
    private void ShowFrom(Camera3D cam, Vector3 eye, Vector3 look)
    {
        _showCam = cam;
        _showEye = eye;
        _showLook = look;
        _showT = -1;
        _showCut = false;
    }

    /// <summary>One frame of a site's glide; true once its clip is written.</summary>
    private bool ShowStep(MoonSite s, double delta)
    {
        if (_showCam == null) return true;
        if (_showT < 0)
        {
            ClipRecorder.Film(GetTree(), $"show_{_index:00}_{s.Id.Replace(':', '_')}");
            _showT = 0;
        }
        if (!_showCut)
        {
            _showT += delta;
            double u = Math.Clamp(_showT / GlideSeconds, 0, 1);
            float e = (float)(u * u * (3 - 2 * u));
            float turn = Mathf.DegToRad(Swing) * (e - 0.5f);
            // night views and outposts far off close in less, and every glide eases in by a tenth
            float k = Mathf.Lerp(1.06f, 0.92f, e);
            var arm = (_showEye - _showLook).Rotated(Vector3.Up, turn) * k;
            _showCam.GlobalPosition = _showLook + arm;
            _showCam.LookAt(_showLook, Vector3.Up);
            if (_showT >= GlideSeconds)
            {
                ClipRecorder.Cut();
                _showCut = true;
            }
            return false;
        }
        return !ClipRecorder.Busy;
    }

    // ---- the EVA colonist's parts, with a chase camera -------------------------------------------------------------

    private string? _evaClip;
    private double _evaClipT;
    private Vector3 _chaseAt, _chaseLook;
    private bool _chaseSet;

    /// <summary>
    /// The chase camera for the EVA run's film: close behind the colonist's shoulder inside (the tubes are 2.8 m across),
    /// further back and up outside and in a rover, eased after it (no jolt as it turns at a door).
    /// </summary>
    private void Chase(Player.FootPlayer body, ColonistBrain brain, double delta)
    {
        bool near = brain.Status.StartsWith("Walk") || brain.Status.StartsWith("Cycle") || brain.Status.StartsWith("at spot") && !brain.Outside;
        float back = brain.Driving ? 8f : near ? 2.6f : 5f, up = brain.Driving ? 3.2f : near ? 0.75f : 2f;
        var behind = body.GlobalBasis.Z with { Y = 0 };
        if (behind.LengthSquared() < 1e-4f) behind = Vector3.Back;
        behind = behind.Normalized();
        var want = body.GlobalPosition + behind * back + Vector3.Up * (1.5f + up);
        var look = body.GlobalPosition + Vector3.Up * (near ? 1.4f : 1.0f);
        // pulled in short of the first wall between the colonist and where it would stand (a player's chase camera does
        // the same): 2.6 m back in a 4 m airlock it stood in the walls and the rings (#1938)
        var ray = PhysicsRayQueryParameters3D.Create(look, want);
        ray.Exclude = new Godot.Collections.Array<Rid> { body.GetRid() };
        if (body.GetWorld3D().DirectSpaceState.IntersectRay(ray) is { Count: > 0 } hit)
        {
            var at = hit["position"].AsVector3();
            var dir = (want - look).Normalized();
            want = at - dir * 0.3f;
            if ((want - look).Length() < 0.6f) want = look + dir * 0.6f;
        }
        float f = _chaseSet ? 1 - Mathf.Exp(-(float)delta * 2.5f) : 1;
        _chaseAt = _chaseAt.Lerp(want, f);
        _chaseLook = _chaseLook.Lerp(look, f);
        _chaseSet = true;
        // and the eased place too: easing toward a pulled-in spot it would cross the wall on the way
        var eased = PhysicsRayQueryParameters3D.Create(look, _chaseAt);
        eased.Exclude = new Godot.Collections.Array<Rid> { body.GetRid() };
        if (body.GetWorld3D().DirectSpaceState.IntersectRay(eased) is { Count: > 0 } wall)
            _chaseAt = wall["position"].AsVector3() - (_chaseAt - look).Normalized() * 0.3f;
        _airCam ??= new Camera3D { Name = "AirlockShot", Fov = 70 };
        if (_airCam.GetParent() == null) AddChild(_airCam);
        _airCam.GlobalPosition = _chaseAt;
        _airCam.LookAt(_chaseLook, Vector3.Up);
        _airCam.MakeCurrent();
    }

    /// <summary>Films the EVA run's parts: each starts at its leg and runs its seconds (a clip at a time).</summary>
    private void EvaFilm(ColonistBrain brain, double delta)
    {
        if (!Showcase || _headless) return;
        if (_evaClip != null)
        {
            _evaClipT += delta;
            double length = _evaClip switch { "show_eva_out" => 16, "show_eva_drive" => 14, _ => 18 };
            if (_evaClipT >= length)
            {
                ClipRecorder.Cut();
                _evaClip = null;
            }
            return;
        }
        if (ClipRecorder.Busy) return;
        string? next = brain.Status switch
        {
            var st when st.StartsWith("Walk into the chamber") && !_evaFilmed.Contains("show_eva_out") => "show_eva_out",
            var st when st.StartsWith("Drive to the EVA") && !_evaFilmed.Contains("show_eva_drive") => "show_eva_drive",
            var st when st.StartsWith("Cycle open from outside") && !_evaFilmed.Contains("show_eva_in") => "show_eva_in",
            _ => null,
        };
        if (next == null) return;
        _evaFilmed.Add(next);
        ClipRecorder.Film(GetTree(), next);
        _evaClip = next;
        _evaClipT = 0;
    }

    private readonly System.Collections.Generic.HashSet<string> _evaFilmed = new();
}
