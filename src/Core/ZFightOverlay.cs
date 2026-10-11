using System.Globalization;
using Godot;

namespace UnitSport.Core;

/// <summary>
/// The debug menu's z-fighting overlay (#788): every visible mesh within <see cref="Radius"/> of the
/// camera (roofs, walls, roads, vehicles, props, players) scanned with <see cref="ZFight"/>, and the
/// faces that can fight drawn in magenta through everything. Rescanned once the camera has moved
/// <see cref="RescanMetres"/>, or every <see cref="RescanSeconds"/>; the search runs on a worker thread, the meshes are read back once
/// while it is on (<see cref="ZFightScan.Cache"/>). See docs/notes/general/zfight.md.
/// </summary>
public partial class ZFightOverlay : Node3D
{
    public const float Radius = 40f;
    private const float RescanMetres = 15f;
    private const double MinSeconds = 1.0, RescanSeconds = 5.0;

    private readonly WorldOrigin _origin;
    private MeshInstance3D _patches = null!;
    private ZFightScan.Cache _cache = new();
    private Task<List<ZFight.Hit>>? _task;
    private ZFightScan.Scene? _scene;
    private Vector3 _scannedAt;
    private bool _on, _due;
    private double _since = MinSeconds;

    /// <summary>What the last scan found, for the menu's readout; empty while off.</summary>
    public string Status { get; private set; } = "";

    public bool On
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            _due = value;
            if (value) return;
            _patches.Mesh = null;
            _cache = new ZFightScan.Cache();   // let the read-back meshes go
            Status = "";
        }
    }

    public ZFightOverlay(WorldOrigin origin)
    {
        _origin = origin;
        Name = "ZFightOverlay";
    }

    public ZFightOverlay() : this(null!) { }

    public override void _Ready()
    {
        _patches = new MeshInstance3D { CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_patches);
    }

    public override void _Process(double delta)
    {
        if (!_on) return;
        _since += delta;
        if (_task != null)
        {
            if (!_task.IsCompleted) return;
            Show(_task);
            _task = null;
        }
        if (GetViewport().GetCamera3D() is not { } cam || _since < MinSeconds) return;
        // tiles and props stream in round a camera that stands still too
        if (!_due && cam.GlobalPosition.DistanceTo(_scannedAt) < RescanMetres && _since < RescanSeconds) return;
        Scan(cam);
    }

    private void Scan(Camera3D cam)
    {
        // in this node's space: the patches stay put when the floating origin moves the world
        var space = GlobalTransform.AffineInverse();
        var centre = space * cam.GlobalPosition;
        var within = new Aabb(centre - Vector3.One * Radius, Vector3.One * Radius * 2);
        var scene = ZFightScan.Gather(GetParent(), space, within, 
            // the horizon is discarded wherever a real tile draws (HorizonLayer): never a fight
            n => n == this || n is Terrain.HorizonLayer || n is CanvasItem or CanvasLayer or Viewport, _cache);
        _scene = scene;
        _task = Task.Run(() => ZFight.Find(scene.Tris, visibleOnly: true));
        _scannedAt = cam.GlobalPosition;
        _due = false;
        _since = 0;
    }

    private void Show(Task<List<ZFight.Hit>> task)
    {
        if (task.IsFaulted || _scene == null)
        {
            Status = "scan failed";
            GD.PushError($"[zfight] overlay scan failed: {task.Exception?.InnerException}");
            return;
        }
        var hits = task.Result;
        _patches.Mesh = ZFightScan.Patches(hits);
        Status = $"{ZFightScan.Summary(hits)} within {Radius:0} m of {_scene.Tris.Count} triangles{(_scene.Truncated ? " (truncated)" : "")}";
        GD.Print($"[zfight] {ZFightScan.Describe(_scene, hits)}");
        for (int k = 0; k < Math.Min(3, hits.Count) && _origin != null; k++)
        {
            var (e, n) = _origin.ToLv95(GlobalTransform * hits[k].At);
            GD.Print(string.Create(CultureInfo.InvariantCulture, $"[zfight]   place {k + 1}: --at {e:F0},{n:F0}"));
        }
    }
}
