using Godot;
using UnitSport.Audio;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// The fireworks, drawn and heard (client, #1269 part 2, <c>docs/notes/occasions/festival-fireworks.md</c>). It watches the running
/// festivals for a closing show on the books (<see cref="RunningFestival.ShowStart"/>, the server's word) and plays the
/// <see cref="FireworkShow"/> of its seed from the shared clock: every peer sees the same shell at the same moment, a peer that joins
/// late joins the show where it is. Everything is pooled and GPU-side: 24 one-shot particle bursts, 12 climbing sparks, 4 flash
/// lights, 10 sound voices, all made once; a shell costs a material assignment and a <c>Restart</c>, nothing is built per frame
/// and nothing is allocated (the cursor walks the script). Placed from LV95 at each use, so a floating-origin shift moves nothing by hand.
/// The bang is heard <c>distance / 343</c> s after the flash: a shell 2 km off, 6 s late.
/// </summary>
public partial class Fireworks : Node3D, IOriginContainer
{
    public const string NodeName = "Fireworks";

    private const int BurstSlots = 24, RocketSlots = 12, LightSlots = 4, Voices = 10, PendingSounds = 96, PendingBursts = 48;
    private const int StarsPerBurst = 180;

    private enum Look { Peony, Chrysanthemum, Willow, Palm, Ring, Crossette, CrossetteChild, CrossRed, CrossStars, Count }

    private readonly record struct Style(float VMin, float VMax, float Damp, float Gravity, float Life, float Size);

    private static readonly Style[] Styles =
    [
        new(22, 28, 11, -4, 2.3f, 0.9f),    // Peony
        new(20, 27, 10, -5, 3.1f, 0.8f),    // Chrysanthemum: longer lived
        new(13, 19, 4.5f, -9, 4.6f, 0.8f),  // Willow: slow, then the long golden droop
        new(26, 33, 8, -10, 3.3f, 1.3f),    // Palm: few big heavy arms
        new(21, 24, 9, -4, 2.4f, 1.0f),     // Ring: the radial speed of a ring
        new(15, 19, 12, -3, 1.2f, 1.0f),    // Crossette: the shell that splits...
        new(11, 14, 9, -4, 1.7f, 0.8f),     // ...into four small ones
        new(22, 27, 11, -4, 2.3f, 0.9f),    // Swiss cross: the red peony
        new(0, 0, 0, -1.2f, 3.4f, 1.3f),    // ...and its white cross of stars, hanging
    ];

    private static readonly Color[] Palette =
    [
        new(1.0f, 0.16f, 0.12f), new(1.0f, 0.78f, 0.26f), new(1.0f, 0.95f, 0.85f), new(0.3f, 1.0f, 0.4f),
        new(0.3f, 0.55f, 1.0f), new(1.0f, 0.3f, 0.85f), new(1.0f, 0.5f, 0.12f), new(0.85f, 0.9f, 1.0f),
    ];

    public static Fireworks? Instance { get; private set; }

    private readonly WorldOrigin _origin;
    private readonly Func<Vector3, float?> _groundAt;

    // ---- the pools --------------------------------------------------------------------------------

    private sealed class BurstSlot
    {
        public GpuParticles3D P = null!;
        public QuadMesh Q = null!;
        public double BusyUntil;
    }

    private sealed class RocketSlot
    {
        public GpuParticles3D P = null!;
        public QuadMesh Q = null!;
        public int Shell = -1;
        public double E, N;
        public float Height;
        public double Launch;
    }

