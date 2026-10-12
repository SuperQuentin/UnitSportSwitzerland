using Godot;
using UnitSport.Audio;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// A grenade-style throw for the item in hand (#206): hold Aim and the view moves in over the
/// shoulder, a dashed arc shows where the item would fly and a ring where it would land; hold Use
/// to wind up — the arc reaches further, the view narrows, a hum climbs and, at full strength, the
/// picture trembles; let go of Use to throw. Letting go of Aim first puts the throw away.
///
/// <para>
/// The arc is the throw itself, simulated ahead with the same gravity and damping the body will
/// fall with (<see cref="Simulate"/>), from the same origin and velocity <see cref="Launch"/> gives
/// the throw, and stopped by the first thing a ray along it meets. Local only: nobody else sees an
/// arc, they see the wind-up arm pose (<c>ItemAction</c> 3, then 4 for the release).
/// </para>
/// </summary>
public partial class ThrowAim : Node3D, Core.IOriginShiftAware
{
    public const float MinSpeed = 4.5f, MaxSpeed = 21f;
    /// <summary>Seconds of Use held to reach full strength.</summary>
    public const float ChargeTime = 1.0f;
    /// <summary>How far above the view the throw leaves, radians: a lob reads better than a line drive.</summary>
    private const float Loft = 0.2f;
    private const float StepTime = 1f / 60f, MaxTime = 3.2f;

    /// <summary>Aim held with a throwable item.</summary>
    public bool Active { get; private set; }
    /// <summary>Use held while aiming: winding up.</summary>
    public bool Charging { get; private set; }
    /// <summary>The wind-up, 0..1 (eased: quick at first, slower to the top).</summary>
    public float Power { get; private set; }
    /// <summary>Seconds since the last throw left the hand (for the FOV pop and the release pose).</summary>
    public float SinceRelease { get; private set; } = 99f;
    /// <summary>The release pose's strength, set at the throw.</summary>
    public float ReleasePower { get; private set; }

    private float _charge;   // raw 0..1 of ChargeTime
    private float _shownPower;
    private bool _full;
    private float _fullFlash;
    private ImmediateMesh _arcMesh = null!, _markerMesh = null!;
    private MeshInstance3D _arc = null!, _marker = null!;
    private AudioStreamPlayer _hum = null!, _sfx = null!;
    private readonly List<Vector3> _points = new();
    private Vector3? _hitPoint, _hitNormal;

    public override void _Ready()
    {
        TopLevel = true;
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            DisableFog = true,
            RenderPriority = 10,
        };
        _arcMesh = new ImmediateMesh();
        _markerMesh = new ImmediateMesh();
        _arc = new MeshInstance3D { Name = "Arc", Mesh = _arcMesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _marker = new MeshInstance3D { Name = "Marker", Mesh = _markerMesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_arc);
        AddChild(_marker);
        _hum = new AudioStreamPlayer { Name = "Hum", Stream = SfxSynth.ChargeHum, VolumeDb = -40f, Bus = SfxBus.Name };
        _sfx = new AudioStreamPlayer { Name = "Sfx", Bus = SfxBus.Name };
        AddChild(_hum);
        AddChild(_sfx);
    }

    /// <summary>
    /// The origin moved (#185). The arc is drawn in world space under this node, so it stays at the
    /// identity (the shift moved it, as a top-level node); what the arc was drawn from moves.
    /// </summary>
    public void OnOriginShifted(Core.OriginShift shift)
    {
        Transform = Transform3D.Identity;
        for (int i = 0; i < _points.Count; i++) _points[i] = shift.Point(_points[i]);
        if (_hitPoint is { } hit) _hitPoint = shift.Point(hit);
        if (_hitNormal is { } normal) _hitNormal = shift.Direction(normal);
    }

    /// <summary>Where a throw leaves the hand: over the right shoulder, a little ahead.</summary>
    public static Vector3 Origin(FootPlayer player)
    {
        var view = player.Camera.GlobalTransform.Basis;
        var right = new Vector3(view.X.X, 0, view.X.Z).Normalized();
        var ahead = new Vector3(-view.Z.X, 0, -view.Z.Z).Normalized();
        return player.GlobalPosition + Vector3.Up * 1.6f + right * 0.28f + ahead * 0.35f;
    }

