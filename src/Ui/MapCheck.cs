using Godot;
using UnitSport.Core;
using UnitSport.Map;
using UnitSport.Terrain.Format;

namespace UnitSport.Ui;

/// <summary>
/// <c>godot --headless --path . -- --mapcheck</c>: opens the map screen and drives it the way a
/// player would — search a town and jump to it, draw a rectangle, paint and erase with the brush,
/// zoom, then leave — asserting after each step that the selection and the view really changed.
/// Non-zero exit on the first step that fails.
///
/// <para>
/// It runs headless, so the relief picture never finishes drawing and is not what is being checked.
/// What is: that the screen builds against the embedded country map with no data folder, that the
/// selection tools do what their buttons say, and that the page leaves cleanly.
/// </para>
/// </summary>
public partial class MapCheck : Node
{
    private readonly GameShell _shell;
    private int _step;
    private double _wait;
    private bool _acted;
    private int _selectedAfterRect;

    public MapCheck(GameShell shell)
    {
        _shell = shell;
        Name = "MapCheck";
    }

    public static bool Requested() => CmdArgs.Has("--mapcheck");

    private MapScreen? Map => _shell.Top as MapScreen;
    private Selection? Picked => Map?.Picked;

    private (Action Act, Func<bool> Ok, double Timeout, string Name)[] Steps => new (Action, Func<bool>, double, string)[]
    {
        (() => { }, () => _shell.Top is TitleScreen, 5, "title at boot"),
        (() => _shell.Push(MapScreen.Create()), () => Map != null && Picked != null, 5,
            "the map opens on the embedded country map"),

        // the country map itself: without these the rest could pass on an empty map
        (() => { }, () => Map!.Country.TileCount > 40_000, 1,
            "the country map has the whole country in it"),
        (() => { }, () => Map!.Country.Cantons.Count == 26, 1, "26 cantons"),
        (() => { }, () => Map!.Country.Search("Zermatt", 3).Count > 0, 1, "the place search finds Zermatt"),

        (() => Map!.SearchAndJump("Zermatt"), () => Map!.Canvas.ViewCentre.E is > 2_600_000 and < 2_640_000, 2,
            "picking a search result moves the view to it"),

        (() => Map!.SelectRect(new TileId(2620, 1090), new TileId(2629, 1099)),
            () => Picked!.Count == 100, 2, "a 10x10 rectangle selects 100 tiles"),
        (() => _selectedAfterRect = Picked!.Count, () => true, 1, "remember it"),

        (() => Map!.Paint(new TileId(2640, 1100)), () => Picked!.Count == _selectedAfterRect + 1, 2,
            "the brush adds one tile"),
        (() => Map!.Paint(new TileId(2640, 1100), erase: true), () => Picked!.Count == _selectedAfterRect, 2,
            "erase takes it away again"),

        (() => Map!.Canvas.Zoom(+3), () => Map!.Canvas.Scale > 2, 1, "zooming in changes the scale"),

        (() => Map!.ClearSelection(), () => Picked!.Count == 0, 2, "clear empties the selection"),
        (Esc, () => _shell.Top is TitleScreen, 2, "Esc goes back to the title"),

        // the landing role: the same screen, with a marker and a confirmation (#515 phase 5)
        (() => _shell.Push(MapScreen.CreateLanding((SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N),
                landing => _landed = landing)),
            () => Map?.Canvas.Landing != null, 5, "the landing map opens with a marker on Riddes"),
        (() => { }, () => Math.Abs(Map!.Canvas.Landing!.Value.E - SpawnPoint.DefaultLv95E) < 1, 1,
            "the marker is where it was asked for"),
        (() => Map!.MoveLanding(2_600_000, 1_200_000), () => Math.Abs(Map!.Canvas.Landing!.Value.E - 2_600_000) < 1, 2,
            "the marker moves"),
        (() => Map!.ConfirmLanding(), () => _landed is { } l && Math.Abs(l.E - 2_600_000) < 1, 2,
            "confirming reports where to land"),

        // the plumbing the chosen landing actually travels down, asserted rather than assumed
        (() => { }, () => SpawnPoint.ParseTarget(new WorldLaunch { Landing = (2_600_000, 1_200_000) })
                is var (e, n) && Math.Abs(e - 2_600_000) < 1 && Math.Abs(n - 1_200_000) < 1, 1,
            "a launch carrying a landing spawns there"),
        (() => { }, () => SpawnPoint.ParseTarget(new WorldLaunch()) is var (e, _)
                && Math.Abs(e - SpawnPoint.DefaultLv95E) < 1, 1,
            "a launch with no landing still spawns at Riddes"),
    };

    private (double E, double N)? _landed;

    public override void _Process(double delta)
    {
        var steps = Steps;
        if (_step >= steps.Length) return;
        var (act, ok, timeout, name) = steps[_step];
        if (!_acted)
        {
            try
            {
                act();
            }
            catch (Exception e)
            {
                GD.Print($"[mapcheck] FAIL {name}: {e.GetType().Name}: {e.Message}");
                GD.Print("[mapcheck] RESULT: FAILED");
                GetTree().Quit(1);
                _step = steps.Length;
                return;
            }
            _acted = true;
            _wait = 0;
            return;
        }

        _wait += delta;
        if (ok())
        {
            GD.Print($"[mapcheck] ok   {name} ({_wait:F1} s)");
            _acted = false;
            if (++_step == steps.Length)
            {
                GD.Print("[mapcheck] RESULT: ok");
                GetTree().Quit(0);
            }
        }
        else if (_wait > timeout)
        {
            GD.Print($"[mapcheck] FAIL {name} (top={_shell.Top?.Name ?? "none"}, selected={Picked?.Count ?? -1})");
            GD.Print("[mapcheck] RESULT: FAILED");
            GetTree().Quit(1);
            _step = steps.Length;
        }
    }

    private static void Esc()
    {
        foreach (bool down in new[] { true, false })
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = down });
    }
}
