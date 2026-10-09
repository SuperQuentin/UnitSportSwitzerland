using Godot;
using UnitSport.Player;

namespace UnitSport.Core;

/// <summary>
/// <c>--seat kart|car:N|truck:N|moto:N|...</c> (the names of <c>--ride</c>, <see cref="RideProbe.KindNamed"/>):
/// start the session already in that ride, with the controls left to the player. Unlike
/// <c>--ride</c>, a probe with a body of its own that holds the throttle and quits, this seats the
/// real local player, facing <c>--heading</c> (a compass bearing) if given. With <c>--at E,N</c> on
/// a road, that is a drive from the first frame.
///
/// <para>
/// Run behind the loading screen, after <see cref="GroundStart"/> has put the body on open ground
/// (a <c>--seat</c> run starts on foot, as the menus' Explore does, not in the fly camera): the
/// screen says what it is waiting for, and nothing is played before the seat is taken. Gives up
/// after 30 s with a warning and lets the session go on on foot.
/// </para>
/// </summary>
public sealed class SeatStart
{
    private readonly FootPlayer _player;
    private readonly RideKind _kind;
    private double _waited;
    private bool _faced;

    /// <summary>The ride <c>--seat</c> asks for, or null.</summary>
    public static RideKind? Requested => CmdArgs.Value("--seat") is { } name ? RideProbe.KindNamed(name) : null;

    public bool Done { get; private set; }

    /// <summary>What the loading screen says meanwhile.</summary>
    public string Status { get; }

    public SeatStart(FootPlayer player, RideKind kind)
    {
        _player = player;
        _kind = kind;
        Status = $"Getting you into the {Rideable.Create(kind)?.Label ?? kind.ToString()}…";
    }

    /// <summary>One loading frame: true once seated (or given up).</summary>
    public bool Step(double delta)
    {
        if (Done) return true;
        _waited += delta;
        if (_waited > 30 || !GodotObject.IsInstanceValid(_player))
        {
            GD.PushWarning($"[seat] could not seat the player in {_kind} within 30 s; on foot instead");
            Done = true;
            return true;
        }
        // standing and still: SetRide refuses a body in the air or on the move
        if (!_player.IsOnFloor() || _waited < 0.3) return false;
        // turned once: placing the body asks for its placement again, so the ride waits a frame or two
        if (!_faced && CmdArgs.Float("--heading") is float bearing)
        {
            _faced = true;
            _player.PlaceAt(_player.GlobalPosition, -Mathf.DegToRad(bearing));
            return false;
        }
        if (!_player.SetRide(_kind)) return false;
        GD.Print($"[seat] in {_kind} after {_waited:F1} s");
        Done = true;
        return true;
    }
}