    /// <summary>
    /// The throw's velocity at <paramref name="power"/> 0..1: along the view, lofted, plus the
    /// player's own. <paramref name="heft"/> scales the arm's part: under 1 for a heavy thing.
    /// </summary>
    public static Vector3 Launch(FootPlayer player, float power, float heft = 1f)
    {
        var view = player.Camera.GlobalTransform.Basis;
        var dir = (-view.Z).Rotated(view.X.Normalized(), Loft).Normalized();
        return dir * (Mathf.Lerp(MinSpeed, MaxSpeed, power) * heft) + player.Velocity;
    }

    /// <summary>
    /// How hard an item flies, 1 = anything light (#725): the boombox is heavy, it leaves at a
    /// little over half the speed and takes longer to wind up.
    /// </summary>
    public static float HeftOf(ItemId id) => id == ItemId.Radio ? 0.55f : 1f;

    /// <summary>The <see cref="HeftOf"/> of the item in hand, set every frame by <see cref="ItemController"/>.</summary>
    public float Heft { get; set; } = 1f;

    /// <summary>
    /// Runs every frame from <see cref="ItemController"/>. <paramref name="aiming"/>: Aim held with a
    /// throwable item, on foot. <paramref name="useHeld"/>: Use still down. Returns true on the frame
    /// the throw should leave the hand (Use let go after a wind-up), with <see cref="Power"/> its strength.
    /// </summary>
    public bool Step(FootPlayer? player, bool aiming, bool useHeld, float dt)
    {
        SinceRelease += dt;
        bool release = false;
        if (!aiming || player == null)
        {
            if (Charging) Cancel();
            Active = false;
        }
        else
        {
            Active = true;
            if (Charging)
            {
                _charge = Mathf.Min(1f, _charge + dt / (ChargeTime * (2f - Heft)));   // a heavy thing winds up slower
                Power = 1f - (1f - _charge) * (1f - _charge);   // ease out
                if (_charge >= 1f && !_full)
                {
                    _full = true;
                    _fullFlash = 1f;
                    Play(SfxSynth.Chime, 2.4f, -10f);
                }
                if (!useHeld)
                {
                    release = true;
                    ReleasePower = Power;
                    SinceRelease = 0f;
                    Charging = false;
                }
            }
        }

        // the hum follows the wind-up, and dies fast when it ends
        if (Charging)
        {
            if (!_hum.Playing) _hum.Play();
            _hum.PitchScale = Mathf.Lerp(0.7f, 1.7f, Power) * (_full ? 1f + 0.02f * Mathf.Sin(Time.GetTicksMsec() / 30f) : 1f);
            _hum.VolumeDb = Mathf.Lerp(-24f, -10f, Power);
        }
        else if (_hum.Playing)
        {
            _hum.VolumeDb -= dt * 160f;
            if (_hum.VolumeDb < -45f) _hum.Stop();
        }

        _fullFlash = Mathf.Max(0f, _fullFlash - dt * 3f);
        if (Active && player != null)
        {
            // the arc lags the wind-up a touch: it stretches out rather than jumps
            float want = Charging ? Power : 0f;
            _shownPower = Mathf.Lerp(_shownPower, want, 1f - Mathf.Exp(-18f * dt));
            Simulate(player, _shownPower);
            Draw(player.Camera, _shownPower);
        }
        else
        {
            _shownPower = 0f;
            _arcMesh.ClearSurfaces();
            _markerMesh.ClearSurfaces();
        }
        if (release)
        {
            Power = 0f;
            _charge = 0f;
            _full = false;
        }
        return release;
    }

    /// <summary>Use pressed while aiming: the wind-up starts.</summary>
    public void BeginCharge()
    {
        if (!Active || Charging) return;
        Charging = true;
        _charge = 0f;
        Power = 0f;
        _full = false;
        Play(SfxSynth.Whoosh, 0.5f, -18f);
    }

