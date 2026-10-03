using System.Collections.Generic;
using Godot;
using UnitSport.Player;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>Named places the viewmodel can be held; blended between smoothly, see <see cref="HeldItemVisual.SetPose"/>.</summary>
public enum ViewPose { Rest, Aim, Eye, Mouth, Plant, Inspect, Read, Head, Raise }

/// <summary>
/// Draws what a player holds, on every copy of that player.
///
/// <para>
/// Two placements. In the local player's first person there is no figure to hold anything, so
/// the item is a <b>viewmodel</b> parented to the camera, low and to the right, swaying back into
/// place when the view turns. Everywhere else — third person, and every remote player — it rides
/// on <see cref="FootPlayer.HandLocal"/>, the figure's right wrist taken from the same rig the
/// body mesh is posed from, so it swings with the arm.
/// </para>
///
/// <para>
/// Reads <see cref="FootPlayer.HeldItemId"/> rather than the inventory: that property is the one
/// that is replicated, so the local and the remote copies go through exactly the same path.
/// </para>
/// </summary>
public partial class HeldItemVisual : Node3D
{
    /// <summary>Where a viewmodel rests in camera space: low, right, and far enough out not to clip the near plane.</summary>
    private static readonly Vector3 ViewmodelRest = new(0.27f, -0.25f, -0.66f);

    /// <summary>
    /// The viewmodel is drawn at this fraction of its real size, pulled in by the same fraction
    /// (same look on screen), so it stays well clear of the near plane.
    /// </summary>
    public const float ViewScale = 0.5f;

    /// <summary>
    /// Visual layer 17: the viewmodel and what hangs on it. Only the screen's camera draws it;
    /// door portal cameras leave it out and doorway ghosts do not copy it, or it would show a
    /// second time through a doorway it is held in front of.
    /// </summary>
    public const uint ViewmodelLayer = 1u << 16;

    /// <summary>
    /// The vertex stage of every viewmodel material: depth squeezed into the nearest 1 % of the
    /// range (reversed z, 1 at the near plane). The item still sorts against itself but never goes
    /// behind the world: a wall it pokes into, or a doorway's portal quad while stepping through.
    /// </summary>
    public const string ViewmodelVertex = @"
void vertex() {
    POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
    POSITION.z = mix(POSITION.w, POSITION.z, 0.01);
}
";

    private static readonly Dictionary<(bool unshaded, bool nearest), Shader> ViewShaders = new();
    private static readonly Dictionary<Material, Material> ViewMaterials = new();

