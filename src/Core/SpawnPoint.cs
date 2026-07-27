using Godot;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// Places the camera somewhere worth looking at on startup, and drops it onto the ground
/// once the terrain under it has streamed in.
///
/// The height cannot be resolved at boot: chunks load asynchronously, so the spawn begins
/// at a safe altitude and settles as soon as the ground is known. Without this the camera
/// starts at the world origin, which after a large import is usually empty space.
/// </summary>
public partial class SpawnPoint : Node
{
    /// <summary>
    /// Riddes, in the Rhône valley — the densest part of the originally imported area,
    /// so it has buildings, roads, rail and the river all in view.
    /// </summary>
    public const double DefaultLv95E = 2583250;
    public const double DefaultLv95N = 1113250;

    /// <summary>Default height above ground the spectator settles at, in metres.</summary>
    public const float ViewHeight = 220f;

    /// <summary>Altitude used before the ground is known; above any Swiss summit.</summary>
    private const float PreloadAltitude = 4800f;

    private readonly Node3D _target;
    private readonly ChunkManager _chunks;
    private readonly float _arrivalHeight;
    private bool _settled;
    private double _waited;

    /// <param name="target">Node to move — a camera, or a body.</param>
    /// <param name="chunks">Streamer, asked for the ground height.</param>
    /// <param name="origin">World origin for the LV95 conversion.</param>
    /// <param name="lv95E">Destination easting.</param>
    /// <param name="lv95N">Destination northing.</param>
    /// <param name="arrivalHeight">
    /// Height above ground to settle at. A free camera wants a viewpoint; a walking body
    /// wants to land, so <see cref="Teleporter"/> passes a couple of metres instead.
    /// </param>
    public SpawnPoint(Node3D target, ChunkManager chunks, WorldOrigin origin,
        double lv95E, double lv95N, float arrivalHeight = ViewHeight)
    {
        _target = target;
        _chunks = chunks;
        _arrivalHeight = arrivalHeight;
        Name = "SpawnPoint";
        target.GlobalPosition = origin.ToWorld(lv95E, lv95N, PreloadAltitude);

        // A body left with its old velocity would carry the fall it was already in, and its
        // own placement pass must run again at the new location.
        if (target is CharacterBody3D body)
        {
            body.Velocity = Vector3.Zero;
            if (body is UnitSport.Player.FootPlayer player) player.RequestReplacement();
        }
    }

    /// <summary>Reads an optional "--at E,N" override, in LV95 metres.</summary>
    public static (double E, double N) ParseTarget()
    {
        var args = OS.GetCmdlineUserArgs();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--at")
            {
                var parts = args[i + 1].Split(',');
                if (parts.Length == 2
                    && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out double e)
                    && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out double n))
                    return (e, n);
                GD.PushWarning($"[spawn] could not read --at {args[i + 1]}, expected E,N in LV95");
            }
        return (DefaultLv95E, DefaultLv95N);
    }

    public override void _Process(double delta)
    {
        if (_settled) return;
        _waited += delta;

        if (_chunks.TryGetHeight(_target.GlobalPosition, out float ground))
        {
            var p = _target.GlobalPosition;
            _target.GlobalPosition = new Vector3(p.X, ground + _arrivalHeight, p.Z);
            if (_target is CharacterBody3D body) body.Velocity = Vector3.Zero;
            _settled = true;
            GD.Print($"[spawn] settled at ground {ground:F0} m + {_arrivalHeight:F0} m");
            QueueFree();
        }
        else if (_waited > 15.0)
        {
            // no terrain here at all — leave the camera high rather than dropping it
            // through empty space, and say so
            GD.PushWarning("[spawn] no terrain streamed under the spawn point after 15 s; " +
                           "is it outside the imported tiles?");
            _settled = true;
            QueueFree();
        }
    }
}
