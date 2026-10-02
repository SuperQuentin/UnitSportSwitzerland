using Godot;

namespace UnitSport.Core;

/// <summary>
/// What this client may offer the player. Offline you own the world, so everything is allowed;
/// on a server, what changes the shared world out of nothing is an operator's.
///
/// <para>
/// This only shapes the menus. The server is told nothing by it and trusts none of it: the admin
/// flag arrives from the server (<c>ChatManager.AdminStatus</c>), and the server re-checks every
/// privileged request on its own side (<c>VehicleManager.RequestPark</c>, the admin commands).
/// </para>
///
/// <para>
/// The line drawn, and why:
/// <list type="bullet">
/// <item><b>Spawning a vehicle</b> (bike, car, helicopter, plane) — admin. It is left in the
/// world for everyone, can be abandoned or wrecked anywhere, and would make the loot and vehicle
/// parts pointless if free.</item>
/// <item><b>Equipment</b> (skis, paraglider, wingsuit) — everyone. It is worn and gone when taken
/// off; nothing is left behind for the others.</item>
/// <item><b>Getting into</b> a vehicle already in the world — everyone.</item>
/// <item><b>Place search teleport and the fly camera</b> — everyone. <c>/city</c> already moves
/// any player to any town, and the fly camera leaves the body where it stands.</item>
/// </list>
/// </para>
/// </summary>
public static class Permissions
{
    /// <summary>The server says this client is an operator. Meaningless offline.</summary>
    public static bool IsAdmin { get; private set; }

    /// <summary>Raised when <see cref="IsAdmin"/> changes, so an open menu can re-enable its rows.</summary>
    public static event Action? Changed;

    internal static void SetAdmin(bool admin)
    {
        if (IsAdmin == admin) return;
        IsAdmin = admin;
        Changed?.Invoke();
    }

    /// <summary>Back to a non-admin, on leaving a server.</summary>
    internal static void Reset() => SetAdmin(false);

    /// <summary>Connected to a server, i.e. not the world of your own.</summary>
    public static bool Online
    {
        get
        {
            if (Engine.GetMainLoop() is not SceneTree tree) return false;
            var peer = tree.Root.Multiplayer.MultiplayerPeer;
            return peer is not null and not OfflineMultiplayerPeer
                && peer.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connected
                && !tree.Root.Multiplayer.IsServer();
        }
    }

    /// <summary>
    /// The travel menu is closed for this player: a Battle Royale match rides only what it finds
    /// (#183). Raises <see cref="Changed"/>.
    /// </summary>
    public static bool RidesLocked { get; private set; }

    public static void SetRidesLocked(bool locked)
    {
        if (RidesLocked == locked) return;
        RidesLocked = locked;
        Changed?.Invoke();
    }

    /// <summary>A new vehicle may be conjured from the travel menu.</summary>
    public static bool CanSpawnVehicles => !Online || IsAdmin;
}
