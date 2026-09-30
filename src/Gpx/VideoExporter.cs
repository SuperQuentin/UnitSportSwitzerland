using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Gpx;

/// <summary>
/// Renders a replay to a video file, one frame at a time, as slowly as it needs to.
///
/// <para>
/// The whole point is that it is <b>not</b> real time. Recording the screen while the game plays
/// gives you whatever the machine managed that second: dropped frames, and — far worse here —
/// terrain that had not streamed in yet. This drives the clock itself, in exact 1/fps steps, and
/// before capturing each frame it waits for <see cref="ChunkManager.Settled"/>. A frame may take
/// eight milliseconds or eight seconds; the recording cannot tell the difference, because
/// nothing downstream reads the real frame time.
/// </para>
///
/// <para>
/// That last part is what makes it correct rather than merely slow. The race clock, the runners'
/// stride phase and position easing, and the camera's own lag are all handed the same fixed step
/// (<see cref="RacePlayback.StepTo"/>, <see cref="PlaybackCamera.Step"/>) instead of
/// <c>delta</c>. Godot's own <c>--write-movie</c> mode does the fixed-timestep half of this, but
/// it cannot wait for a background tile load, so it would happily film the holes.
/// </para>
/// </summary>
public partial class VideoExporter : Node
{
    private enum Phase { Idle, Warmup, Advance, Settle, Capture, Grabbing, Encoding, Finished }

    private RacePlayback _race = null!;
    private PlaybackCamera _camera = null!;
    private ChunkManager _chunks = null!;

    private Phase _phase = Phase.Idle;
    private string _directory = "";
    private int _fps = 30;
    private int _frame;
    private int _total;
    private double _step;
    private double _clockSpeed = 1;
    private int _stableFrames;
    private double _waiting;
    private int _encodePid = -1;
    private string _videoPath = "";
    private int _timeouts;
    private double _lastReport;
    private int _duplicates;
    private ulong _lastHash;
    private ExportPrefetcher? _prefetch;
    private System.Diagnostics.Process? _ffmpeg;
    private System.Threading.Channels.Channel<byte[]>? _frames;
    private Task? _writer;
    private volatile string? _pipeError;
    private readonly System.Text.StringBuilder _ffmpegLog = new();
    private bool _pngFallback;
    private Vector2I _frameSize;
    private CanvasLayer? _overlay;
    private ProgressBar? _bar;
    private Label? _readout;
    private double _elapsed;

    /// <summary>
    /// Frames to hold a settled world before grabbing, so the render matches the state.
    ///
    /// <para>
    /// One, not two. Every commit the settle test waits for has already happened on the main
    /// thread by the time it reports settled, and <see cref="GrabAsync"/> then waits for a
    /// complete draw of its own — so the second frame was insurance against a failure that has
    /// its own detector: an early grab reads the previous buffer, which is exactly what the
    /// duplicate-frame hash catches and reports. At four or five rendered frames per captured
    /// one, dropping it is worth about a fifth of the whole export.
    /// </para>
    /// </summary>
    private const int StableFramesNeeded = 1;

    /// <summary>
    /// Give up waiting for terrain after this long and capture anyway.
    ///
    /// <para>
    /// A tile that is genuinely missing — outside the built region, or a failed load — never
    /// settles, and an export that hangs for ever on it is worse than one frame with a hole.
    /// The count of these is reported at the end rather than swallowed.
    /// </para>
    /// </summary>
    private const double SettleTimeoutSeconds = 6.0;

    /// <summary>
    /// How long to wait for the world before the first frame.
    ///
    /// <para>
    /// Separate from, and far longer than, the per-frame budget. Starting cold means the LOD
    /// rings have several hundred tiles to fetch, decode and mesh, and at six concurrent builds
    /// that is a minute or so — during which every frame would hit the per-frame timeout and
    /// capture a hole. Measured on a 600-frame export: 63 frames arrived before the world had
    /// loaded, which was 378 of the 435 seconds the whole export took, and all of them were at
    /// the start of the video.
    /// </para>
    ///
    /// <para>
    /// This was ten minutes, which was honestly what a cold start needed: the rings reach nine
    /// tiles out, so a stationary camera wanted around 360 tiles — and a second anchor nobody had
    /// noticed wanted 360 more, a hundred kilometres away. With that anchor dropped, the far
    /// rings reading a coarse tile instead of a full one, and the wait scoped to what the camera
    /// can actually see, it is now a ceiling for a genuinely missing tile rather than an expected
    /// wait. If an export spends two minutes here, something is wrong and the report says what.
    /// </para>
    /// </summary>
    private const double WarmupTimeoutSeconds = 120.0;

