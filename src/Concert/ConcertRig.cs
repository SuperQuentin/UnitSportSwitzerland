using Godot;
using UnitSport.Core;
using UnitSport.Styles;
using UnitSport.Terrain;
using UnitSport.Terrain.Stadiums;

namespace UnitSport.Concert;

/// <summary>
/// A concert on stage (#885): the stage built from its <see cref="StagePlan"/> (one prop mesh, its
/// collision), and the rig drawn every frame from the DMX the <see cref="LightDesk"/> writes, decoded
/// fixture by fixture as the real heads would read it: 16-bit pan and tilt turn the head and its
/// beam, dimmer and shutter (strobing) its intensity, RGBW, CMY or the colour wheel its colour, zoom
/// its cone; blinders, strobes and pixel bars light their cells; each screen's media-server layer
/// plays its clip on a grid of pixels. Beams are additive cones and lens flares (no volumetrics in
/// the mobile renderer); the haze decides how much of a beam shows. A few real spot lights carry the
/// rig's colour onto the stage and the people in front of it.
///
/// <para>
/// The node sits at the stadium's centre spot, turned to its frame: children are in stadium
/// coordinates (x = u across the pitch, z = v along it, y over the pitch). Nothing per frame
/// allocates: the arrays are made once, the multimeshes are written in place.
/// </para>
/// </summary>
public partial class ConcertRig : Node3D
{
    public ConcertEvent Event { get; }
    /// <summary>Plays the show whatever the hour (<c>--concert</c>, the model viewer).</summary>
    public bool ForceLive { get; set; }
    /// <summary>Fills the stadium with its audience (not in the model viewer).</summary>
    public bool WithCrowd { get; set; } = true;

    /// <summary>The rigs on stage now, by stadium: what the show input and the relay drive.</summary>
    public static readonly Dictionary<string, ConcertRig> Live = new();

    public LightDesk Desk => _desk;
    public StagePlan? Plan => _plan;
    public DmxPatch Patch => _plan.Patch;

    /// <summary>
    /// Universes from outside (an operator's Art-Net or sACN, here or relayed by the server): while a
    /// frame came in the last <see cref="ExternalHold"/> seconds they are the rig's DMX and the desk
    /// rests; then the desk takes over again.
    /// </summary>
    private byte[][] _external = Array.Empty<byte[]>();
    private double _externalAt = double.NegativeInfinity;
    public const double ExternalHold = 2.0;
    public bool ExternalLive => Core.RealClock.Now - _externalAt < ExternalHold;

    /// <summary>One universe from outside, the rig's own numbering (0 = the first patched).</summary>
    public void TakeUniverse(int universe, ReadOnlySpan<byte> data)
    {
        if (_plan == null || universe < 0 || universe >= _plan.Patch.Universes.Count) return;
        if (_external.Length != _plan.Patch.Universes.Count)
            _external = Enumerable.Range(0, _plan.Patch.Universes.Count).Select(_ => new byte[DmxPatch.Channels]).ToArray();
        data[..Math.Min(data.Length, DmxPatch.Channels)].CopyTo(_external[universe]);
        _externalAt = Core.RealClock.Now;
    }

    private StagePlan _plan = null!;
    private LightDesk _desk = null!;
    private Fixture[] _heads = Array.Empty<Fixture>(), _cells = Array.Empty<Fixture>(), _screens = Array.Empty<Fixture>();
    private MultiMesh _bodies = null!, _beams = null!, _glows = null!;
    private MultiMesh[] _pixels = Array.Empty<MultiMesh>();
    private int _glowCount;
    private readonly List<SpotLight3D> _lamps = new();
    private OmniLight3D? _crowdLight;
    private float _haze;
    private ConcertCrowd? _crowd;
    private ConcertSfx _sfx = null!;
    private ConcertMusic _music = null!;
    private ConcertPa _pa = null!;
    private ConcertCameras _cameras = null!;
    private ConcertPerformers _performers = null!;
    private MeshInstance3D[] _feeds = Array.Empty<MeshInstance3D>();
    private MultiMeshInstance3D[] _pixelNodes = Array.Empty<MultiMeshInstance3D>();
    public ConcertCameras Cameras => _cameras;
    public ConcertMusic Music => _music;
    private float _energy;

    private const float BeamLength = 70f;

    /// <summary>
    /// How far each beam reaches before it meets something solid (the roof, a stand, the pitch): a beam
    /// stops there instead of shining on through the stadium's walls. One ray, reused, checks a quarter
    /// of the beams a frame (each about 15 times a second); the stage's own body is not in its way, so
    /// beams still climb through the grid into the sky.
    /// </summary>
    private float[] _reach = Array.Empty<float>();

