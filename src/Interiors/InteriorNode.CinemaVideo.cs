using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// A hall's video on its screen (#1298): the frames this client decodes (<see cref="CinemaVideoStream"/>) go where
/// the cat slides go in that hall alone: the pasted picture, the High-detail projector light (the frame in a square
/// of black, as the slides' <see cref="Letterboxed"/>), the marched beam's picture, the picture in the port, and
/// the lens's glow, the plain beam and the hall's wash in the frame's mean colour. The slides' materials are shared
/// by every hall, so the video's hall gets its own copies while it plays, and the slideshow leaves them alone
/// (<see cref="VideoOwned"/>). One hall at a time decodes, so one set per interior. The sound comes from the
/// screen (<see cref="CinemaVideoSpeaker"/>), and that hall's film music is off (<see cref="MuteFilmMusic"/>).
///
/// <para>
/// Every video's pasted picture is <c>shaders/cinema_screen.gdshader</c>: the hall's display (fit and ratio,
/// <see cref="CinemaVideo.Fit"/>) applied per pixel to the decoded frame, nothing decoded again when it changes; the curtains
/// frame what it shows. A 3D film (phase 6) comes with both eyes side by side:
/// each eye its own half in a headset, red-cyan anaglyph on a flat display (<see cref="Anaglyph"/>, the viewer's choice);
/// the projector light, the beam and the port take the left eye (a second texture), and the picture is never thrown by
/// the light (a light carries one picture), so a 3D hall keeps its pasted screen at High detail too.
/// </para>
/// </summary>
public partial class InteriorNode
{
    /// <summary>The materials and lights the slideshow must not repaint: the video's, while it plays.</summary>
    private static readonly HashSet<GodotObject> VideoOwned = new();

    private int _videoHall = -1;
    private Image? _videoSquareImage, _videoEyeImage, _videoSmall;
    private ImageTexture? _videoTexture, _videoSquare, _videoSquareB, _videoEye;
    private StandardMaterial3D? _videoGlow, _videoPlain;
    private ShaderMaterial? _videoScreen, _videoPort;
    private CinemaVideoSpeaker? _videoSpeaker;
    /// <summary>The halls whose film music gives way to a video, a bit each.</summary>
    private int _videoMuted;

    /// <summary>Whether the video's hall shows a 3D film now: its screen is never thrown by the light.</summary>
    private bool _videoIs3d;

    private static readonly StringName GHeadset = "headset", GAnaglyph = "anaglyph", GStereo = "stereo", GContent = "content",
        GPictureRatio = "picture_ratio", GFrame = "frame", GFrameRatio = "frame_ratio", GFit = "fit";
    private int _stereoHow = -1;

    /// <summary>What the screen's shader was last given: the display and the picture's measured size (written on change only).</summary>
    private (int Display, float Width, float Height) _screenSet = (-1, 0, 0);

    /// <summary>The video's hall shows its pasted picture at every detail: a 3D film or a display other than the default (a light carries the frame as decoded).</summary>
    private bool _videoPastedOnly;

    /// <summary>
    /// A 3D film on a flat display: red-cyan anaglyph (true, the default: most players have no 3D display) or the left eye
    /// alone (no glasses). This viewer's, kept in <c>user://cinema.cfg</c>.
    /// </summary>
    public static bool Anaglyph
    {
        get => _anaglyph ??= LoadAnaglyph();
        set
        {
            _anaglyph = value;
            var cfg = new ConfigFile();
            cfg.Load(CinemaConfig);
            cfg.SetValue("3d", "anaglyph", value);
            cfg.Save(CinemaConfig);
        }
    }

    private static bool? _anaglyph;
    private const string CinemaConfig = "user://cinema.cfg";

    private static bool LoadAnaglyph()
    {
        var cfg = new ConfigFile();
        return cfg.Load(CinemaConfig) != Error.Ok || (bool)cfg.GetValue("3d", "anaglyph", true);
    }