    /// <summary>
    /// How far out the world must be complete before a frame is captured, in tiles.
    ///
    /// <para>
    /// With fog on, six: fog runs from 3.5 km to 8 km and its colour is the same as the sky, so
    /// a tile that has not arrived past ring 8 is indistinguishable from one that has; at ring 6
    /// it is still about a third visible, which is the last distance where a hole would show.
    /// With fog off (the default now) a missing far tile shows as the 100 m horizon lattice in
    /// its place - the same ridge, coarser - so twelve rings is where the difference stops being
    /// visible in a frame, and the render distance caps it either way.
    /// </para>
    /// </summary>
    public int SettleRings { get; set; } = Math.Min(Core.GameSettings.Current.Fog ? 6 : 12,
        Core.GameSettings.Current.RenderDistanceRings);

    /// <summary>Whether the visible world is complete, and how to say why it is not.</summary>
    private bool WorldReady => _chunks.SettledNear(_camera.GlobalPosition, SettleRings);

    public bool Running => _phase is not (Phase.Idle or Phase.Finished);

    /// <summary>0..1 through the capture, for the progress readout.</summary>
    public double Progress => _total == 0 ? 0 : Math.Clamp(_frame / (double)_total, 0, 1);

    public string Status { get; private set; } = "";

    /// <summary>Raised when the export finishes, fails or is cancelled.</summary>
    public event Action? Completed;

    public static VideoExporter Create(RacePlayback race, PlaybackCamera camera, ChunkManager chunks) =>
        new()
        {
            Name = "VideoExporter",
            _race = race,
            _camera = camera,
            _chunks = chunks,
        };

    public override void _Ready() => SetProcess(false);

    /// <summary>
    /// Starts an export of the whole race into <paramref name="directory"/>.
    ///
    /// <para>
    /// The camera mode and playback speed are taken as they stand, because they are what the
    /// player just spent time setting up. Speed is baked in: at 2x the video is half as long and
    /// each frame advances twice as much race time, which is a real editorial choice and not a
    /// rendering detail.
    /// </para>
    /// </summary>
    /// <summary>Warm-up budget for this run. Overridable so a test does not wait ten minutes.</summary>
    public double WarmupSeconds { get; set; } = WarmupTimeoutSeconds;

    public bool Begin(string directory, int fps)
    {
        if (Running) return false;
        if (_race.Runners.Count == 0)
        {
            Status = "nothing loaded to export";
            return false;
        }

        var error = DirAccess.MakeDirRecursiveAbsolute(directory);
        if (error != Error.Ok)
        {
            Status = $"cannot write to {directory}";
            GD.PushError($"[export] {Status}: {error}");
            return false;
        }

        _directory = directory;
        _fps = Math.Clamp(fps, 1, 120);
        _clockSpeed = Math.Max(0.05, _race.Speed);
        _step = _clockSpeed / _fps;
        _total = Math.Max(1, (int)Math.Ceiling(_race.Duration / _step));
        _frame = 0;
        _timeouts = 0;

        // the exporter owns the clock and the camera for the duration
        _race.Playing = false;
        _race.SetProcess(false);
        _camera.SetProcess(false);
        _camera.Current = true;

        // Nobody is watching this render, so the budgets that keep a live frame smooth are pure
        // cost here — see ChunkManager.OfflineMode.
        _chunks.OfflineMode = true;

        // The route is known in full before frame one, so no frame should ever be the first to
        // ask for a tile. This reads the whole thing into the cache in route order while the
        // render works through it.
        if (_race.Focused is { } focused && _chunks.Source != null)
        {
            _prefetch = ExportPrefetcher.Create(focused.Active, _chunks);
            _prefetch.Start();
        }

        // start from a clean state rather than wherever the player had scrubbed to
        _race.StepTo(0, 0);
        _camera.Step(0);

        _pngFallback = !HasFfmpeg();
        if (_pngFallback)
            GD.Print("[export] ffmpeg is not on PATH — writing a PNG sequence and encode.bat");

        BuildOverlay();

        _phase = Phase.Warmup;
        _waiting = 0;
        _stableFrames = 0;
        _elapsed = 0;
        _duplicates = 0;
        _lastHash = 0;
        SetProcess(true);

        var size = GetViewport().GetVisibleRect().Size;
        GD.Print($"[export] {_total} frames at {_fps} fps ({_race.Duration / _clockSpeed:F0} s of video, "
                 + $"{_clockSpeed:0.##}x), {size.X:F0}x{size.Y:F0} -> {directory}");
        return true;
    }