    private readonly BurstSlot[] _bursts = new BurstSlot[BurstSlots];
    private readonly RocketSlot[] _rockets = new RocketSlot[RocketSlots];
    private readonly OmniLight3D[] _lights = new OmniLight3D[LightSlots];
    private readonly double[] _lightAge = new double[LightSlots];
    private readonly float[] _lightPeak = new float[LightSlots];
    private readonly AudioStreamPlayer3D[] _voices = new AudioStreamPlayer3D[Voices];
    private int _nextVoice;
    private readonly ParticleProcessMaterial?[] _materials = new ParticleProcessMaterial?[(int)Look.Count * FireworkShow.Colours];
    private ParticleProcessMaterial? _trail;
    private StandardMaterial3D? _starMaterial;
    private static ImageTexture? _crossPoints;
    private static int _crossCount;
    private readonly Random _jitter = new(1269);

    // sounds waiting for the speed of sound: when (show time), where (LV95 + altitude), which bank
    private readonly double[] _soundAt = new double[PendingSounds], _soundE = new double[PendingSounds], _soundN = new double[PendingSounds];
    private readonly float[] _soundAlt = new float[PendingSounds];
    private readonly byte[] _soundBank = new byte[PendingSounds];
    private int _soundCount;

    // bursts waiting for their moment: the four halves of a crossette, a quarter second later
    private readonly double[] _laterAt = new double[PendingBursts], _laterE = new double[PendingBursts], _laterN = new double[PendingBursts];
    private readonly float[] _laterAlt = new float[PendingBursts];
    private readonly byte[] _laterLook = new byte[PendingBursts], _laterColour = new byte[PendingBursts];
    private int _laterCount;

    // ---- the show on now --------------------------------------------------------------------------

    private FireworkShow? _show;
    private double _start, _doneStart, _nextLook;
    private int _launchCursor, _burstCursor;
    private double _siteE, _siteN, _ground = double.NaN;
    private bool _sees = true, _hears = true;

    /// <summary>Seconds added to the clock: a check seeks through a show that lasts six minutes (never set in the game).</summary>
    public double Skew { get; set; }

    /// <summary>Shells that burst in sight of this viewer, climbs started, bangs and crackles played, since the node began (checks).</summary>
    public int BurstsFired { get; private set; }
    public int RocketsFired { get; private set; }
    public int SoundsPlayed { get; private set; }
    public int ShellsSkipped { get; private set; }
    public int PeakBursts { get; private set; }

    /// <summary>The seed of the show playing, or 0.</summary>
    public int ShowSeed => _show?.Seed ?? 0;

    /// <summary>Seconds into the show playing now, or -1 when none is.</summary>
    public double ShowTime => _show == null ? -1 : Clock - _start;

    /// <summary>The shell the burst cursor stands at (checks).</summary>
    public int Cursor => _burstCursor;

    /// <summary>Particles a full set of pools can hold: the budget of the plan (6 000).</summary>
    public const int ParticleBudget = BurstSlots * StarsPerBurst + RocketSlots * 48;

    private static double Clock => ClockSync.ServerNow;

    public Fireworks(WorldOrigin origin, Func<Vector3, float?> groundAt)
    {
        Name = NodeName;
        _origin = origin;
        _groundAt = groundAt;
    }

    public Fireworks() : this(WorldOrigin.SwissDefault(), _ => null) { }

