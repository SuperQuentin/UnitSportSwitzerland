using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// Verification helper: mounts a vehicle, holds the throttle, and reports what happened.
///
/// <para>
/// <c>godot --path . -- --ride bike|skis|car[:N]|r1|monster,seconds[,out.png] [--at E,N] [--heading deg]
/// [--brake-at s] [--midshot s]</c>
/// (<c>--heading</c>: compass bearing to ride along, 0 = north, 90 = east; <c>--brake-at</c>: let
/// go of the throttle and brake from then on, to stop somewhere, say inside a garage;
/// <c>--midshot</c>: one more screenshot then, next to out.png as out_mid.png; <c>--setup name</c>: a car
/// preset, <see cref="CarSetups"/>; <c>--wall D</c>: a solid wall D m ahead, to crash into (#214), with
/// <c>--crashshots t1,t2,...</c> screenshots that many seconds after the rider is thrown; <c>--sideview</c>:
/// out.png from a camera keeping pace abeam for the last second)
/// </para>
///
/// <para>
/// Riding is the one part of this that cannot be checked from a screenshot. Speed, gradient
/// response and whether the body is still on top of the terrain are numbers, and the whole
/// chain — mount, vehicle model, character body, heightfield collision — only fails in ways
/// that look like "it feels wrong" unless something prints them. This is the same trick as
/// <see cref="TunnelProbe"/>: drive it headlessly and assert on the result.
/// </para>
/// </summary>
public partial class RideProbe : Node
{
    private readonly ChunkManager? _chunks;   // null on --world flat
    private readonly WorldOrigin _origin;
    private readonly RideKind _kind;
    private readonly double _seconds;
    private readonly string? _shot;
    private Camera3D? _closeCam;

    private FootPlayer? _player;
    private double _elapsed;
    private double _sinceReport;
    private float _topSpeed;
    private float _startAltitude;
    /// <summary>Where the ride started, kept in LV95: the origin may move under it (#185).</summary>
    private GlobalPos _start;
    private bool _mounted;
    private bool _done;
    private bool _midShot;
    private bool _stopped;
    // --wall (#214): when the rider went limp, how far the body flew, and the shots still to take
    private double _thrownAt = -1, _restedAt = -1, _sinceCrashReport;
    private Vector3 _thrownFrom;
    private float _thrownFarthest;
    private readonly System.Collections.Generic.List<float> _crashShots = new();

    private readonly System.Collections.Generic.List<float> _reached = new();
    /// <summary>A motorbike's worst use of its wheelie / stoppie limit; 1 or more would be a flip.</summary>
    private float _worstPitch;

    public RideProbe(ChunkManager? chunks, WorldOrigin origin, RideKind kind, double seconds,
        string? shot = null)
    {
        _chunks = chunks;
        _origin = origin;
        _kind = kind;
        _seconds = seconds;
        _shot = shot;

        // The probe checks the physics against real-world numbers (180 W -> 32.7 km/h flat), so
        // it rides the Sim profile unless told otherwise with --profile game. Not saved.
        if (!CmdArgs.Has("--profile"))
            Core.GameSettings.Current.RideProfile = Core.RideProfile.Sim;
    }

    /// <summary>Returns the requested vehicle and duration, or null when --ride was not given.</summary>
    public static (RideKind Kind, double Seconds, string? Shot)? ParseArgs()
    {
        var args = CmdArgs.All;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--ride") continue;

            var parts = args[i + 1].Split(',');
            var kind = KindNamed(parts[0]);
            double seconds = 20;
            if (parts.Length > 1) double.TryParse(parts[1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out seconds);
            return (kind, seconds, parts.Length > 2 ? parts[2] : null);
        }
        return null;
    }