    public void Cancel()
    {
        if (!Running) return;
        GD.Print($"[export] cancelled after {_frame} frames");
        Status = $"cancelled at frame {_frame}";
        Release();
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        RefreshOverlay();

        switch (_phase)
        {
            case Phase.Warmup:
                // Let the world arrive before frame one, on a budget of its own. Without this
                // the opening seconds of every video are the only part with terrain missing.
                _waiting += delta;
                if (WorldReady) _stableFrames++;
                else _stableFrames = 0;

                Status = $"loading terrain… {_waiting:F0}s";
                DisplayServer.WindowSetTitle($"UnitSport — {Status}");

                if (_waiting - _lastReport > 5)
                {
                    _lastReport = _waiting;
                    GD.Print($"[export] waiting for terrain ({_waiting:F0}s) — "
                        + _chunks.SettleReport(_camera.GlobalPosition));
                    GD.Print($"[export]   worker time: {_chunks.BuildTimeReport()}");
                }

                if (_stableFrames >= StableFramesNeeded || _waiting > WarmupSeconds)
                {
                    if (_waiting > WarmupSeconds)
                        GD.Print("[export] world never settled; recording anyway");
                    else
                        GD.Print($"[export] world loaded in {_waiting:F1}s, starting capture");
                    _phase = Phase.Advance;
                }
                break;

            case Phase.Advance:
                // The clock moves by exactly one video frame, never by how long the last one
                // took to render. This is the line that makes the export deterministic.
                _race.StepTo(_frame * _step, _step);

                // The clock advances by _step TRACK seconds; the camera advances by one frame of
                // SCREEN time. They are different units and the camera wants the second one: every
                // easing rate in PlaybackCamera and in the shots is a screen rate, and Absolute
                // Cinema measures shot length in screen seconds too. Passing _step here applied
                // the speed multiplier twice, so an export paced visibly differently from the
                // preview the player had just set up at the same multiplier.
                _camera.Step(1.0 / _fps);
                _stableFrames = 0;
                _waiting = 0;
                _phase = Phase.Settle;
                break;

            case Phase.Settle:
                _waiting += delta;
                if (WorldReady) _stableFrames++;
                else _stableFrames = 0;

                if (_stableFrames >= StableFramesNeeded) _phase = Phase.Capture;
                else if (_waiting > SettleTimeoutSeconds)
                {
                    _timeouts++;
                    _phase = Phase.Capture;
                }
                break;

            case Phase.Capture:
                // Grabbing the viewport texture straight from _Process reads whatever the
                // render thread last left there, which during an export is nothing useful —
                // measured as 75 identical frames of empty sky. Waiting for the draw to
                // actually complete is the documented way to screenshot a live viewport.
                _phase = Phase.Grabbing;
                _ = CaptureFrameAsync();
                break;

            case Phase.Grabbing:
                break;   // CaptureFrameAsync moves it on

            case Phase.Encoding:
                if (_ffmpeg != null)
                {
                    // the writer drains the channel and closes stdin; ffmpeg exits after that
                    if ((_writer?.IsCompleted ?? true) && _ffmpeg.HasExited) FinishEncode();
                }
                else if (_encodePid < 0 || !OS.IsProcessRunning(_encodePid)) FinishEncode();
                break;
        }
    }

