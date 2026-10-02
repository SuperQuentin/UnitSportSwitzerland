using System.Net;
using System.Net.Sockets;

namespace UnitSport.Net;

/// <summary>Shared UDP plumbing (<c>docs/notes/net/udp-receive-loop.md</c>).</summary>
public static class Udp
{
    /// <summary>
    /// Receives datagrams until <paramref name="token"/> is cancelled or <paramref name="udp"/> is
    /// disposed, handing each to <paramref name="handle"/> on a thread-pool thread. A
    /// <see cref="SocketException"/> (ICMP port unreachable reported on the next receive, Windows)
    /// is skipped; an exception from <paramref name="handle"/> (malformed packet) drops that packet only.
    /// </summary>
    public static async Task ReceiveLoop(UdpClient udp, CancellationToken token, Action<byte[], IPEndPoint> handle)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult got;
            try { got = await udp.ReceiveAsync(token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            try { handle(got.Buffer, got.RemoteEndPoint); }
            catch (Exception) { /* malformed packet or the asker went away: ignore it */ }
        }
    }
}
