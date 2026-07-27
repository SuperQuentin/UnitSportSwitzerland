using Godot;

namespace UnitSport.Net;

/// <summary>Thin wrapper around ENetMultiplayerPeer setup for either role.</summary>
public partial class NetworkManager : Node
{
    public const int DefaultPort = 7777;
    public const int MaxClients = 32;

    /// <summary>
    /// Starts listening.
    /// </summary>
    /// <param name="port">UDP port. ENet is UDP — this is not a TCP listener.</param>
    /// <param name="bindIp">
    /// Interface to bind to, or null for all of them.
    /// <para>
    /// The default binds to the IPv6 wildcard with dual-stack, so the server answers on every
    /// interface — LAN, Tailscale, and anything a router forwards to it. Naming one address
    /// instead is how you expose the server on a tailnet <i>only</i>: bound to the 100.x
    /// address, a forwarded port on the public interface simply has nothing listening.
    /// </para>
    /// </summary>
    public bool StartServer(int port, string? bindIp = null)
    {
        var peer = new ENetMultiplayerPeer();

        if (!string.IsNullOrWhiteSpace(bindIp))
        {
            // Must be set before CreateServer; it is read when the host socket is opened.
            peer.SetBindIP(bindIp);
            GD.Print($"[net] binding to {bindIp} only");
        }

        var err = peer.CreateServer(port, MaxClients);
        if (err != Error.Ok)
        {
            GD.PushError(
                $"[net] server failed to listen on {port}: {err}. "
                + (err == Error.CantCreate
                    ? "Something else is already using that UDP port, or the bind address is "
                      + "not one of this machine's interfaces."
                    : string.Empty));
            return false;
        }

        Multiplayer.MultiplayerPeer = peer;

        bool boundToOne = !string.IsNullOrWhiteSpace(bindIp);

        GD.Print($"[net] server listening on UDP {port}"
                 + (boundToOne ? $" at {bindIp}" : " (all interfaces)"));

        // Listing every interface would be a lie when bound to one of them.
        foreach (string address in boundToOne ? [bindIp!] : LocalAddresses())
            GD.Print($"[net]   reachable at {address}:{port}");

        return true;
    }

    public bool StartClient(string host, int port)
    {
        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(host, port);
        if (err != Error.Ok)
        {
            GD.PushError($"[net] client failed to connect to {Format(host, port)}: {err}");
            return false;
        }
        Multiplayer.MultiplayerPeer = peer;
        GD.Print($"[net] connecting to {Format(host, port)}");
        return true;
    }

    /// <summary>
    /// Splits "host", "host:port", "[v6]" or "[v6]:port" into its parts.
    ///
    /// <para>
    /// A bare colon-split is wrong the moment IPv6 is involved, and Tailscale hands out an
    /// IPv6 address alongside the 100.x one: <c>fd7a:115c:a1e0::1</c> split on ':' yields
    /// "fd7a" as the host and tries "115c" as the port. Bracket form is the standard way to
    /// disambiguate, and an unbracketed address with more than one colon can only be IPv6.
    /// </para>
    /// </summary>
    public static (string Host, int Port) ParseEndpoint(string endpoint, int defaultPort = DefaultPort)
    {
        endpoint = endpoint.Trim();

        if (endpoint.StartsWith('['))
        {
            int close = endpoint.IndexOf(']');
            if (close > 0)
            {
                string inner = endpoint[1..close];
                string rest = endpoint[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out int bracketPort))
                    return (inner, bracketPort);
                return (inner, defaultPort);
            }
        }

        int colons = endpoint.Count(c => c == ':');

        // Exactly one colon means host:port. More than one means a bare IPv6 literal, which
        // carries no port of its own.
        if (colons == 1)
        {
            string[] parts = endpoint.Split(':');
            if (int.TryParse(parts[1], out int port)) return (parts[0], port);
            return (parts[0], defaultPort);
        }

        return (endpoint, defaultPort);
    }

    private static string Format(string host, int port) =>
        host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

    /// <summary>
    /// Addresses this machine can be reached on, so the operator can read one off the log
    /// instead of hunting for it. Link-local and loopback are skipped as not useful to share.
    /// </summary>
    private static IEnumerable<string> LocalAddresses()
    {
        foreach (string address in IP.GetLocalAddresses())
        {
            if (address.StartsWith("127.") || address == "::1") continue;
            if (address.StartsWith("169.254.") || address.StartsWith("fe80")) continue;
            if (address.Contains(':')) continue;   // IPv6 is noisy; the v4 list is what people use

            yield return address;
        }
    }

    /// <summary>Reads an optional "--bind &lt;ip&gt;" from the server command line.</summary>
    public static string? ParseBindArg()
    {
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--bind")
                return args[i + 1];
        return null;
    }
}