    /// <summary>The fastest strobe running now (Hz, 0 when none is fast), its duty, its clock, and what the screens do with it.</summary>
    private float _strobeHz, _strobeDuty = 0.1f, _strobeT, _feedLevel = 1f, _screenFlash = -1f;

    /// <summary>
    /// Each head's motors: where its pan and tilt really are (degrees) and how fast they turn. The DMX is
    /// only the target, as on a real fixture: the steppers chase it at their top speed and acceleration
    /// (<see cref="Motor"/>), so a jump on the desk is a sweep on the rig.
    /// </summary>
    private float[] _pan = Array.Empty<float>(), _tilt = Array.Empty<float>(), _panV = Array.Empty<float>(), _tiltV = Array.Empty<float>();
    private bool _motorsSet;
    private RayCast3D? _ray;
    private int _rayNext;
    private StaticBody3D? _stageBody;

    public ConcertRig(ConcertEvent e)
    {
        Event = e;
        Name = "Concert";
    }

    public ConcertRig() : this(ConcertPlanner.Plan(StadiumCatalog.Geneva, 1)) { }

    /// <summary>An imported rig to build in place of the generated one (#941), set before it enters the tree.</summary>
    public Mvr.RigData? Imported { get; init; }

    /// <summary>The ground's height at a point of the venue's frame (an open air's slope, an arena's floor), set by the director (#948).</summary>
    public Func<Vector2, float>? Ground { get; init; }

    /// <summary>A stadium's protection floor's top over the centre spot (#956): over the terrain's bumps, set by the director.</summary>
    public float FloorTop { get; init; } = 0.1f;

    public override void _Ready()
    {
        _plan = new StagePlan(Event);
        if (Ground != null && Event.Stadium.Kind == VenueKind.OpenAir) _plan.Lift(Ground);
        if (Imported != null) _plan.UseRig(Imported);
        _desk = new LightDesk(_plan);
        BuildStage();
        BuildObjects();
        var fx = _plan.Patch.Fixtures;
        _heads = fx.Where(f => f.Type.Kind is FixtureKind.Spot or FixtureKind.Beam or FixtureKind.Wash).ToArray();
        _reach = _heads.Select(f => f.Type.Kind == FixtureKind.Wash ? BeamLength * 0.55f : BeamLength).ToArray();
        _pan = new float[_heads.Length]; _tilt = new float[_heads.Length];
        _panV = new float[_heads.Length]; _tiltV = new float[_heads.Length];
        _ray = new RayCast3D { Name = "BeamRay", Enabled = false, HitBackFaces = true, HitFromInside = false };
        AddChild(_ray);
        if (_stageBody != null) _ray.AddException(_stageBody);
        _cells = fx.Where(f => f.Type.Kind is FixtureKind.Blinder or FixtureKind.Strobe or FixtureKind.PixelBar).ToArray();
        _screens = fx.Where(f => f.Type.Kind == FixtureKind.Screen).ToArray();
        BuildRig();
        // the artists, and the cameras that film them for the screens, this screen and NDI
        _performers = new ConcertPerformers(_plan, _desk);
        AddChild(_performers);
        _cameras = new ConcertCameras(_plan, _desk);
        AddChild(_cameras);
        // the music from the PA: the setlist on the shared clock, read for the desk
        _music = new ConcertMusic(Event, _plan.PaHangs.ToArray());
        AddChild(_music);
        _pa = new ConcertPa(_plan);
        AddChild(_pa);
        // pyro, CO2, confetti, fog: fixtures of the rig like the rest
        _sfx = new ConcertSfx(_plan.Patch, Event);
        AddChild(_sfx);
        // the audience, during the show
        if (WithCrowd)
        {
            // a stadium's crowd on the protection floor
            var floor = FloorTop;
            _crowd = new ConcertCrowd(_plan) { Visible = false, Ground = Ground ?? (Event.Stadium.Kind == VenueKind.Stadium ? _ => floor : null) };
            AddChild(_crowd);
        }
        Live[Event.Stadium.Name] = this;
        GD.Print($"[concert] {Event}: {fx.Count} fixtures, {_plan.Patch.Universes.Count} universes ({_plan.Patch.ChannelsUsed} channels)");
    }

    // ---- the stage ----------------------------------------------------------------------------------

