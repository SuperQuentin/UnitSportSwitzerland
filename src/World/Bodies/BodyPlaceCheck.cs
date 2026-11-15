using System;
using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--bodyplacecheck [all|saturn|saturnnear|jupiter|moon]</c> (#2036, windowed: a headless run draws no frame, so nothing
/// is placed before one; <c>--frame earth --chunks fixture:straight --bodies DIR</c>): every part of a body drawn where the
/// body is, at any speed. The fly camera is put over a body (<c>/goto</c>), looking straight down at it, and strafes past it
/// for <see cref="Seconds"/> at its own full speed (half the distance to the surface a second) with the boost held (six
/// times that: the user's flight), moved by its own step in its <c>_Process</c> as a player's keys move it. A part placed a
/// frame behind the camera is a frame's travel off: at the boost by Saturn about 1° (tens of pixels). Every frame, just before it is drawn (the last of the frame's
/// <see cref="RenderingServer.FramePreDraw"/> handlers), each part's centre is put on the screen against the body's true
/// centre from the frame tree, pixels:
/// <list type="bullet">
/// <item>the globe (<see cref="BodyGlobe.DrawnCentre"/>), shrunk about the camera past the far plane;</item>
/// <item>the rings' slab: its centre, and its plane (the camera's height over it the shader is given, against the one the
/// drawn centre and pole give, as an angle seen from the camera);</item>
/// <item>the air's shell (<see cref="Atmosphere"/>, on the anchor's body);</item>
/// <item>the sky's sphere for the body (<see cref="SkyBodies"/>; the Moon's <see cref="MoonDisc"/>).</item>
/// </list>
/// Each must stay within <see cref="MaxPx"/>. Saturn far is the user's picture (2.3 million km up); near, its globe fills the
/// view; Jupiter is another giant; the Moon is seen from the Earth's side (another body's globe under the root).
/// First the AIR readout (the jump's airtime popup): up on foot, gone once the fly camera parks the walker. Without
/// <c>--nohud</c> (it hides every layer). Pictures in <c>test_output/bodies/bodyplace_*.png</c>.
/// </summary>
public partial class BodyPlaceCheck : Node
{
    public static bool Requested => CmdArgs.Has("--bodyplacecheck");

    /// <summary>How far any part may be drawn off the body's true centre, pixels.</summary>
    public const float MaxPx = 1f;

    /// <summary>How long each pass strafes at full speed, seconds.</summary>
    public const double Seconds = 3;

    private readonly WorldOrigin _origin;
    private int _failed;

    private sealed record Leg(string Name, BodyId Body, double Lat, double Lon, double Km);

    private static readonly Leg[] Legs =
    [
        // the user's picture: 2.3 million km over Saturn (past /goto's million: the camera is moved out from there)
        new("saturn", BodyId.Saturn, 2.294, 26.957, 2_300_000),
        new("saturnnear", BodyId.Saturn, 14.0, 138.5, 250_000),
        new("jupiter", BodyId.Jupiter, 10.0, 40.0, 1_500_000),
        // from the Earth's side (past the Moon's sphere of influence): another body's globe, or the sky's disc
        new("moon", BodyId.Moon, 10.0, 0.0, 150_000),
    ];

    /// <summary>/goto's highest (<see cref="BodyGoto"/>), km.</summary>
    private const double GotoMaxKm = 1_000_000;

    /// <summary>A body's sphere of influence's radius, km, roughly: past it the anchor is its parent's.</summary>
    private static double SphereKm(BodyId body) => body == BodyId.Moon ? 66_000 : 1e9;

    public BodyPlaceCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "BodyPlaceCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[bodyplace] {what}"));

    private void Expect(bool ok, string what)
    {
        Log((ok ? "ok   " : "FAIL ") + what);
        if (!ok) _failed++;
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Wait(double seconds)
    {
        double until = GameClock.Now + seconds;
        while (GameClock.Now < until) await Frame();
    }

    private async Task Run()
    {
        await Wait(3);
        if (GetTree().Root.FindChild(ChatManager.NodeName, recursive: true, owned: false) is not ChatManager chat) { Finish("no chat"); return; }
        _air = Find<Atmosphere>(GetTree().Root);
        _moonDisc = Find<MoonDisc>(GetTree().Root);
        string which = CmdArgs.Value("--bodyplacecheck") is { Length: > 0 } v && !v.StartsWith("--", StringComparison.Ordinal) ? v : "all";
        // the AIR readout (#2036): on foot (a run starts in the fly camera: the spectator key), a landing's popup up, then
        // the fly camera again, which parks the walker (ClientWorld.SetBodyParked): the popup must go with its layer.
        // Run without --nohud (it hides every layer)
        await Toggle();
        await Wait(3);
        var feel = Find<Player.PlayerFeel>(GetTree().Root);
        feel?.Popup("AIR  0.9 s", true);
        await Frame();
        bool shownOnFoot = feel?.ScreenShown == true;
        await Toggle();
        await Wait(0.5);
        bool goneParked = feel != null && !feel.ScreenShown;
        Log($"the walker {(feel == null ? "never came" : "came")}; the fly camera {(GetViewport()?.GetCamera3D() is Player.SpectatorCamera ? "is" : "is not")} the view again");
        RenderingServer.FramePreDraw += Measure;
        try
        {
            foreach (var leg in Legs)
                if (which == "all" || which == leg.Name) await Fly(chat, leg);
            Expect(shownOnFoot && goneParked, $"the walker's AIR popup up ({shownOnFoot}), gone once the walker is parked ({goneParked})");
        }
        finally
        {
            RenderingServer.FramePreDraw -= Measure;
            Input.ActionRelease(PlayerInput.MoveRight);
            Input.ActionRelease(PlayerInput.FlyBoost);
        }
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    /// <summary>The spectator key (Shift+T): the fly camera to the walker or back, through ClientWorld's own input.</summary>
    private async Task Toggle()
    {
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.Spectator, Pressed = true });
        await Frame();
        Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.Spectator, Pressed = false });
        await Frame();
    }

    private static T? Find<T>(Node n) where T : class
    {
        if (n is T t) return t;
        foreach (var c in n.GetChildren())
            if (Find<T>(c) is { } found) return found;
        return null;
    }

    private Atmosphere? _air;
    private MoonDisc? _moonDisc;

    // the pass being measured, and its worst offsets, pixels
    private Leg? _leg;
    private int _frames, _globeFrames, _ringFrames, _airFrames, _skyFrames;
    private float _globe, _ringCentre, _ringPlane, _air2, _sky;

    private void Reset(Leg? leg)
    {
        _leg = leg;
        _frames = _globeFrames = _ringFrames = _airFrames = _skyFrames = 0;
        _globe = _ringCentre = _ringPlane = _air2 = _sky = 0;
    }

    /// <summary>Last of the frame's placing: every part's centre on the screen against the body's true one.</summary>
    private void Measure()
    {
        if (_leg is not { } leg || GetViewport()?.GetCamera3D() is not { } cam) return;
        var body = CelestialBody.Of(leg.Body);
        var truth = _origin.ToWorld(GlobalPos.On(leg.Body, new EcefPos(0, 0, 0)));
        if (cam.IsPositionBehind(truth)) return;
        var at = cam.UnprojectPosition(truth);
        float Off(Vector3 p) => cam.IsPositionBehind(p) ? float.PositiveInfinity : cam.UnprojectPosition(p).DistanceTo(at);
        _frames++;
        var globe = BodyViews.Instance?.GlobeOf(leg.Body);
        if (globe?.DrawnCentre is { } g)
        {
            _globeFrames++;
            _globe = Math.Max(_globe, Off(g));
            if (globe.RingsDrawn is { } rings)
            {
                _ringFrames++;
                _ringCentre = Math.Max(_ringCentre, Off(rings.Centre));
                // the plane: the camera's height over the slab the shader is given, against the drawn centre's and pole's
                var fromCentre = cam.GlobalPosition - rings.Centre;
                float height = fromCentre.Dot(rings.Pole);
                float focal = GetViewport().GetVisibleRect().Size.Y * 0.5f / Mathf.Tan(Mathf.DegToRad(CameraLens.FovOf(cam)) * 0.5f);
                _ringPlane = Math.Max(_ringPlane, Math.Abs(height - rings.Height) / Math.Max(fromCentre.Length(), 1e-3f) * focal);
            }
        }
        if (_origin.Body == leg.Body && body.Air != null && _air?.ShellCentre is { } shell)
        {
            _airFrames++;
            _air2 = Math.Max(_air2, Off(shell));
        }
        if (leg.Body == BodyId.Moon)
        {
            if (_moonDisc is { Visible: true } disc && !MoonDisc.HiddenByGlobe)
            {
                _skyFrames++;
                _sky = Math.Max(_sky, Off(disc.GlobalPosition));
            }
        }
        else if (body.Sky is { } sky && SkyBodies.Instance?.PlacedAt(sky) is { } placed && !SkyBodies.HiddenByGlobe.Contains(sky))
        {
            _skyFrames++;
            _sky = Math.Max(_sky, Off(placed));
        }
    }

    private async Task Fly(ChatManager chat, Leg leg)
    {
        var body = CelestialBody.Of(leg.Body);
        chat.Send(string.Create(CultureInfo.InvariantCulture, $"/goto {body.Name.ToLowerInvariant()} {leg.Lat:F3} {leg.Lon:F3} {Math.Min(leg.Km, GotoMaxKm):F0}"));
        await Wait(1.5);
        if (GetViewport()?.GetCamera3D() is not Player.SpectatorCamera cam) { Expect(false, $"{leg.Name}: no fly camera"); return; }
        if (_origin.Body != leg.Body && (leg.Km <= SphereKm(leg.Body))) { Expect(false, $"{leg.Name}: /goto left the anchor on {_origin.Body}"); return; }
        if (leg.Km > GotoMaxKm)
        {
            // on out along the vertical there, in the check's own frame (the shifter re-centres it next frame)
            cam.GlobalPosition += _origin.UpAt(cam.GlobalPosition) * (float)((leg.Km - GotoMaxKm) * 1000);
            await Wait(1);
        }
        // straight down at the body, at the wheel's top (full speed: the cap)
        var down = (_origin.ToWorld(GlobalPos.On(leg.Body, new EcefPos(0, 0, 0))) - cam.GlobalPosition).Normalized();
        var hint = Mathf.Abs(down.Dot(Vector3.Up)) > 0.9f ? Vector3.Forward : Vector3.Up;
        cam.GlobalBasis = Basis.LookingAt(down, hint);
        cam.Speed = Player.SpectatorSpeed.MaxSpeed;
        await Wait(1);
        Shot($"bodyplace_{leg.Name}_still");

        Reset(leg);
        Input.ActionPress(PlayerInput.MoveRight);
        Input.ActionPress(PlayerInput.FlyBoost);
        double t0 = GameClock.Now;
        bool shot = false;
        while (GameClock.Now - t0 < Seconds)
        {
            await Frame();
            // keep the body in view: the look turned to it at the start of each frame (the strafe moves the camera after)
            var toBody = (_origin.ToWorld(GlobalPos.On(leg.Body, new EcefPos(0, 0, 0))) - cam.GlobalPosition).Normalized();
            if (toBody.IsFinite() && toBody.LengthSquared() > 0.5f)
                cam.GlobalBasis = Basis.LookingAt(toBody, Mathf.Abs(toBody.Dot(cam.GlobalBasis.Y)) > 0.95f ? cam.GlobalBasis.Z : cam.GlobalBasis.Y);
            if (!shot && GameClock.Now - t0 > Seconds / 2)
            {
                Shot($"bodyplace_{leg.Name}_moving");
                shot = true;
            }
        }
        Input.ActionRelease(PlayerInput.MoveRight);
        Input.ActionRelease(PlayerInput.FlyBoost);
        var (frames, globeFrames, ringFrames, airFrames, skyFrames) = (_frames, _globeFrames, _ringFrames, _airFrames, _skyFrames);
        var (globe, ringCentre, ringPlane, air, sky) = (_globe, _ringCentre, _ringPlane, _air2, _sky);
        Reset(null);
        Log(string.Create(CultureInfo.InvariantCulture,
            $"{leg.Name}: {frames} frames at full speed and boost ({cam.FlownSpeed * cam.BoostMultiplier / 1000:F0} km/s at the end), the anchor on {_origin.Body}; off the true centre at worst: globe {globe:F2} px ({globeFrames} frames), rings' centre {ringCentre:F2} px and plane {ringPlane:F2} px ({ringFrames}), air {air:F2} px ({airFrames}), sky sphere {sky:F2} px ({skyFrames})"));
        Expect(frames > 30, $"{leg.Name}: measured {frames} frames");
        Expect(globeFrames + skyFrames > 0, $"{leg.Name}: the body drawn ({globeFrames} by its globe, {skyFrames} by the sky)");
        Expect(globe <= MaxPx, $"{leg.Name}: the globe on the body's centre ({globe:F2} px of {MaxPx})");
        Expect(ringCentre <= MaxPx && ringPlane <= MaxPx, $"{leg.Name}: the rings with it ({ringCentre:F2} px, plane {ringPlane:F2} px)");
        Expect(air <= MaxPx, $"{leg.Name}: the air with it ({air:F2} px)");
        Expect(sky <= MaxPx, $"{leg.Name}: the sky's sphere with it ({sky:F2} px)");
        await Wait(1);
        Shot($"bodyplace_{leg.Name}_stopped");
    }

    private void Shot(string name)
    {
        string dir = ProjectSettings.GlobalizePath("res://test_output/bodies");
        System.IO.Directory.CreateDirectory(dir);
        var image = GetViewport().GetTexture().GetImage();
        string path = System.IO.Path.Combine(dir, name + ".png");
        image.SavePng(path);
        Log($"picture {path}");
    }

    private void Finish(string? fail)
    {
        if (fail == null) GD.Print("[bodyplace] RESULT: ok");
        else GD.PrintErr($"[bodyplace] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
