using System.Threading.Tasks;
using Godot;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Ui;

namespace UnitSport.Core;

/// <summary>
/// <c>--seat kart|car:N|truck:N|moto:N|...</c> (the names of <c>--ride</c>, <see cref="RideProbe.KindNamed"/>):
/// start the session already in that ride, on the nearest road, with the controls left to the
/// player. Unlike <c>--ride</c>, a probe with a body of its own that holds the throttle and quits,
/// this seats the real local player.
///
/// <para>
/// A command-line run boots without a loading screen (<see cref="GameShell.Direct"/>), so this puts
/// up its own (<see cref="SeatOverlay"/>) the moment the world exists: it says what it waits for and
/// swallows every input until the seat is taken, and the body is held still meanwhile. Then, once
/// <see cref="GroundStart"/> has the body on open ground: the road nearest the spawn
/// (<see cref="RaceRoute"/>, as the race checks find it), the body stood on it facing along it (the
/// way nearer <c>--heading</c>, a compass bearing, if given), and the ride. Without a road in reach it
/// stays where it is, facing <c>--heading</c>. Gives up after 30 s with a warning, on foot.
/// </para>
/// </summary>
public sealed class SeatStart
{
    private enum Phase { Waiting, Road, Seat }

    private readonly RideKind _kind;
    private readonly SeatOverlay _overlay;
    private FootPlayer? _player;
    private ChunkManager? _chunks;
    private WorldOrigin? _origin;
    private Phase _phase = Phase.Waiting;
    private Task<RaceRoute?>? _road;
    private double _waited, _settle;

    /// <summary>The ride <c>--seat</c> asks for, or null.</summary>
    public static RideKind? Requested => CmdArgs.Value("--seat") is { } name ? RideProbe.KindNamed(name) : null;

    public bool Done { get; private set; }

    /// <summary>Puts the overlay up under <paramref name="host"/> at once; <see cref="Attach"/> gives it the body when there is one.</summary>
    public SeatStart(Node host, RideKind kind)
    {
        _kind = kind;
        string label = Rideable.Create(kind)?.Label ?? kind.ToString();
        _overlay = new SeatOverlay($"Getting into the {label}");
        host.AddChild(_overlay);
        _overlay.Status("Building the world");
    }

    /// <summary>The body is in the world: hold it still and take the seat from here.</summary>
    public void Attach(FootPlayer player, ChunkManager chunks, WorldOrigin origin)
    {
        (_player, _chunks, _origin) = (player, chunks, origin);
        player.WalkControls = Still;
        _phase = Phase.Road;
        _overlay.Status("Finding solid ground");
    }

    private static (Vector3, bool) Still() => (Vector3.Zero, false);

    /// <summary>One frame: true once seated (or given up).</summary>
    public bool Step(double delta)
    {
        if (Done) return true;
        if (_player == null || _chunks == null || _origin == null) return false;
        _waited += delta;
        if (_waited > 30 || !GodotObject.IsInstanceValid(_player))
        {
            GD.PushWarning($"[seat] could not seat the player in {_kind} within 30 s; on foot instead");
            Finish();
            return true;
        }
        if (!_player.IsOnFloor()) return false;

        if (_phase == Phase.Road)
        {
            if (_road == null)
            {
                _overlay.Status("Finding the road");
                var source = _chunks.Source;
                var frame = _origin.Frame;
                var at = _player.GlobalPosition;
                _road = source == null ? Task.FromResult<RaceRoute?>(null) : Task.Run(() => RaceRoute.BuildAsync(source, frame, at));
                return false;
            }
            if (!_road.IsCompleted) return false;
            OntoRoad(_road.IsCompletedSuccessfully ? _road.Result : null);
            _phase = Phase.Seat;
            _settle = 0.3;
            _overlay.Status("Taking the seat");
            return false;
        }

        // a moment on the ground where it was put: SetRide refuses a body in the air or on the move
        if ((_settle -= delta) > 0) return false;
        if (!_player.SetRide(_kind)) return false;
        GD.Print($"[seat] in {_kind} after {_waited:F1} s");
        Finish();
        return true;
    }

    /// <summary>Stands the body on the road's nearest point, facing along it; without one, turns it to <c>--heading</c>.</summary>
    private void OntoRoad(RaceRoute? route)
    {
        float? bearing = CmdArgs.Float("--heading");
        // compass bearing to a world direction: the node faces −Z (north), +X is east
        Vector3? wish = bearing is float b ? new Vector3(Mathf.Sin(Mathf.DegToRad(b)), 0f, -Mathf.Cos(Mathf.DegToRad(b))) : null;
        var at = _player!.GlobalPosition;
        Vector3 facing = wish ?? -_player.GlobalBasis.Z;
        if (route is { Centre.Count: >= 2 })
        {
            route.Follow(_origin!.Frame);
            var along = RaceRoute.Flat(route.Centre[1] - route.Centre[0]);
            if (along.LengthSquared() > 1e-4f)
            {
                along = along.Normalized();
                if (wish is { } w && along.Dot(w) < 0f) along = -along;
                facing = along;
                var road = route.Centre[0];
                float ground = _chunks!.TryGetSurface(road, out float h) ? h : road.Y;
                at = new Vector3(road.X, ground + 0.5f, road.Z);
                GD.Print($"[seat] on the road {MathX.FlatDistance(road, _player.GlobalPosition):F0} m from where the body landed");
            }
        }
        else GD.Print("[seat] no road in reach: seated where the body landed");
        _player.PlaceAt(at, Mathf.Atan2(-facing.X, -facing.Z));
    }

    private void Finish()
    {
        Done = true;
        if (GodotObject.IsInstanceValid(_player)) _player!.WalkControls = null;
        if (GodotObject.IsInstanceValid(_overlay)) _overlay.QueueFree();
    }
}

/// <summary>
/// <see cref="SeatStart"/>'s screen: dark over everything, the ride's name and the step it is on.
/// Every input stops here until it goes, so nothing is played before the seat is taken.
/// </summary>
public partial class SeatOverlay : CanvasLayer
{
    private readonly Label _status = null!;

    public SeatOverlay() { }

    public SeatOverlay(string title)
    {
        Name = "SeatOverlay";
        Layer = 100;
        ProcessMode = ProcessModeEnum.Always;
        var dim = new ColorRect { Color = new Color(0.05f, 0.06f, 0.08f, 0.94f), MouseFilter = Control.MouseFilterEnum.Stop };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(dim);
        var box = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        box.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        box.AddThemeConstantOverride("separation", 14);
        dim.AddChild(box);
        box.AddChild(UiKit.Text(title, UiTheme.FontTitle, align: HorizontalAlignment.Center));
        _status = UiKit.Text("", UiTheme.FontHeading, UiTheme.TextDim, align: HorizontalAlignment.Center);
        box.AddChild(_status);
        box.AddChild(UiKit.Text("Nothing to drive or press until you are in it", UiTheme.FontBody, UiTheme.TextDim, align: HorizontalAlignment.Center));
    }

    public void Status(string text) => _status.Text = text;

    public override void _Input(InputEvent e) => GetViewport().SetInputAsHandled();
}