    /// <summary>
    /// Shows a decoded frame (RGBA8, <see cref="CinemaVideo.Width"/> x <see cref="CinemaVideo.Height"/>, twice as wide for a
    /// 3D film) on hall <paramref name="hall"/>'s screen, light, beam, port and glow, its sound from <paramref name="stream"/>;
    /// null gives the hall its slides back. Per new frame (30 a second at most): one texture update, a second for the light
    /// at High detail or for a 3D film's left eye; no mesh.
    /// </summary>
    public void ShowVideo(int hall, Image? picture, Color tint, ICinemaFeed? stream, int display = 0)
    {
        if (picture == null || hall < 0) { StopVideo(); return; }
        var s = ScreenOf(hall);
        if (s == null) return;
        bool stereo = stream is { Stereo: not CinemaVideo.Stereo.Mono };
        if (_videoHall != hall || stereo != _videoIs3d) { StopVideo(); StartVideo(s, stereo); }
        // a sharper picture than the light's (#1319) and the headset's layer, which shows through the screen's mesh, too
        bool pastedOnly = stereo || display != 0 || picture.GetHeight() > CinemaVideo.Height || LayerWanted(stereo);
        if (pastedOnly != _videoPastedOnly) { _videoPastedOnly = pastedOnly; RefreshScreens(); }
        // a texture is the size it was made: a new one when the picture's size changes (2D to 3D, the relay's to one's own)
        if (_videoTexture == null || _videoTexture.GetWidth() != picture.GetWidth() || _videoTexture.GetHeight() != picture.GetHeight())
        {
            _videoTexture = ImageTexture.CreateFromImage(picture);
            Bind(s);
        }
        else _videoTexture.Update(picture);
        // what the light, the beam and the port show: the picture, or a 3D film's left eye
        var one = picture;
        if (stereo)
        {
            int w = picture.GetWidth() / 2, h = picture.GetHeight();
            if (_videoEyeImage == null || _videoEyeImage.GetWidth() != w || _videoEyeImage.GetHeight() != h)
            {
                _videoEyeImage = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
                _videoEyeImage.BlitRect(picture, new Rect2I(0, 0, w, h), Vector2I.Zero);
                _videoEye = ImageTexture.CreateFromImage(_videoEyeImage);
                Bind(s);
            }
            else
            {
                _videoEyeImage.BlitRect(picture, new Rect2I(0, 0, w, h), Vector2I.Zero);
                _videoEye!.Update(_videoEyeImage);
            }
            one = _videoEyeImage;
            // the eyes' way: a shader parameter written on change only
            int how = (XR.XrSession.Active ? 1 : 0) | (Anaglyph ? 2 : 0);
            if (how != _stereoHow)
            {
                _stereoHow = how;
                _videoScreen!.SetShaderParameter(GHeadset, (how & 1) != 0);
                _videoScreen.SetShaderParameter(GAnaglyph, (how & 2) != 0);
            }
        }
        // the hall's display and the picture's measured size into the screen's shader, on change only
        float cw = stream?.ContentWidth ?? 1f, ch = stream?.ContentHeight ?? 1f;
        var (fit, ratio) = CinemaVideo.UnpackDisplay(display);
        float pictureRatio = cw / Math.Max(0.05f, ch) * CinemaVideo.ScreenRatio;
        bool displayChanged = _screenSet != (display, cw, ch);
        if (displayChanged)
        {
            _screenSet = (display, cw, ch);
            // the screen, the picture in the port and the beam all show the same framing
            SetDisplay(_videoScreen!, display, cw, ch);
            SetDisplay(_videoPort!, display, cw, ch);
            if (s.Marched != null) SetDisplay(s.Marched, display, cw, ch);
        }
        FeedLayer(s, stereo, new Vector2I(picture.GetWidth(), picture.GetHeight()), displayChanged, display, cw, ch);
        if (s.Projector is { Visible: true } spot)
        {
            // the light carries the Low size (#1319): its picture lights the room and whoever stands in the beam, the
            // sharper one is the pasted screen's
            if (one.GetWidth() > CinemaVideo.Width)
            {
                _videoSmall ??= Image.CreateEmpty(CinemaVideo.Width, CinemaVideo.Height, false, Image.Format.Rgba8);
                _videoSmall.CopyFrom(one);
                _videoSmall.Resize(CinemaVideo.Width, one.GetHeight() * CinemaVideo.Width / one.GetWidth(), Image.Interpolation.Nearest);
                one = _videoSmall;
            }
            int w = one.GetWidth(), h = one.GetHeight();
            if (_videoSquareImage == null || _videoSquareImage.GetWidth() != w)
            {
                _videoSquareImage = Image.CreateEmpty(w, w, false, Image.Format.Rgba8);
                _videoSquareImage.Fill(Colors.Black);
                _videoSquare = ImageTexture.CreateFromImage(_videoSquareImage);
                _videoSquareB = ImageTexture.CreateFromImage(_videoSquareImage);
            }
            _videoSquareImage.BlitRect(one, new Rect2I(0, 0, w, h), new Vector2I(0, (w - h) / 2));
            // a light copies its projector texture into the renderer's atlas when the texture is set, not when its
            // pixels change: two textures taking turns, the one the light is not using updated and set each frame
            var next = spot.LightProjector == _videoSquare ? _videoSquareB! : _videoSquare!;
            next.Update(_videoSquareImage);
            spot.LightProjector = next;
        }
        var full = Full(tint);
        _videoGlow!.AlbedoColor = full * 1.6f;
        _videoPlain!.AlbedoColor = full;
        _lit?.SetShaderParameter(GGlow, new Vector3(tint.R, tint.G, tint.B) * GlowGain);
        if (_videoSpeaker != null) _videoSpeaker.Video = stream;
        // the side curtains to the edges of what the screen shows
        FrameCurtains(hall, CinemaVideo.VisibleWidth(fit, ratio, pictureRatio));
    }

