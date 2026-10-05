using Godot;
using UnitSport.Core;

namespace UnitSport.Ui;

/// <summary>
/// Play solo: two cards — Explore the country, or replay GPX tracks as a race of ghosts — and a
/// strip of quick options for the session about to start (time of day, traffic, trains), which
/// are the same saved settings the World tab holds.
/// </summary>
public partial class SoloScreen : Screen
{
    private readonly List<string> _tracks = new();
    private Label _trackList = null!;
    private Button _startGpx = null!;
    private Button _explore = null!;

    public static SoloScreen Create() => new() { Name = "Solo" };

    public override void _Ready()
    {
        var (body, _) = Framed("Play solo", "Offline, on this machine", new Vector2(900, 560));

        var cards = UiKit.HBox(16);
        body.AddChild(cards);

        // --- Explore
        var exploreBody = UiKit.VBox(8);
        exploreBody.AddChild(UiKit.Text("Explore", UiTheme.FontHeading, UiTheme.Text, bold: true));
        exploreBody.AddChild(UiKit.Text(InputHints.Format(
            "Fly over the real Swiss terrain, drop on foot with {toggle_mode}, ride, drive or fly anything. "
            + "{teleport} searches a town and takes you there."), UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        exploreBody.AddChild(UiKit.Spacer(expand: true));
        _explore = UiKit.Button("Start exploring", primary: true);
        // the map first, to pick where to land (#515)
        _explore.Pressed += () => Shell.LaunchVia(new WorldLaunch { Mode = GameMode.Explore });
        exploreBody.AddChild(_explore);
        var exploreCard = UiKit.Card(exploreBody);
        exploreCard.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        exploreCard.CustomMinimumSize = new Vector2(0, 210);
        cards.AddChild(exploreCard);

        // --- GPX replay
        var gpxBody = UiKit.VBox(8);
        gpxBody.AddChild(UiKit.Text("GPX replay", UiTheme.FontHeading, UiTheme.Text, bold: true));
        gpxBody.AddChild(UiKit.Text("Replay a recorded ride or run over the terrain. Pick several to race them as ghosts.",
            UiTheme.FontSmall, UiTheme.TextDim, wrap: true));
        _trackList = UiKit.Text("No track chosen", UiTheme.FontSmall, UiTheme.TextFaint, wrap: true);
        gpxBody.AddChild(_trackList);
        gpxBody.AddChild(UiKit.Spacer(expand: true));
        var gpxButtons = UiKit.HBox(8);
        var choose = UiKit.Button("Choose tracks…");
        choose.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        choose.Pressed += () => Shell.Push(GpxPicker.Create(_tracks, picked =>
        {
            _tracks.Clear();
            _tracks.AddRange(picked);
            ShowTracks();
        }));
        gpxButtons.AddChild(choose);
        _startGpx = UiKit.Button("Start replay", primary: true);
        _startGpx.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _startGpx.Pressed += StartReplay;
        gpxButtons.AddChild(_startGpx);
        gpxBody.AddChild(gpxButtons);
        var gpxCard = UiKit.Card(gpxBody);
        gpxCard.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        cards.AddChild(gpxCard);

        // --- quick options for this session
        body.AddChild(UiKit.Spacer(2));
        body.AddChild(UiKit.Section("Session"));
        var s = GameSettings.Current;
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 28);
        body.AddChild(grid);
        var left = UiKit.VBox(2); left.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var right = UiKit.VBox(2); right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        grid.AddChild(left); grid.AddChild(right);
        UiKit.SliderRow(left, "Start time", 0, 23.5, 0.5, s.StartHour,
            v => GameSettings.Current.StartHour = (float)v, SettingsScreen.Clock);
        UiKit.SliderRow(left, "Day length", 0, 120, 1, s.DayLengthMinutes,
            v => GameSettings.Current.DayLengthMinutes = (float)v, v => v <= 0 ? "stopped" : $"{v:F0} min");
        UiKit.SliderRow(right, "Traffic", 0, 150, 5, s.TrafficCars,
            v => GameSettings.Current.TrafficCars = (int)v, v => v <= 0 ? "off" : $"{v:F0} cars");
        UiKit.ToggleRow(right, "Trains", s.Trains, on => GameSettings.Current.Trains = on);
        // the sliders are wide in Settings; here two columns share the panel
        foreach (var row in left.GetChildren().Concat(right.GetChildren()).OfType<HBoxContainer>())
            if (row.GetChildCount() > 1 && row.GetChild(1) is Control c) c.CustomMinimumSize = new Vector2(200, 0);

        ShowTracks();
    }

    private void ShowTracks()
    {
        _startGpx.Disabled = _tracks.Count == 0;
        _trackList.Text = _tracks.Count switch
        {
            0 => "No track chosen",
            1 => System.IO.Path.GetFileNameWithoutExtension(_tracks[0]),
            _ => $"{_tracks.Count} tracks: " + string.Join(", ", _tracks.Take(3).Select(System.IO.Path.GetFileNameWithoutExtension))
                 + (_tracks.Count > 3 ? "…" : ""),
        };
        _trackList.AddThemeColorOverride("font_color", _tracks.Count == 0 ? UiTheme.TextFaint : UiTheme.Amber);
    }

    private void StartReplay()
    {
        if (_tracks.Count == 0) return;
        var recent = GameSettings.Current.RecentGpx;
        foreach (string t in _tracks) { recent.Remove(t); recent.Insert(0, t); }
        while (recent.Count > 10) recent.RemoveAt(recent.Count - 1);
        GameSettings.Current.Save();
        Shell.Launch(new WorldLaunch { Mode = GameMode.GpxReplay, GpxPaths = _tracks.ToList() });
    }

    public override void OnShown() => _explore.CallDeferred(Control.MethodName.GrabFocus);
}
