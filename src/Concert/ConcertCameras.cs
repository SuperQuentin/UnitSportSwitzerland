using Godot;
using UnitSport.Avatar;

namespace UnitSport.Concert;

/// <summary>
/// A concert's broadcast cameras (#885): the rigs a crew rigs (<see cref="CameraRig"/>: a long lens at
/// front of house, a dolly on rails in the pit, a track down the side of the pitch, a wire cam on four
/// cables from the roof, a jib, a Steadicam on stage, a crowd camera, two wides, a drone, the artist's
/// point of view), each drawn as its kit (the rails, the cables, the arm, the rotors) and moving on its
/// own from the clock until a player operates it. The program is the camera on air: held by the show's
/// operator (<see cref="LightDesk.Camera"/>) or cut by the auto-director on the music, the same cut on
/// every peer, favouring a camera somebody operates.
///
/// <para>
/// Renders, each only while something shows it: a small one (the Concert screens setting, 256 to
/// 1024 wide, nearest; <c>/concert imag</c> overrides it for the run) for the screens' camera clip (IMAG), the program (1280 x 720) for the program view and NDI, and this player's
/// viewfinder while operating a camera. SubViewports sharing the world, as the wall mirrors are.
/// </para>
/// </summary>
public partial class ConcertCameras : Node3D
{
    public static string[] Names => CameraRig.Names;

    private readonly StagePlan _plan;
    private readonly LightDesk _desk;
    private List<CameraRig> _rigs = new();
    private SubViewport _imag = null!, _program = null!, _finder = null!;
    private Camera3D _imagCam = null!, _programCam = null!, _finderCam = null!;
    private Node3D[] _bodies = Array.Empty<Node3D>();
    private MeshInstance3D[][] _cables = Array.Empty<MeshInstance3D[]>();
    private MeshInstance3D[] _arms = Array.Empty<MeshInstance3D>();
    private StandardMaterial3D _kit = null!;

    public IReadOnlyList<CameraRig> Rigs => _rigs;
    /// <summary>The camera on air now, and whether the operator holds it there.</summary>
    public int OnAir { get; private set; }
    public bool Manual => _desk.Camera >= 0;
    /// <summary>The camera this player operates, or -1.</summary>
    public int Operating { get; set; } = -1;

    public bool ImagWanted { get; set; }
    /// <summary><c>/concert imag &lt;width&gt;</c>: this run's IMAG width over the setting, 0 to follow it (#996).</summary>
    public static int ImagOverride;
    public static int ImagWidth => ImagOverride > 0 ? ImagOverride : Core.GameSettings.Current.ConcertScreenWidth;
    private static Vector2I ImagSize => new(ImagWidth, ImagWidth * 9 / 16);
    private int _tick;
    public static int ProgramWanted;

    public Texture2D ImagTexture => _imag.GetTexture();
    public Texture2D ProgramTexture => _program.GetTexture();
    public Texture2D FinderTexture => _finder.GetTexture();

    public ConcertCameras(StagePlan plan, LightDesk desk)
    {
        _plan = plan;
        _desk = desk;
        Name = "Cameras";
    }

    public ConcertCameras() : this(null!, null!) { }

    public override void _Ready()
    {
        _rigs = CameraRig.For(_plan);
        (_imag, _imagCam) = Port("Imag", ImagSize);
        // the screens' feed sees the bowl, not the country: a second render of the whole world cost as much as the concert
        _imagCam.Far = 450f;
        (_program, _programCam) = Port("Program", new Vector2I(1280, 720));
        (_finder, _finderCam) = Port("Viewfinder", new Vector2I(1280, 720));
        BuildKit();
    }

    private (SubViewport, Camera3D) Port(string name, Vector2I size)
    {
        var port = new SubViewport
        {
            Name = name, Size = size, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            HandleInputLocally = false, Msaa3D = Viewport.Msaa.Disabled,
        };
        AddChild(port);
        // near past the rig's own kit (the lens hood ends 0.55 m out, a drone's rotors 0.68 m): the
        // screens' feed never hides the body on air, and filmed the back of its hood as a black square
        var cam = new Camera3D { Name = "Camera", Near = 0.7f, Far = 3000f };
        port.AddChild(cam);
        cam.MakeCurrent();
        return (port, cam);
    }

