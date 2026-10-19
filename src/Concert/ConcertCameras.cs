using Godot;
using UnitSport.Avatar;

namespace UnitSport.Concert;

/// <summary>
/// A concert's broadcast cameras (#885): eight positions as a crew places them — a long lens from front
/// of house, both sides of the stage, the crowd from the stage, a cable cam over the pitch, a jib along
/// the barrier, a wide from the far stand, a drone over the roof — and the program: the one camera on
/// air, cut by an auto-director on the music (long shots in a calm verse, a cut a bar at a peak; the
/// same cut on every peer, from the shared beat) or held by the operator (<see cref="LightDesk.Camera"/>).
///
/// <para>
/// Two renders of the program, each only while something shows it: a small one (256 x 144, nearest
/// filtering) for the LED screens' camera clip (IMAG: the pixels are the screen's own), and a big one
/// (1280 x 720) for the program view on this screen and the NDI output (<see cref="ConcertNdi"/>).
/// Both are SubViewports sharing the world, as the wall mirrors are.
/// </para>
/// </summary>
public partial class ConcertCameras : Node3D
{
    public static readonly string[] Names = { "FOH long lens", "Stage left", "Stage right", "Crowd", "Cable cam", "Jib", "Wide", "Drone" };

    private readonly StagePlan _plan;
    private readonly LightDesk _desk;
    private SubViewport _imag = null!, _program = null!;
    private Camera3D _imagCam = null!, _programCam = null!;
    private Vector3 _artist;
    private float _h, _hw, _d;

    /// <summary>The camera on air now, and why (the operator's, or the auto-director's).</summary>
    public int OnAir { get; private set; }
    public bool Manual => _desk.Camera >= 0;

    /// <summary>Asked for this frame by the screens; asked for by the program view or NDI (a count of who wants it).</summary>
    public bool ImagWanted { get; set; }
    public static int ProgramWanted;

    public Texture2D ImagTexture => _imag.GetTexture();
    public Texture2D ProgramTexture => _program.GetTexture();
    public SubViewport ProgramPort => _program;

    public ConcertCameras(StagePlan plan, LightDesk desk)
    {
        _plan = plan;
        _desk = desk;
        Name = "Cameras";
    }

    public ConcertCameras() : this(null!, null!) { }

    public override void _Ready()
    {
        var e = _plan.Event;
        _h = e.DeckHeight;
        _hw = e.Width / 2;
        _d = _plan.Round ? 0 : e.Depth;
        _artist = _plan.StagePoint(0, _h + 1.5f, _plan.Round ? 0 : -_d * 0.35f);
        (_imag, _imagCam) = Port("Imag", new Vector2I(256, 144));
        (_program, _programCam) = Port("Program", new Vector2I(1280, 720));
    }

    private (SubViewport, Camera3D) Port(string name, Vector2I size)
    {
        var port = new SubViewport
        {
            Name = name, Size = size, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            HandleInputLocally = false, Msaa3D = Viewport.Msaa.Disabled,
        };
        AddChild(port);
        var cam = new Camera3D { Name = "Camera", Far = 3000f };
        port.AddChild(cam);
        cam.MakeCurrent();
        return (port, cam);
    }

    /// <summary>Where camera <paramref name="i"/> is and looks at a moment (s), the stadium frame, and its field of view.</summary>
    public (Vector3 At, Vector3 Look, float Fov) Shot(int i, double t)
    {
        float s(double rate, float phase = 0) => MathF.Sin((float)(t * rate) + phase);
        var stagey = _plan.StagePoint(0, _h + 1.3f, _plan.Round ? 0 : -_d * 0.4f);
        float far = _plan.Round ? 60f : 75f;
        return i switch
        {
            0 => (_plan.Desk + new Vector3(0, 2.8f, 0), _artist, 14f + 3f * s(0.1)),
            1 => (_plan.StagePoint(-_hw + 2.5f, _h + 1.7f, -1.2f), stagey, 48f),
            2 => (_plan.StagePoint(_hw - 2.5f, _h + 1.7f, -1.2f), stagey, 48f),
            3 => (_plan.StagePoint(0, _h + 4f, 0.5f), _plan.StagePoint(0, 1.5f, 25f), 70f),
            4 => (_plan.StagePoint(12f * s(0.13), 16f + 2f * s(0.07), 22f + 10f * s(0.09)), _artist, 55f),
            5 => (_plan.StagePoint(14f * s(0.25), 4f + 3f * s(0.4, 1f), 6f), _artist, 40f),
            6 => (_plan.StagePoint(0, 22f, far), _plan.StagePoint(0, _h + 3f, -_d * 0.5f), 45f),
            _ => (new Vector3(120f * MathF.Cos((float)(t * 0.05)), 70f, 120f * MathF.Sin((float)(t * 0.05))), new Vector3(0, 5f, 0), 55f),
        };
    }

