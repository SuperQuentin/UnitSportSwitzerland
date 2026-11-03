using Godot;
using UnitSport.Concert.Mvr;

namespace UnitSport.Concert;

/// <summary>
/// An imported screen's own video (#975): its MVR source on a quad the size of the screen, filling it
/// (an LED wall's content is made for the wall; no letterbox bars of nothing). A <see cref="VideoSource.File"/> plays from this
/// machine's cache (<see cref="ConcertMvrTool.VideoFile"/>, Ogg Theora, looping, muted: the PA plays the
/// show) in a <see cref="VideoStreamPlayer"/> kept in a SubViewport that never renders, so it is never
/// drawn on the player's screen; a <see cref="VideoSource.Ndi"/> source is received by
/// <see cref="ConcertNdi.Receiver"/>. Nothing decodes or uploads while no one is near. Where the
/// source cannot play (no cache here, no NDI runtime, CITP, a capture device) there is no node and the
/// screen shows the show's content as a generated one does. Client only.
/// </summary>
public sealed partial class ConcertScreenVideo : Node3D
{
    private readonly VideoSource _kind;
    private readonly string _name;
    private readonly string? _path;
    private readonly Vector2 _size;
    private MeshInstance3D _quad = null!;
    private StandardMaterial3D _mat = null!;
    private VideoStreamPlayer? _player;
    private ConcertNdi.Receiver? _ndi;
    private string? _ndiStatus;
    private ImageTexture? _tex;
    private Image? _image;
    private byte[]? _frame;
    private int _frameW, _frameH;
    private double _nextFrame;
    private bool _bound, _shown;
    private float _level = -1f;

    private ConcertScreenVideo(VideoSource kind, string name, string? path, Vector2 size)
    {
        _kind = kind;
        _name = name;
        _path = path;
        _size = size;
        Name = "Video";
    }

    /// <summary>The imported rig's screens' rows, in the patch's order of its screens (<see cref="RigData.PatchInto"/> keeps the rows' order).</summary>
    public static RigData.FixtureRow[] Rows(RigData? rig) =>
        rig == null ? System.Array.Empty<RigData.FixtureRow>()
            : rig.Fixtures.Where(r => rig.Types[r.Type].Kind == (int)FixtureKind.Screen).ToArray();

    /// <summary>A screen's video when its source can play on this machine; null leaves it the show's content.</summary>
    public static ConcertScreenVideo? For(RigData.FixtureRow row, Fixture screen)
    {
        if (row.VideoName is not { } name || DisplayServer.GetName() == "headless") return null;
        var size = new Vector2(screen.ScreenSize.X, screen.ScreenSize.Y);
        switch ((VideoSource)row.VideoSource)
        {
            case VideoSource.File when row.VideoHash is { } hash:
                string path = ConcertMvrTool.VideoFile(hash);
                if (Godot.FileAccess.FileExists(path)) return new ConcertScreenVideo(VideoSource.File, name, path, size);
                GD.Print($"[concert] {row.Name}: video {name} is not cached here ('/concert videos <file.mvr>'): the show's content instead");
                return null;
            case VideoSource.Ndi:
                return new ConcertScreenVideo(VideoSource.Ndi, name, null, size);
            default:
                return null;
        }
    }

    public override void _Ready()
    {
        _mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear, DisableFog = true,
        };
        _quad = new MeshInstance3D { Name = "Picture", Visible = false, Mesh = new QuadMesh { Size = _size }, MaterialOverride = _mat };
        AddChild(_quad);
        if (_kind == VideoSource.File)
        {
            // the player decodes into its own texture; its viewport never draws, so neither does it
            var vp = new SubViewport { Name = "Decoder", Size = new Vector2I(2, 2), RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
            _player = new VideoStreamPlayer { Name = "Player", Stream = new VideoStreamTheora { File = _path! }, Loop = true, Volume = 0f };
            vp.AddChild(_player);
            AddChild(vp);
        }
    }

    public override void _ExitTree()
    {
        _ndi?.Dispose();
        _ndi = null;
    }

    /// <summary>
    /// A frame of the show: plays while someone is <paramref name="near"/> (paused, or not received,
    /// otherwise), at <paramref name="level"/> (the screen's dimmer and the strobe's wash). Whether it
    /// shows a picture: if not, the screen paints the show's content.
    /// </summary>
    public bool Step(bool near, float level)
    {
        if (_player != null)
        {
            if (near && !_player.IsPlaying()) _player.Play();
            if (_player.Paused == near) _player.Paused = !near;
            if (!_bound && near && _player.GetVideoTexture() is { } t && t.GetWidth() > 0)
                Bind(t);
        }
        else if (near) ReceiveNdi();
        bool show = _bound && level > 0.05f;
        if (show && MathF.Abs(level - _level) > 0.01f)
        {
            _level = level;
            _mat.AlbedoColor = new Color(level, level, level);
        }
        if (show != _shown) _quad.Visible = _shown = show;
        return show;
    }

    /// <summary>The newest NDI frame onto the texture, at most 30 times a second.</summary>
    private void ReceiveNdi()
    {
        if (_ndi == null && _ndiStatus == null)
        {
            _ndi = ConcertNdi.Receiver.Start(_name, out string? why);
            _ndiStatus = why ?? "looking";
            GD.Print($"[concert] screen NDI \"{_name}\": {why ?? "looking for it on the network"}");
        }
        if (_ndi == null) return;
        if (_ndi.Status != _ndiStatus)
        {
            _ndiStatus = _ndi.Status;
            GD.Print($"[concert] screen NDI \"{_name}\": {_ndiStatus}");
        }
        double now = Core.RealClock.Now;
        if (now < _nextFrame || !_ndi.Take(ref _frame, out int w, out int h)) return;
        _nextFrame = now + 1.0 / 30;
        _image ??= Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        _image.SetData(w, h, false, Image.Format.Rgba8, _frame!);
        if (_tex == null || w != _frameW || h != _frameH)
        {
            _tex = ImageTexture.CreateFromImage(_image);
            _frameW = w;
            _frameH = h;
            Bind(_tex);
        }
        else _tex.Update(_image);
    }

    private void Bind(Texture2D tex)
    {
        _bound = true;
        _mat.AlbedoTexture = tex;
    }
}
