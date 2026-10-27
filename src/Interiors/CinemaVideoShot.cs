using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --cinemavideoshot &lt;link&gt;,&lt;out prefix&gt;</c> (#1298, windowed): a video on a real hall's screen.
/// The model viewer's Town cinema, lights dimmed, the camera in the seats facing the screen; the link decoded as in
/// the game (<see cref="CinemaVideoStream"/>, from now on the clock) and shown through <see cref="InteriorNode.ShowVideo"/>.
/// Takes <c>&lt;prefix&gt;_pasted.png</c> once frames come, then <c>&lt;prefix&gt;_thrown.png</c> with the projector light
/// (High detail's way) and <c>&lt;prefix&gt;_booth.png</c> from the booth's side, printing where the video is against the
/// clock at each.
/// </summary>
public partial class CinemaVideoShot : Node3D
{
    public static string[]? ParseArgs() => CmdArgs.Value("--cinemavideoshot")?.Split(',', 2) is { Length: 2 } a ? a : null;

    private readonly string _link, _prefix;
    private InteriorNode _node = null!;
    private CinemaVideoStream _stream = null!;
    private Camera3D _cam = null!;
    private double _t0, _shotAt = -1;
    private int _step;
    private Vector3 _target, _booth;

    public CinemaVideoShot(string link, string prefix) { _link = link; _prefix = prefix; }
    public CinemaVideoShot() : this("", "") { }

    public override void _Ready()
    {
        var id = new TileId(2600, 1200);
        var tile = new BuildingTile { Id = id, Buildings = new List<Building> { BowlingProbe.Box(500, 500, 18, 34, 9, BuildingKind.Commercial) } };
        Venues.Override = new List<VenueRecord>
        {
            new("n1", VenueType.Cinema, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, "", "Town"),
        };
        var layout = InteriorGenerator.Generate(tile, 0, null, null)!;
        Venues.Override = null;
        _node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout),
            Styles.StyleKit.Material(Styles.MaterialRole.Interior), Transform3D.Identity);
        AddChild(_node);
        _node.ShowCinema(CinemaBooths.Level(CinemaBooths.Lights.Off), ~0, null, glow: true, real: false);

        var screen = layout.Furniture.First(f => f.Type == FurnitureType.MovieScreen);
        _target = new Vector3(screen.X, layout.FloorY(screen.Floor) + 2.6f, screen.Z);
        var facing = new Basis(Vector3.Up, screen.Turns * Mathf.Pi / 2) * Vector3.Back;
        var proj = layout.Furniture.FirstOrDefault(f => f.Type == FurnitureType.Porthole);
        _booth = proj != null ? new Vector3(proj.X, layout.FloorY(proj.Floor) + proj.Lift - 1.2f, proj.Z) - facing * 1.5f : _target + facing * 20f;
        _cam = new Camera3D { Current = true, Near = 0.05f };
        AddChild(_cam);
        _cam.LookAtFromPosition(_target + facing * 12f + new Vector3(0, 0.6f, 0), _target, Vector3.Up);

        _t0 = Net.ClockSync.ServerNow;
        _stream = new CinemaVideoStream(_link, _t0);
        GD.Print($"[cinemavideo] {_link} on hall {CinemaBooths.HallAt(layout, screen.X, screen.Z) + 1}");
    }

    public override void _Process(double delta)
    {
        double pos = CinemaVideo.PositionAt(Net.ClockSync.ServerNow, _t0);
        if (_stream.Pump(pos) && _stream.Current is { } frame) _node.ShowVideo(0, frame, _stream.Tint, _stream);
        if (_stream.Failed || pos > 90) { Done($"FAILED ({_stream.Status})"); return; }
        if (_stream.CurrentIndex < 0) return;
        if (_shotAt < 0) _shotAt = pos + 2;
        if (pos < _shotAt) return;
        string[] names = { "pasted", "thrown", "booth" };
        Shoot($"{_prefix}_{names[_step]}.png", pos);
        _step++;
        if (_step == 1) _node.ShowCinema(CinemaBooths.Level(CinemaBooths.Lights.Off), ~0, null, glow: true, real: true);
        if (_step == 2) _cam.LookAtFromPosition(_booth, _target, Vector3.Up);
        if (_step < 3) { _node.ProjectFor(_node.ToLocal(_cam.GlobalPosition)); _shotAt = pos + 2; return; }
        Done("ok");
    }

    private void Shoot(string path, double pos)
    {
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(path);
        GD.Print($"[cinemavideo] {path}: the hall at {pos:F2} s shows {_stream.CurrentTime:F2} s (off {(pos - _stream.CurrentTime) * 1000:F0} ms, restarts {_stream.Restarts})");
    }

    private void Done(string result)
    {
        _stream.Dispose();
        GD.Print($"[cinemavideo] RESULT: {result}");
        GetTree().Quit(result == "ok" ? 0 : 1);
        SetProcess(false);
    }
}
