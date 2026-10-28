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
/// </summary>
public partial class InteriorNode
{
    /// <summary>The materials and lights the slideshow must not repaint: the video's, while it plays.</summary>
    private static readonly HashSet<GodotObject> VideoOwned = new();

    private int _videoHall = -1;
    private Image? _videoImage, _videoSquareImage;
    private ImageTexture? _videoTexture, _videoSquare;
    private StandardMaterial3D? _videoPicture, _videoPort, _videoGlow, _videoPlain;
    private CinemaVideoSpeaker? _videoSpeaker;
    /// <summary>The halls whose film music gives way to a video, a bit each.</summary>
    private int _videoMuted;

    /// <summary>
    /// Shows a decoded frame (RGBA8, <see cref="CinemaVideo.Width"/> x <see cref="CinemaVideo.Height"/>) on hall
    /// <paramref name="hall"/>'s screen, light, beam, port and glow, its sound from <paramref name="stream"/>; null gives
    /// the hall its slides back. Per new frame (30 a second at most): two texture updates (one at High detail), no mesh.
    /// </summary>
    public void ShowVideo(int hall, Image? picture, Color tint, ICinemaFeed? stream)
    {
        if (picture == null || hall < 0) { StopVideo(); return; }
        var s = ScreenOf(hall);
        if (s == null) return;
        if (_videoHall != hall) { StopVideo(); StartVideo(s); }
        _videoTexture!.Update(picture);
        if (s.Projector is { Visible: true } spot)
        {
            _videoSquareImage!.BlitRect(picture, new Rect2I(0, 0, CinemaVideo.Width, CinemaVideo.Height),
                new Vector2I(0, (CinemaVideo.Width - CinemaVideo.Height) / 2));
            _videoSquare!.Update(_videoSquareImage);
            if (spot.LightProjector != _videoSquare) spot.LightProjector = _videoSquare;
        }
        var full = Full(tint);
        _videoGlow!.AlbedoColor = full * 1.6f;
        _videoPlain!.AlbedoColor = full;
        _lit?.SetShaderParameter(GGlow, new Vector3(tint.R, tint.G, tint.B) * GlowGain);
        if (_videoSpeaker != null) _videoSpeaker.Video = stream;
    }

    /// <summary>Hall <paramref name="hall"/>'s screen, or null: a loop, no closure, as it runs every frame.</summary>
    private CinemaScreen? ScreenOf(int hall)
    {
        foreach (var s in _screens)
            if (s.No == hall) return s;
        return null;
    }

    private void StartVideo(CinemaScreen s)
    {
        _videoHall = s.No;
        _videoImage ??= Image.CreateEmpty(CinemaVideo.Width, CinemaVideo.Height, false, Image.Format.Rgba8);
        _videoTexture ??= ImageTexture.CreateFromImage(_videoImage);
        if (_videoSquareImage == null)
        {
            _videoSquareImage = Image.CreateEmpty(CinemaVideo.Width, CinemaVideo.Width, false, Image.Format.Rgba8);
            _videoSquareImage.Fill(Colors.Black);
            _videoSquare = ImageTexture.CreateFromImage(_videoSquareImage);
        }
        _videoPicture ??= Copy(CatMaterial(), _videoTexture);
        _videoPort ??= Copy(PortMaterial(), _videoTexture);
        _videoGlow ??= (StandardMaterial3D)LensGlowMaterial().Duplicate();
        _videoPlain ??= (StandardMaterial3D)PlainBeam().Duplicate();
        s.Picture.MaterialOverride = _videoPicture;
        if (s.Port != null) s.Port.MaterialOverride = _videoPort;
        if (s.Glow != null) s.Glow.MaterialOverride = _videoGlow;
        if (s.Marched != null) { s.Marched.SetShaderParameter(GPicture, _videoTexture); VideoOwned.Add(s.Marched); }
        if (s.Beam != null && s.Beam.MaterialOverride is StandardMaterial3D) s.Beam.MaterialOverride = _videoPlain;
        if (s.Projector != null) VideoOwned.Add(s.Projector);
        // the sound from the screen, filling the hall as its surround speakers would
        _videoSpeaker = new CinemaVideoSpeaker
        {
            Name = "VideoSound",
            Position = s.Picture.Position,
            Spread = Math.Max(4f, new Vector2(s.Hall.Size.X, s.Hall.Size.Z).Length() / 2),
        };
        AddChild(_videoSpeaker);
        GD.Print($"[cinema] hall {s.No + 1} shows its video");
    }

    private static StandardMaterial3D Copy(StandardMaterial3D m, Texture2D picture)
    {
        var copy = (StandardMaterial3D)m.Duplicate();
        copy.AlbedoTexture = picture;
        copy.TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear;
        return copy;
    }

    /// <summary>The video's hall back to the slides: the shared materials, the current slide, its wash; the sound gone.</summary>
    private void StopVideo()
    {
        if (_videoHall < 0) return;
        var s = ScreenOf(_videoHall);
        _videoHall = -1;
        if (_videoSpeaker != null && IsInstanceValid(_videoSpeaker)) _videoSpeaker.QueueFree();
        _videoSpeaker = null;
        if (s == null) return;
        s.Picture.MaterialOverride = CatMaterial();
        if (s.Port != null) s.Port.MaterialOverride = PortMaterial();
        if (s.Glow != null) s.Glow.MaterialOverride = LensGlowMaterial();
        if (s.Marched != null) { VideoOwned.Remove(s.Marched); s.Marched.SetShaderParameter(GPicture, Slide(_slide)); }
        if (s.Beam != null && s.Beam.MaterialOverride == _videoPlain) s.Beam.MaterialOverride = PlainBeam();
        if (s.Projector != null) { VideoOwned.Remove(s.Projector); s.Projector.LightProjector = Letterboxed(_slide); }
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