    private async Task CaptureFrameAsync()
    {
        var image = await GrabAsync();
        if (_phase != Phase.Grabbing || image == null) return;   // cancelled while waiting

        // A frame identical to the one before it means the render thread had not produced the
        // new one yet and we read the old buffer again. In a video that is a stutter, or a whole
        // stretch that looks like it repeats. Giving the renderer another frame and grabbing
        // again is the fix; counting what is left is how we know whether it worked.
        ulong hash = Hash(image);
        if (_frame > 0 && hash == _lastHash)
        {
            image = await GrabAsync();
            if (_phase != Phase.Grabbing || image == null) return;

            hash = Hash(image);
            if (hash == _lastHash) _duplicates++;
        }
        _lastHash = hash;

        if (_frame == 0 && !StartOutput(image)) return;
        if (!await WriteFrameAsync(image)) return;

        _frame++;
        Status = $"frame {_frame}/{_total}";
        UpdateTitle();
        _phase = _frame >= _total ? StartEncode() : Phase.Advance;
    }

    /// <summary>
    /// Opens the frame sink, now that the exact frame size is known.
    ///
    /// <para>
    /// Deliberately at the first captured frame rather than in <see cref="Begin"/>: the raw pipe
    /// has to declare the video size up front and the only honest source for it is an image that
    /// has actually been rendered. The viewport's reported size is the internal resolution, which
    /// is usually right and is not the same statement.
    /// </para>
    /// </summary>
    private bool StartOutput(Image image)
    {
        _frameSize = new Vector2I(image.GetWidth(), image.GetHeight());
        _videoPath = System.IO.Path.Combine(_directory, "run.mp4");

        // A reference still, always. It costs one PNG and it is the only way to tell a video
        // that came out upside down or colour-swapped from one that was rendered that way —
        // this file is known-good, because it goes through the same SavePng that produced every
        // frame of every export before the pipe existed.
        string reference = System.IO.Path.Combine(_directory, "frame_00000.png");
        image.SavePng(reference);

        GD.Print($"[export] first frame: camera {_camera.GlobalPosition:F0}, "
            + $"prims={Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame)}, "
            + $"{_frameSize.X}x{_frameSize.Y}");

        if (_pngFallback) return true;

        var arguments = PipeArguments();
        WriteScript(arguments, piped: true);

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(UnitSport.Core.BundledTools.Resolve("ffmpeg"))
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in arguments) start.ArgumentList.Add(a);

            _ffmpeg = System.Diagnostics.Process.Start(start);
            if (_ffmpeg == null) throw new IOException("ffmpeg did not start");

