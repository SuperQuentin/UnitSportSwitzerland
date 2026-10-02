using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Vehicles;

namespace UnitSport.World;

/// <summary>
/// <c>godot --path . -- --connect 127.0.0.1 --eggcheck ride|watch [--at E,N]</c> — the Africa Twin
/// egg over the network, for two clients on one server. Both print what they see of the egg (its
/// model and position) and of the other players (their ride and position) each second.
/// <c>ride</c> walks up to the egg, gets on, rides 40 m, then both go 70 km away (the Mollendruz
/// tiles) so the site's tile unloads everywhere, the rider gets off there, and both come back:
/// a new egg must be standing there. <c>watch</c> only travels. Times are from the moment this
/// client first sees the egg.
/// </summary>
public partial class EggProbe : Node
{
    private readonly bool _ride;
    private readonly Func<FootPlayer?> _player;
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private double _t = -1, _log;
    private Vector3? _hold;   // a teleport target held until the ground there has collision
    private int _step;
    private string _last = "";

    private static readonly (double E, double N) Far = (2515500, 1165500);
    private static readonly (double E, double N) Home = (2582700, 1112830);

    public static string? Mode() => CmdArgs.Value("--eggcheck");

    public EggProbe(string mode, Func<FootPlayer?> player, ChunkManager chunks, WorldOrigin origin)
    {
        Name = "EggProbe";
        _ride = mode == "ride";
        _player = player;
        _chunks = chunks;
        _origin = origin;
    }

    public override void _PhysicsProcess(double delta)
    {
        var me = _player();
        var egg = VehicleManager.Instance?.GetNodeOrNull<VehicleBody>("veh_africatwin_egg");
        if (_t < 0) { if (egg != null && me != null) _t = 0; else return; }
        _t += delta;

        if (_hold is { } at && me != null)
        {
            if (_chunks.HasCollisionAt(at) && _chunks.TryGetHeight(at, out float g))
            {
                me.GlobalPosition = at with { Y = g + 0.5f };
                me.Velocity = Vector3.Zero;
                _hold = null;
                Say($"arrived at {me.GlobalPosition}");
            }
            else me.GlobalPosition = at with { Y = 3000f };
        }

        if (me != null)
        {
            if (_ride && _step == 0 && _t > 3 && egg != null)
            {
                _step = 1;
                me.GlobalPosition = egg.GlobalPosition + egg.GlobalTransform.Basis.X * 1.5f + Vector3.Up * 0.5f;
                Say($"walked up to the egg ({Label(egg.Kind)})");
            }
            else if (_ride && _step == 1 && _t > 4)
            {
                _step = 2;
                Say($"get on: {me.TryInteract()}");
            }
            else if (_ride && _step == 2 && _t > 7)
            {
                _step = 3;
                me.GlobalPosition += -me.GlobalTransform.Basis.Z * 40f + Vector3.Up * 0.5f;
                Say($"rode 40 m on {Label(me.Ride)} to {me.GlobalPosition}");
            }
            else if (_step < 4 && _t > 15)
            {
                _step = 4;
                _hold = _origin.ToWorld(Far.E, Far.N, 0);
                Say("leaving for the Mollendruz");
            }
            else if (_ride && _step == 4 && _t > 30 && _hold == null)
            {
                _step = 5;
                Say($"get off far away: {me.TryInteract()}");
            }
            else if (_step is 4 or 5 && _t > 60)
            {
                _step = 6;
                _hold = _origin.ToWorld(Home.E, Home.N, 0);
                Say("coming back");
            }
            else if (_step == 6 && _t > 90)
            {
                Say("done");
                GetTree().Quit();
            }
        }

        _log += delta;
        if (_log < 1) return;
        _log = 0;
        string others = string.Join(", ", GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>()
            .Where(p => p != me).Select(p => $"{p.Name} {Label(p.Ride)} at {Round(p.GlobalPosition)}"));
        string state = egg != null
            ? $"egg {Label(egg.Kind)} at {Round(egg.GlobalPosition)} yaw {Mathf.RadToDeg(egg.Rotation.Y):F0} auth {egg.GetMultiplayerAuthority()}"
            : "no egg";
        string line = $"{state} | me {(me != null ? Label(me.Ride) : "-")} | others: {others}";
        if (line != _last) Say(line);
        _last = line;
    }

    private static string Label(RideKind k) => MotorbikeCatalog.For(k)?.Label ?? k.ToString();
    private static Vector3 Round(Vector3 v) => new(Mathf.Round(v.X), Mathf.Round(v.Y), Mathf.Round(v.Z));
    private void Say(string s) => GD.Print($"[eggcheck] t={_t:F1} {s}");
}
