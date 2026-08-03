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
    private FileDialog? _dialog;

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

        _camera = PlaybackCamera.Create(_race);
        AddChild(_camera);

        _hud = PlaybackHud.Create(_race, _camera);
        _hud.AddRequested += ShowPicker;
        _hud.ClearRequested += ClearRace;
        _hud.ExitRequested += () => ExitRequested?.Invoke();
        AddChild(_hud);

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
        _hud.SetProcess(active);
        SetProcessUnhandledInput(active);
        if (!active && _dialog != null) _dialog.Hide();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;

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
            }
            RefreshRibbon();
        }
        catch (Exception e)
        {
            GD.PushError($"[gpx] failed to add {track.Name}: {e}");
        }
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
        AddChild(_ribbon);
    }
}