            // Drained on a thread of its own: ffmpeg writes progress to stderr continuously, and
            // a full pipe buffer would deadlock the encoder against the frames we are feeding it.
            _ffmpeg.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_ffmpegLog)
                {
                    _ffmpegLog.AppendLine(e.Data);
                    if (_ffmpegLog.Length > 8192) _ffmpegLog.Remove(0, _ffmpegLog.Length - 8192);
                }
            };
            _ffmpeg.BeginErrorReadLine();
        }
        catch (Exception e)
        {
            GD.PushWarning($"[export] could not start ffmpeg ({e.Message}); writing PNG frames");
            _pngFallback = true;
            _ffmpeg = null;
            return true;
        }

        // Bounded, so a slow encoder applies back-pressure instead of the render building a queue
        // of 3 MB frames until memory runs out. Single reader, so frame order needs no sequencing.
        _frames = System.Threading.Channels.Channel.CreateBounded<byte[]>(
            new System.Threading.Channels.BoundedChannelOptions(8)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });

        var stdin = _ffmpeg.StandardInput.BaseStream;
        var reader = _frames.Reader;
        _writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var bytes in reader.ReadAllAsync().ConfigureAwait(false))
                    await stdin.WriteAsync(bytes).ConfigureAwait(false);
                await stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception e) { _pipeError = e.Message; }
            finally { try { stdin.Close(); } catch (Exception) { } }
        });

        GD.Print($"[export] streaming raw frames to ffmpeg -> {_videoPath}");
        return true;
    }

    /// <summary>ffmpeg reading uncompressed frames from its own stdin.</summary>
    private string[] PipeArguments() => new[]
    {
        "-y",
        "-f", "rawvideo",
        "-pixel_format", "rgba",
        "-video_size", $"{_frameSize.X}x{_frameSize.Y}",
        "-framerate", _fps.ToString(),
        "-i", "-",
        "-c:v", "libx264", "-preset", "slow", "-crf", "18",
        "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p",
        _videoPath,
    };

    /// <summary>
    /// Hands one frame to the sink.
    ///
    /// <para>
    /// Down the pipe there is no PNG compression and no file: a 1152x648 frame is 3 MB of memcpy
    /// into a buffered stream, and ffmpeg encodes it on its own core while the game renders the
    /// next one. Compressing each frame to PNG on the main thread, only for ffmpeg to decompress
    /// it again at the end, was the second-largest cost in an export once the terrain waits were
    /// gone.
    /// </para>
    /// </summary>
    private async Task<bool> WriteFrameAsync(Image image)
    {
        if (_pngFallback)
        {
            if (_frame == 0) return true;   // frame 0 is already on disk as the reference still
            string path = System.IO.Path.Combine(_directory, $"frame_{_frame:D5}.png");
            var error = image.SavePng(path);
            if (error != Error.Ok) GD.PushError($"[export] could not write {path}: {error}");
            return true;
        }

        if (_pipeError != null)
        {
            GD.PushError($"[export] ffmpeg stopped accepting frames: {_pipeError}\n{FfmpegLog()}");
            Status = "encoder failed";
            Release();
            return false;
        }

        if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
        await _frames!.Writer.WriteAsync(image.GetData());
        return true;
    }

    private string FfmpegLog()
    {
        lock (_ffmpegLog) return _ffmpegLog.ToString();
    }

    /// <summary>
    /// Waits for one complete draw and reads it back, with the progress overlay suppressed.
    ///
    /// <para>
    /// The overlay lives in the same viewport the frames are grabbed from, so it has to be hidden
    /// for the frame that is captured or it burns into the video. Hiding it takes effect on the
    /// <i>next</i> render, which is exactly the one this awaits — so the cost is one extra
    /// rendered frame per captured frame, and the export is not running in real time anyway.
    /// </para>
    /// </summary>
    private async Task<Image?> GrabAsync()
    {
        if (_overlay != null) _overlay.Visible = false;

        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

        Image? image = _phase == Phase.Grabbing ? GetViewport().GetTexture().GetImage() : null;
        if (_overlay != null) _overlay.Visible = true;
        return image;
    }

    /// <summary>Cheap content hash, sampled rather than complete — it only has to spot equality.</summary>
    private static ulong Hash(Image image)
    {
        var data = image.GetData();
        ulong h = 14695981039346656037;

        for (int i = 0; i < data.Length; i += 997)
        {
            h ^= data[i];
            h *= 1099511628211;
        }

        return h;
    }

    /// <summary>
    /// The on-screen progress panel.
    ///
    /// <para>
    /// An export can run for ten minutes before the first frame while terrain loads, and a window
    /// that shows a frozen scene and nothing else is indistinguishable from a hang. The window
    /// title alone was not enough — this is in front of you.
    /// </para>
    /// </summary>
    private void BuildOverlay()
    {
        _overlay?.QueueFree();

        _overlay = new CanvasLayer { Name = "ExportProgress", Layer = 50 };

        var panel = new PanelContainer
        {
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1, AnchorBottom = 1,
            OffsetLeft = -260, OffsetRight = 260, OffsetTop = -96, OffsetBottom = -20,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.05f, 0.07f, 0.92f),
            ContentMarginLeft = 18, ContentMarginRight = 18,
            ContentMarginTop = 12, ContentMarginBottom = 12,
        };
        style.SetCornerRadiusAll(6);
        panel.AddThemeStyleboxOverride("panel", style);
        _overlay.AddChild(panel);

        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);
        panel.AddChild(rows);

        var title = new Label { Text = "Recording", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.AddThemeColorOverride("font_color", new Color(0.98f, 0.72f, 0.10f));
        rows.AddChild(title);

        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.0001, ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 14) };
        rows.AddChild(_bar);

        _readout = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _readout.AddThemeFontSizeOverride("font_size", 13);
        _readout.AddThemeColorOverride("font_color", new Color(0.80f, 0.84f, 0.88f));
        rows.AddChild(_readout);

        var hint = new Label { Text = "Esc to cancel", HorizontalAlignment = HorizontalAlignment.Center };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", new Color(0.5f, 0.54f, 0.6f));
        rows.AddChild(hint);

        AddChild(_overlay);
    }

    private void RefreshOverlay()
    {
        if (_bar == null || _readout == null) return;

        _bar.Value = _phase == Phase.Warmup ? 0 : Progress;

        if (_phase == Phase.Warmup)
        {
            _readout.Text = $"loading terrain… {_waiting:F0}s of {WarmupSeconds:F0}s "
                + $"({_chunks.SettleReport(_camera.GlobalPosition)})";
            return;
        }

        if (_phase == Phase.Encoding)
        {
            _readout.Text = "encoding video…";
            return;
        }

        // Rate from frames actually captured, so the estimate reflects the terrain waits rather
        // than the render time — those waits are most of an export.
        double perFrame = _frame > 0 ? _elapsed / _frame : 0;
        string eta = perFrame > 0
            ? TimeSpan.FromSeconds(perFrame * (_total - _frame)).ToString(@"hh\:mm\:ss")
            : "--:--:--";

        _readout.Text = $"frame {_frame} / {_total}   ·   {Progress:P0}   ·   {eta} left";
    }

    private void UpdateTitle() =>
        DisplayServer.WindowSetTitle($"UnitSport — exporting {Progress:P0} ({_frame}/{_total})");

    /// <summary>
    /// Hands the frames to ffmpeg if it is on PATH, and writes the command either way.
    ///
    /// <para>
    /// The script is written even when the encode runs, because it is the record of exactly what
    /// produced the file — and the escape hatch when someone wants a different codec, bitrate or
    /// container without re-rendering three thousand frames.
    /// </para>
    /// </summary>
    private Phase StartEncode()
    {
        // The pipe has been encoding all along; all that is left is to close its stdin and let
        // ffmpeg finish writing the container.
        if (!_pngFallback && _frames != null)
        {
            _frames.Writer.TryComplete();
            Status = "encoding…";
            DisplayServer.WindowSetTitle("UnitSport — encoding video");
            GD.Print($"[export] {_frame} frames sent, finishing {_videoPath}");
            return Phase.Encoding;
        }

        _videoPath = System.IO.Path.Combine(_directory, "run.mp4");
        string pattern = System.IO.Path.Combine(_directory, "frame_%05d.png");

        var arguments = new[]
        {
            "-y", "-framerate", _fps.ToString(),
            "-i", pattern,
            "-c:v", "libx264", "-preset", "slow", "-crf", "18",
            // yuv420p is what players and browsers actually accept, and it needs even
            // dimensions — the pad keeps an odd window size from failing at the last step
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p",
            _videoPath,
        };

        WriteScript(arguments, piped: false);

        if (!HasFfmpeg())
        {
            GD.Print($"[export] {_frame} frames written. ffmpeg was not found on PATH — "
                     + "run encode.bat in that folder to make the video.");
            Status = $"{_frame} frames (no ffmpeg)";
            Release();
            return Phase.Finished;
        }

        _encodePid = OS.CreateProcess(UnitSport.Core.BundledTools.Resolve("ffmpeg"), arguments);
        if (_encodePid <= 0)
        {
            GD.PushError("[export] could not start ffmpeg; the frames and encode.bat are still there");
            Status = $"{_frame} frames (encode failed to start)";
            Release();
            return Phase.Finished;
        }

        Status = "encoding…";
        DisplayServer.WindowSetTitle("UnitSport — encoding video");
        GD.Print($"[export] {_frame} frames captured, encoding to {_videoPath}");
        return Phase.Encoding;
    }

    /// <summary>Closes the raw pipe, killing ffmpeg if the export was cancelled mid-run.</summary>
    private void ClosePipe()
    {
        if (_ffmpeg == null) return;

        try
        {
            _frames?.Writer.TryComplete();
            _writer?.Wait(TimeSpan.FromSeconds(30));
            if (!_ffmpeg.WaitForExit(30_000)) _ffmpeg.Kill(entireProcessTree: true);
        }
        catch (Exception e) { GD.PushWarning($"[export] closing the encoder: {e.Message}"); }

        _ffmpeg.Dispose();
        _ffmpeg = null;
        _frames = null;
        _writer = null;
    }

    private void FinishEncode()
    {
        bool ok = Godot.FileAccess.FileExists(_videoPath);
        if (_ffmpeg is { ExitCode: not 0 })
        {
            ok = false;
            GD.PushError($"[export] ffmpeg exited {_ffmpeg.ExitCode}\n{FfmpegLog()}");
        }
        GD.Print(ok
            ? $"[export] done: {_videoPath}"
            : $"[export] ffmpeg finished but {_videoPath} is missing — see encode.bat");
        if (_timeouts > 0)
            GD.Print($"[export] {_timeouts} frame(s) captured before terrain finished loading");
        GD.Print(_duplicates == 0
            ? "[export] no duplicate frames"
            : $"[export] {_duplicates} DUPLICATE frame(s) survived a retry - the video will stutter there");

        Status = ok ? "video written" : "encode failed";
        Release();

        // "--export" runs unattended, so there is nobody to see the HUD go back to normal
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--export") >= 0) GetTree().Quit(ok ? 0 : 1);
    }

    /// <summary>
    /// Writes down the exact command that produced the video.
    ///
    /// <para>
    /// With a PNG sequence this is a runnable script and the escape hatch for a different codec
    /// without re-rendering. With the pipe it is a record only — the frames were never on disk to
    /// re-encode — so it says so rather than leaving a script that reads from stdin and hangs.
    /// </para>
    /// </summary>
    private void WriteScript(string[] arguments, bool piped)
    {
        string quoted = string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        string script = System.IO.Path.Combine(_directory, piped
            ? "encode-command.txt"
            : OS.GetName() == "Windows" ? "encode.bat" : "encode.sh");

        using var file = Godot.FileAccess.Open(script, Godot.FileAccess.ModeFlags.Write);
        if (file == null) return;

        if (piped)
        {
            file.StoreLine("# Frames were streamed straight into ffmpeg and never written to disk,");
            file.StoreLine("# so this is a record of the encode, not a script you can re-run.");
            file.StoreLine($"ffmpeg {quoted}");
            return;
        }

        if (OS.GetName() != "Windows") file.StoreLine("#!/bin/sh");
        file.StoreLine($"ffmpeg {quoted}");
    }

    private static bool HasFfmpeg()
    {
        var output = new Godot.Collections.Array();
        return OS.Execute(UnitSport.Core.BundledTools.Resolve("ffmpeg"), new[] { "-version" }, output) == 0;
    }

    /// <summary>
    /// What the export actually cost the loader, which is the number this whole path is tuned
    /// against.
    ///
    /// <para>
    /// Printed rather than inferred because every claim about it is easy to get wrong by
    /// arithmetic: the split between full and coarse tiles says whether the LOD rings are reading
    /// what they should, and the cache hit rate says whether a route that doubles back is paying
    /// for its own ground twice.
    /// </para>
    /// </summary>
    private void ReportLoadStats()
    {
        var (full, coarse, bytes) = _chunks.LoadStats;
        string line = $"[export] tiles read: {full} full + {coarse} coarse "
            + $"= {bytes / 1048576.0:F0} MB";

        if (full + coarse > 0)
        {
            // what the same rings would have cost with no companion tile
            long naive = (full + coarse) * (ChunkFormat.GridSize * ChunkFormat.GridSize * 2L + 32);
            line += $" (was {naive / 1048576.0:F0} MB, {(double)naive / Math.Max(1, bytes):F1}x)";
        }

        if (_chunks.Source is CachingChunkSource cache)
            line += $"; cache {cache.HitRate:P0} hit over {cache.CachedEntries} entries, "
                + $"{cache.CachedBytes / 1048576.0:F0} MB";

        if (_prefetch != null) line += $"; prefetched {_prefetch.Prefetched}/{_prefetch.Total}";

        GD.Print(line);
        GD.Print($"[export] worker time: {_chunks.BuildTimeReport()}");
    }

    /// <summary>Gives the clock, the camera and the window title back.</summary>
    private void Release()
    {
        if (_frame > 0 || _phase == Phase.Warmup) ReportLoadStats();
        _phase = Phase.Finished;
        SetProcess(false);
        _chunks.OfflineMode = false;
        _prefetch?.Stop();
        _prefetch = null;
        ClosePipe();
        _overlay?.QueueFree();
        _overlay = null;
        _bar = null;
        _readout = null;
        _race.SetProcess(true);
        _camera.SetProcess(true);
        DisplayServer.WindowSetTitle("UnitSport Switzerland");
        Completed?.Invoke();
    }
}