    /// <summary>
    /// A ride by the name <c>--ride</c> and <c>--seat</c> take: bike, skis, r1, monster, kart, a320,
    /// freighter, an124, the works machines, moto:N, truck:N, car:N. Anything else is on foot.
    /// </summary>
    public static RideKind KindNamed(string word)
    {
        var name = word.ToLowerInvariant();
        return name switch
        {
                "bike" or "roadbike" => RideKind.RoadBike,
                "skis" or "ski" => RideKind.Skis,
                "r1" => (RideKind)MotorbikeCatalog.First,
                // the aircraft (#421): parked, on their brakes
                "a320" => RideKind.A320,
                "freighter" => RideKind.Freighter,
                "an124" => RideKind.An124,
                "monster" => (RideKind)(MotorbikeCatalog.First + 1),
                "kart" => CarCatalog.Kart.Kind,
                // works machinery (#583): the mast is worked with the shift paddles while driving
                "forklift" => RideKind.Forklift,
                "excavator" => RideKind.Excavator,
                "miniexcavator" => RideKind.MiniExcavator,
                "loader" => RideKind.WheelLoader,
                "loaderforks" => RideKind.WheelLoaderForks,
                "roller" => RideKind.CompactRoller,
                "dumper" => RideKind.MiniDumper,
                "telehandler" => RideKind.Telehandler,
                // moto:N = MotorbikeCatalog.All[N]
                _ when name.StartsWith("moto:") && int.TryParse(name[5..], out int b) => MotorbikeCatalog.All[b].Kind,
                // truck:N = HeavyCatalog.All[N]; --trailer M couples TrailerCatalog.All[M], full
                _ when name.StartsWith("truck") => (RideKind)(HeavyCatalog.First
                    + (name.Length > 6 && int.TryParse(name[6..], out int h) ? h : 0)),
                // car = the first in the roster, car:N = CarCatalog.All[N]
                _ when name.StartsWith("car") => (RideKind)(CarCatalog.First
                    + (name.Length > 4 && int.TryParse(name[4..], out int n) ? n : 0)),
                _ => RideKind.OnFoot,
            };
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;

        // The player cannot be placed until the tile under it has streamed in and grown
        // collision; before that it would fall through the world and the run would measure
        // nothing but gravity.
        if (_player == null)
        {
            var (e, n) = SpawnPoint.ParseTarget();
            var at = _origin.ToWorld(e, n, 0);
            if (!TestWorld.TryGround(_chunks, at, out float ground)) return;

            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            // --heading is a compass bearing: a node faces −Z (north) and +yaw turns it toward −X
            // (west). Set before the node enters the tree, whose _Ready takes its view from it.
            if (CmdArgs.Float("--heading") is float bearing)
                _player.Rotation = new Vector3(0, -Mathf.DegToRad(bearing), 0);
            AddChild(_player);
            _player.GlobalPosition = new Vector3(at.X, ground + 1.5f, at.Z);
            if (_chunks == null) _player.DebugLaunch(_player.GlobalPosition, Vector3.Zero);   // flat world: no terrain to wait for
            _start = _origin.ToGlobal(_player.GlobalPosition);
            _startAltitude = ground;
            GD.Print($"[ride] spawned at LV95 {e:F0}/{n:F0}, ground {ground:F1} m");
            return;
        }

        if (!_mounted && _kind == RideKind.OnFoot)
        {
            // "--ride foot": run straight ahead (and stand still from --brake-at on)
            if (!_player.IsOnFloor()) return;
            _mounted = true;
            float stopAt = CmdArgs.Float("--brake-at") ?? float.MaxValue;
            var ahead = -_player.GlobalBasis.Z with { Y = 0 };
            _player.WalkControls = () => (_elapsed < stopAt ? ahead.Normalized() : Vector3.Zero, true);
            GD.Print("[ride] on foot");
            // "--trailer M" on foot: that trailer parked 14 m ahead, to walk up to (the trailer's intro, #517)
            if (CmdArgs.Int("--trailer") is int lone)
                GD.Print(_player.SpawnTrailer(lone, 0.5f) ? $"[ride] parked {TrailerCatalog.All[lone].Label} ahead" : "[ride] TRAILER REFUSED");
            return;
        }

        if (!_mounted)
        {
            // one frame of settling, or the mount is refused for being airborne
            if (!_player.IsOnFloor()) return;
            _mounted = _player.SetRide(_kind);
            GD.Print(_mounted
                ? $"[ride] mounted {_kind}"
                : $"[ride] MOUNT REFUSED for {_kind}");
            if (!_mounted) { _done = true; GetTree().Quit(1); }
            if (_mounted && CmdArgs.Value("--setup") is { } setupWord)
            {
                var setup = CarSetups.Parse(setupWord);
                GD.Print(setup != null && _player.SetCarSetup(setup.Id) ? $"[ride] preset {setup.Name}" : "[ride] PRESET REFUSED");
            }

            if (_mounted && CmdArgs.Int("--trailer") is int trailer)
                GD.Print(_player.SpawnTrailer(trailer, 1f) ? $"[ride] coupled {TrailerCatalog.All[trailer].Label}" : "[ride] TRAILER REFUSED");
            // full throttle, straight ahead — the probe measures the model, not the steering —
            // unless --steer asks for a turn (−1 left .. 1 right)
            float steer = CmdArgs.Float("--steer") ?? 0f;
            if (_mounted && CmdArgs.Float("--wall") is { } wallAt) SpawnWall(wallAt);
            float brakeAt = CmdArgs.Float("--brake-at") ?? float.MaxValue;
            // (the brake, held at a standstill, would reverse: let go once stopped)
            _player.RideControls = () =>
            {
                if (_elapsed < brakeAt) return new RideInput(steer != 0f && _player.RideSpeed > 5f ? 0f : 1f, 0f, steer, false);
                _stopped |= Mathf.Abs(_player.RideSpeed) < 0.3f;
                return new RideInput(0f, _stopped ? 0f : 1f, 0f, false, Handbrake: _stopped);
            };
            return;
        }

        _elapsed += delta;
        _topSpeed = Mathf.Max(_topSpeed, _player.RideSpeed);
        foreach (float kmh in new[] { 80f, 100f, 200f })
            if (_player.RideSpeed * 3.6f >= kmh && !_reached.Contains(kmh))
            {
                _reached.Add(kmh);
                GD.Print($"[ride] 0-{kmh:F0} km/h in {_elapsed:F2} s");
            }
        if (_player.Vehicle is Motorbike moto)
            _worstPitch = Mathf.Max(_worstPitch, Mathf.Abs(moto.PitchUse));

        if (_shot != null && CmdArgs.Float("--midshot") is { } mid && _elapsed >= mid && !_midShot)
        {
            _midShot = true;
            string path = _shot.Replace(".png", "_mid.png");
            GD.Print(GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok ? $"[ride] wrote {path}" : $"[ride] FAILED to write {path}");
        }

        WatchCrash(delta);

        // --closeup (#421): the last second from a camera in front of the captain's screens or gauges
        if (_shot != null && CmdArgs.Has("--closeup") && _closeCam == null && _elapsed >= _seconds - 1.0
            && _player.GetChildren().OfType<Avatar.AirlinerRig>().FirstOrDefault()?.Cockpit is { } deck
            && deck.ScreenFrame(0) is { } a && deck.ScreenFrame(1) is { } b)
        {
            var at = (a.Origin + b.Origin) * 0.5f;
            _closeCam = new Camera3D { Fov = 40f, Near = 0.02f };
            AddChild(_closeCam);
            _closeCam.GlobalPosition = at + a.Basis.Z.Normalized() * 0.5f;
            _closeCam.LookAt(at, a.Basis.Y.Normalized());
            _closeCam.MakeCurrent();
        }
        // --sideview (#764): the last second from 9 m abeam on the left, level, keeping pace, so the
        // body's pitch on a slope shows against the horizon
        if (_shot != null && CmdArgs.Has("--sideview") && _elapsed >= _seconds - 1.0)
        {
            if (_closeCam == null)
            {
                _closeCam = new Camera3D { Fov = 50f };
                AddChild(_closeCam);
                _closeCam.MakeCurrent();
            }
            var at = _player.GlobalPosition + Vector3.Up * 0.8f;
            _closeCam.GlobalPosition = at - _player.GlobalBasis.X * 9f + Vector3.Up * 1.5f;
            _closeCam.LookAt(at, Vector3.Up);
        }

        _sinceReport += delta;
        if (_sinceReport >= 1.0)
        {
            _sinceReport = 0;
            var p = _player.GlobalPosition;
            float clearance = TestWorld.TryGround(_chunks, p, out float g) ? p.Y - g : float.NaN;
            GD.Print($"[ride] t={_elapsed,5:F1}s  v={_player.RideSpeed,5:F1} m/s "
                + $"({_player.RideSpeed * 3.6f,5:F1} km/h)  alt={p.Y,7:F1}  clearance={clearance,5:F2}"
                + (_player.Vehicle is Motorbike bike ? $"  on {bike.Surface}  gear {bike.Gear}" : "")
                + (_player.Vehicle is Car car ? $"  on {(_chunks is { } c ? Audio.Surfaces.At(c, p, false) : Audio.Surface.Asphalt)}  gear {car.Gear}"
                    + $"  pitch {Mathf.RadToDeg(_player.BodyPose.Basis.GetEuler().X),5:F1}°  roll {Mathf.RadToDeg(_player.BodyPose.Basis.GetEuler().Z),5:F1}°" : "")
                + (_player.Vehicle is Truck truck ? $"  on {(_chunks is { } tc ? Audio.Surfaces.At(tc, p, false) : Audio.Surface.Asphalt)}  gear {truck.GearLabel} {truck.Rpm:F0} rpm"
                    + $"  joints {string.Join(" ", truck.Articulation.Take(truck.SectionCount - 1).Select(j => $"{Mathf.RadToDeg(j):F0}°"))}" : "")
                + (Vehicles.GarageUi.GarageNear?.Invoke(p) == true ? "  at a garage" : "")
                + (Inside() is { } inside ? $"  inside {inside.DressedKind()} {inside.Key}" : "")
                + (_kind == RideKind.OnFoot && Interiors.DoorIndex.Nearest(p, 1.6f) is { } door ? $"  [E] door {door.Key}" : ""));
        }

        if (_elapsed < _seconds) return;
        _done = true;

        var end = _player.GlobalPosition;
        float travelled = (float)_origin.ToGlobal(end).HorizontalDistanceTo(_start);
        bool underground = TestWorld.TryGround(_chunks, end, out float endGround) && end.Y < endGround - 1.5f;
        if (Inside() is { } building)
        {
            // driven in through the door's portal: the interior's floor is the ground in there
            float floor = Interiors.InteriorManager.Instance?.CurrentNode?.GlobalPosition.Y ?? Interiors.InteriorManager.InteriorBaseY;
            underground = end.Y < floor - 1.5f;
            GD.Print($"[ride] ended inside {building.DressedKind()} {building.Key}, {end.Y - floor:F2} m over its floor, "
                + $"speed {_player.RideSpeed:F1} m/s");
        }

        GD.Print($"[ride] {_kind}: {travelled:F0} m in {_seconds:F0} s, "
            + $"top {_topSpeed:F1} m/s ({_topSpeed * 3.6f:F1} km/h), "
            + $"climbed {end.Y - _startAltitude:F1} m"
            + (_player.Vehicle is Motorbike ? $", worst pitch {_worstPitch:P0} of the wheelie/stoppie limit" : ""));
        if (CmdArgs.Float("--wall") != null)
        {
            bool thrown = _thrownAt >= 0, rested = _restedAt >= 0;
            GD.Print(thrown && rested && !underground
                ? $"[crash] RESULT: thrown at {_thrownAt:F1} s, flew {_thrownFarthest:F1} m, at rest after {_restedAt - _thrownAt:F1} s, {_player.CrashBones} bones"
                : $"[crash] RESULT: FAILED — thrown {thrown}, came to rest {rested}, underground {underground}");
            if (_shot != null) GetViewport().GetTexture().GetImage().SavePng(_shot);
            GetTree().Quit(thrown && rested && !underground ? 0 : 1);
            return;
        }
        GD.Print(travelled > 5 && !underground
            ? "[ride] RESULT: rode under its own power and stayed on the surface"
            : underground
                ? "[ride] RESULT: FAILED — ended below the terrain"
                : "[ride] RESULT: FAILED — went nowhere");

        if (_shot != null)
        {
            // the rider's own chase camera is Current, so this frames what a player would see
            var image = GetViewport().GetTexture().GetImage();
            GD.Print(image.SavePng(_shot) == Error.Ok
                ? $"[ride] wrote {_shot}"
                : $"[ride] FAILED to write {_shot}");
        }

        GetTree().Quit(travelled > 5 && !underground ? 0 : 1);
    }

