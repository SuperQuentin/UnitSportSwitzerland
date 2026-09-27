using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Gpx;

/// <summary>
/// Owns the race: the shared clock, its runners, the course ribbon, camera and HUD.
///
/// The HUD exists from the start rather than appearing with the first track, so the
/// controls are discoverable before anything is loaded.
/// </summary>
public partial class GpxSession : Node
{
    private ChunkManager _chunks = null!;
    private WorldOrigin _origin = null!;
    private Camera3D _previousCamera = null!;

    private RacePlayback _race = null!;
    private PlaybackCamera _camera = null!;
    private PlaybackHud _hud = null!;
    private TrackRibbon? _ribbon;

    /// <summary>
    /// How solid the course line is drawn, 0 to 1. Held here rather than on the ribbon because
    /// <see cref="RefreshRibbon"/> throws the ribbon away and builds a new one whenever the snap
    /// is toggled or the focus changes - the setting has to outlive the node it applies to.
    /// </summary>
    private float _ribbonOpacity = 1f;
    private LensLayer _lens = null!;
    private FileDialog? _dialog;
    private FileDialog? _exportDialog;
    private VideoExporter _exporter = null!;

    public static GpxSession Create(ChunkManager chunks, WorldOrigin origin, Camera3D previous) => new()
    {
        Name = "GpxSession",
        _chunks = chunks,
        _origin = origin,
        _previousCamera = previous,
    };

    /// <summary>Raised when the player asks to leave replay — HUD button or finish banner.</summary>
    public event Action? ExitRequested;

    public bool Active { get; private set; }

    public override void _Ready()
    {
        _race = RacePlayback.Create(_chunks, _origin);
        // matching finishes on its own schedule, so the course line has to be told
        _race.SnapChanged += RefreshRibbon;
        AddChild(_race);

        _camera = PlaybackCamera.Create(_race, _chunks);
        _camera.ModeChanged += EnsureCinemaPlan;
        AddChild(_camera);

        // Snapping changes the course, and the course is what the director analysed. Without this
        // the timeline keeps cutting to corners and climbs that belong to the other variant.
        _race.SnapChanged += EnsureCinemaPlan;

        _exporter = VideoExporter.Create(_race, _camera, _chunks);
        _exporter.Completed += EndExport;
        AddChild(_exporter);

        _hud = PlaybackHud.Create(_race, _camera);
        _hud.Exporter = _exporter;
        _hud.AddRequested += ShowPicker;
        _hud.ClearRequested += ClearRace;
        _hud.ExitRequested += () => ExitRequested?.Invoke();
        _hud.ExportRequested += fps =>
        {
            if (_exporter.Running) _exporter.Cancel();
            else ShowExportPicker(fps);
        };
        _hud.PathOpacityChanged += SetRibbonOpacity;
        AddChild(_hud);

        // Added after the HUD so it sits earlier in the tree, but the ordering that matters is
        // its Layer (5) against the HUD's (10): the post-process must bend the world and not the
        // controls drawn over it.
        _lens = LensLayer.Create();
        AddChild(_lens);
        _hud.LensCycleRequested += () => _hud.LensName = _lens.Cycle().Name;

        SetActive(false);
    }

    /// <summary>
    /// Enters replay. With nothing loaded the file picker opens straight away, so choosing
    /// the mode from the menu leads somewhere instead of showing an empty HUD.
    /// </summary>
    public void Begin()
    {
        SetActive(true);
        if (_race.Runners.Count == 0) ShowPicker();
        else _camera.Current = true;
    }

    /// <summary>Leaves replay: drops the runners and hands the camera back.</summary>
    public void End()
    {
        ClearRace();
        SetActive(false);
    }

