using Godot;

namespace UnitSport.Net;

/// <summary>Thin wrapper around ENetMultiplayerPeer setup for either role.</summary>
public partial class NetworkManager : Node
{
    public const int DefaultPort = 7777;
    public const int MaxClients = 32;

    public bool StartServer(int port)
    {
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateServer(port, MaxClients);
        if (err != Error.Ok)
        {
            GD.PushError($"[net] server failed to listen on {port}: {err}");
            return false;
        }
        Multiplayer.MultiplayerPeer = peer;
        GD.Print($"[net] server listening on {port}");
        return true;
    }

    public bool StartClient(string host, int port)
    {
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(host, port);
        if (err != Error.Ok)
        {
            GD.PushError($"[net] client failed to connect to {host}:{port}: {err}");
            return false;
        }
        Multiplayer.MultiplayerPeer = peer;
        GD.Print($"[net] connecting to {host}:{port}");
        return true;
    }
}