    private void BuildStage()
    {
        var g = new StadiumBuilder.Geo(mesh: true, collision: true);
        var unseen = new StadiumBuilder.Geo(mesh: false, collision: true);
        foreach (var b in _plan.Boxes)
            (b.Hidden ? unseen : g).Box(b.Centre, b.Size, new Basis(Vector3.Up, b.Yaw), b.Color.SrgbToLinear() with { A = 0f }, b.Solid);
        g.Faces.AddRange(unseen.Faces);
        foreach (var r in _plan.Ramps) g.Solid(r.A, r.B, r.C, r.D);
        ConcertFloor.Build(_plan, FloorTop, g);
        if (g.Data() is { } data)
            AddChild(new MeshInstance3D { Name = "Stage", Mesh = ChunkNode.ToPropMesh(data, StyleKit.Material(MaterialRole.Prop)) });
        if (g.Faces.Count > 0)
        {
            var body = _stageBody = new StaticBody3D { Name = "StageBody" };
            body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = g.Faces.ToArray(), BackfaceCollision = true } });
            AddChild(body);
        }
    }

    // ---- the rig ------------------------------------------------------------------------------------

    private static StandardMaterial3D Additive(Texture2D? texture = null, bool billboard = false) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        VertexColorUseAsAlbedo = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        BlendMode = BaseMaterial3D.BlendModeEnum.Add,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoTexture = texture,
        BillboardMode = billboard ? BaseMaterial3D.BillboardModeEnum.Enabled : BaseMaterial3D.BillboardModeEnum.Disabled,
        BillboardKeepScale = true,
        NoDepthTest = false,
        DisableFog = true,
    };

    private MultiMeshInstance3D Multi(string name, Mesh mesh, int count, Material material, out MultiMesh mm)
    {
        mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh, InstanceCount = count };
        var mi = new MultiMeshInstance3D { Name = name, Multimesh = mm, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(mi);
        return mi;
    }

    /// <summary>A cone, apex at the origin, its open base a unit circle one metre down −Y; bright at the apex, gone at the base.</summary>
    [Showcase("Concert", "Beam cone")]
    private static ArrayMesh Cone()
    {
        const int N = 12;
        var v = new List<Vector3>();
        var c = new List<Color>();
        for (int i = 0; i < N; i++)
        {
            float a0 = i * Mathf.Tau / N, a1 = (i + 1) * Mathf.Tau / N;
            v.Add(Vector3.Zero); c.Add(new Color(1, 1, 1, 1));
            v.Add(new Vector3(MathF.Cos(a0), -1, MathF.Sin(a0))); c.Add(new Color(1, 1, 1, 0));
            v.Add(new Vector3(MathF.Cos(a1), -1, MathF.Sin(a1))); c.Add(new Color(1, 1, 1, 0));
        }
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = c.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static Texture2D Flare() => new GradientTexture2D
    {
        Width = 64, Height = 64, Fill = GradientTexture2D.FillEnum.Radial,
        FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
        Gradient = new Gradient { Colors = new[] { new Color(1, 1, 1, 1), new Color(1, 1, 1, 0.25f), new Color(1, 1, 1, 0) }, Offsets = new[] { 0f, 0.25f, 1f } },
    };

    private void BuildRig()
    {
        var body = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.08f, 0.09f), Roughness = 0.6f };
        Multi("Heads", new BoxMesh { Size = new Vector3(0.45f, 0.55f, 0.45f) }, _heads.Length + _cells.Length, body, out _bodies);
        // the cones' far side only: half the fill of both, and a beam still shows from inside it
        var beamMat = Additive();
        beamMat.CullMode = BaseMaterial3D.CullModeEnum.Front;
        Multi("Beams", Cone(), _heads.Length, beamMat, out _beams);
        _glowCount = _heads.Length;
        foreach (var f in _cells) _glowCount += CellsOf(f);
        Multi("Flares", new QuadMesh { Size = Vector2.One }, _glowCount, Additive(Flare(), billboard: true), out _glows);

        // the cells' bodies stand still: placed once
        for (int i = 0; i < _cells.Length; i++)
        {
            var f = _cells[i];
            var size = f.Type.Kind == FixtureKind.PixelBar ? new Vector3(2.4f, 0.25f, 0.25f) : new Vector3(1f, 0.5f, 0.3f);
            var basis = Facing(f.Home) * Basis.FromScale(size / new Vector3(0.45f, 0.55f, 0.45f));
            _bodies.SetInstanceTransform(_heads.Length + i, new Transform3D(basis, f.Position));
            _bodies.SetInstanceColor(_heads.Length + i, Colors.White);
        }

        // the screens: a grid of pixels each, facing out
        _pixels = new MultiMesh[_screens.Length];
        _pixelNodes = new MultiMeshInstance3D[_screens.Length];
        _feeds = new MeshInstance3D[_screens.Length];
        var screenMat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, DisableFog = true };
        for (int s = 0; s < _screens.Length; s++)
        {
            var f = _screens[s];
            float pw = f.ScreenSize.X / f.PixelsX, ph = f.ScreenSize.Y / f.PixelsY;
            var mi = Multi($"Screen{s}", new QuadMesh { Size = new Vector2(pw * 0.92f, ph * 0.92f) }, f.PixelsX * f.PixelsY, screenMat, out _pixels[s]);
            mi.Position = f.Position;
            // a quad shows its +Z side: the screen's +Z looks at the audience
            mi.Basis = Facing(-f.Home);
            _pixelNodes[s] = mi;
            // the camera clip: the program's low render on the screen, its pixels the LED's own
            var feed = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest, DisableFog = true,
            };
            _feeds[s] = new MeshInstance3D
            {
                Name = $"Feed{s}", Position = f.Position, Basis = Facing(-f.Home), Visible = false,
                Mesh = new QuadMesh { Size = new Vector2(f.ScreenSize.X, f.ScreenSize.Y) }, MaterialOverride = feed,
            };
            AddChild(_feeds[s]);
            for (int y = 0; y < f.PixelsY; y++)
                for (int x = 0; x < f.PixelsX; x++)
                    _pixels[s].SetInstanceTransform(y * f.PixelsX + x, new Transform3D(Basis.Identity,
                        new Vector3((x + 0.5f) * pw - f.ScreenSize.X / 2, (y + 0.5f) * ph - f.ScreenSize.Y / 2, 0)));
        }

        // a few real lights: front-truss spots onto the stage, and one over the crowd
        var front = _plan.Patch.Fixtures.Where(f => f.Group == RigGroup.FrontTruss).ToArray();
        // three, wide: every real light is paid for on every pixel it reaches
        for (int i = 0; i < 3 && front.Length > 0; i++)
        {
            var f = front[(int)((i + 0.5f) / 3 * front.Length)];
            var lamp = new SpotLight3D { Position = f.Position, SpotRange = 45f, SpotAngle = 40f, LightEnergy = 0f, ShadowEnabled = false };
            AddChild(lamp);
            lamp.LookAt(ToGlobal(f.Position + Vector3.Down * 10f - f.Home * 6f), Vector3.Forward);
            _lamps.Add(lamp);
        }
        _crowdLight = new OmniLight3D { Position = _plan.Patch.Fixtures.FirstOrDefault()?.Position + Vector3.Down * 4f ?? Vector3.Up * 10f, OmniRange = 40f, LightEnergy = 0f, ShadowEnabled = false };
        AddChild(_crowdLight);
    }

    private static int CellsOf(Fixture f) => f.Type.Cells;

    /// <summary>A basis whose −Z looks along <paramref name="dir"/> (what a screen or a cell faces).</summary>
    private static Basis Facing(Vector3 dir)
    {
        var z = -dir.Normalized();
        var x = Vector3.Up.Cross(z);
        if (x.LengthSquared() < 1e-6f) x = Vector3.Right;
        x = x.Normalized();
        return new Basis(x, z.Cross(x), z);
    }

    /// <summary>
    /// The concert playing round a point, for the dances (<see cref="Items.RadioManager.NearestMusic"/>):
    /// anyone in the bowl dances to it, on its beat.
    /// </summary>
    public static Items.RadioManager.Music? MusicNear(Vector3 point, float within = 160f)
    {
        foreach (var rig in Live.Values)
        {
            if (!IsInstanceValid(rig) || rig._music?.Song is not { } song) continue;
            if (rig.GlobalPosition.DistanceTo(point) > within) continue;
            var speaker = rig._music.GetChildOrNull<Items.RadioSpeaker>(0);
            if (speaker == null) continue;
            return new Items.RadioManager.Music(speaker, song.Id, rig._music.SongStartedAt);
        }
        return null;
    }

    public override void _ExitTree()
    {
        if (Live.TryGetValue(Event.Stadium.Name, out var r) && r == this) Live.Remove(Event.Stadium.Name);
    }

    // ---- every frame ------------------------------------------------------------------------------

    /// <summary>Whether the show is on at this hour (the stage stands all day; the rig plays in the evening).</summary>
    private bool ShowOn()
    {
        if (ForceLive) return true;
        double h = World.WorldClock.CurrentHour;
        return h >= Event.StartHour && h < Event.EndHour;
    }

    public override void _Process(double delta)
    {
        double now = Net.ClockSync.ServerNow;
        var p = _plan.Patch;
        _desk.Music = _music.Step(now, ShowOn());
        if (ExternalLive)
            for (int u = 0; u < _external.Length; u++) _external[u].CopyTo(p.Universes[u], 0);
        else _desk.Write(ShowOn() ? now % 86400.0 : -1.0);
        // the song's loudness rides the master: a quiet bridge dims the rig a little
        float master = Math.Clamp(_desk.Master, 0f, 1f) * (_desk.Music is { } song ? 0.6f + 0.4f * song.Level : 1f);
        _sfx.Step(now);
        float t = (float)(now % 3600.0);

        _haze = 0;
        foreach (var f in p.Fixtures)
            if (f.Type.Kind == FixtureKind.Hazer) _haze = MathF.Max(_haze, p.Get01(f, Attr.Pump));
        float haze = 0.25f + 0.75f * _haze;

        // the motors follow the DMX
        float dt = Math.Min((float)delta, 0.1f);
        for (int i = 0; i < _heads.Length; i++)
        {
            var f = _heads[i];
            float pan01 = p.Get16(f, Attr.Pan, Attr.PanFine), tilt01 = p.Get16(f, Attr.Tilt, Attr.TiltFine);
            // an inverted head turns the other way for the same DMX (#959)
            float wantPan = ((f.InvertPan ? 1f - pan01 : pan01) - 0.5f) * f.Type.PanRange;
            float wantTilt = ((f.InvertTilt ? 1f - tilt01 : tilt01) - 0.5f) * f.Type.TiltRange;
            if (!_motorsSet) { _pan[i] = wantPan; _tilt[i] = wantTilt; continue; }
            // degrees a second, and a second squared: a beam head is quick, a wash a heavier yoke
            (float v, float a) = f.Type.Kind switch { FixtureKind.Beam => (300f, 1800f), FixtureKind.Spot => (220f, 1100f), _ => (160f, 700f) };
            Motor(ref _pan[i], ref _panV[i], wantPan, v, a, dt);
            Motor(ref _tilt[i], ref _tiltV[i], wantTilt, v * 0.9f, a * 0.9f, dt);
        }
        _motorsSet = true;

        // a quarter of the beams find what they hit this frame
        int rays = (_heads.Length + 3) / 4;
        for (int k = 0; k < rays && _ray != null && _heads.Length > 0; k++)
        {
            int i = _rayNext++ % _heads.Length;
            var f = _heads[i];
            float max = f.Type.Kind == FixtureKind.Wash ? BeamLength * 0.55f : BeamLength;
            var dir = Direction(i);
            _ray.Position = f.Position + dir * 0.4f;
            _ray.TargetPosition = _ray.Basis.Inverse() * (dir * max);
            _ray.ForceRaycastUpdate();
            float reach = _ray.IsColliding() ? _ray.GlobalPosition.DistanceTo(_ray.GetCollisionPoint()) / Scale.X + 0.4f : max;
            // the venue's own walls and roof, whether or not its collision is built (it is only near a body)
            reach = Math.Min(reach, VenueReach(f.Position, dir, max));
            _reach[i] = Mathf.Lerp(_reach[i], reach, 0.6f);
        }

        int glow = 0;
        Color washSum = default, beamSum = default;
        for (int i = 0; i < _heads.Length; i++)
        {
            var f = _heads[i];
            var dir = Direction(i);
            // no dimmer channel (an imported LED par): its colours carry the level
            float dim = (f.Type.Offset(Attr.Dimmer) >= 0 ? p.Get01(f, Attr.Dimmer) : 1f) * Shutter(f, t) * master;
            var col = ColorOf(f);
            float zoom = p.Get01(f, Attr.Zoom);
            float angle = Mathf.DegToRad(Mathf.Lerp(f.Type.BeamMin, f.Type.BeamMax, zoom));
            float len = _reach[i];
            float radius = len * MathF.Tan(angle / 2);
            // the head turns with its beam
            _bodies.SetInstanceTransform(i, new Transform3D(Facing(dir), f.Position));
            _bodies.SetInstanceColor(i, Colors.White);
            // the cone: Y down the beam, X and Z its radius
            var side = dir.Cross(Mathf.Abs(dir.Y) > 0.95f ? Vector3.Right : Vector3.Up).Normalized();
            var other = side.Cross(dir).Normalized();
            _beams.SetInstanceTransform(i, new Transform3D(new Basis(side * radius, -dir * len, other * radius), f.Position));
            float strength = f.Type.Kind switch { FixtureKind.Beam => 0.6f, FixtureKind.Spot => 0.27f, _ => 0.1f };
            _beams.SetInstanceColor(i, (col * (dim * strength * haze)) with { A = 1f });
            _glows.SetInstanceTransform(glow, new Transform3D(Basis.FromScale(Vector3.One * (f.Type.Kind == FixtureKind.Wash ? 1.4f : 0.9f)), f.Position + dir * 0.35f));
            _glows.SetInstanceColor(glow++, (col * dim) with { A = 1f });
            if (f.Type.Kind == FixtureKind.Wash) washSum += col * dim;
            else beamSum += col * dim;
        }
        _strobeHz = 0f;
        _strobeDuty = 0.1f;
        for (int i = 0; i < _cells.Length; i++)
        {
            var f = _cells[i];
            int n = CellsOf(f);
            var basis = Facing(f.Home);
            for (int c = 0; c < n; c++)
            {
                Color col;
                float size;
                if (f.Type.Kind == FixtureKind.PixelBar)
                {
                    var co = f.Type.CellOffsets;
                    float own = f.Type.Offset(Attr.Dimmer) >= 0 ? p.Get01(f, Attr.Dimmer) : 1f;
                    col = co.Length >= c * 3 + 3
                        ? new Color(p.Get(f, co[c * 3]) / 255f, p.Get(f, co[c * 3 + 1]) / 255f, p.Get(f, co[c * 3 + 2]) / 255f) * own
                        : Colors.Black;
                    size = 0.5f;
                }
                else if (f.Type.Kind == FixtureKind.Blinder)
                {
                    var co = f.Type.CellOffsets;
                    float own = f.Type.Offset(Attr.Dimmer) >= 0 ? p.Get01(f, Attr.Dimmer) : 1f;
                    col = new Color(1f, 0.78f, 0.45f) * (c < co.Length ? p.Get(f, co[c]) / 255f * own : 0f);   // tungsten
                    size = 1.3f;
                }
                else
                {
                    float rate = 1f + 24f * p.Get01(f, Attr.Rate), duty = 0.05f + 0.3f * p.Get01(f, Attr.Duration);
                    float on = (t * rate) % 1f < duty ? 1f : 0f;
                    // a fast strobe running: what the screens and the dancers follow
                    if (p.Get01(f, Attr.Dimmer) > 0.5f && rate >= 6f && rate > _strobeHz) { _strobeHz = rate; _strobeDuty = duty; }
                    col = Colors.White * (p.Get01(f, Attr.Dimmer) * on);
                    size = 2.2f;
                }
                float along = n == 1 ? 0 : (c - (n - 1) / 2f) * (f.Type.Kind == FixtureKind.PixelBar ? 0.3f : 0.24f);
                _glows.SetInstanceTransform(glow, new Transform3D(Basis.FromScale(Vector3.One * size), f.Position + basis.X * along - basis.Z * 0.2f));
                _glows.SetInstanceColor(glow++, col with { A = 1f });
                beamSum += col * 0.3f;
            }
        }
        _strobeT = t;
        bool flash = _strobeHz > 0f && (t * _strobeHz) % 1f < _strobeDuty;
        // the screens under a strobe: dim between the flashes, washed out on each (the camera feeds too)
        float feedLevel = _strobeHz <= 0f ? 1f : flash ? 1.6f : 0.3f;
        if (MathF.Abs(feedLevel - _feedLevel) > 0.01f)
        {
            _feedLevel = feedLevel;
            foreach (var feed in _feeds)
                if (feed.MaterialOverride is StandardMaterial3D fm) fm.AlbedoColor = new Color(feedLevel, feedLevel, feedLevel);
        }
        _screenFlash = _strobeHz <= 0f ? -1f : flash ? 1f : 0f;
        bool imag = false;
        for (int s = 0; s < _screens.Length; s++)
        {
            bool camera = p.Get(_screens[s], _screens[s].Type.Offset(Attr.Clip)) / 32 == LightDesk.CameraClip && p.Get01(_screens[s], Attr.Dimmer) > 0.05f;
            if (_feeds[s].Visible != camera)
            {
                _feeds[s].Visible = camera;
                _pixelNodes[s].Visible = !camera;
                if (camera && _feeds[s].MaterialOverride is StandardMaterial3D m) m.AlbedoTexture = _cameras.ImagTexture;
            }
            imag |= camera;
            if (!camera) PaintScreen(_screens[s], _pixels[s], t);
        }
        // the artists dance and the cameras film only while someone can see it
        bool near = GetViewport()?.GetCamera3D() is { } eye && eye.GlobalPosition.DistanceTo(GlobalPosition) < 900f;
        if (near && (ShowOn() || ExternalLive)) _performers.Step(_strobeHz, _strobeT);
        _cameras.ImagWanted = imag && near;
        _cameras.Step(now % 86400.0);
        // the PA bounces on the kick
        if (near)
        {
            bool live = (ShowOn() || ExternalLive) && _desk.Section is not (ShowSection.Blackout or ShowSection.WalkIn);
            float beatPh = _desk.Beat - MathF.Floor(_desk.Beat);
            float kick = !live ? 0f : _desk.Music is { } mu ? mu.Kick : MathF.Exp(-beatPh * 6f);
            _pa.Pump(kick, _desk.Music is { HasBands: true } mb ? mb.Bass : 0.6f);
        }

        // the real lights follow the rig as a whole
        float washN = Math.Max(1, _heads.Length / 3);
        var wash = washSum / washN;
        foreach (var lamp in _lamps)
        {
            lamp.LightColor = new Color(Math.Min(1f, wash.R), Math.Min(1f, wash.G), Math.Min(1f, wash.B));
            lamp.LightEnergy = 3f * Math.Min(1f, (wash.R + wash.G + wash.B) / 2f);
        }
        if (_crowdLight != null)
        {
            var b = beamSum / Math.Max(1, _heads.Length);
            _crowdLight.LightColor = new Color(Math.Min(1f, b.R * 2f), Math.Min(1f, b.G * 2f), Math.Min(1f, b.B * 2f));
            _crowdLight.LightEnergy = 0.6f * Math.Min(1f, b.R + b.G + b.B);
        }
        DriveCrowd(washSum + beamSum);
    }

    /// <summary>The audience follows the show: how hard it dances by section, the rig's colour on it.</summary>
    private void DriveCrowd(Color light)
    {
        if (_crowd == null) return;
        bool on = ShowOn() || ExternalLive;
        if (_crowd.Visible != on) _crowd.Visible = on;
        if (!on) return;
        float want = ExternalLive ? 0.6f : _desk.Section switch
        {
            ShowSection.Calm => 0.12f, ShowSection.Groove => 0.55f, ShowSection.Chorus => 0.9f,
            ShowSection.Peak => 1f, ShowSection.Blackout => 0.3f, _ => 0.05f,
        };
        _energy = Mathf.Lerp(_energy, want, 0.05f);
        float n = Math.Max(1, _heads.Length);
        var tint = new Color(Math.Min(0.6f, light.R / n * 0.9f), Math.Min(0.6f, light.G / n * 0.9f), Math.Min(0.6f, light.B / n * 0.9f));
        // under a fast strobe the crowd is seen only in its flashes: its beat stands still between them
        float beat = _desk.Beat;
        if (_strobeHz > 0f)
        {
            float bpm = _music.Song?.Bpm ?? Event.Bpm;
            beat -= (_strobeT * _strobeHz) % 1f / _strobeHz * bpm / 60f;
        }
        _crowd.Drive(beat, _energy, tint, _desk.Section == ShowSection.Chorus ? 1f : 0f);
    }

    /// <summary>The beam's direction from 16-bit pan and tilt: tilt tips it from the yoke's axis towards the fixture's home side, pan turns that about the axis.</summary>
    private Vector3 Direction(int i)
    {
        var f = _heads[i];
        // an imported plot's focus point is the beam at pan and tilt centre (#959)
        float pan = Mathf.DegToRad(_pan[i] + f.HomePan), tilt = Mathf.DegToRad(_tilt[i] + f.HomeTilt);
        var up = f.Up;
        var fwd = (f.Home - up * f.Home.Dot(up));
        fwd = fwd.LengthSquared() < 1e-6f ? up.Cross(Vector3.Right).Normalized() : fwd.Normalized();
        var right = up.Cross(fwd);
        return (up * MathF.Cos(tilt) + (fwd * MathF.Cos(pan) + right * MathF.Sin(pan)) * MathF.Sin(tilt)).Normalized();
    }

    /// <summary>
    /// How far a beam goes before the venue stops it (#948), from its shape alone: out of the open
    /// column over the pitch below the roof's edge (into the stands, under the roof), or up into an
    /// arena's ceiling. Over a stadium's roof edge it goes on into the sky. An open air stops nothing.
    /// </summary>
    private float VenueReach(Vector3 p, Vector3 dir, float max)
    {
        var s = Event.Stadium;
        if (s.Kind == VenueKind.OpenAir) return max;
        float a = s.FrontHalfU + Math.Max(0f, s.RoofInset), b = s.FrontHalfV + Math.Max(0f, s.RoofInset);
        if (MathF.Abs(p.X) > a || MathF.Abs(p.Z) > b) return max;   // a head outside the column: the physics ray's job
        float t = max;
        if (MathF.Abs(dir.X) > 1e-4f) t = Math.Min(t, ((dir.X > 0 ? a : -a) - p.X) / dir.X);
        if (MathF.Abs(dir.Z) > 1e-4f) t = Math.Min(t, ((dir.Z > 0 ? b : -b) - p.Z) / dir.Z);
        float ceiling = s.RoofInner - s.RoofThickness;
        if (s.Kind == VenueKind.Arena)
            return dir.Y > 1e-4f ? Math.Max(0.5f, Math.Min(t, (ceiling - p.Y) / dir.Y)) : Math.Max(0.5f, t);
        // a stadium: leaving the column over the roof's edge, the beam is in the sky
        return p.Y + dir.Y * t > s.RoofInner ? max : Math.Max(0.5f, t);
    }

    /// <summary>
    /// One axis of a moving head's stepper: toward <paramref name="target"/> no faster than
    /// <paramref name="vmax"/>, speeding up and braking at <paramref name="amax"/>, braking in time to stop
    /// on it.
    /// </summary>
    public static void Motor(ref float pos, ref float vel, float target, float vmax, float amax, float dt)
    {
        float d = target - pos;
        float stop = MathF.Sign(d) * MathF.Sqrt(2f * amax * MathF.Abs(d));
        float want = Math.Clamp(stop, -vmax, vmax);
        vel += Math.Clamp(want - vel, -amax * dt, amax * dt);
        float step = vel * dt;
        // never past the target in one step
        if (MathF.Abs(step) >= MathF.Abs(d) && MathF.Sign(step) == MathF.Sign(d)) { pos = target; vel = 0f; }
        else pos += step;
    }

    /// <summary>The shutter: 0..7 closed, 8..15 open, 16..131 strobing 1..20 Hz, above open.</summary>
    private float Shutter(Fixture f, float t)
    {
        int o = f.Type.Offset(Attr.Shutter);
        if (o < 0) return 1f;
        int v = _plan.Patch.Get(f, o);
        if (v < 8) return 0f;
        if (v < 16 || v > 131) return 1f;
        float hz = 1f + 19f * (v - 16) / 115f;
        return (t * hz) % 1f < 0.35f ? 1f : 0f;
    }

    private Color ColorOf(Fixture f)
    {
        var p = _plan.Patch;
        switch (f.Type.Kind)
        {
            case FixtureKind.Wash:
                float w = p.Get01(f, Attr.White);
                return new Color(p.Get01(f, Attr.Red) + w, p.Get01(f, Attr.Green) + w, p.Get01(f, Attr.Blue) + w);
            case FixtureKind.Spot:
                var cmy = new Color(1f - p.Get01(f, Attr.Cyan), 1f - p.Get01(f, Attr.Magenta), 1f - p.Get01(f, Attr.Yellow));
                int slot = p.Get(f, f.Type.Offset(Attr.ColorWheel)) / 8;
                return slot > 0 && slot < FixtureType.Wheel.Length ? cmy * FixtureType.Wheel[slot] : cmy;
            default:
                int s = p.Get(f, f.Type.Offset(Attr.ColorWheel)) / 8;
                return FixtureType.Wheel[Math.Clamp(s, 0, FixtureType.Wheel.Length - 1)];
        }
    }

    /// <summary>A screen's media-server layer: clip 0 a drifting gradient, 1 slow waves, 2 bars on the beat, 3 a pulse from the centre, 4 flashing noise.</summary>
    private void PaintScreen(Fixture f, MultiMesh mm, float t)
    {
        var p = _plan.Patch;
        float dim = p.Get01(f, Attr.Dimmer);
        int clip = p.Get(f, f.Type.Offset(Attr.Clip)) / 32;
        float speed = p.Get01(f, Attr.ClipSpeed) * 4f;
        var a = new Color(p.Get01(f, Attr.Red), p.Get01(f, Attr.Green), p.Get01(f, Attr.Blue));
        var b = new Color(p.Get01(f, Attr.Red2), p.Get01(f, Attr.Green2), p.Get01(f, Attr.Blue2));
        float beat = _desk.Beat, ph = beat - MathF.Floor(beat);
        int nx = f.PixelsX, ny = f.PixelsY;
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                float u = (float)x / nx, v = (float)y / ny, k;
                switch (clip)
                {
                    case 0: k = 0.5f + 0.5f * MathF.Sin((u + t * speed * 0.1f) * MathF.Tau); break;
                    case 1: k = 0.5f + 0.5f * MathF.Sin(u * 6f + v * 3f + t * speed); break;
                    case 2: k = ((int)(u * 8f) + (int)MathF.Floor(beat)) % 2 == 0 ? 1f - ph : 0.1f; break;
                    case 3:
                        float r = MathF.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f) * 0.3f);
                        k = MathF.Max(0f, 1f - MathF.Abs(r - ph * 0.7f) * 6f);
                        break;
                    default:
                        uint h = (uint)(x * 73856093 ^ y * 19349663 ^ (int)(beat * 2f) * 83492791);
                        k = (h % 7 == 0 ? 1f : 0.05f) * (1f - ph);
                        break;
                }
                var col = a.Lerp(b, k) * dim;
                // a strobe: the wall sinks toward black between flashes and burns white on each
                if (_screenFlash >= 0f) col = _screenFlash > 0f ? col * 0.5f + new Color(0.6f, 0.6f, 0.6f) : col * 0.22f;
                mm.SetInstanceColor(y * nx + x, col);
            }
    }

    [Showcase("Concert", "Stage")]
    private static IEnumerable<(string, Func<Node3D>)> Showcase()
    {
        // one of each layout, from seeds that give them
        var seen = new HashSet<StageLayout>();
        for (int seed = 1; seen.Count < Enum.GetValues<StageLayout>().Length && seed < 500; seed++)
        {
            var e = ConcertPlanner.Plan(StadiumCatalog.Geneva, seed);
            if (!seen.Add(e.Layout)) continue;
            yield return ($"{e.Layout}: {e.Artist} ({e.Genre}, screens {e.Screens})", () => new ConcertRig(e) { ForceLive = true, WithCrowd = false });
        }
    }
}