    /// <summary>A wall across the road <paramref name="ahead"/> m in front: 30 m wide, 4 m high, on the ground.</summary>
    private void SpawnWall(float ahead)
    {
        var fwd = -_player!.GlobalBasis.Z with { Y = 0 };
        var at = _player.GlobalPosition + fwd.Normalized() * ahead;
        _chunks.TryGetHeight(at, out float g);
        var size = new Vector3(30f, 4f, 1f);
        var wall = new StaticBody3D { Name = "CrashWall" };
        wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        wall.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size } });
        AddChild(wall);
        wall.GlobalTransform = new Transform3D(Basis.LookingAt(fwd.Normalized(), Vector3.Up), new Vector3(at.X, g + size.Y * 0.5f - 0.3f, at.Z));
        if (CmdArgs.Value("--crashshots") is { } shots)
            foreach (var t in shots.Split(','))
                if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) _crashShots.Add(v);
        GD.Print($"[crash] wall {ahead:F0} m ahead");
    }

    /// <summary>--wall: the throw, the flight and the rest, printed; the timed shots taken.</summary>
    private void WatchCrash(double delta)
    {
        if (CmdArgs.Float("--wall") == null || _player == null) return;
        bool limp = _player.Ragdolled;
        if (limp && _thrownAt < 0)
        {
            _thrownAt = _elapsed;
            _thrownFrom = _player.GlobalPosition;
            GD.Print($"[crash] thrown at t={_elapsed:F2}s");
        }
        if (_thrownAt < 0) return;
        var p = _player.GlobalPosition;
        if (limp) _thrownFarthest = Mathf.Max(_thrownFarthest, new Vector2(p.X - _thrownFrom.X, p.Z - _thrownFrom.Z).Length());
        if (!limp && _restedAt < 0)
        {
            _restedAt = _elapsed;
            GD.Print($"[crash] at rest at t={_elapsed:F2}s, {_player.CrashBones} bones broken, health {_player.Health:F0}"
                + (XR.XrSession.Active ? $", VR anchor {XR.XrSession.Anchor?.Name}" : ""));
        }
        _sinceCrashReport += delta;
        if (limp && _sinceCrashReport >= 0.2)
        {
            _sinceCrashReport = 0;
            float clearance = _chunks.TryGetHeight(p, out float g) ? p.Y - g : float.NaN;
            GD.Print($"[crash] t={_elapsed - _thrownAt,4:F1}s hips {p.X - _thrownFrom.X,6:F1} {p.Y - _thrownFrom.Y,5:F1} {p.Z - _thrownFrom.Z,6:F1}  clearance {clearance:F2}"
                + (XR.XrSession.Active ? $"  VR anchor {XR.XrSession.Anchor?.Name} at {XR.XrSession.Anchor?.GlobalPosition.DistanceTo(p):F1} m" : ""));
        }
        for (int i = _crashShots.Count - 1; i >= 0; i--)
        {
            if (_elapsed - _thrownAt < _crashShots[i] || _shot == null) continue;
            string path = _shot.Replace(".png", "_crash" + _crashShots[i].ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".png");
            GD.Print(GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok ? $"[crash] wrote {path}" : $"[crash] FAILED to write {path}");
            _crashShots.RemoveAt(i);
        }
    }

    /// <summary>The interior the rider is in (walked or driven in through its door), if any.</summary>
    private Interiors.InteriorLayout? Inside() => _player?.Indoors == true ? Interiors.InteriorManager.Instance?.Current : null;
}
