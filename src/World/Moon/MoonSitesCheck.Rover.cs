using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Bodies;
using UnitSport.Vehicles;

namespace UnitSport.World.Moon;

public partial class MoonSitesCheck
{
    /// <summary>
    /// <c>--moonrovercheck [open|cabin|kart]</c> (#1943), after the last named colony (<c>--moonsites artemis
    /// --moonrovercheck</c>): the walker steps in on the colony's road by the hangar, gets into the rover, drives full
    /// throttle out along the road for 8 s, brakes to a stop, gets out and the rover stays parked on the ground. Fails if the
    /// mount is refused, the rover leaves the ground for long, ends under it, or is not parked after. Prints the gravity
    /// the vehicle drove on and the top speed. Windowed: <c>rover_drive.png</c> behind it on the road.
    /// </summary>
    public static bool RoverCheck => CmdArgs.Has("--moonrovercheck");

    private int _rover;
    private double _roverT, _roverTop, _roverAir;
    private Vector3 _roverFrom;
    private Camera3D? _roverCam;

    private RideKind RoverKind()
    {
        var word = CmdArgs.Value("--moonrovercheck");
        return word switch
        {
            "kart" => CarCatalog.Kart.Kind,
            "cabin" => MoonRovers.Cabin.Kind,
            _ => MoonRovers.Open.Kind,
        };
    }

    private void RoverStep(double delta)
    {
        _roverT += delta;
        var s = _sites[_index];
        var me = _local();
        var colony = MoonColonies.Find(PlacedId(s));
        if (colony == null || MoonSurface.Active?.Sites.TryGetValue(colony.Id, out var placed) != true) { Finish("no colony in place for the rover"); return; }
        var plan = ColonyPlan.For(colony);
        var frame = placed!.Node.GlobalTransform;
        var (rx, rz) = plan.Road[0];
        var (ex, ez) = plan.Road[^1];
        var along = (frame.Basis * new Vector3((float)(ex - rx), 0, (float)(ez - rz))).Normalized();
        var start = frame * new Vector3((float)rx, 0, (float)rz) + along * 14;
        switch (_rover)
        {
            case 1:
            {
                var cam = (Camera3D)GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false)!;
                cam.GlobalPosition = start + Vector3.Up * 1.7f;
                cam.LookAt(start + along * 10 + Vector3.Up * 1.7f, Vector3.Up);
                Key(Godot.Key.T, true);
                RoverNext(2);
                break;
            }
            case 2:
                Key(Godot.Key.T, false);
                RoverNext(3);
                break;
            case 3:
                if (me is { } m && m.Camera.Current && m.IsOnFloor() && _roverT > 1.5)
                {
                    m.PlaceAt(start + Vector3.Up * 0.1f, Mathf.Atan2(-along.X, -along.Z));
                    RoverNext(4);
                }
                else if (_roverT > 20) Finish("the walker did not stand within 20 s of T");
                break;
            case 4:
                if (_roverT < 1.0 || me == null || !me.IsOnFloor()) break;
                var kind = RoverKind();
                if (!me.SetRide(kind)) { Finish($"the mount of {kind} was refused"); return; }
                _roverFrom = me.GlobalPosition;
                RoverLog(string.Create(CultureInfo.InvariantCulture, $"in the {kind} on the road; the vehicles' gravity {Rideable.Gravity:F2} m/s²"));
                me.RideControls = () => new RideInput(_rover == 5 ? 1f : 0f, _rover == 6 ? 1f : 0f, 0f, false, Handbrake: _rover == 7);
                _roverCam = new Camera3D { Name = "RoverCam", Fov = 60 };
                AddChild(_roverCam);
                if (!_headless) _roverCam.MakeCurrent();
                RoverNext(5);
                break;
            case 5:
            case 6:
            {
                var p = me!.GlobalPosition;
                _roverTop = System.Math.Max(_roverTop, me.RideSpeed);
                var ground = _ground()!.Heights;
                var axes = _origin.Frame.Axes!;
                var n = ground.NormalUnder(axes.ToEcef(p.X, p.Y, p.Z));
                var (_, gy, _) = axes.FromEcef(ground.Ground(n));
                double over = p.Y - gy;
                if (over > 0.6) _roverAir += delta;
                if (over < -0.4) { Finish(string.Create(CultureInfo.InvariantCulture, $"the rover sank {-over:F2} m under the ground")); return; }
                // the camera behind and over it
                var back = me.GlobalBasis.Z with { Y = 0 };
                _roverCam!.GlobalPosition = p + back.Normalized() * 7 + Vector3.Up * 3;
                _roverCam.LookAt(p + Vector3.Up * 0.8f, Vector3.Up);
                if (_rover == 5 && _roverT > 8)
                {
                    RoverLog(string.Create(CultureInfo.InvariantCulture,
                        $"8 s at full throttle: {me.RideSpeed * 3.6f:F1} km/h, {p.DistanceTo(_roverFrom):F0} m along the road, {_roverAir:F1} s off the ground"));
                    if (!_headless) { _pending = "rover_drive"; _pendingFrames = 3; }
                    RoverNext(6);
                }
                else if (_rover == 6 && Mathf.Abs(me.RideSpeed) < 0.3f && _roverT > 0.5)
                {
                    RoverLog(string.Create(CultureInfo.InvariantCulture, $"stopped in {_roverT:F1} s, the top speed {_roverTop * 3.6:F1} km/h"));
                    RoverNext(7);
                }
                else if (_roverT > 30) Finish("the rover did not stop within 30 s");
                break;
            }
            case 7:
                if (_roverT < 0.5) break;
                if (!me!.TryInteract()) { Finish("E in the rover did not get out"); return; }
                RoverNext(8);
                break;
            case 8:
                if (_roverT < 3) break;
            {
                VehicleBody? parked = null;
                foreach (var c in VehicleManager.Instance!.GetChildren())
                    if (c is VehicleBody v && v.GlobalPosition.DistanceTo(me!.GlobalPosition) < 8) parked = v;
                if (parked == null) { Finish("no rover parked by the walker after getting out"); return; }
                RoverLog(string.Create(CultureInfo.InvariantCulture, $"out; the rover parked {parked.GlobalPosition.DistanceTo(me!.GlobalPosition):F1} m away"));
                if (_roverAir > 2.5) { Finish(string.Create(CultureInfo.InvariantCulture, $"the rover was {_roverAir:F1} s off the ground on a level road")); return; }
                Finish(null);
                break;
            }
        }
    }

    private void RoverNext(int stage)
    {
        _rover = stage;
        _roverT = 0;
    }

    private static void RoverLog(string what) => GD.Print($"[moonsites]   rover: {what}");
}