    /// <summary>
    /// Shows or hides everything this mode owns. Input handling goes with it, or G would
    /// still open the GPX picker while exploring.
    /// </summary>
    private void SetActive(bool active)
    {
        Active = active;
        _hud.Visible = active;
        // the lens belongs to replay; leaving it on would distort Explore as well
        _lens.Visible = active;
        _hud.SetProcess(active);
        SetProcessUnhandledInput(active);
        if (!active && _dialog != null) _dialog.Hide();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

        // While exporting, the HUD is hidden and the clock belongs to the exporter, so Esc has
        // to cancel rather than open the main menu — and it must be consumed here, or ClientWorld
        // opens the menu on the same press.
        if (_exporter.Running)
        {
            if (key.PhysicalKeycode == Key.Escape)
            {
                _exporter.Cancel();
                GetViewport().SetInputAsHandled();
            }
            return;
        }

        switch (key.PhysicalKeycode)
        {
            case Key.G:
                ShowPicker();
                break;
            case Key.Space when _race.Runners.Count > 0:
                _race.TogglePlay();
                break;
            case Key.C when _race.Runners.Count > 0:
                _camera.AdoptCurrentOrientation();
                _camera.CycleMode();
                break;
            case Key.H:
                _hud.ToggleUi();
                break;
            case Key.F when _race.Runners.Count > 0:
                _race.CycleFocus();
                RefreshRibbon();
                break;
            case Key.R when _race.Runners.Count > 0 && !_race.Matching:
                _race.SetSnapToRoads(!_race.SnapToRoads);
                RefreshRibbon();
                break;
        }
    }

    /// <summary>
    /// Asks where the video should go, then starts the export.
    ///
    /// <para>
    /// The HUD is hidden for the duration — it is drawn into the same viewport the frames are
    /// grabbed from, so leaving it up would burn the leaderboard and the scrubber into every
    /// frame. Progress goes to the window title instead, and <b>Esc</b> cancels.
    /// </para>
    /// </summary>
    private void ShowExportPicker(int fps)
    {
        if (_exportDialog == null)
        {
            _exportDialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenDir,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "Choose a folder for the exported video",
                Size = new Vector2I(860, 580),
            };
            _exportDialog.DirSelected += directory => StartExport(directory, _pendingFps);
            AddChild(_exportDialog);
        }

