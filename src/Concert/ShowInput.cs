using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Godot;
using UnitSport.Concert.Protocols;

namespace UnitSport.Concert;

/// <summary>
/// The show operator's ears (#885): Art-Net (UDP 6454), sACN (UDP 5568, a multicast group per
/// universe) and OSC (UDP 9000 by default) from a controller on this machine or the LAN — Chataigne,
/// TouchDesigner, a console. Off until the operator turns it on (<c>/concert input …</c>); only the
/// operator's game opens these ports. Frames and cues are read on the receive loop's thread
/// (<see cref="Net.Udp.ReceiveLoop"/>) and handed over to the main thread, which gives them to the
/// <see cref="ConcertService"/> to play here and send to the server.
///
/// <para>
/// Universe numbering: the rig's universe <c>i</c> is Art-Net port-address <see cref="ArtNetBase"/> + i
/// and sACN universe <see cref="SacnBase"/> + i (sACN counts from 1), as the exported patch says.
/// Art-Net answers ArtPoll, so the controller lists the game as a node.
/// </para>
/// </summary>
public sealed class ShowInput : IDisposable
{
    public int ArtNetBase { get; init; }
    public int SacnBase { get; init; } = 1;
    public int OscPort { get; init; } = Osc.DefaultPort;
    public int Universes { get; init; } = 4;

    public bool ArtNetOn => _artnet != null;
    public bool SacnOn => _sacn != null;
    public bool OscOn => _osc != null;

    /// <summary>Frames in, newest wins per universe (the rig's numbering); the main thread drains it.</summary>
    private readonly ConcurrentDictionary<int, byte[]> _frames = new();
    /// <summary>OSC messages in, in order.</summary>
    public readonly ConcurrentQueue<OscMessage> Cues = new();

    private UdpClient? _artnet, _sacn, _osc;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Packets read so far, by protocol: what <c>/concert input</c> reports.</summary>
    public long ArtNetPackets, SacnPackets, OscPackets;

    public string StartArtNet()
    {
        if (_artnet != null) return "Art-Net already listening";
        try
        {
            _artnet = Bind(ArtNet.Port);
            _artnet.EnableBroadcast = true;
            var udp = _artnet;
            Task.Run(() => Net.Udp.ReceiveLoop(udp, _stop.Token, (p, from) =>
            {
                switch (ArtNet.OpCode(p))
                {
                    case ArtNet.OpDmx when ArtNet.ParseDmx(p) is { } dmx:
                        Interlocked.Increment(ref ArtNetPackets);
                        Take(dmx.Universe - ArtNetBase, dmx.Data);
                        break;
                    case ArtNet.OpPoll:
                        var local = LocalAddressFor(from.Address);
                        var reply = ArtNet.EncodePollReply(local.GetAddressBytes(), "UnitSport", "UnitSport Switzerland concert rig",
                            Enumerable.Range(ArtNetBase, Universes).ToArray());
                        udp.Send(reply, reply.Length, from);
                        break;
                }
            }));
            return $"Art-Net listening on UDP {ArtNet.Port}, universes {ArtNetBase}..{ArtNetBase + Universes - 1}";
        }
        catch (Exception e)
        {
            _artnet = null;
            return $"Art-Net could not listen on {ArtNet.Port}: {e.Message}";
        }
    }

    public string StartSacn()
    {
        if (_sacn != null) return "sACN already listening";
        try
        {
            _sacn = Bind(Sacn.Port);
            for (int u = 0; u < Universes; u++)
                try { _sacn.JoinMulticastGroup(IPAddress.Parse(Sacn.Multicast(SacnBase + u))); }
                catch (SocketException) { /* no multicast route: unicast still arrives */ }
            var udp = _sacn;
            Task.Run(() => Net.Udp.ReceiveLoop(udp, _stop.Token, (p, from) =>
            {
                if (Sacn.ParseData(p) is not { } dmx) return;
                Interlocked.Increment(ref SacnPackets);
                Take(dmx.Universe - SacnBase, dmx.Data);
            }));
            return $"sACN listening on UDP {Sacn.Port} (multicast {Sacn.Multicast(SacnBase)}..), universes {SacnBase}..{SacnBase + Universes - 1}";
        }
        catch (Exception e)
        {
            _sacn = null;
            return $"sACN could not listen on {Sacn.Port}: {e.Message}";
        }
    }

    public string StartOsc()
    {
        if (_osc != null) return "OSC already listening";
        try
        {
            _osc = Bind(OscPort);
            var udp = _osc;
            Task.Run(() => Net.Udp.ReceiveLoop(udp, _stop.Token, (p, from) =>
            {
                foreach (var m in Osc.Parse(p))
                {
                    Interlocked.Increment(ref OscPackets);
                    Cues.Enqueue(m);
                }
            }));
            return $"OSC listening on UDP {OscPort} (/concert/go, /concert/section, /concert/bpm, /concert/blackout, /concert/master)";
        }
        catch (Exception e)
        {
            _osc = null;
            return $"OSC could not listen on {OscPort}: {e.Message}";
        }
    }

    public void StopAll()
    {
        _artnet?.Dispose(); _artnet = null;
        _sacn?.Dispose(); _sacn = null;
        _osc?.Dispose(); _osc = null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        StopAll();
    }

    private static UdpClient Bind(int port)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        // another listener on the machine (a visualiser) may share the port
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        return udp;
    }

    private void Take(int universe, byte[] data)
    {
        if (universe < 0 || universe >= Universes) return;
        _frames[universe] = data;
    }

    /// <summary>Main thread: each universe that came in since the last call, newest frame only.</summary>
    public void Drain(Action<int, byte[]> take)
    {
        foreach (var key in _frames.Keys)
            if (_frames.TryRemove(key, out var data)) take(key, data);
    }

    /// <summary>The address of ours a poller reaches us on, for ArtPollReply.</summary>
    private static IPAddress LocalAddressFor(IPAddress remote)
    {
        if (IPAddress.IsLoopback(remote)) return IPAddress.Loopback;
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(remote, ArtNet.Port);
            return ((IPEndPoint)probe.LocalEndPoint!).Address;
        }
        catch (SocketException) { return IPAddress.Loopback; }
    }
}