    // ---- the kit you see -----------------------------------------------------------------------------

    private MeshInstance3D Box(Node parent, Vector3 size)
    {
        var m = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = _kit, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent.AddChild(m);
        return m;
    }

    private void BuildKit()
    {
        _kit = new StandardMaterial3D { AlbedoColor = new Color(0.09f, 0.09f, 0.1f), Roughness = 0.5f };
        _bodies = new Node3D[_rigs.Count];
        _cables = new MeshInstance3D[_rigs.Count][];
        _arms = new MeshInstance3D[_rigs.Count];
        for (int i = 0; i < _rigs.Count; i++)
        {
            var r = _rigs[i];
            var body = new Node3D { Name = $"Cam{i + 1}" };
            AddChild(body);
            // the camera itself: a body and a lens hood, -Z forward
            _bodies[i] = body;
            if (r.Hidden) continue;
            Box(body, new Vector3(0.32f, 0.34f, 0.55f));
            Box(body, new Vector3(0.22f, 0.22f, 0.3f)).Position = new Vector3(0, 0, -0.4f);
            if (r.Kind == RigKind.Drone)
                foreach (var (x, z) in new[] { (-0.45f, -0.45f), (0.45f, -0.45f), (-0.45f, 0.45f), (0.45f, 0.45f) })
                    Box(body, new Vector3(0.45f, 0.03f, 0.45f)).Position = new Vector3(x, 0.25f, z);
            _bodies[i] = body;
            switch (r.Kind)
            {
                case RigKind.Rail:
                    // two rails on sleepers along the track, and the dolly's deck
                    var along = r.B - r.A;
                    float len = along.Length();
                    var mid = ((r.A + r.B) / 2) with { Y = r.A.Y - 1.45f };
                    var basis = Basis.LookingAt(along.Normalized(), Vector3.Up);
                    foreach (float off in new[] { -0.35f, 0.35f })
                    {
                        var rail = Box(this, new Vector3(0.06f, 0.06f, len));
                        rail.Transform = new Transform3D(basis, mid + basis.X * off);
                    }
                    Box(body, new Vector3(1.0f, 0.12f, 1.2f)).Position = new Vector3(0, -1.35f, 0);
                    Box(body, new Vector3(0.1f, 1.3f, 0.1f)).Position = new Vector3(0, -0.7f, 0);
                    break;
                case RigKind.Wire:
                    _cables[i] = new MeshInstance3D[4];
                    for (int c = 0; c < 4; c++) _cables[i][c] = Box(this, new Vector3(0.03f, 0.03f, 1f));
                    break;
                case RigKind.Jib:
                    // its foot a hair above the deck or the pitch: never coplanar (zfight)
                    Box(this, new Vector3(0.6f, 1.8f, 0.6f)).Position = r.A + new Vector3(0, 0.915f, 0);
                    _arms[i] = Box(this, new Vector3(0.18f, 0.18f, 1f));
                    break;
                case RigKind.Fixed when r.A.Y > 2f:
                    // a tripod on its platform
                    Box(body, new Vector3(0.08f, 1.2f, 0.08f)).Position = new Vector3(0, -0.75f, 0);
                    break;
            }
        }
    }

    /// <summary>The wire cam's four cables run to the roof's inner corners.</summary>
    private Vector3[] Anchors()
    {
        var s = _plan.Event.Stadium;
        float u = s.FrontHalfU + s.RoofInset, v = s.FrontHalfV + s.RoofInset, y = s.RoofInner - 1f;
        return new[] { new Vector3(-u, y, -v), new Vector3(u, y, -v), new Vector3(u, y, v), new Vector3(-u, y, v) };
    }

    private Vector3[]? _anchors;

