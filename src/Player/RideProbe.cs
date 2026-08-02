using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// Verification helper: mounts a vehicle, holds the throttle, and reports what happened.
///
/// <para>
/// <c>godot --path . -- --ride bike|skis,seconds[,out.png] [--at E,N]</c>
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
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly RideKind _kind;
    private readonly double _seconds;
    private readonly string? _shot;

    private FootPlayer? _player;
    private double _elapsed;
    private double _sinceReport;
    private float _topSpeed;
    private float _startAltitude;
    private Vector3 _start;
    private bool _mounted;
    private bool _done;

    public RideProbe(ChunkManager chunks, WorldOrigin origin, RideKind kind, double seconds,
        string? shot = null)
    {
        _chunks = chunks;
        _origin = origin;
        _kind = kind;
        _seconds = seconds;
        _shot = shot;
    }

    /// <summary>Returns the requested vehicle and duration, or null when --ride was not given.</summary>
    public static (RideKind Kind, double Seconds, string? Shot)? ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--ride") continue;

            var parts = args[i + 1].Split(',');
            var kind = parts[0].ToLowerInvariant() switch
            {
                "bike" or "roadbike" => RideKind.RoadBike,
                "skis" or "ski" => RideKind.Skis,
                _ => RideKind.OnFoot,
            };
            double seconds = 20;
            if (parts.Length > 1) double.TryParse(parts[1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out seconds);
            return (kind, seconds, parts.Length > 2 ? parts[2] : null);
        }
        return null;
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
            if (!_chunks.TryGetHeight(at, out float ground)) return;

            _player = new FootPlayer { Name = "Probe", Terrain = _chunks };
            AddChild(_player);
            _player.GlobalPosition = new Vector3(at.X, ground + 1.5f, at.Z);
            _start = _player.GlobalPosition;
            _startAltitude = ground;
            GD.Print($"[ride] spawned at LV95 {e:F0}/{n:F0}, ground {ground:F1} m");
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

            // full throttle, straight ahead — the probe measures the model, not the steering
            _player.RideControls = () => new RideInput(1f, 0f, 0f, false);
            return;
        }

        _elapsed += delta;
        _topSpeed = Mathf.Max(_topSpeed, _player.RideSpeed);

        _sinceReport += delta;
        if (_sinceReport >= 2.0)
        {
            _sinceReport = 0;
            var p = _player.GlobalPosition;
            float clearance = _chunks.TryGetHeight(p, out float g) ? p.Y - g : float.NaN;
            GD.Print($"[ride] t={_elapsed,5:F1}s  v={_player.RideSpeed,5:F1} m/s "
                + $"({_player.RideSpeed * 3.6f,5:F1} km/h)  alt={p.Y,7:F1}  clearance={clearance,5:F2}");
        }

        if (_elapsed < _seconds) return;
        _done = true;

        var end = _player.GlobalPosition;
        float travelled = new Vector2(end.X - _start.X, end.Z - _start.Z).Length();
        bool underground = _chunks.TryGetHeight(end, out float endGround) && end.Y < endGround - 1.5f;

        GD.Print($"[ride] {_kind}: {travelled:F0} m in {_seconds:F0} s, "
            + $"top {_topSpeed:F1} m/s ({_topSpeed * 3.6f:F1} km/h), "
            + $"climbed {end.Y - _startAltitude:F1} m");
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
}
