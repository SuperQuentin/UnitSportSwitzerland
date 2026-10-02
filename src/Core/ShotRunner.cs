using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// Headless-ish verification helper: places the camera, waits for streaming to settle,
/// writes a PNG and quits. Lets screenshots be captured from the command line without
/// an editor attached.
///
///   godot --path . -- --shot x,y,z,pitchDeg,yawDeg,seconds,out.png
///   godot --path . -- --shot-queue shots.txt [--nohud]
///
/// The queue form stays up and takes one shot per line appended to the file (same seven
/// fields as --shot; blank lines and # comments skipped, "quit" exits). On macOS every
/// launch brings Godot to the front, so a series of pictures should cost one launch.
/// A queued shot's y may be "g1.7": that high above the ground, once it has streamed in. Or
/// "i1.6", inside a house (#320): x, z is a point in front of a front door (within 30 m, on its
/// outer side); that door is opened, and once its interior is built the camera goes as far
/// behind the doorway as the point stands in front of it, 1.6 m above the sill, turned as asked,
/// carried into the rooms (<see cref="Interiors.DoorLink.ToInside"/>): stand in front of a house
/// looking at it, and the picture is from that far inside, looking on. Offline only, and best
/// last in a queue: the door is left open.
/// A line starting with '/' is typed into the chat between two shots (<c>/style cartoon</c>,
/// <c>/time set 19:30</c>), so one launch can picture a live change.
/// Each shot logs the frame time averaged over its last second of settling.
/// </summary>
public partial class ShotRunner : Node
{
    private readonly record struct Shot(Vector3 Position, float PitchDeg, float YawDeg, double SettleSeconds, string OutPath,
        float? AboveGround = null, string? Command = null, float? Inside = null);

    /// <summary>How far from an "i" shot's point its door may be, m.</summary>
    private const float InsideDoorReach = 30f;

    /// <summary>How long an "i" shot waits for its door and its interior before it fails, s.</summary>
    private const double InsideTimeout = 40;

    // an "i" shot: the door it goes in by, and whether the camera is through it yet
    private string? _door;
    private bool _through;
    private double _waited;

    /// <summary>Runs a queued chat line ("/style cartoon"); the client world sends it to its chat.</summary>
    public System.Action<string>? RunCommand { get; set; }

    /// <summary>The ground height under a point, once loaded; for the "g" heights of queued shots.</summary>
    public System.Func<Vector3, float?>? GroundHeight { get; set; }

    private readonly Camera3D _camera;

    /// <summary>
    /// Shot positions are world space as the game started (the manifest's origin), as they always
    /// were; the origin moves since (#185), so each is mapped from that first frame when it is aimed.
    /// </summary>
    public WorldOrigin? Origin
    {
        get => _origin;
        init { _origin = value; _start = value?.Frame; }
    }
    private readonly WorldOrigin? _origin;
    private readonly OriginFrame? _start;
    private readonly bool _hideHud = HideHudRequested();
    private List<CanvasLayer>? _hudLayers;
    private Shot? _shot;
    private double _elapsed;
    private bool _done;

    // queue mode only
    private readonly string? _queuePath;
    private readonly Queue<Shot?> _pending = new(); // null = quit
    private int _consumedLines;
    private double _sincePoll = double.MaxValue;
    private bool _failed;

    // frame time over the last second of a shot's settle
    private double _frameMs;
    private int _frames;

    public ShotRunner(Camera3D camera, Vector3 position, float pitchDeg, float yawDeg,
        double settleSeconds, string outPath)
    {
        _camera = camera;
        Aim(new Shot(position, pitchDeg, yawDeg, settleSeconds, outPath));
    }

    private ShotRunner(Camera3D camera, string queuePath)
    {
        _camera = camera;
        _queuePath = queuePath;
    }

    public static ShotRunner ForQueue(Camera3D camera, string queuePath, WorldOrigin? origin = null)
    {
        GD.Print($"[shot-queue] watching {queuePath}");
        return new ShotRunner(camera, queuePath) { Origin = origin };
    }

    /// <summary>Parses "--shot x,y,z,pitch,yaw,seconds,path" from the command line.</summary>
    public static string[]? ParseArgs() => CmdArgs.Value("--shot")?.Split(',') is { Length: 7 } parts ? parts : null;

    /// <summary>Parses "--shot-queue path" from the command line.</summary>
    public static string? ParseQueueArg() => CmdArgs.Value("--shot-queue");