    public static Fireworks Create(Node world, ChunkManager? chunks, WorldOrigin origin)
    {
        var f = new Fireworks(origin, p => chunks != null && chunks.TryGetHeight(p, out float h) ? h : null);
        world.AddChild(f);
        return f;
    }

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        var puff = Puff();
        _starMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = puff,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
            DisableFog = true,
            NoDepthTest = false,
        };
        for (int i = 0; i < BurstSlots; i++)
        {
            var slot = _bursts[i] = new BurstSlot();
            slot.Q = new QuadMesh { Size = Vector2.One, Material = _starMaterial };
            slot.P = Pool($"Burst{i}", StarsPerBurst, 1f, slot.Q);
            slot.P.OneShot = true;
            slot.P.Explosiveness = 1f;
            slot.P.Randomness = 0.2f;
            AddChild(slot.P);
        }
        for (int i = 0; i < RocketSlots; i++)
        {
            var slot = _rockets[i] = new RocketSlot();
            slot.Q = new QuadMesh { Size = Vector2.One, Material = _starMaterial };
            slot.P = Pool($"Rocket{i}", 48, 0.9f, slot.Q);
            slot.P.ProcessMaterial = Trail();
            slot.P.Emitting = false;
            AddChild(slot.P);
        }
        for (int i = 0; i < LightSlots; i++)
        {
            _lights[i] = new OmniLight3D { Name = $"Flash{i}", OmniRange = 320f, LightEnergy = 0f, Visible = false, ShadowEnabled = false };
            AddChild(_lights[i]);
        }
        for (int i = 0; i < Voices; i++)
        {
            _voices[i] = new AudioStreamPlayer3D { Name = $"Voice{i}", Bus = SfxBus.Name, MaxDistance = (float)FestivalShows.DrawRangeM, UnitSize = 140f };
            AddChild(_voices[i]);
        }
        // touch the banks now, so the first shell does not stall the frame it is heard in
        _ = FireworkSounds.Bang;
        _ = FireworkSounds.Launch;
        _ = FireworkSounds.Crackle;
    }

    private GpuParticles3D Pool(string name, int amount, float life, Mesh mesh) => new()
    {
        Name = name,
        Amount = amount,
        Lifetime = life,
        LocalCoords = false,
        Emitting = false,
        DrawPass1 = mesh,
        VisibilityAabb = new Aabb(new Vector3(-160, -160, -160), new Vector3(320, 320, 320)),
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

    // ---- following the server ---------------------------------------------------------------------

    public override void _Process(double delta)
    {
        double now = Clock + Skew;
        if (_show == null)
        {
            if (now >= _nextLook) { _nextLook = now + 0.5; FindShow(); }
            if (_show == null) return;
        }
        double t = now - _start;
        if (t > _show.Duration + 6)
        {
            _doneStart = _start;
            _show = null;
            return;
        }
        if (t < 0) return;

        var cam = GetViewport()?.GetCamera3D();
        var shells = _show.Shells;
        while (_launchCursor < shells.Length && shells[_launchCursor].Launch <= t) Launch(_launchCursor++, t, cam);
        while (_burstCursor < shells.Length && shells[_burstCursor].T <= t) Burst(_burstCursor++, t, cam);
        Later(t, cam);
        MoveRockets(t);
        FadeLights(delta);
        PlaySounds(t);
    }

    private void FindShow()
    {
        var svc = FestivalService.Instance;
        if (svc == null) return;
        var running = svc.Running;
        for (int i = 0; i < running.Count; i++)
        {
            var r = running[i];
            if (r.ShowStart <= 0 || r.Site is not { } site || r.ShowStart == _doneStart) continue;
            var om = OccasionManager.Instance;
            _sees = om == null || om.IsActive(r.Id, OccasionFacets.Atmosphere);
            _hears = om == null || om.IsActive(r.Id, OccasionFacets.Audio);
            Begin(r.ShowStart, r.ShowSeed, site.E, site.N);
            return;
        }
    }

    private void Begin(double start, int seed, double e, double n)
    {
        _show = FireworkShow.Plan(seed);
        _start = start;
        _siteE = e;
        _siteN = n;
        _ground = double.NaN;
        _soundCount = 0;
        _laterCount = 0;
        foreach (var r in _rockets) Park(r);
        // a peer that joins late joins the show where it is: shells that already burst are not played at once
        double t = Clock + Skew - start;
        _burstCursor = _launchCursor = _show.First(t - 0.5);
        GD.Print($"[fireworks] show of seed {seed}: {_show.Shells.Length} shells, {_show.Duration / 60:F0} min, joined at {Math.Max(0, t):F1} s, cursor {_burstCursor}");
    }

    // ---- shells -----------------------------------------------------------------------------------

    private Vector3 WorldOf(double e, double n, float alt) => _origin.ToWorld(e, n, alt);

    private float GroundAt(double e, double n, Camera3D? cam)
    {
        if (!double.IsNaN(_ground)) return (float)_ground;
        var probe = WorldOf(e, n, 0);
        if (_groundAt(probe) is { } h) return (float)(_ground = h);
        // the site's tile is not loaded (far away): the viewer's own height is the best guess, and not kept
        return cam?.GlobalPosition.Y ?? 400f;
    }

    private (double E, double N, float Alt) Spot(in Shell s, Camera3D? cam) =>
        (_siteE + s.East, _siteN + s.North, GroundAt(_siteE, _siteN, cam) + s.Height);

    private bool InRange(Camera3D? cam, in Vector3 at, out float distance)
    {
        distance = cam == null ? float.MaxValue : cam.GlobalPosition.DistanceTo(at);
        return distance <= FestivalShows.DrawRangeM;
    }

    private void Launch(int index, double t, Camera3D? cam)
    {
        var s = _show!.Shells[index];
        var (e, n, alt) = Spot(s, cam);
        float ground = alt - s.Height;
        var from = WorldOf(_siteE, _siteN, ground);
        if (!_sees || !InRange(cam, from, out float d)) return;
        RocketSlot? slot = null;
        foreach (var r in _rockets)
            if (r.Shell < 0) { slot = r; break; }
        if (slot == null) return;
        slot.Shell = index;
        slot.E = e;
        slot.N = n;
        slot.Height = s.Height;
        slot.Launch = s.Launch;
        slot.Q.Size = Vector2.One * Mathf.Clamp(d / 500f, 1f, 6f) * 0.9f;
        slot.P.GlobalPosition = from;
        slot.P.Restart();
        slot.P.Emitting = true;
        RocketsFired++;
        Sound(t + FestivalShows.SoundDelay(d), _siteE, _siteN, ground, 1);
    }

    private void MoveRockets(double t)
    {
        foreach (var r in _rockets)
        {
            if (r.Shell < 0) continue;
            float u = (float)((t - r.Launch) / FireworkShow.RiseSeconds);
            if (u >= 1f) { Park(r); continue; }
            // a mortar throws it fast and it slows as it climbs
            float rise = 1f - (1f - u) * (1f - u);
            double ground = GroundAt(_siteE, _siteN, null);
            double e = _siteE + (r.E - _siteE) * u, n = _siteN + (r.N - _siteN) * u;
            r.P.GlobalPosition = WorldOf(e, n, (float)ground + r.Height * rise);
        }
    }

    private static void Park(RocketSlot r)
    {
        r.Shell = -1;
        r.P.Emitting = false;
    }

    private void Burst(int index, double t, Camera3D? cam)
    {
        var s = _show!.Shells[index];
        var (e, n, alt) = Spot(s, cam);
        var at = WorldOf(e, n, alt);
        if (!_sees || !InRange(cam, at, out float d)) { ShellsSkipped++; Free(index); return; }
        Free(index);
        BurstsFired++;
        Flash(at, Palette[s.Colour % FireworkShow.Colours], d);
        double heard = t + FestivalShows.SoundDelay(d);
        Sound(heard, e, n, alt, 0);
        switch (s.Kind)
        {
            case ShellKind.Peony: Fire(Look.Peony, s.Colour, at, d, cam); break;
            case ShellKind.Chrysanthemum: Fire(Look.Chrysanthemum, s.Colour, at, d, cam); Sound(heard + 0.5, e, n, alt, 2); break;
            case ShellKind.Willow: Fire(Look.Willow, s.Colour, at, d, cam); Sound(heard + 0.8, e, n, alt, 2); break;
            case ShellKind.Palm: Fire(Look.Palm, s.Colour, at, d, cam); break;
            case ShellKind.Ring: Fire(Look.Ring, s.Colour, at, d, cam); break;
            case ShellKind.Crossette:
                Fire(Look.Crossette, s.Colour, at, d, cam);
                // four halves a moment later, along the fan and up and down
                double sx = Math.Cos(_show.Heading), sy = -Math.Sin(_show.Heading);
                Later(t + 0.9, e + sx * 9, n + sy * 9, alt, Look.CrossetteChild, s.Colour);
                Later(t + 0.9, e - sx * 9, n - sy * 9, alt, Look.CrossetteChild, s.Colour);
                Later(t + 0.9, e, n, alt + 9, Look.CrossetteChild, s.Colour);
                Later(t + 0.9, e, n, alt - 9, Look.CrossetteChild, s.Colour);
                Sound(heard + 0.9, e, n, alt, 2);
                break;
            case ShellKind.SwissCross:
                Fire(Look.CrossRed, 0, at, d, cam);
                Fire(Look.CrossStars, 2, at, d, cam);
                break;
        }
        int live = 0;
        foreach (var b in _bursts) if (b.BusyUntil > t + _start) live++;
        PeakBursts = Math.Max(PeakBursts, live);
    }

    private void Free(int index)
    {
        foreach (var r in _rockets)
            if (r.Shell == index) Park(r);
    }

    private void Later(double at, double e, double n, float alt, Look look, int colour)
    {
        if (_laterCount >= PendingBursts) return;
        int i = _laterCount++;
        (_laterAt[i], _laterE[i], _laterN[i], _laterAlt[i], _laterLook[i], _laterColour[i]) = (at, e, n, alt, (byte)look, (byte)colour);
    }

    private void Later(double t, Camera3D? cam)
    {
        for (int i = _laterCount - 1; i >= 0; i--)
        {
            if (_laterAt[i] > t) continue;
            var at = WorldOf(_laterE[i], _laterN[i], _laterAlt[i]);
            if (_sees && InRange(cam, at, out float d))
            {
                Fire((Look)_laterLook[i], _laterColour[i], at, d, cam);
                Flash(at, Palette[_laterColour[i] % FireworkShow.Colours], d, 0.4f);
                Sound(t + FestivalShows.SoundDelay(d), _laterE[i], _laterN[i], _laterAlt[i], 2);
                BurstsFired++;
            }
            int last = --_laterCount;
            (_laterAt[i], _laterE[i], _laterN[i], _laterAlt[i], _laterLook[i], _laterColour[i]) =
                (_laterAt[last], _laterE[last], _laterN[last], _laterAlt[last], _laterLook[last], _laterColour[last]);
        }
    }

    /// <summary>Starts one pooled burst of a look at a world point: its material, its size for the distance, its tilt, a restart.</summary>
    private void Fire(Look look, int colour, in Vector3 at, float distance, Camera3D? cam)
    {
        var style = Styles[(int)look];
        BurstSlot slot = _bursts[0];
        foreach (var b in _bursts)
        {
            if (b.BusyUntil <= Clock + Skew) { slot = b; break; }
            if (b.BusyUntil < slot.BusyUntil) slot = b;
        }
        slot.P.Emitting = false;
        slot.P.ProcessMaterial = Material(look, colour);
        slot.P.Lifetime = style.Life;
        slot.Q.Size = Vector2.One * style.Size * Mathf.Clamp(distance / 300f, 1f, 8f);
        var basis = Basis.Identity;
        if (look == Look.Ring)
            basis = new Basis(Vector3.Right, (float)_jitter.NextDouble() * 1.1f) * new Basis(Vector3.Up, (float)_jitter.NextDouble() * Mathf.Tau);
        else if (look == Look.CrossStars && cam != null)
        {
            // the cross faces the viewer, upright
            var away = new Vector3(at.X - cam.GlobalPosition.X, 0, at.Z - cam.GlobalPosition.Z);
            if (away.LengthSquared() > 1e-4f) basis = Basis.LookingAt(away.Normalized(), Vector3.Up);
        }
        slot.P.GlobalTransform = new Transform3D(basis, at);
        slot.P.Restart();
        slot.BusyUntil = Clock + Skew + style.Life * 1.3 + 0.3;
    }

    // ---- light and sound --------------------------------------------------------------------------

    private void Flash(in Vector3 at, Color colour, float distance, float strength = 1f)
    {
        if (distance > 6000f) return;
        int pick = 0;
        for (int i = 1; i < LightSlots; i++)
            if (_lightAge[i] > _lightAge[pick]) pick = i;
        var l = _lights[pick];
        l.GlobalPosition = at;
        l.LightColor = colour.Lerp(Colors.White, 0.35f);
        _lightPeak[pick] = 9f * strength;
        l.LightEnergy = _lightPeak[pick];
        l.Visible = true;
        _lightAge[pick] = 0;
    }

    private void FadeLights(double delta)
    {
        for (int i = 0; i < LightSlots; i++)
        {
            var l = _lights[i];
            if (!l.Visible) { _lightAge[i] = 99; continue; }
            _lightAge[i] += delta;
            float k = 1f - (float)(_lightAge[i] / 0.5);
            if (k <= 0f) { l.Visible = false; l.LightEnergy = 0f; continue; }
            l.LightEnergy = _lightPeak[i] * k * k;
        }
    }

    private void Sound(double at, double e, double n, float alt, int bank)
    {
        if (!_hears || _soundCount >= PendingSounds) return;
        int i = _soundCount++;
        (_soundAt[i], _soundE[i], _soundN[i], _soundAlt[i], _soundBank[i]) = (at, e, n, alt, (byte)bank);
    }

    private void PlaySounds(double t)
    {
        for (int i = _soundCount - 1; i >= 0; i--)
        {
            if (_soundAt[i] > t) continue;
            // a sound more than a second late (a seek, a stalled frame) is dropped, not played in a pile
            if (t - _soundAt[i] < 1.0)
            {
                var voice = _voices[_nextVoice];
                _nextVoice = (_nextVoice + 1) % Voices;
                var (stream, pitch, db) = _soundBank[i] switch
                {
                    0 => FireworkSounds.Bang.Pick(_jitter),
                    1 => FireworkSounds.Launch.Pick(_jitter),
                    _ => FireworkSounds.Crackle.Pick(_jitter),
                };
                voice.Stream = stream;
                voice.PitchScale = pitch;
                voice.VolumeDb = db - (_soundBank[i] == 1 ? 6f : 0f);
                voice.GlobalPosition = WorldOf(_soundE[i], _soundN[i], _soundAlt[i]);
                voice.Play();
                SoundsPlayed++;
            }
            int last = --_soundCount;
            (_soundAt[i], _soundE[i], _soundN[i], _soundAlt[i], _soundBank[i]) = (_soundAt[last], _soundE[last], _soundN[last], _soundAlt[last], _soundBank[last]);
        }
    }

    // ---- materials, made once ---------------------------------------------------------------------

    private ParticleProcessMaterial Material(Look look, int colourIndex)
    {
        int key = (int)look * FireworkShow.Colours + (colourIndex & (FireworkShow.Colours - 1));
        if (_materials[key] is { } cached) return cached;
        var style = Styles[(int)look];
        var colour = Palette[colourIndex & (FireworkShow.Colours - 1)];
        var m = new ParticleProcessMaterial
        {
            Direction = Vector3.Up,
            Spread = 180f,
            InitialVelocityMin = style.VMin,
            InitialVelocityMax = style.VMax,
            DampingMin = style.Damp * 0.9f,
            DampingMax = style.Damp * 1.1f,
            Gravity = new Vector3(0, style.Gravity, 0),
            ScaleMin = 0.7f,
            ScaleMax = 1.0f,
            ColorRamp = Ramp(colour, look == Look.Willow),
        };
        if (look == Look.Ring)
        {
            m.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring;
            m.EmissionRingAxis = Vector3.Forward;
            m.EmissionRingRadius = 0.5f;
            m.EmissionRingInnerRadius = 0.4f;
            m.EmissionRingHeight = 0.1f;
            m.Spread = 0f;
            m.InitialVelocityMin = m.InitialVelocityMax = 0f;
            m.RadialVelocityMin = style.VMin;
            m.RadialVelocityMax = style.VMax;
        }
        else if (look == Look.CrossStars)
        {
            m.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Points;
            m.EmissionPointTexture = CrossPoints();
            m.EmissionPointCount = _crossCount;
            m.EmissionShapeScale = new Vector3(11, 11, 1);
            m.InitialVelocityMin = m.InitialVelocityMax = 0f;
            m.DampingMin = m.DampingMax = 0f;
        }
        else
        {
            m.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere;
            m.EmissionSphereRadius = 0.4f;
        }
        return _materials[key] = m;
    }

    private ParticleProcessMaterial Trail() => _trail ??= new ParticleProcessMaterial
    {
        EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
        EmissionSphereRadius = 0.3f,
        Direction = Vector3.Down,
        Spread = 40f,
        InitialVelocityMin = 1f,
        InitialVelocityMax = 4f,
        Gravity = new Vector3(0, -5, 0),
        ScaleMin = 0.4f,
        ScaleMax = 0.9f,
        ColorRamp = Ramp(new Color(1f, 0.7f, 0.3f), false),
    };

    /// <summary>White hot, the colour, a dimmer colour, gone.</summary>
    private static GradientTexture1D Ramp(Color colour, bool droop) => new()
    {
        Gradient = new Gradient
        {
            Offsets = [0f, 0.12f, droop ? 0.75f : 0.6f, 1f],
            Colors =
            [
                colour.Lerp(Colors.White, 0.8f),
                colour,
                new Color(colour.R * 0.8f, colour.G * 0.7f, colour.B * 0.6f, 0.8f),
                new Color(colour.R * 0.5f, colour.G * 0.4f, colour.B * 0.3f, 0f),
            ],
        },
    };

    private static ImageTexture CrossPoints()
    {
        if (_crossPoints != null) return _crossPoints;
        // a Swiss cross in the XY plane, [-1, 1]: a vertical and a horizontal arm, each 0.36 wide
        var pts = new List<Vector3>();
        for (float a = -1f; a <= 1.001f; a += 0.1f)
            for (float b = -0.18f; b <= 0.181f; b += 0.12f)
            {
                pts.Add(new Vector3(b, a, 0));
                if (MathF.Abs(a) > 0.2f) pts.Add(new Vector3(a, b, 0));
            }
        var data = new byte[pts.Count * 12];
        var floats = new float[pts.Count * 3];
        for (int i = 0; i < pts.Count; i++) (floats[i * 3], floats[i * 3 + 1], floats[i * 3 + 2]) = (pts[i].X, pts[i].Y, pts[i].Z);
        Buffer.BlockCopy(floats, 0, data, 0, data.Length);
        _crossCount = pts.Count;
        return _crossPoints = ImageTexture.CreateFromImage(Image.CreateFromData(pts.Count, 1, false, Image.Format.Rgbf, data));
    }

    private static GradientTexture2D Puff() => new()
    {
        Width = 32,
        Height = 32,
        Fill = GradientTexture2D.FillEnum.Radial,
        FillFrom = new Vector2(0.5f, 0.5f),
        FillTo = new Vector2(1f, 0.5f),
        Gradient = new Gradient
        {
            Colors = [new Color(1, 1, 1, 1), new Color(1, 1, 1, 0.7f), new Color(1, 1, 1, 0)],
            Offsets = [0f, 0.4f, 1f],
        },
    };
}
