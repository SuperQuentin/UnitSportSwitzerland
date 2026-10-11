using Godot;
using UnitSport.Core;

namespace UnitSport.Movie;

/// <summary>
/// The shot library's pictures (#777): a small still of every shot at the playhead, rendered one at a
/// time through a viewport of its own that draws only when asked, and a bigger live picture of the shot
/// the pointer is over. Stills are made again when the playhead rests somewhere new or a shot changes.
/// </summary>
public partial class ShotThumbs : Node
{
    public static readonly Vector2I StillSize = new(112, 63), HoverSize = new(384, 216);

    private readonly MovieStage _stage;
    private readonly WorldOrigin _origin;
    private readonly Func<int, Vector3?> _actorAt;
    private SubViewport _still = null!, _hover = null!;
    private Camera3D _stillCam = null!, _hoverCam = null!;
    private readonly Dictionary<int, ImageTexture> _textures = new();
    private readonly Queue<int> _queue = new();
    private int _rendering = -1, _wait;
    private double _shotTime = double.NaN, _restSince;

    /// <summary>The shot shown live in the hover picture, or -1.</summary>
    public int Hovered { get; set; } = -1;
    /// <summary>The live picture of <see cref="Hovered"/>.</summary>
    public Texture2D HoverTexture => _hover.GetTexture();
    /// <summary>A still is ready or changed: the library redraws its rows.</summary>
    public event Action? Updated;

    public ShotThumbs(MovieStage stage, WorldOrigin origin, Func<int, Vector3?> actorAt)
    {
        _stage = stage;
        _origin = origin;
        _actorAt = actorAt;
        Name = "ShotThumbs";
    }

    public override void _Ready()
    {
        _still = new SubViewport { Size = StillSize, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        _still.AddChild(_stillCam = new Camera3D { Current = true, Near = 0.1f, Far = 4000 });
        AddChild(_still);
        _hover = new SubViewport { Size = HoverSize, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        _hover.AddChild(_hoverCam = new Camera3D { Current = true, Near = 0.1f, Far = 4000 });
        AddChild(_hover);
    }

    /// <summary>The still of shot <paramref name="index"/>, or null until it has been made.</summary>
    public Texture2D? Still(int index) => _textures.GetValueOrDefault(index);

    /// <summary>Every still made again (a shot was added, edited or removed).</summary>
    public void Invalidate()
    {
        _queue.Clear();
        for (int i = 0; i < _stage.Project.Cameras.Count; i++) _queue.Enqueue(i);
    }

    public override void _Process(double delta)
    {
        // the playhead rested somewhere new for a moment: the stills show it
        double now = RealClock.Now;
        if (!Mathf.IsEqualApprox((float)_shotTime, (float)_stage.Time))
        {
            _shotTime = _stage.Time;
            _restSince = now;
        }
        else if (_restSince > 0 && now - _restSince > 0.4)
        {
            _restSince = 0;
            Invalidate();
        }

        bool hovering = Hovered >= 0 && Hovered < _stage.Project.Cameras.Count && Aim(_hoverCam, Hovered);
        var mode = hovering ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (_hover.RenderTargetUpdateMode != mode) _hover.RenderTargetUpdateMode = mode;

        if (_rendering >= 0)
        {
            // the frame asked for has been drawn: keep it
            if (++_wait < 2) return;
            var image = _still.GetTexture().GetImage();
            if (_textures.TryGetValue(_rendering, out var tex) && tex.GetSize() == new Vector2(image.GetWidth(), image.GetHeight())) tex.Update(image);
            else _textures[_rendering] = ImageTexture.CreateFromImage(image);
            _rendering = -1;
            Updated?.Invoke();
            return;
        }
        while (_queue.Count > 0)
        {
            int next = _queue.Dequeue();
            if (next >= _stage.Project.Cameras.Count || !Aim(_stillCam, next)) continue;
            _rendering = next;
            _wait = 0;
            _still.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
            break;
        }
    }

    /// <summary>Puts <paramref name="cam"/> where shot <paramref name="index"/> films from at the playhead.</summary>
    private bool Aim(Camera3D cam, int index)
    {
        if (!_stage.Project.ShotPose(index, _stage.Time, _stage.Mount, out var pose, out _)) return false;
        cam.GlobalTransform = StudioCamera.PoseTransform(_origin, pose, _actorAt);
        cam.Fov = CameraTrack.Fov(pose.Lens);
        return true;
    }
}
