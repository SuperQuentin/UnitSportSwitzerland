using Godot;

namespace UnitSport.Concert;

/// <summary>
/// The crowd's LED wristbands (#986), read from the rig's DMX every frame: each IR emitter (a moving
/// <see cref="FixtureKind.IrHead"/> on the rig's stepper motors, a fixed <see cref="FixtureKind.IrFlood"/>)
/// as a cone with its colour code and power, and the transmitter's "all bands" (colour, level, effect),
/// packed for <c>shaders/concert/crowd.gdshader</c>, which tests every person against the cones.
///
/// <para>
/// A real band keeps its colour about half a second after the IR goes (<see cref="Hold"/>): an emitter
/// turned off still lights its last cone that long, and a moving head drags a trail (its beam a
/// moment ago, <see cref="Trail"/>) that the shader lights too, so a sweep leaves colour behind it.
/// Nothing allocates; <see cref="Dirty"/> says whether the uniforms need sending.
/// </para>
/// </summary>
public sealed class Wristbands
{
    /// <summary>The shader's emitter slots; the lit ones are packed first.</summary>
    public const int Max = 16;
    public const float Hold = 0.5f;
    private const float Trail = 0.25f;

    /// <summary>The transmitter's effects: its effect channel in quarters (192..255 plays solid).</summary>
    public const int Solid = 0, Twinkle = 1, BeatFlash = 2;
    public static byte EffectValue(int effect) => (byte)(effect * 64 + 8);
    public static int EffectOf(byte value) => value / 64 is var q && q <= BeatFlash ? q : Solid;

    private readonly DmxPatch _p;
    private readonly Fixture[] _emitters;
    private readonly Fixture? _transmitter;
    private readonly float[] _pan, _tilt, _panV, _tiltV, _level, _since;
    private readonly Vector3[] _dir, _old;
    private readonly Color[] _col;
    private bool _motorsSet;
    private float _allLevel, _allSince;
    private Color _allCol;
    private int _allEffect;
    private float _allRate;

    /// <summary>Four per lit emitter, world frame: (position, cos of the half angle), (beam now), (beam a moment ago), (colour, power).</summary>
    public readonly Vector4[] Emitters = new Vector4[Max * 4];
    public int Count { get; private set; }
    /// <summary>The transmitter: colour and level; its effect and rate.</summary>
    public Vector4 All { get; private set; }
    public Vector2 Effect { get; private set; }
    public bool Dirty { get; set; } = true;
    public int Patched => _emitters.Length;

    public Wristbands(DmxPatch patch)
    {
        _p = patch;
        _emitters = patch.Fixtures.Where(f => f.Type.Kind is FixtureKind.IrHead or FixtureKind.IrFlood).Take(Max).ToArray();
        _transmitter = patch.Fixtures.FirstOrDefault(f => f.Type.Kind == FixtureKind.Wristbands);
        int n = _emitters.Length;
        _pan = new float[n]; _tilt = new float[n]; _panV = new float[n]; _tiltV = new float[n];
        _level = new float[n]; _since = new float[n];
        _dir = new Vector3[n]; _old = new Vector3[n];
        _col = new Color[n];
        for (int i = 0; i < n; i++) _dir[i] = _old[i] = _emitters[i].Home;
    }

    /// <summary>Reads the DMX for this frame; <paramref name="toWorld"/> takes the rig's frame to the world's.</summary>
    public void Step(float dt, Transform3D toWorld)
    {
        int n = 0;
        float follow = 1f - MathF.Exp(-dt / Trail);
        for (int i = 0; i < _emitters.Length; i++)
        {
            var f = _emitters[i];
            var dir = f.Home;
            if (f.Type.Kind == FixtureKind.IrHead)
            {
                float wantPan = (_p.Get16(f, Attr.Pan, Attr.PanFine) - 0.5f) * f.Type.PanRange;
                float wantTilt = (_p.Get16(f, Attr.Tilt, Attr.TiltFine) - 0.5f) * f.Type.TiltRange;
                if (!_motorsSet) { _pan[i] = wantPan; _tilt[i] = wantTilt; }
                // a spot's yoke
                ConcertRig.Motor(ref _pan[i], ref _panV[i], wantPan, 220f, 1100f, dt);
                ConcertRig.Motor(ref _tilt[i], ref _tiltV[i], wantTilt, 200f, 1000f, dt);
                dir = ConcertRig.Direction(f, _pan[i], _tilt[i]);
            }
            float level = _p.Get01(f, Attr.Dimmer);
            var col = new Color(_p.Get01(f, Attr.Red), _p.Get01(f, Attr.Green), _p.Get01(f, Attr.Blue));
            if (level > 0.02f && col.R + col.G + col.B > 0.02f)
            {
                // just switched on: no trail from wherever it pointed in the dark
                if (_level[i] <= 0f) _old[i] = dir;
                _dir[i] = dir;
                _level[i] = level;
                _col[i] = col;
                _since[i] = 0f;
            }
            else if ((_since[i] += dt) > Hold) _level[i] = 0f;
            // off but holding: the bands keep the last cone they saw
            _old[i] = (_old[i] + (_dir[i] - _old[i]) * follow).Normalized();
            if (_level[i] <= 0f) continue;
            float half = Mathf.DegToRad(Mathf.Lerp(f.Type.BeamMin, f.Type.BeamMax, _p.Get01(f, Attr.Zoom))) * 0.5f;
            var at = toWorld * f.Position;
            var now = (toWorld.Basis * _dir[i]).Normalized();
            var then = (toWorld.Basis * _old[i]).Normalized();
            Put(n * 4, new Vector4(at.X, at.Y, at.Z, MathF.Cos(half)));
            Put(n * 4 + 1, new Vector4(now.X, now.Y, now.Z, 0f));
            Put(n * 4 + 2, new Vector4(then.X, then.Y, then.Z, 0f));
            Put(n * 4 + 3, new Vector4(_col[i].R, _col[i].G, _col[i].B, _level[i]));
            n++;
        }
        _motorsSet = true;
        if (n != Count) { Count = n; Dirty = true; }

        if (_transmitter is { } t)
        {
            float level = _p.Get01(t, Attr.Dimmer);
            if (level > 0.02f)
            {
                _allLevel = level;
                _allCol = new Color(_p.Get01(t, Attr.Red), _p.Get01(t, Attr.Green), _p.Get01(t, Attr.Blue));
                _allEffect = EffectOf(_p.Get(t, t.Type.Offset(Attr.Effect)));
                _allRate = _p.Get01(t, Attr.Rate);
                _allSince = 0f;
            }
            else if ((_allSince += dt) > Hold) _allLevel = 0f;
            var all = new Vector4(_allCol.R, _allCol.G, _allCol.B, _allLevel);
            var effect = new Vector2(_allEffect, _allRate);
            if (all != All || effect != Effect) { All = all; Effect = effect; Dirty = true; }
        }
    }

    private void Put(int i, Vector4 v)
    {
        if (Emitters[i] == v) return;
        Emitters[i] = v;
        Dirty = true;
    }
}