    /// <summary>Hall <paramref name="hall"/>'s screen, or null: a loop, no closure, as it runs every frame.</summary>
    private CinemaScreen? ScreenOf(int hall)
    {
        foreach (var s in _screens)
            if (s.No == hall) return s;
        return null;
    }

    private void StartVideo(CinemaScreen s, bool stereo)
    {
        _videoHall = s.No;
        _videoIs3d = stereo;
        _videoScreen ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cinema_screen.gdshader") };
        _videoScreen.SetShaderParameter(GStereo, stereo);
        _screenSet = (-1, 0, 0);
        _stereoHow = -1;
        _videoPort ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/cinema_port.gdshader") };
        _videoGlow ??= (StandardMaterial3D)LensGlowMaterial().Duplicate();
        _videoPlain ??= (StandardMaterial3D)PlainBeam().Duplicate();
        s.Picture.MaterialOverride = _videoScreen;
        if (s.Port != null) s.Port.MaterialOverride = _videoPort;
        if (s.Glow != null) s.Glow.MaterialOverride = _videoGlow;
        if (s.Marched != null) VideoOwned.Add(s.Marched);
        if (s.Beam != null && s.Beam.MaterialOverride is StandardMaterial3D) s.Beam.MaterialOverride = _videoPlain;
        if (s.Projector != null) VideoOwned.Add(s.Projector);
        Bind(s);

        // the sound from the screen, filling the hall as its surround speakers would; a few metres into the hall, not on
        // the screen, whose mesh and wall the occlusion rays would hit (heard muffled, as if behind a wall, #1365)
        var into = s.Hall.GetCenter() - s.Picture.Position;
        into.Y = 0;
        _videoSpeaker = new CinemaVideoSpeaker
        {
            Name = "VideoSound",
            Position = s.Picture.Position + into.LimitLength(3f),
            Spread = Math.Max(4f, new Vector2(s.Hall.Size.X, s.Hall.Size.Z).Length() / 2),
        };
        AddChild(_videoSpeaker);
        GD.Print($"[cinema] hall {s.No + 1} shows its video{(stereo ? " in 3D" : "")}");
    }