    private void PlaceKit()
    {
        _anchors ??= Anchors();
        for (int i = 0; i < _rigs.Count; i++)
        {
            var r = _rigs[i];
            var at = r.Position;
            // a fixed camera on a stand keeps its stand upright: only its head turns
            _bodies[i].Transform = new Transform3D(r.Basis, at);
            if (_cables[i] is { } cables)
                for (int c = 0; c < 4; c++)
                {
                    var a = _anchors[c];
                    var d = at - a;
                    cables[c].Transform = new Transform3D(Basis.LookingAt(d.Normalized(), Mathf.Abs(d.Normalized().Y) > 0.99f ? Vector3.Forward : Vector3.Up)
                        .Scaled(new Vector3(1, 1, d.Length())), a + d / 2);
                }
            if (_arms[i] is { } arm)
            {
                var pivot = r.A + new Vector3(0, 1.8f, 0);
                var d = at - pivot;
                // the arm runs on past the pivot as its counterweight
                arm.Transform = new Transform3D(Basis.LookingAt(d.Normalized(), Vector3.Up).Scaled(new Vector3(1, 1, d.Length() * 1.3f)), pivot + d * 0.35f);
            }
        }
    }

    // ---- who is on air -------------------------------------------------------------------------------

    private static int BarsPerCut(ShowSection s) => s switch
    {
        ShowSection.Calm => 8, ShowSection.Groove => 4, ShowSection.Chorus => 2, ShowSection.Peak => 1, _ => 8,
    };

    /// <summary>The auto-director's camera at a beat: the same on every peer; an operated camera gets every other cut.</summary>
    private int AutoCamera(float beat, ShowSection section)
    {
        int bars = BarsPerCut(section);
        int cut = (int)MathF.Floor(beat / 4f / bars);
        uint Hash(int c)
        {
            uint h = (uint)(c * 2654435761u ^ (uint)_plan.Event.Seed);
            return h ^ (h >> 15);
        }
        int n = _rigs.Count;
        int cam = (int)(Hash(cut) % (uint)n);
        if (cam == (int)(Hash(cut - 1) % (uint)n)) cam = (cam + 1) % n;
        // the wides and the drone breathe in a calm verse, not at a peak
        if (section is ShowSection.Chorus or ShowSection.Peak && (cam is 7 or 8 or 9)) cam = (cam + 4) % n;
        // the crew's shots: every other cut goes to a camera somebody operates
        if ((cut & 1) == 0)
        {
            int operated = 0;
            foreach (var r in _rigs) if (r.Operator != 0) operated++;
            if (operated > 0)
            {
                int pick = (int)(Hash(cut + 7919) % (uint)operated);
                for (int i = 0; i < n; i++)
                    if (_rigs[i].Operator != 0 && pick-- == 0) return i;
            }
        }
        return cam;
    }

    /// <summary>A player took or left a camera (<see cref="ConcertService"/>).</summary>
    public void SetOperator(int cam, long peer)
    {
        if (cam >= 0 && cam < _rigs.Count) _rigs[cam].Operator = peer;
    }

    /// <summary>An operated camera's state from its operator, relayed.</summary>
    public void SetState(int cam, float[] state)
    {
        if (cam >= 0 && cam < _rigs.Count && cam != Operating) _rigs[cam].Unpack(state);
    }

    /// <summary>One frame: the rigs that nobody operates move on their own, the program is chosen and the renders asked for switched on.</summary>
    public void Step(double t)
    {
        foreach (var r in _rigs)
            if (r.Operator == 0) r.Auto(t);
        PlaceKit();
        OnAir = _desk.Camera >= 0 ? Math.Min(_desk.Camera, _rigs.Count - 1) : AutoCamera(_desk.Beat, _desk.Section);
        var program = Pose(_rigs[OnAir]);
        bool imag = ImagWanted, prog = ProgramWanted > 0, finder = Operating >= 0;
        if (imag) Aim(_imagCam, program, _rigs[OnAir].Fov);
        if (prog) Aim(_programCam, program, _rigs[OnAir].Fov);
        if (finder) Aim(_finderCam, Pose(_rigs[Operating]), _rigs[Operating].Fov);
        // the screens' feed at half the frame rate: 256x144 on an LED wall does not need more
        _tick++;
        // Once must be asked again every time: the server drops back to disabled after the render, but
        // the property still reads Once, so comparing with it froze the feed on its first frame
        if (imag && _imag.Size.X != ImagWidth) _imag.Size = ImagSize;
        if (imag && (_tick & 1) == 0) _imag.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        else if (!imag && _imag.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled) _imag.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        Mode(_program, prog);
        Mode(_finder, finder);
        // an operator's own camera hides its body from its own lens
        for (int i = 0; i < _bodies.Length; i++)
        {
            bool hide = i == Operating || i == OnAir && prog;
            if (_bodies[i].Visible == hide) _bodies[i].Visible = !hide;
        }
    }