    /// <summary>Aim let go mid wind-up: nothing thrown.</summary>
    public void Cancel()
    {
        Charging = false;
        _charge = 0f;
        Power = 0f;
        _full = false;
    }

    /// <summary>Camera tremble the wind-up asks for, radians.</summary>
    public float Shake => !Charging ? 0f : _full ? 0.0045f + 0.0035f * _fullFlash : 0.0012f * Power;

    /// <summary>The field of view the throw asks for, or null: in close while aiming, narrower with the wind-up, a wide pop on release.</summary>
    public float? Fov
    {
        get
        {
            if (SinceRelease < 0.25f)
                return Mathf.Lerp(66f + 10f * ReleasePower, 62f, SinceRelease / 0.25f);
            if (!Active) return null;
            return 62f - 10f * Power;
        }
    }

    private void Play(AudioStream stream, float pitch, float db)
    {
        _sfx.Stream = stream;
        _sfx.PitchScale = pitch;
        _sfx.VolumeDb = db;
        _sfx.Play();
    }

    /// <summary>The path's rays: one query, reused (#221).</summary>
    private readonly Core.RayQuery _ray = new();

    /// <summary>The throw's path at <paramref name="power"/>, until it meets something or runs out of time.</summary>
    private void Simulate(FootPlayer player, float power)
    {
        _points.Clear();
        _hitPoint = _hitNormal = null;
        float g = (float)ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f);
        float damp = (float)ProjectSettings.GetSetting("physics/3d/default_linear_damp", 0.1f);
        var p = Origin(player);
        var v = Launch(player, power, Heft);
        var space = player.GetWorld3D().DirectSpaceState;
        var exclude = player.SelfExclude;
        _points.Add(p);
        var from = p;
        for (float t = 0; t < MaxTime; t += StepTime)
        {
            v += Vector3.Down * g * StepTime;
            v *= Mathf.Max(0f, 1f - damp * StepTime);
            p += v * StepTime;
            // a ray every other step: plenty for a path, half the queries
            if (((int)(t / StepTime) & 1) == 1 || t + StepTime >= MaxTime)
            {
                var hit = _ray.Cast(space, from, p, uint.MaxValue, exclude);
                if (hit.Count > 0)
                {
                    _hitPoint = hit["position"].AsVector3();
                    _hitNormal = hit["normal"].AsVector3();
                    _points.Add(_hitPoint.Value);
                    return;
                }
                _points.Add(p);
                from = p;
            }
        }
    }

    private Color Tint(float power)
    {
        var cool = new Color(1f, 0.95f, 0.75f);
        var warm = new Color(1f, 0.62f, 0.18f);
        var hot = new Color(1f, 0.28f, 0.16f);
        var c = power < 0.6f ? cool.Lerp(warm, power / 0.6f) : warm.Lerp(hot, (power - 0.6f) / 0.4f);
        if (_full) c = c.Lerp(Colors.White, 0.35f * (0.5f + 0.5f * Mathf.Sin(Time.GetTicksMsec() / 45f)) + 0.5f * _fullFlash);
        return c;
    }

    /// <summary>Dashes running along the path toward where it lands, as a ribbon facing the camera; then the ring.</summary>
    private void Draw(Camera3D camera, float power)
    {
        _arcMesh.ClearSurfaces();
        _markerMesh.ClearSurfaces();
        if (_points.Count < 2) return;
        var eye = camera.GlobalPosition;
        var tint = Tint(power);
        float time = Time.GetTicksMsec() / 1000f;
        const float dash = 0.42f, period = 0.7f;
        float scroll = time * (1.6f + 3f * power);

        _arcMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        float s = 0f;
        int quads = 0;
        for (int i = 1; i < _points.Count; i++)
        {
            var a = _points[i - 1];
            var b = _points[i];
            float len = a.DistanceTo(b);
            if (len < 1e-4f) continue;
            // the segment cut into dash pieces
            float u = s;
            while (u < s + len)
            {
                float phase = Mathf.PosMod(u - scroll, period);
                float pieceEnd = phase < dash ? u + (dash - phase) : u + (period - phase);
                pieceEnd = Mathf.Min(pieceEnd, s + len);
                if (phase < dash)
                {
                    var p0 = a.Lerp(b, (u - s) / len);
                    var p1 = a.Lerp(b, (pieceEnd - s) / len);
                    Quad(p0, p1, u, pieceEnd, eye, tint, power);
                    quads++;
                }
                u = pieceEnd + 1e-4f;
            }
            s += len;
        }
        if (quads == 0) Quad(_points[0], _points[0] + Vector3.Up * 0.001f, 0, 0.001f, eye, tint, power);   // a surface is never empty
        _arcMesh.SurfaceEnd();

        if (_hitPoint is { } hp && _hitNormal is { } n) Ring(hp, n, tint, power, time);
    }

    private void Quad(Vector3 p0, Vector3 p1, float s0, float s1, Vector3 eye, Color tint, float power)
    {
        var dir = (p1 - p0).Normalized();
        float W(float s) => Mathf.Lerp(0.03f, 0.055f, power) * (1f + s * 0.07f);
        // fades in off the hand, so it never sits across the middle of the view
        float A(float s) => Mathf.Clamp((s - 0.2f) / 0.7f, 0f, 1f) * 0.92f;
        var side0 = dir.Cross(eye - p0).Normalized() * W(s0);
        var side1 = dir.Cross(eye - p1).Normalized() * W(s1);
        var c0 = new Color(tint, A(s0));
        var c1 = new Color(tint, A(s1));
        Vertex(_arcMesh, p0 - side0, c0); Vertex(_arcMesh, p0 + side0, c0); Vertex(_arcMesh, p1 + side1, c1);
        Vertex(_arcMesh, p0 - side0, c0); Vertex(_arcMesh, p1 + side1, c1); Vertex(_arcMesh, p1 - side1, c1);
    }

    /// <summary>The landing: a breathing ring on the surface, a dot in its middle and four ticks turning round it.</summary>
    private void Ring(Vector3 at, Vector3 normal, Color tint, float power, float time)
    {
        var up = normal.Normalized();
        var side = Mathf.Abs(up.Dot(Vector3.Forward)) > 0.9f ? Vector3.Right : Vector3.Forward;
        var x = up.Cross(side).Normalized();
        var z = x.Cross(up).Normalized();
        var c = at + up * 0.04f;
        float r = 0.32f + 0.18f * power + 0.04f * Mathf.Sin(time * 8f);
        var color = new Color(tint, 0.85f);
        const int n = 28;

        _markerMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        Vector3 P(float angle, float radius) => c + (x * Mathf.Cos(angle) + z * Mathf.Sin(angle)) * radius;
        for (int i = 0; i < n; i++)
        {
            float a0 = Mathf.Tau * i / n, a1 = Mathf.Tau * (i + 1) / n;
            Vertex(_markerMesh, P(a0, r - 0.05f), color); Vertex(_markerMesh, P(a0, r), color); Vertex(_markerMesh, P(a1, r), color);
            Vertex(_markerMesh, P(a0, r - 0.05f), color); Vertex(_markerMesh, P(a1, r), color); Vertex(_markerMesh, P(a1, r - 0.05f), color);
            // the dot
            Vertex(_markerMesh, c, color); Vertex(_markerMesh, P(a0, 0.06f), color); Vertex(_markerMesh, P(a1, 0.06f), color);
        }
        // four ticks outside the ring, turning
        for (int k = 0; k < 4; k++)
        {
            float a = time * 1.8f + Mathf.Tau * k / 4f;
            var tip = P(a, r + 0.04f);
            var l = P(a - 0.12f, r + 0.16f);
            var rr = P(a + 0.12f, r + 0.16f);
            Vertex(_markerMesh, tip, color); Vertex(_markerMesh, l, color); Vertex(_markerMesh, rr, color);
        }
        _markerMesh.SurfaceEnd();
    }

    private static void Vertex(ImmediateMesh mesh, Vector3 at, Color color)
    {
        mesh.SurfaceSetColor(color);
        mesh.SurfaceAddVertex(at);
    }
}
