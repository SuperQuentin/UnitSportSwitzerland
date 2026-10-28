using Godot;

namespace UnitSport.Interiors;

/// <summary>What the bowling game (#1323) asks of the interiors on the server: a centre's plan, and where a player stands in it.</summary>
public partial class InteriorManager
{
    /// <summary>The plan of <paramref name="key"/> as this peer has it (the server plans every interior someone opened).</summary>
    public InteriorLayout? Planned(string key) => _cache.GetValueOrDefault(key);

    /// <summary>
    /// Where <paramref name="peer"/>'s body stands in plan <paramref name="l"/>'s frame, as this peer sees it: the
    /// server's view of a remote body, offline the one player. Null when there is no body (a free camera).
    /// </summary>
    public Vector3? PlayerLocal(long peer, InteriorLayout l) => LocalOf(peer, l);
}