    /// <summary>The auto-director: how many bars a shot holds, by how hard the music goes.</summary>
    private static int BarsPerCut(ShowSection s) => s switch
    {
        ShowSection.Calm => 8, ShowSection.Groove => 4, ShowSection.Chorus => 2, ShowSection.Peak => 1, _ => 8,
    };

    /// <summary>Which camera the auto-director has on air at a beat of the show: the same on every peer.</summary>
    private int AutoCamera(float beat, ShowSection section)
    {
        int bars = BarsPerCut(section);
        int cut = (int)MathF.Floor(beat / 4f / bars);
        uint h = (uint)(cut * 2654435761u ^ (uint)_plan.Event.Seed);
        h ^= h >> 15;
        int cam = (int)(h % (uint)Names.Length);
        // never the same shot twice in a row
        uint p = (uint)((cut - 1) * 2654435761u ^ (uint)_plan.Event.Seed);
        p ^= p >> 15;
        if (cam == (int)(p % (uint)Names.Length)) cam = (cam + 1) % Names.Length;
        // the drone and the wide only in a calm verse or a walk-in
        if (cam >= 6 && section is ShowSection.Chorus or ShowSection.Peak) cam = cam - 6;
        return cam;
    }

    /// <summary>One frame: the program camera chosen, placed, and the renders that something shows switched on.</summary>
    public void Step(double t)
    {
        OnAir = _desk.Camera >= 0 ? Math.Min(_desk.Camera, Names.Length - 1) : AutoCamera(_desk.Beat, _desk.Section);
        var (at, look, fov) = Shot(OnAir, t);
        var g = GlobalTransform;
        var from = g * at;
        var to = g * look;
        var pose = Transform3D.Identity.Translated(from).LookingAt(to, Vector3.Up);
        bool imag = ImagWanted, program = ProgramWanted > 0;
        if (imag)
        {
            _imagCam.GlobalTransform = pose;
            _imagCam.Fov = fov;
        }
        if (program)
        {
            _programCam.GlobalTransform = pose;
            _programCam.Fov = fov;
        }
        var wantImag = imag ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        var wantProgram = program ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (_imag.RenderTargetUpdateMode != wantImag) _imag.RenderTargetUpdateMode = wantImag;
        if (_program.RenderTargetUpdateMode != wantProgram) _program.RenderTargetUpdateMode = wantProgram;
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
        }
    }

    /// <summary>One frame: each artist's move, on the beat; still between songs.</summary>
    public void Step()
    {
        float beat = _desk.Beat, phase = beat - MathF.Floor(beat);
        int bar = (int)MathF.Floor(beat / 4f);
        float barPhase = beat / 4f - bar;
        bool dancing = _desk.Section is not (ShowSection.Blackout or ShowSection.WalkIn);
        int moves = HumanMeshBuilder.MoveCount(_style);
        foreach (var (node, palette, offset) in _people)
        {
            var dance = dancing ? new DanceParams(_style, ((bar / 2) + offset) % moves, phase, barPhase, bar, 1f) : (DanceParams?)null;
            HumanMeshBuilder.BuildStride(palette, 0f, 0f, dance: dance, into: (ArrayMesh)node.Mesh);
        }
    }
}
