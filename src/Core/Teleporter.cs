using Godot;
using UnitSport.Terrain;

namespace UnitSport.Core;

/// <summary>
/// Moves whatever the player is currently controlling to an LV95 position.
///
/// The thing to move is resolved at the moment of the jump, not when this node was built.
/// The earlier teleport captured the spectator camera once at startup, which is why it
/// silently did nothing in multiplayer: there you are an on-foot networked `FootPlayer`, so
/// the jump moved a camera that was not even current. Asking `ClientWorld` each time is what
/// makes the same Tab search work when flying, when walking, and when connected to a server.
/// </summary>
public sealed partial class Teleporter : Node
{
    /// <summary>Height above ground a flying camera arrives at, in metres.</summary>
    private const float FlyingArrivalHeight = 220f;

    /// <summary>Height above ground a walking body arrives at — just enough to land.</summary>
    private const float WalkingArrivalHeight = 2.0f;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;

    public Teleporter(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
        Name = "Teleporter";
    }

    /// <summary>
    /// Supplies the node to move. Set by <see cref="ClientWorld"/> so it can hand back the
    /// spectator camera, the local on-foot player, or the networked player as appropriate.
    /// </summary>
    public Func<Node3D?> ActiveTarget { get; set; } = () => null;

    /// <summary>Raised after a jump is requested, for the chat log and the HUD.</summary>
    public event Action<string>? Teleported;

    /// <summary>
    /// Jumps to an LV95 easting/northing. The height is not known until the terrain under
    /// the destination has streamed in, so this arrives high and lets <see cref="SpawnPoint"/>
    /// settle it.
    /// </summary>
    /// <returns>False when there is nothing to move — no camera and no player yet.</returns>
    public bool TeleportTo(double lv95E, double lv95N, string? label = null)
    {
        var target = ActiveTarget();
        if (target is null)
        {
            GD.PushWarning("[teleport] nothing to move: no active camera or player");
            return false;
        }

        // A body arrives at walking height; a free camera arrives high enough to see where
        // it landed. Dropping a CharacterBody3D from 220 m would be a long fall.
        float arrival = target is CharacterBody3D ? WalkingArrivalHeight : FlyingArrivalHeight;

        // Any spawn still settling would fight this one for the height.
        foreach (var node in GetParent().GetChildren())
            if (node is SpawnPoint pending) pending.QueueFree();

        GetParent().AddChild(new SpawnPoint(target, _chunks, _origin, lv95E, lv95N, arrival));

        string where = label ?? $"{lv95E:F0}/{lv95N:F0}";
        GD.Print($"[teleport] {target.Name} -> {where}");
        Teleported?.Invoke(where);
        return true;
    }
}