    private static bool HideHudRequested() => CmdArgs.Has("--nohud");

    /// <summary>
    /// Hides every CanvasLayer. The tree is walked once, then followed through
    /// <c>NodeAdded</c>: a whole-tree <c>FindChildren</c> every frame visited each tile node and
    /// was 90 % of the main thread at 40 rings, the very cost the shots were measuring (#221).
    /// </summary>
    private void HideHud()
    {
        if (_hudLayers == null)
        {
            _hudLayers = new List<CanvasLayer>();
            foreach (var node in GetTree().Root.FindChildren("*", "CanvasLayer", true, false))
                _hudLayers.Add((CanvasLayer)node);
            GetTree().NodeAdded += OnNodeAdded;
        }
        _hudLayers.RemoveAll(layer => !IsInstanceValid(layer));
        foreach (var layer in _hudLayers) layer.Visible = false;
    }

    private void OnNodeAdded(Node node)
    {
        if (node is CanvasLayer layer) _hudLayers!.Add(layer);
    }

    public override void _ExitTree()
    {
        if (_hudLayers != null) GetTree().NodeAdded -= OnNodeAdded;
        _hudLayers = null;
    }

    private void Aim(Shot shot)
    {
        _shot = shot;
        _elapsed = 0;
        _camera.Position = _origin != null && _start != null ? _origin.Since(_start).Point(shot.Position) : shot.Position;
        _camera.Rotation = new Vector3(Mathf.DegToRad(shot.PitchDeg), Mathf.DegToRad(shot.YawDeg), 0);
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        if (_queuePath != null && _shot == null && !NextFromQueue(delta)) return;

        // A mode may make its own camera current — GPX playback does, from a deferred call
        // that lands after this node is constructed. The shot asked for one exact
        // transform, so take it back every frame until the picture is written.
        _camera.Current = true;
        // every frame: a panel opened since (the start menu, a toast) would land in the picture
        if (_hideHud) HideHud();
        if (_shot!.Value.Inside is { } inside && !_through)
        {
            GoInside(inside, delta);
            return;
        }
        _elapsed += delta;
        if (_elapsed > _shot!.Value.SettleSeconds - 1.0)
        {
            _frameMs += delta * 1000.0;
            _frames++;
        }
        if (_shot.Value.AboveGround is { } above && GroundHeight?.Invoke(_camera.Position) is { } ground)
            _camera.Position = _camera.Position with { Y = ground + above };
        if (_elapsed < _shot.Value.SettleSeconds) return;

        bool ok = Save(_shot.Value.OutPath);
        _failed |= !ok;
        if (_through) Interiors.InteriorManager.Instance?.CameraInside(null);
        _through = false;
        _door = null;
        _shot = null;
        if (_queuePath == null)
        {
            _done = true;
            GetTree().Quit(ok ? 0 : 1);
        }
    }

    /// <summary>Starts the next queued shot, reading lines appended since the last poll. False while idle.</summary>
    private bool NextFromQueue(double delta)
    {
        _sincePoll += delta;
        if (_pending.Count == 0 && _sincePoll >= 0.5)
        {
            _sincePoll = 0;
            ReadQueue();
        }
        if (_pending.Count == 0) return false;

        var next = _pending.Dequeue();
        if (next is { Command: { } command })
        {
            GD.Print($"[shot-queue] {command}");
            RunCommand?.Invoke(command);
            return false;
        }
        if (next is { } shot)
        {
            Aim(shot);
            return true;
        }
        GD.Print("[shot-queue] quit");
        _done = true;
        GetTree().Quit(_failed ? 1 : 0);
        return false;
    }