        _pendingFps = fps;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _exportDialog.PopupCentered();
    }

    private int _pendingFps = 30;

    private void StartExport(string directory, int fps)
    {
        // its own folder per run, so a second export never interleaves with the first one's
        // frames — ffmpeg reads a numbered sequence and would silently splice them
        string folder = System.IO.Path.Combine(directory,
            $"gpx_{DateTime.Now:yyyyMMdd_HHmmss}");

        if (!_exporter.Begin(folder, fps))
        {
            GD.PushError($"[export] {_exporter.Status}");
            return;
        }

        _hud.Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void EndExport()
    {
        _hud.Visible = Active;
        GD.Print($"[export] {_exporter.Status}");
    }

    private bool _planning;
    private string _plannedFor = "";

    /// <summary>
    /// Analyses the focused run so the director knows what is coming.
    ///
    /// <para>
    /// Off the main thread, because it reads the map tiles along the whole route — and only once
    /// per track, since nothing about a recording changes while you watch it. Until it lands the
    /// mode still works; it simply has no timeline to cut ahead of, so it plays as a chase.
    /// </para>
    /// </summary>
    private async void EnsureCinemaPlan()
    {
        if (_camera.Mode != CameraMode.Cinema || _planning) return;

        var focused = _race.Focused;
        var source = _chunks.Source;
        if (focused == null || source == null) return;

        string key = $"{focused.Track.Name}|{focused.UseSnapped}";
        if (key == _plannedFor) return;

        _planning = true;
        _plannedFor = key;

        try
        {
            var track = focused.Active;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var corridor = await Cinema.Corridor.LoadAsync(track, source, _chunks.AvailableTiles)
                .ConfigureAwait(true);
            var profile = Cinema.TrackAnalyser.Analyse(track, _origin, corridor);

            // seeded from the track, so the same run always cuts the same way
            ulong seed = (ulong)track.Name.GetHashCode() ^ (ulong)track.Points.Count;
            _camera.SetCinemaPlan(profile.Events, seed);

            GD.Print($"[cinema] {track.Name}: {profile.Events.Count} events, "
                + $"{profile.RevisitShare:P0} revisited ground, ready in {clock.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            GD.PushError($"[cinema] could not analyse the run: {e.Message}");
            _plannedFor = "";
        }
        finally
        {
            _planning = false;
        }
    }

    public void ShowPicker()
    {
        if (_dialog == null)
        {
            _dialog = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFiles,   // several at once = a race
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "Add GPX tracks (select more than one to race them)",
                Size = new Vector2I(860, 580),
            };
            _dialog.AddFilter("*.gpx", "GPX tracks");
            _dialog.FilesSelected += paths => { foreach (string p in paths) Load(p); };
            _dialog.FileSelected += Load;
            AddChild(_dialog);
        }
        // the fly-cam captures the mouse; the dialog needs it back
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _dialog.PopupCentered();
    }

    /// <summary>Adds one track as another ghost in the current race.</summary>
    public void Load(string path)
    {
        GpxTrack track;
        try
        {
            track = GpxParser.Parse(path);
        }
        catch (Exception e)
        {
            GD.PushError($"[gpx] could not read {path}: {e.Message}");
            return;
        }

        if (track.Points.Count < 2)
        {
            GD.PushError($"[gpx] {path} has too few points to play back");
            return;
        }

        // InvariantCulture: this is a fr-CH machine, where the default would log "8,05 km"
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        GD.Print($"[gpx] +{track.Name}: {track.Points.Count} pts, " +
                 $"{(track.Length / 1000).ToString("0.00", inv)} km, " +
                 $"{TimeSpan.FromSeconds(track.Duration):hh\\:mm\\:ss}, " +
                 $"ascent {track.Ascent.ToString("0", inv)} m, timing={track.HasTiming}");

        // Where the track is, in both frames. The LV95 pair says which tiles have to exist;
        // the world one is what --shot needs to point a camera at it.
        double midE = (track.Points.Min(p => p.E) + track.Points.Max(p => p.E)) * 0.5;
        double midN = (track.Points.Min(p => p.N) + track.Points.Max(p => p.N)) * 0.5;
        var centre = _origin.ToWorld(midE, midN, 0);
        GD.Print($"[gpx]  centre LV95 {midE.ToString("0", inv)}/{midN.ToString("0", inv)}"
                 + $"  world {centre.X.ToString("0", inv)},{centre.Z.ToString("0", inv)}");

        try
        {
            bool first = _race.Runners.Count == 0;
            _race.Add(track);

            if (first)
            {
                _camera.Current = true;
                _race.Seek(0);

                // "--snap" turns road matching on as soon as there is something to match, which
                // is how it gets verified headlessly alongside --gpx and --shot
                if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--snap") >= 0)
                    _race.SetSnapToRoads(true);

                // "--speed <n>" sets the playback multiplier, which is what the cut-pacing
                // check varies - the whole claim being tested is that shot length does not
                // depend on it.
                var sargs = OS.GetCmdlineUserArgs();
                int si = Array.IndexOf(sargs, "--speed");
                if (si >= 0 && si + 1 < sargs.Length && double.TryParse(sargs[si + 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double sp))
                    _race.Speed = sp;

                // "--path <0..100>" sets the course line's opacity, so the setting can be
                // screenshotted and exported headlessly like the lens.
                int pi = Array.IndexOf(sargs, "--path");
                if (pi >= 0 && pi + 1 < sargs.Length && float.TryParse(sargs[pi + 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float op))
                    SetRibbonOpacity(op / 100f);

                // "--lens <n>" picks a lens by index, which is how the optics get screenshotted
                // with --shot and compared against the undistorted frame.
                var user = OS.GetCmdlineUserArgs();
                int li = Array.IndexOf(user, "--lens");
                if (li >= 0 && li + 1 < user.Length && int.TryParse(user[li + 1], out int lens))
                    _hud.LensName = _lens.Select(lens).Name;

                // "--cinemamode" starts in Absolute Cinema, which is how it gets exported and
                // screenshotted without anyone pressing C four times.
                if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--cinemamode") >= 0)
                {
                    _camera.Mode = CameraMode.Cinema;
                    EnsureCinemaPlan();
                }

                // "--forceshot <name>" pins Absolute Cinema to one shot, matching the HUD's
                // override list — how a single shot gets screenshotted or exported on its own
                // without hoping the director happens to cut to it in time.
                int fi = Array.IndexOf(user, "--forceshot");
                if (fi >= 0 && fi + 1 < user.Length) _camera.ForcedCinemaShot = user[fi + 1];

                // "--bubble off" turns off the zoom bubble, for a screenshot comparison against
                // one with it on. "--arrow" is the old name, from when it was a "HERE" marker.
                foreach (var flag in new[] { "--bubble", "--arrow" })
                    if (Array.IndexOf(user, flag) is var bi && bi >= 0 && bi + 1 < user.Length)
                        _camera.ZoomBubbleEnabled = user[bi + 1] != "off";

                int ci = Array.IndexOf(sargs, "--cinemastats");
                if (ci >= 0 && ci + 1 < sargs.Length && double.TryParse(sargs[ci + 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double secs))
                {
                    _statsFor = secs;
                    SetProcess(true);
                }

                // "--export <dir>[,fps][,speed]" runs the whole thing from the command line,
                // which is both how it gets verified and how you would batch a folder of tracks
                // overnight without sitting through the render.
                var args = OS.GetCmdlineUserArgs();
                int i = Array.IndexOf(args, "--export");
                if (i >= 0 && i + 1 < args.Length) Callable.From(() =>
                {
                    var parts = args[i + 1].Split(',');
                    int fps = parts.Length > 1 && int.TryParse(parts[1], out int f) ? f : 30;
                    if (parts.Length > 2 && double.TryParse(parts[2],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double s))
                        _race.Speed = s;
                    if (parts.Length > 3 && double.TryParse(parts[3],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double w))
                        _exporter.WarmupSeconds = w;
                    StartExport(parts[0], fps);
                }).CallDeferred();
            }
            RefreshRibbon();
        }
        catch (Exception e)
        {
            GD.PushError($"[gpx] failed to add {track.Name}: {e}");
        }
    }

    /// <summary>
    /// "--cinemastats &lt;seconds&gt;": runs Absolute Cinema for a stretch of SCREEN time and
    /// reports how often it cut, then quits.
    ///
    /// <para>
    /// The one number that says whether the pacing is right. Shot durations are held in screen
    /// seconds, so the same run at 1x and at 32x should cut about the same number of times in the
    /// same wall-clock minute - and before the lead cap and the one-cut-per-event rule that was
    /// wildly untrue. Eyeballing "it feels too fast" cannot distinguish a director that cuts
    /// twice as often from one that cuts twenty times as often.
    /// </para>
    /// </summary>
    private double _statsFor;
    private double _statsElapsed;

    public override void _Process(double delta)
    {
        if (_statsFor <= 0) return;

        _statsElapsed += delta;
        if (_statsElapsed < _statsFor) return;

        GD.Print($"[cinemastats] {_statsElapsed:F1} s screen at {_race.Speed:0.##}x: "
            + $"{_camera.CinemaCuts} cuts, {_camera.CinemaRejected} placements rejected, "
            + $"{_statsElapsed / Math.Max(1, _camera.CinemaCuts):F2} s per shot");
        GetTree().Quit();
    }

    private void ClearRace()
    {
        _race.Clear();
        _ribbon?.QueueFree();
        _ribbon = null;
        if (IsInstanceValid(_previousCamera)) _previousCamera.Current = true;
    }

    /// <summary>
    /// Camera to restore on the way out. Explore mode may have swapped between the fly cam
    /// and the on-foot camera since this session was created.
    /// </summary>
    public void SetReturnCamera(Camera3D camera) => _previousCamera = camera;

    /// <summary>
    /// The ribbon shows the focused runner's course. Ghosts usually share a route, so
    /// drawing one ribbon per runner would only stack coincident geometry.
    /// </summary>
    private void RefreshRibbon()
    {
        var focused = _race.Focused;
        if (focused == null) return;

        // compare against the *active* variant: flipping the road snap changes the course under
        // the runner, and a ribbon still drawn from the raw fixes would contradict it
        var course = focused.Active;
        if (_ribbon != null && _ribbon.Track == course) return;

        _ribbon?.QueueFree();
        _ribbon = TrackRibbon.Create(course, _chunks, _origin);
        _ribbon.Opacity = _ribbonOpacity;
        AddChild(_ribbon);
    }

    /// <summary>Fades the course line out, down to nothing. Survives a ribbon rebuild.</summary>
    private void SetRibbonOpacity(float opacity)
    {
        _ribbonOpacity = Mathf.Clamp(opacity, 0f, 1f);
        if (_ribbon != null) _ribbon.Opacity = _ribbonOpacity;
    }
}
