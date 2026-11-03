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
/// <item><b>Place search teleport and the fly camera</b> — everyone, unless the server was started
/// with <c>--no-teleport</c> (#799, <see cref="CanTeleport"/>): then only admins jump, and walking back
/// from the fly camera returns to the body where it stands.</item>
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

    /// <summary>Back to a non-admin who may teleport, on leaving a server.</summary>
    internal static void Reset()
    {
        SetAdmin(false);
        SetServerTeleport(true);
        SetServerBody(true, true);
    }

    /// <summary>The server lets players who are not admins teleport (<c>Net/Arrival</c>, #799). Meaningless offline.</summary>
    public static bool ServerTeleport { get; private set; } = true;

    internal static void SetServerTeleport(bool allowed)
    {
        if (ServerTeleport == allowed) return;
        ServerTeleport = allowed;
        Changed?.Invoke();
    }

    /// <summary>
    /// Offline, the player chose the adventure's travel network (<c>Settings > Gameplay > Travel</c>, #1226): free jumps
    /// are off for it as on a <c>--adventure-travel network</c> server.
    /// </summary>
    public static bool OfflineTravelNetwork { get; private set; }

    public static void SetOfflineTravelNetwork(bool on)
    {
        if (OfflineTravelNetwork == on) return;
        OfflineTravelNetwork = on;
        Changed?.Invoke();
    }

    /// <summary>
    /// The server counts stamina (#1225): <c>--stamina off</c> drops the meter while keeping the
    /// Adventure layer (<c>Net/Arrival</c>). Meaningless offline (always on).
    /// </summary>
    public static bool ServerStamina { get; private set; } = true;

    /// <summary>The server's air rises in thermals, ridge lift and updrafts (#1225); <c>--lift off</c> stills it.</summary>
    public static bool ServerLift { get; private set; } = true;

    /// <summary>The server's <c>--alcohol off|mild|real</c> (#1271), as <c>Net/Arrival</c> carried it: 0, 1, 2.</summary>
    public static int ServerAlcohol { get; private set; } = 2;

    internal static void SetServerAlcohol(int mode) => ServerAlcohol = Math.Clamp(mode, 0, 2);

    /// <summary>The server's <c>--crevasses off|on</c> (#1372), as <c>Net/Arrival</c> carried it. Meaningless offline (always on).</summary>
    public static bool ServerCrevasses { get; private set; } = true;

    internal static void SetServerCrevasses(bool on) => ServerCrevasses = on;

    /// <summary>
    /// Hidden crevasses open under an unroped player on a glacier (#1372): the server allows them, not in a Battle Royale match,
    /// and the player's <c>Settings › Gameplay › Crevasses</c> is on; a <c>--crevasses off|on</c> on this command line wins over the setting
    /// (offline and checks).
    /// </summary>
    public static bool CrevassesOn => ServerCrevasses && !InMatch && (CmdArgs.Value("--crevasses")?.ToLowerInvariant() switch
    {
        "off" => false,
        "on" => true,
        _ => GameSettings.Current.Crevasses,
    });

    internal static void SetServerBody(bool stamina, bool lift)
    {
        if (ServerStamina == stamina && ServerLift == lift) return;
        ServerStamina = stamina;
        ServerLift = lift;
        Changed?.Invoke();
    }

    /// <summary>
    /// Thermals, ridge lift and updrafts carry this player's gliders (#1225): the server did not say
    /// <c>--lift off</c>, and not in a Battle Royale match (a parachute hovering in a thermal would change the drop).
    /// </summary>
    public static bool LiftOn => ServerLift && !InMatch;

    /// <summary>The place search, the VR hand map and the fly camera may move the player somewhere else.</summary>
    public static bool CanTeleport => Online ? IsAdmin || ServerTeleport : !OfflineTravelNetwork;

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
    /// This player is in a Battle Royale match, admin or not, so it plays on equal terms (#183, #425):
    /// the travel menu is closed (it rides only what it finds), and so are the fly camera, the debug
    /// menu and the item catalogue (<see cref="AdminTools"/>). The server refuses the admin commands
    /// that would touch an entrant on its own side. Raises <see cref="Changed"/>.
    /// </summary>
    public static bool InMatch { get; private set; }

    public static void SetInMatch(bool inMatch)
    {
        if (InMatch == inMatch) return;
        InMatch = inMatch;
        Changed?.Invoke();
    }

    /// <summary>The debug menu and the item catalogue may be offered: alone, or as a server's admin, never in a match.</summary>
    public static bool AdminTools => !InMatch && (!Online || IsAdmin);

    /// <summary>A new vehicle may be conjured from the travel menu.</summary>
    public static bool CanSpawnVehicles => !InMatch && (!Online || IsAdmin);
}