    /// <summary>The current textures into the hall's materials: the picture on the screen, one eye (or the picture) on the beam and the port.</summary>
    private void Bind(CinemaScreen s)
    {
        if (_videoTexture == null) return;
        Texture2D one = _videoIs3d && _videoEye != null ? _videoEye : _videoTexture;
        _videoScreen?.SetShaderParameter(GPicture, _videoTexture);
        _layerPicture?.SetShaderParameter(GPicture, _videoTexture);
        _videoPort!.SetShaderParameter(GPicture, one);
        s.Marched?.SetShaderParameter(GPicture, one);
    }

    /// <summary>
    /// A display (<see cref="CinemaVideo.Fit"/>, the frame's ratio) and the picture's measured share of the frame into a
    /// material that frames it (<c>shaders/cinema_display.gdshaderinc</c>: the screen, the port, the beam); (0, 1, 1) the
    /// slides' whole frame.
    /// </summary>
    private static void SetDisplay(ShaderMaterial m, int display, float width, float height)
    {
        var (fit, ratio) = CinemaVideo.UnpackDisplay(display);
        float frame = CinemaVideo.FrameWidth(ratio);
        m.SetShaderParameter(GContent, new Vector4((1 - width) / 2, (1 - height) / 2, width, height));
        m.SetShaderParameter(GPictureRatio, width / Math.Max(0.05f, height) * CinemaVideo.ScreenRatio);
        m.SetShaderParameter(GFrame, frame);
        m.SetShaderParameter(GFrameRatio, frame * CinemaVideo.ScreenRatio);
        m.SetShaderParameter(GFit, (int)fit);
    }

    /// <summary>The video's hall back to the slides: the shared materials, the current slide, its wash; the sound gone.</summary>
    private void StopVideo()
    {
        if (_videoHall < 0) return;
        DropLayer();
        var s = ScreenOf(_videoHall);
        // the slides fill the screen: the curtains drawn back
        FrameCurtains(_videoHall, 1f);
        _videoHall = -1;
        bool was3d = _videoIs3d || _videoPastedOnly;
        _videoIs3d = false;
        _videoPastedOnly = false;
        if (_videoSpeaker != null && IsInstanceValid(_videoSpeaker)) _videoSpeaker.QueueFree();
        _videoSpeaker = null;
        if (s == null) return;
        s.Picture.MaterialOverride = CatMaterial();
        if (s.Port != null) s.Port.MaterialOverride = PortMaterial();
        if (s.Glow != null) s.Glow.MaterialOverride = LensGlowMaterial();
        if (s.Marched != null)
        {
            VideoOwned.Remove(s.Marched);
            s.Marched.SetShaderParameter(GPicture, Slide(_slide));
            // the slides fill the screen: the beam's framing back to the whole frame
            SetDisplay(s.Marched, 0, 1f, 1f);
        }
        if (s.Beam != null && s.Beam.MaterialOverride == _videoPlain) s.Beam.MaterialOverride = PlainBeam();
        if (s.Projector != null) { VideoOwned.Remove(s.Projector); s.Projector.LightProjector = Letterboxed(_slide); }
        if (was3d) RefreshScreens();
        ShowGlow(true);
    }

    /// <summary>The halls whose film music is off for a video (a bit each), on change only.</summary>
    public void MuteFilmMusic(int halls)
    {
        if (halls == _videoMuted) return;
        _videoMuted = halls;
        RefreshScreens();
    }

    public override void _ExitTree()
    {
        base._ExitTree();
        StopVideo();
    }
}