    /// <summary><c>/concert imag [width|auto]</c>: the screens' feed size for this run, live; client-local.</summary>
    public static string ImagCommand(string[] a)
    {
        if (a.Length == 0) return $"Concert screens feed: {ImagSize.X}x{ImagSize.Y} ({(ImagOverride > 0 ? "/concert imag" : "setting")}). /concert imag <64..1920>|auto";
        if (a[0] == "auto") ImagOverride = 0;
        else if (int.TryParse(a[0], out int w) && w >= 64 && w <= 1920) ImagOverride = w & ~15;
        else return "/concert imag <64..1920>|auto";
        return $"Concert screens feed: {ImagSize.X}x{ImagSize.Y}.";
    }

    private Transform3D Pose(CameraRig r) => GlobalTransform * new Transform3D(r.Basis, r.Position);

    private static void Aim(Camera3D cam, Transform3D pose, float fov)
    {
        cam.GlobalTransform = pose;
        cam.Fov = fov;
    }

    private static void Mode(SubViewport port, bool on)
    {
        var want = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (port.RenderTargetUpdateMode != want) port.RenderTargetUpdateMode = want;
    }
}

/// <summary>
/// The artists on stage (#885): a DJ, a singer and two dancers, or a band of four, by genre — the game's
/// own figures, dancing the music's moves on the show's beat, rebuilt in place (one mesh each).
/// What the cameras film.
/// </summary>
public partial class ConcertPerformers : Node3D
{
    private readonly StagePlan _plan;
    private readonly LightDesk _desk;
    private readonly List<(MeshInstance3D Node, HumanPalette Palette, int Offset)> _people = new();

    /// <summary>
    /// Poses built once and kept (a figure costs milliseconds to build): per artist, move and frame,
    /// <see cref="PerBeat"/> frames a beat over the move's 8 counts. At most one new pose a frame; until
    /// it is built the artist holds the one before.
    /// </summary>
    private readonly Dictionary<long, (Godot.Collections.Array Arrays, Mesh.ArrayFormat Format)[]> _poses = new();
    private const int PerBeat = 8, MaxPoses = 1200;
    /// <summary>
    /// The pose each artist shows. Its mesh is never swapped: the figure material's face parameters
    /// are per instance, and Godot registers them again on every new mesh without freeing the old
    /// (the buffer overflowed, an error a swap); a pose is copied into the artist's own mesh instead.
    /// </summary>
    private readonly List<long> _showing = new();
    private ArrayMesh? _scratch;
    private Audio.Cd.MusicStyle _style;

    public ConcertPerformers(StagePlan plan, LightDesk desk)
    {
        _plan = plan;
        _desk = desk;
        Name = "Performers";
    }

    public ConcertPerformers() : this(null!, null!) { }