    /// <summary>
    /// The viewmodel's copy of a held item's material: the same look (albedo, texture, vertex
    /// colour, roughness, specular, shading) through <see cref="ViewmodelVertex"/>. A shader
    /// material is taken as it is: it must include <see cref="ViewmodelVertex"/> itself.
    /// </summary>
    public static Material ForView(Material material)
    {
        if (material is not StandardMaterial3D s) return material;
        if (ViewMaterials.TryGetValue(s, out var cached)) return cached;
        bool unshaded = s.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded;
        bool nearest = s.TextureFilter == BaseMaterial3D.TextureFilterEnum.Nearest;
        if (!ViewShaders.TryGetValue((unshaded, nearest), out var shader))
            ViewShaders[(unshaded, nearest)] = shader = new Shader { Code = $@"
shader_type spatial;
{(unshaded ? "render_mode unshaded;" : "")}
uniform vec4 albedo : source_color = vec4(1.0);
uniform sampler2D albedo_tex : source_color, hint_default_white, {(nearest ? "filter_nearest" : "filter_linear_mipmap")};
uniform bool vertex_color;
uniform float roughness = 1.0;
uniform float specular = 0.5;
{ViewmodelVertex}
void fragment() {{
    vec3 c = albedo.rgb * texture(albedo_tex, UV).rgb;
    if (vertex_color) c *= COLOR.rgb;
    ALBEDO = c;
{(unshaded ? "" : "    ROUGHNESS = roughness;\n    SPECULAR = specular;")}
}}" };
        var m = new ShaderMaterial { Shader = shader };
        m.SetShaderParameter("albedo", s.AlbedoColor);
        if (s.AlbedoTexture != null) m.SetShaderParameter("albedo_tex", s.AlbedoTexture);
        m.SetShaderParameter("vertex_color", s.VertexColorUseAsAlbedo);
        m.SetShaderParameter("roughness", s.Roughness);
        m.SetShaderParameter("specular", s.SpecularMode == BaseMaterial3D.SpecularModeEnum.Disabled ? 0f : s.MetallicSpecular);
        return ViewMaterials[s] = m;
    }

    private readonly FootPlayer _player;
    private MeshInstance3D _inHand = null!;
    private MeshInstance3D? _viewmodel;
    private MeshInstance3D _handFore = null!;   // the shotgun's slide handle on the figure ...
    private MeshInstance3D? _viewFore;          // ... and on the viewmodel
    private float _pumpT;                       // seconds into the pump; negative while it waits out its delay
    private bool _pumping;
    private ItemId _shown = ItemId.None;
    private string? _shownData;

    // a Polaroid print sliding out of the bottom of the camera viewmodel (ShowPrint)
    private MeshInstance3D? _print;
    private float _printT;
    private static ArrayMesh? _printMesh;

    private Basis _lastCamera = Basis.Identity;
    private Vector3 _sway;
    private float _raise;   // 0 lowered .. 1 at rest; eased in when the item changes

    private ViewPose _pose = ViewPose.Rest;
    private Vector3 _curPos = ViewmodelRest, _curRot = new(0, 0.12f, 0);
    private float _blendStart = 1f;

    // one-shot animation (eat, plant...): pose weight 0..1 over in / hold / out
    private bool _shotActive;
    private ViewPose _shotPose;
    private float _shotIn, _shotHold, _shotOut, _shotT;
    private bool _shotPeaked;
    private System.Action? _shotPeak, _shotEnd;
    private float _shotW;

    /// <summary>The pose being blended toward.</summary>
    public ViewPose Pose => _pose;

    /// <summary>0 just after the pose changed .. 1 arrived.</summary>
    public float PoseBlend01 { get; private set; } = 1f;

    /// <summary>True once the current pose is reached and no one-shot is playing.</summary>
    public bool PoseSettled => PoseBlend01 >= 0.98f && !_shotActive;

    /// <summary>Blends the viewmodel toward <paramref name="pose"/> (eased, exponential damping).</summary>
    public void SetPose(ViewPose pose)
    {
        if (pose == _pose) return;
        _pose = pose;
        var (p, r) = PoseTransform(pose);
        _blendStart = Mathf.Max(0.01f, (p - _curPos).Length() + (r - _curRot).Length() * 0.2f);
        PoseBlend01 = 0f;
    }

    /// <summary>
    /// Plays a there-and-back animation to <paramref name="target"/> over the current pose:
    /// eases in for <paramref name="inTime"/>, calls <paramref name="onPeak"/> once on arrival,
    /// holds, eases out for <paramref name="outTime"/>. A new call replaces one in progress.
    /// </summary>
    public void PlayOneShot(ViewPose target, float inTime, float hold, float outTime, System.Action? onPeak = null, System.Action? onEnd = null)
    {
        _shotActive = true;
        _shotPose = target;
        _shotIn = Mathf.Max(0.01f, inTime);
        _shotHold = Mathf.Max(0f, hold);
        _shotOut = Mathf.Max(0.01f, outTime);
        _shotT = 0f;
        _shotPeaked = false;
        _shotPeak = onPeak;
        _shotEnd = onEnd;
    }

    /// <summary>True while a one-shot (eat, put on a hat...) is playing; the owner mirrors it into <c>ItemAction</c>.</summary>
    public bool OneShotActive => _shotActive;

    /// <summary>Stops a one-shot at once without firing its peak or end (the item was switched away).</summary>
    public void CancelOneShot()
    {
        _shotActive = false;
        _shotW = 0f;
        _shotPeak = null;
        _shotEnd = null;
    }

    /// <summary>Advances the one-shot clock; runs in every view, because the third-person owner has no viewmodel to drive it.</summary>
    private void StepShot(float dt)
    {
        if (!_shotActive) { _shotW = 0f; return; }
        _shotT += dt;
        float w;
        if (_shotT < _shotIn) w = _shotT / _shotIn;
        else if (_shotT < _shotIn + _shotHold) w = 1f;
        else w = 1f - (_shotT - _shotIn - _shotHold) / _shotOut;
        if (!_shotPeaked && _shotT >= _shotIn) { _shotPeaked = true; var peak = _shotPeak; _shotPeak = null; peak?.Invoke(); }
        if (_shotT >= _shotIn + _shotHold + _shotOut)
        {
            _shotActive = false;
            w = 0f;
            var end = _shotEnd;
            _shotEnd = null;
            end?.Invoke();
        }
        w = Mathf.Clamp(w, 0f, 1f);
        _shotW = Mathf.SmoothStep(0f, 1f, w);
    }

    /// <summary>Text drawn on the GPS screen (first person); null leaves it blank.</summary>
    public string? ScreenText { get; set; }

    private SubViewport? _screenVp;
    private Label? _screenLabel;
    private MeshInstance3D? _screenQuad;

    /// <summary>Position and euler rotation (camera space, full scale) of a pose for the held item.</summary>
    private (Vector3 pos, Vector3 rot) PoseTransform(ViewPose pose)
    {
        var use = ItemDefs.Get(_shown)?.Use;
        return pose switch
        {
            // shotgun shouldered: the barrel line (y +0.025 above the grip) sits on the screen centre line
            ViewPose.Aim when use == ItemUse.Shoot => (new Vector3(0.0f, -0.105f, -0.42f), new Vector3(0.085f, 0f, 0f)),
            ViewPose.Aim => (new Vector3(0.05f, -0.16f, -0.50f), new Vector3(0, 0.05f, 0)),
            // camera raised in front of the eye, slightly below centre; binoculars right at the eyes
            ViewPose.Eye when use == ItemUse.Optic => (new Vector3(0f, -0.03f, -0.20f), Vector3.Zero),
            ViewPose.Eye => (new Vector3(0f, -0.12f, -0.38f), Vector3.Zero),
            // a bottle is upright in the hand: tipped ~70 degrees so its neck comes to the mouth; food jabs up and in
            ViewPose.Mouth when _shown == ItemId.WaterBottle => (new Vector3(0.06f, -0.17f, -0.30f), new Vector3(1.25f, 0, -0.25f)),
            ViewPose.Mouth => (new Vector3(0.0f, -0.10f, -0.26f), new Vector3(0.45f, 0, 0)),
            // GPS held up: low centre, top tipped away so the screen faces the eye
            ViewPose.Read => (new Vector3(0.0f, -0.16f, -0.30f), new Vector3(-0.65f, 0, 0)),
            // a hat lifted above the eye line, about to go on
            ViewPose.Head => (new Vector3(0.0f, 0.06f, -0.30f), new Vector3(0.3f, 0, 0)),
            ViewPose.Plant => (new Vector3(0.10f, -0.42f, -0.50f), new Vector3(-0.9f, 0.1f, 0)),
            // flag lifted high, before it is stabbed down (Plant)
            ViewPose.Raise => (new Vector3(0.10f, -0.10f, -0.50f), new Vector3(0.15f, 0.1f, 0)),
            ViewPose.Inspect => (new Vector3(0.02f, -0.06f, -0.36f), new Vector3(0.3f, 0.6f, 0.1f)),
            _ => (ViewmodelRest, new Vector3(0, 0.12f, 0)),
        };
    }

    /// <summary>Hats are drawn at their worn size, which is too big to hold up: shrunk in the hand.</summary>
    private float ItemScale => ItemDefs.Get(_shown)?.Use == ItemUse.Wear ? 0.55f : 1f;

    /// <summary>Set while the item is at the eye (binoculars) or a photo is being taken: nothing to draw.</summary>
    public bool Suppressed { get; set; }

    /// <summary>
    /// Per-instance data of the held stack (a photo's id), for items drawn from it. Set by the
    /// local <see cref="ItemController"/> only: remote copies see the item, not which one.
    /// </summary>
    public string? HeldData { get; set; }

    /// <summary>The first-person viewmodel is on screen right now.</summary>
    public bool ViewmodelShown => _viewmodel != null && IsInstanceValid(_viewmodel) && _viewmodel.Visible;

    /// <summary>
    /// A print slides out of the bottom of the viewmodel (the Polaroid camera) and hangs there,
    /// drawn with <paramref name="material"/> (the caller animates its developing), until
    /// <see cref="HidePrint"/> or the item changes. False if there is no viewmodel on screen.
    /// </summary>
    public bool ShowPrint(Material material)
    {
        if (!ViewmodelShown) return false;
        HidePrint();
        _printMesh ??= PhotoVisuals.BuildCard(Vector3.Zero);
        _print = new MeshInstance3D
        {
            Name = "Print", Mesh = _printMesh, MaterialOverride = ForView(material),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = ViewmodelLayer,
            Position = new Vector3(0, 0.03f, 0),
        };
        _viewmodel!.AddChild(_print);
        _printT = 0f;
        return true;
    }

    public void HidePrint()
    {
        if (_print != null && IsInstanceValid(_print)) _print.QueueFree();
        _print = null;
    }
    /// <summary>Seconds a shot takes to cycle: the pump starts this long after the shot (see <see cref="Pump"/>) and lasts <see cref="PumpTime"/>.</summary>
    public const float PumpDelay = 0.35f, PumpTime = 0.30f;

    /// <summary>How far the slide handle travels back toward the shooter, m.</summary>
    private const float PumpTravel = 0.11f;

    /// <summary>Cycles the action: after <paramref name="delay"/> seconds the fore-end slides back and forward again.</summary>
    public void Pump(float delay = PumpDelay) { _pumpT = -Mathf.Max(0f, delay); _pumping = true; }

    /// <summary>The felt recoil of a shot, 0..1: a hard up-and-back jolt with a little roll, settling in ~0.25 s (viewmodel only).</summary>
    public float Recoil { get; set; }

    /// <summary>A short push toward the camera, 0..1 — the recoil of a shutter or a bite.</summary>
    public float Kick { get; set; }

    public HeldItemVisual(FootPlayer player) => _player = player;

    public HeldItemVisual() : this(null!) { }

    public override void _Ready()
    {
        _inHand = new MeshInstance3D
        {
            Name = "InHand",
            MaterialOverride = ItemDefs.Material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(_inHand);
        _handFore = NewForeEnd();
        _inHand.AddChild(_handFore);
    }

    private static MeshInstance3D NewForeEnd() => new()
    {
        Name = "ForeEnd",
        Mesh = ItemDefs.ShotgunForeEnd(),
        MaterialOverride = ItemDefs.Material,
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        Visible = false,
    };

    public override void _Process(double delta)
    {
        if (_player == null) return;
        float dt = (float)delta;

        var id = (ItemId)_player.HeldItemId;
        // holstered swimming (#301), and limp after a crash: the hand it hung from is not drawn (#380)
        bool onFoot = _player.Ride == RideKind.OnFoot && !_player.RidingAlong && !_player.IsSwimming && !_player.Ragdolled;
        if (id != _shown || HeldData != _shownData)
        {
            if (id != _shown)
            {
                _raise = 0f;
                HidePrint();
            }
            _shown = id;
            _shownData = HeldData;
            var mesh = ItemDefs.HandMesh(id);
            var material = ItemDefs.HandMaterial(id, _shownData) ?? ItemDefs.Material;
            _inHand.Mesh = mesh;
            _inHand.MaterialOverride = material;
            if (_viewmodel != null)
            {
                _viewmodel.Mesh = mesh;
                _viewmodel.MaterialOverride = ForView(material);
            }
        }
        bool any = id != ItemId.None && onFoot && !Suppressed;

        // the slide handle: back and forth along the barrel (mesh -Z is forward, so back is +Z)
        float slide = 0f;
        if (_pumping)
        {
            _pumpT += dt;
            if (_pumpT >= PumpTime) _pumping = false;
        }
        if (_pumping && _pumpT >= 0f) slide = Mathf.Sin(Mathf.Clamp(_pumpT / PumpTime, 0f, 1f) * Mathf.Pi);
        bool gun = id == ItemId.Shotgun;
        _handFore.Visible = gun;
        _handFore.Position = new Vector3(0, 0, slide * PumpTravel);
        if (_viewFore != null && IsInstanceValid(_viewFore))
        {
            _viewFore.Visible = gun;
            _viewFore.Position = new Vector3(0, 0, slide * PumpTravel);
        }

        // --- on the figure ---
        if (_player.HandLocal is { } hand && any)
        {
            _inHand.Visible = true;
            // the wrist is the end of the arm, so the grip sits a hand's length past it
            _inHand.Scale = Vector3.One * ItemScale;
            _inHand.Transform = new Transform3D(hand.Basis, hand.Origin + hand.Basis * new Vector3(0, -0.05f, -0.03f))
                * RadioBounce(0.7f);
        }
        else _inHand.Visible = false;
        StepSparkles(_inHand.Visible, dt);
        StepTorch(id == ItemId.Torch && any, dt);

        // --- in front of the local camera ---
        if (!_player.IsMultiplayerAuthority()) return;
        bool firstPerson = _player.IsFirstPerson || _player.ScopeView;
        StepShot(dt);
        EnsureViewmodel();
        if (_viewmodel == null) return;

        _viewmodel.Visible = any && firstPerson;
        if (!_viewmodel.Visible)
        {
            _raise = 0f;
            return;
        }

        // Sway: the item trails a turn by the angle the camera just swept, then springs back.
        // It is the whole difference between something held and something painted on the lens.
        var cam = _player.Camera.GlobalTransform.Basis;
        var turn = _lastCamera.Inverse() * cam;
        _lastCamera = cam;
        var euler = turn.GetEuler();
        var target = new Vector3(-euler.Y, euler.X, 0) * 0.35f;
        target = target.LimitLength(0.06f);
        _sway = _sway.Lerp(target, MathX.Damp(10f, dt));

        _raise = Mathf.MoveToward(_raise, 1f, dt * 4f);
        Kick = Mathf.MoveToward(Kick, 0f, dt * 5f);
        float lowered = (1f - _raise * _raise) * 0.25f;

        // ease toward the pose; Rest keeps the old hand-held tilt
        var (tp, tr) = PoseTransform(_pose);
        float k = MathX.Damp(12f, dt);
        _curPos = _curPos.Lerp(tp, k);
        _curRot = _curRot.Lerp(tr, k);
        float remaining = (tp - _curPos).Length() + (tr - _curRot).Length() * 0.2f;
        PoseBlend01 = Mathf.Clamp(1f - remaining / _blendStart, 0f, 1f);
        if (remaining < 0.004f) PoseBlend01 = 1f;

        var pos = _curPos;
        var rot = _curRot;
        if (_shotActive)
        {
            var (sp, sr) = PoseTransform(_shotPose);
            pos = pos.Lerp(sp, _shotW);
            rot = rot.Lerp(sr, _shotW);
        }

        // less sway once the item is raised to a pose
        float swayScale = _pose == ViewPose.Rest ? 1f : 0.25f;
        UpdateScreen();
        _viewmodel.Scale = Vector3.One * ViewScale * ItemScale;
        Recoil = Mathf.MoveToward(Recoil, 0f, dt * 4.5f);
        float rc = Recoil * Recoil;   // squared: a sharp hit that tails off
        _viewmodel.Position = (pos + _sway * swayScale + new Vector3(rc * 0.012f, -lowered + rc * 0.03f, Kick * 0.06f + rc * 0.13f)) * ViewScale;
        _viewmodel.Rotation = rot + new Vector3(Kick * 0.3f + rc * 0.22f, rc * 0.03f, rc * 0.07f);
        if (_shown == ItemId.Radio) _viewmodel.Transform *= RadioBounce(0.45f);

        if (_print != null && IsInstanceValid(_print))
        {
            // out of the slot in 0.8 s, easing to a stop, with a slight droop as it comes free
            _printT += dt;
            float u = Mathf.Clamp(_printT / 0.8f, 0f, 1f);
            u = 1f - (1f - u) * (1f - u);
            _print.Position = new Vector3(0, Mathf.Lerp(0.03f, -0.045f, u), 0.001f);
            _print.Rotation = new Vector3(-0.12f * u, 0, 0);
        }
    }

    private OmniLight3D? _torchLight;
    private float _torchT;

    /// <summary>The name of a held torch's light: probes look for it.</summary>
    public const string TorchLightName = "TorchLight";

    /// <summary>
    /// A torch in the hand lights the world round it (#272), on every copy of the player: it follows
    /// the replicated <see cref="FootPlayer.HeldItemId"/>, so others see it with no extra state.
    /// </summary>
    private void StepTorch(bool lit, float dt)
    {
        if (lit && _torchLight == null)
        {
            _torchLight = new OmniLight3D
            {
                Name = TorchLightName, TopLevel = true, LightColor = new Color(1f, 0.66f, 0.32f),
                LightEnergy = 1.4f, OmniRange = 7f, ShadowEnabled = false,
            };
            AddChild(_torchLight);
        }
        if (_torchLight == null) return;
        _torchLight.Visible = lit;
        if (!lit) return;
        _torchT += dt;
        _torchLight.LightEnergy = 1.4f + 0.25f * Mathf.Sin(_torchT * 13f) + 0.15f * Mathf.Sin(_torchT * 29f);
        // at the flame on the figure's hand; with no figure drawn, about where a hand would hold it up
        _torchLight.GlobalPosition = _inHand.Visible && _inHand.IsInsideTree()
            ? _inHand.GlobalTransform * new Vector3(0, 0.42f, 0)
            : _player.GlobalPosition + Vector3.Up * 1.7f;
    }

    private RadioSparkles? _sparkles;

    /// <summary>A playing radio in the figure's hand sparkles (#387); anything else in the hand, at once nothing.</summary>
    private void StepSparkles(bool shown, float dt)
    {
        if (!shown || _shown != ItemId.Radio || RadioPlay.Decode(_player.HeldRadio) is not { } play)
        {
            _sparkles?.Off();
            return;
        }
        if (_sparkles == null) _inHand.AddChild(_sparkles = new RadioSparkles());
        bool beating = RadioBody.BeatOf(play.CdId, play.StartedAt, Net.ClockSync.ServerNow, out float phase, out int beat, out _, out _);
        _sparkles.Step(true, beating, phase, beat, dt);
    }

    /// <summary>A playing radio in the hand bounces to its beat (#261), a little less than on the ground; identity otherwise.</summary>
    private Transform3D RadioBounce(float amount) =>
        _shown == ItemId.Radio && _player != null && RadioPlay.Decode(_player.HeldRadio) is { } play
        && RadioBody.BeatOf(play.CdId, play.StartedAt, Net.ClockSync.ServerNow, out float phase, out int beat, out _, out _)
            ? RadioBody.Bounce(phase, beat, 0.11f, amount)
            : Transform3D.Identity;

    /// <summary>
    /// The GPS screen: a SubViewport drawing the readout in a Label, shown on a quad just in front of the
    /// device's face. Chosen over the HUD panel because the numbers then live on the device you hold up;
    /// the HUD panel stays as the third-person / fallback display.
    /// </summary>
    private void UpdateScreen()
    {
        bool gps = _shown == ItemId.Gps && _viewmodel != null;
        if (!gps)
        {
            if (_screenQuad != null) _screenQuad.Visible = false;
            if (_screenVp != null) _screenVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            return;
        }
        if (_screenVp == null)
        {
            _screenVp = new SubViewport { Size = new Vector2I(96, 84), RenderTargetUpdateMode = SubViewport.UpdateMode.Always, TransparentBg = false };
            var bg = new ColorRect { Color = new Color(0.62f, 0.78f, 0.55f) };
            bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _screenVp.AddChild(bg);
            _screenLabel = new Label { Position = new Vector2(4, 3), Size = new Vector2(90, 78) };
            _screenLabel.AddThemeFontSizeOverride("font_size", 13);
            _screenLabel.AddThemeColorOverride("font_color", new Color(0.08f, 0.16f, 0.08f));
            _screenLabel.AddThemeConstantOverride("line_spacing", -2);
            _screenVp.AddChild(_screenLabel);
            AddChild(_screenVp);
            _screenQuad = new MeshInstance3D
            {
                Name = "GpsScreen",
                Mesh = new QuadMesh { Size = new Vector2(0.07f, 0.061f) },
                Position = new Vector3(0, 0.075f, 0.0153f),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Layers = ViewmodelLayer,
                MaterialOverride = ForView(new StandardMaterial3D
                {
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoTexture = _screenVp.GetTexture(),
                    TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
                }),
            };
            _viewmodel!.AddChild(_screenQuad);
        }
        _screenVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _screenQuad!.Visible = _viewmodel!.Visible;
        if (_screenLabel != null && _screenLabel.Text != (ScreenText ?? "")) _screenLabel.Text = ScreenText ?? "";
    }

    private void EnsureViewmodel()
    {
        if (_viewmodel != null && IsInstanceValid(_viewmodel)) return;
        var camera = _player.Camera;
        if (camera == null) return;

        _viewmodel = new MeshInstance3D
        {
            Name = "Viewmodel",
            Mesh = ItemDefs.HandMesh(_shown),
            MaterialOverride = ForView(ItemDefs.HandMaterial(_shown, _shownData) ?? ItemDefs.Material),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = ViewmodelLayer,
        };
        camera.AddChild(_viewmodel);
        _viewFore = NewForeEnd();
        _viewFore.Layers = ViewmodelLayer;
        _viewFore.MaterialOverride = ForView(ItemDefs.Material);
        _viewmodel.AddChild(_viewFore);
        _lastCamera = camera.GlobalTransform.Basis;
    }

    public override void _ExitTree()
    {
        if (_viewmodel != null && IsInstanceValid(_viewmodel)) _viewmodel.QueueFree();
        _viewmodel = null;
    }
}