    private void ReadQueue()
    {
        string text;
        try
        {
            text = File.ReadAllText(_queuePath!);
        }
        catch (IOException)
        {
            return; // not written yet, or being replaced
        }
        // only whole lines: the writer may be half-way through the last one
        int end = text.LastIndexOf('\n');
        string[] lines = end < 0 ? [] : text[..end].Split('\n');
        // a file shorter than what was read is a new queue, not more of the old one
        if (lines.Length < _consumedLines) _consumedLines = 0;
        for (int i = _consumedLines; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line == "quit") _pending.Enqueue(null);
            else if (line.StartsWith('/')) _pending.Enqueue(new Shot(default, 0, 0, 0, "", Command: line));
            else if (TryParse(line, out var shot)) _pending.Enqueue(shot);
            else
            {
                GD.PrintErr($"[shot-queue] line {i + 1} is not x,y,z,pitch,yaw,seconds,path: {line}");
                _failed = true;
            }
        }
        _consumedLines = lines.Length;
    }

    private static bool TryParse(string line, out Shot shot)
    {
        shot = default;
        var p = line.Split(',');
        // InvariantCulture: this project is developed on a fr-CH machine (invariant-culture-floats)
        var inv = CultureInfo.InvariantCulture;
        if (p.Length != 7
            || !float.TryParse(p[0], NumberStyles.Float, inv, out float x)
            || !(float.TryParse(p[1], NumberStyles.Float, inv, out float y)
                || ((p[1].StartsWith('g') || p[1].StartsWith('i')) && float.TryParse(p[1][1..], NumberStyles.Float, inv, out y)))
            || !float.TryParse(p[2], NumberStyles.Float, inv, out float z)
            || !float.TryParse(p[3], NumberStyles.Float, inv, out float pitch)
            || !float.TryParse(p[4], NumberStyles.Float, inv, out float yaw)
            || !double.TryParse(p[5], NumberStyles.Float, inv, out double seconds)
            || p[6].Trim().Length == 0)
            return false;
        // above the ground: start high, so nothing is clipped while the ground streams in
        bool aboveGround = p[1].StartsWith('g'), inside = p[1].StartsWith('i');
        shot = new Shot(new Vector3(x, aboveGround || inside ? 2000f : y, z), pitch, yaw, seconds, p[6].Trim(),
            aboveGround ? y : null, Inside: inside ? y : null);
        return true;
    }

    /// <summary>
    /// An "i" shot before it is through the door: down to the ground, the door opened, the
    /// interior waited for, then the camera carried through. The settle starts inside.
    /// </summary>
    private void GoInside(float height, double delta)
    {
        var interiors = Interiors.InteriorManager.Instance;
        _waited += delta;
        if (interiors == null || _waited > InsideTimeout)
        {
            GD.PrintErr($"[shot] FAILED {_shot!.Value.OutPath}: no door or interior near {_camera.GlobalPosition} after {_waited:F0} s");
            _failed = true;
            _shot = null;
            _door = null;
            _waited = 0;
            return;
        }
        if (GroundHeight?.Invoke(_camera.GlobalPosition) is not { } ground) return;
        var at = _camera.GlobalPosition with { Y = ground + height };
        _camera.GlobalPosition = at;
        _door = interiors.OpenDoorForCamera(at, InsideDoorReach) ?? _door;
        if (_door == null || interiors.BuiltLink(_door) is not { } link) return;

        // as far behind the doorway as the point stands in front of it, at the height asked above
        // the sill, turned as asked: then carried into the rooms
        var local = link.Outside.AffineInverse() * at;
        var behind = link.Outside * new Vector3(local.X, height, -local.Z);
        _camera.GlobalTransform = link.ToInside * (_camera.GlobalTransform with { Origin = behind });
        interiors.CameraInside(link.Plan);
        GD.Print($"[shot] inside through door {_door}: door at {link.Outside.Origin}, facing {link.Outside.Basis.Z}");
        _through = true;
        _waited = 0;
        _elapsed = 0;
    }

    /// <summary>
    /// Writes the last drawn frame. Saved under a temporary name and renamed, so a script
    /// polling for the PNG never opens half of it.
    /// </summary>
    private bool Save(string outPath)
    {
        var image = GetViewport().GetTexture().GetImage();
        string path = ProjectSettings.GlobalizePath(outPath);
        string part = path + ".part";
        var err = image.SavePng(part);
        if (err == Error.Ok)
        {
            try
            {
                File.Move(part, path, overwrite: true);
            }
            catch (IOException e)
            {
                GD.PrintErr($"[shot] FAILED to move {part} to {path}: {e.Message}");
                return false;
            }
        }
        GD.Print(err == Error.Ok
            ? $"[shot] wrote {outPath} ({image.GetWidth()}x{image.GetHeight()}) at {_camera.Position}"
            : $"[shot] FAILED to write {outPath}: {err}");
        GD.Print($"[shot] fps={Engine.GetFramesPerSecond()} " +
                 $"prims={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)} " +
                 $"draws={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} " +
                 $"mem={Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0:F0}MB" +
                 (_frames > 0 ? $" frame={_frameMs / _frames:F1}ms" : ""));
        _frameMs = 0;
        _frames = 0;
        return err == Error.Ok;
    }
}