    public override void _Ready()
    {
        var e = _plan.Event;
        _style = e.Genre switch
        {
            Genre.Rock => Audio.Cd.MusicStyle.Rock, Genre.Electronic => Audio.Cd.MusicStyle.Electronic,
            Genre.HipHop => Audio.Cd.MusicStyle.HipHop, Genre.Ballad => Audio.Cd.MusicStyle.Chill, _ => Audio.Cd.MusicStyle.Pop,
        };
        float h = e.DeckHeight, z = _plan.Round ? 0 : -e.Depth * 0.35f;
        float[] xs = e.Genre switch
        {
            Genre.Electronic => new[] { 0f },
            Genre.Rock => new[] { 0f, -5f, 5f, -1.5f },
            _ => new[] { 0f, -3.5f, 3.5f },
        };
        for (int i = 0; i < xs.Length; i++)
        {
            // the drummer (a band's fourth) sits back on a riser
            float back = e.Genre == Genre.Rock && i == 3 ? -4f : 0f;
            var at = _plan.StagePoint(xs[i], h, z + back);
            var face = (_plan.StagePoint(xs[i], h, z + back + 10f) - at) with { Y = 0 };
            var node = new MeshInstance3D
            {
                Name = $"Artist{i}", Position = at, MaterialOverride = HumanMeshBuilder.FigureMaterial(),
                Basis = new Basis(Vector3.Up, Mathf.Atan2(-face.X, -face.Z)), Mesh = new ArrayMesh(),
            };
            AddChild(node);
            _people.Add((node, HumanPalette.ForRider(e.Seed + i * 7), i * 3));
            _showing.Add(long.MinValue);
        }
    }

    /// <summary>One frame: each artist's move, on the beat; still between songs.</summary>
    /// <summary>
    /// One frame: each artist's move, on the beat; still between songs. Under a fast strobe
    /// (<paramref name="strobeHz"/>, on the rig's clock <paramref name="strobeT"/>) each flash shows a
    /// new pose, a move's hit picked at random, held until the next: the freeze-frame dance of a strobe.
    /// </summary>
    public void Step(float strobeHz = 0f, float strobeT = 0f)
    {
        float beat = _desk.Beat, phase = beat - MathF.Floor(beat);
        int bar = (int)MathF.Floor(beat / 4f);
        float barPhase = beat / 4f - bar;
        bool dancing = _desk.Section is not (ShowSection.Blackout or ShowSection.WalkIn);
        int moves = HumanMeshBuilder.MoveCount(_style);
        // the frame within the move's two bars, snapped
        int frame = (int)MathF.Floor(((bar & 1) * 4f + barPhase * 4f) * PerBeat) % (8 * PerBeat);
        uint flashHash = 0;
        if (dancing && strobeHz > 0f)
        {
            // a flash's pose: one of each move's eight hits (on the beats), so few poses to build
            flashHash = (uint)(int)MathF.Floor(strobeT * strobeHz) * 2654435761u;
            flashHash ^= flashHash >> 15;
            frame = (int)(flashHash % 8u) * PerBeat;
            bar = (int)(flashHash >> 8);
        }
        float fBeats = (frame + 0.5f) / PerBeat;
        int fBar = (int)(fBeats / 4f);
        float fBarPhase = fBeats / 4f - fBar, fPhase = fBeats - MathF.Floor(fBeats);
        bool built = false;
        for (int p = 0; p < _people.Count; p++)
        {
            var (node, palette, offset) = _people[p];
            int move = dancing ? ((bar / 2) + offset) % moves : -1;
            if (flashHash != 0) move = (int)((flashHash >> 4) + (uint)offset * 7u) % moves;
            long key = ((long)p << 40) | ((long)(move + 1) << 20) | (long)(dancing ? frame : 0);
            if (_showing[p] == key) continue;
            if (!_poses.TryGetValue(key, out var surfaces))
            {
                if (built) continue;
                if (_poses.Count >= MaxPoses) _poses.Clear();
                var dance = dancing ? new DanceParams(_style, move, fPhase, fBarPhase, fBar, 1f) : (DanceParams?)null;
                _scratch ??= new ArrayMesh();
                HumanMeshBuilder.BuildStride(palette, 0f, 0f, dance: dance, into: _scratch);
                // the format too: the figure's custom channels are packed bytes only it can read back
                surfaces = new (Godot.Collections.Array, Mesh.ArrayFormat)[_scratch.GetSurfaceCount()];
                for (int k = 0; k < surfaces.Length; k++) surfaces[k] = (_scratch.SurfaceGetArrays(k), _scratch.SurfaceGetFormat(k));
                _poses[key] = surfaces;
                built = true;
            }
            var target = (ArrayMesh)node.Mesh;
            target.ClearSurfaces();
            foreach (var (arrays, format) in surfaces)
                target.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, format);
            _showing[p] = key;
        }
    }
}
