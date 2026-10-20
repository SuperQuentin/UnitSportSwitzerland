using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using UnitSport.Concert.Mvr;
using Xunit;
using static UnitSport.Concert.Mvr.MvrXchange;

namespace UnitSport.Tests;

/// <summary>MVR-xchange (#960): the framing, the messages, mDNS records, and two stations trading a file on loopback.</summary>
public class MvrXchangeTests
{
    // ---- framing -------------------------------------------------------------------------------------

    [Fact]
    public void A_package_header_is_big_endian_as_the_spec_lays_it_out()
    {
        var p = Packet(TypeFile, new byte[] { 1, 2, 3 });
        Assert.Equal(HeaderSize + 3, p.Length);
        Assert.Equal(new byte[] { 0x00, 0x0B, 0xE1, 0xBA }, p[..4]);          // 778682
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, p[4..8]);                     // version
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, p[8..12]);                    // number, zero based
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, p[12..16]);                   // count
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, p[16..20]);                   // type: file
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 3 }, p[20..28]);       // uint64 length
        var h = ReadHeader(p)!.Value;
        Assert.Equal((1u, 0u, 1u, TypeFile, 3ul), (h.Version, h.Number, h.Count, h.Type, h.Length));
        Assert.Null(ReadHeader(p[..27]));
        p[0] = 9;
        Assert.Null(ReadHeader(p));
    }

    private static byte[] Package(uint number, uint count, uint type, byte[] payload)
    {
        var p = new byte[HeaderSize + payload.Length];
        WriteHeader(p, number, count, type, (ulong)payload.Length);
        payload.CopyTo(p, HeaderSize);
        return p;
    }

    [Fact]
    public async Task Messages_read_whole_one_package_or_several()
    {
        var json = Encoding.UTF8.GetBytes("{\"Type\":\"MVR_LEAVE_RET\",\"OK\":true}");
        var file = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        using var s = new MemoryStream();
        s.Write(Packet(TypeJson, json));
        for (uint i = 0; i < 3; i++) s.Write(Package(i, 3, TypeFile, file[(int)(i * 1000)..(int)((i + 1) * 1000)]));
        s.Write(Package(1, 1, TypeFile, new byte[] { 7 }));   // libMVRgdtf numbers its one file package 1
        s.Position = 0;
        var a = await ReadAsync(s, default);
        Assert.Equal("MVR_LEAVE_RET", TypeOf(a!.Json!));
        var b = await ReadAsync(s, default);
        Assert.Equal(TypeFile, b!.Type);
        Assert.Equal(file, b.Payload);
        Assert.Equal(new byte[] { 7 }, (await ReadAsync(s, default))!.Payload);
        Assert.Null(await ReadAsync(s, default));   // closed between messages: no error
    }

    [Fact]
    public async Task Malformed_packages_are_refused_not_read_as_lengths()
    {
        async Task Refused<T>(byte[] bytes) where T : Exception => await Assert.ThrowsAnyAsync<T>(() => ReadAsync(new MemoryStream(bytes), default));
        var good = Packet(TypeJson, Encoding.UTF8.GetBytes("{}"));
        var bad = (byte[])good.Clone();
        bad[3] ^= 1;
        await Refused<InvalidDataException>(bad);                                                       // magic
        await Refused<InvalidDataException>(Package(0, 1, 7, new byte[1]));                             // type
        var huge = Package(0, 1, TypeJson, Array.Empty<byte>());
        WriteHeader(huge, 0, 1, TypeJson, MaxJson + 1ul);
        await Refused<InvalidDataException>(huge);                                                      // JSON cap
        WriteHeader(huge, 0, 1, TypeFile, (ulong)MaxFile + 1);
        await Refused<InvalidDataException>(huge);                                                      // file cap
        WriteHeader(huge, 0, 1, TypeFile, ulong.MaxValue);
        await Refused<InvalidDataException>(huge);
        await Refused<InvalidDataException>(Package(0, MaxPackages + 1, TypeFile, new byte[1]));
        await Refused<EndOfStreamException>(good[..20]);                                                // cut in the header
        await Refused<EndOfStreamException>(good[..^1]);                                                // cut in the payload
        var mixed = Package(0, 2, TypeFile, new byte[4]).Concat(Package(1, 2, TypeJson, new byte[4])).ToArray();
        await Refused<InvalidDataException>(mixed);                                                     // another message's package
    }

    // ---- messages ------------------------------------------------------------------------------------

    [Fact]
    public void Join_and_commit_carry_the_specs_field_names()
    {
        var c = new Commit("0f5c6d1e-1111-4222-8333-444455556666", "a7669ff9-bd61-4486-aea6-c190f8ba6b8c", 1234, "Rig.mvr", "first plot");
        var join = Parse(Packet(JoinMessage("Quentin", "4aa291a1-1a62-45fe-aabc-e90e5e2399a8", new[] { c }))[HeaderSize..])!;
        Assert.Equal("MVR_JOIN", join["Type"]!.GetValue<string>());
        Assert.Equal("UnitSport", join["Provider"]!.GetValue<string>());
        Assert.Equal("Quentin", join["StationName"]!.GetValue<string>());
        Assert.Equal(1, join["verMajor"]!.GetValue<int>());
        Assert.Equal(6, join["verMinor"]!.GetValue<int>());
        Assert.Null(join["OK"]);
        var back = Assert.Single(CommitsOf(join));
        Assert.Equal((c.FileUuid, c.StationUuid, 1234L, "Rig.mvr", "first plot"), (back.FileUuid, back.StationUuid, back.FileSize, back.FileName, back.Comment));

        var ret = JoinMessage("Q", "x", Array.Empty<Commit>(), answer: true);
        Assert.Equal(("MVR_JOIN_RET", true, ""), (TypeOf(ret), Bool(ret, "OK"), Str(ret, "Message")));

        var commit = c.ToJson();
        foreach (var key in new[] { "Type", "verMajor", "verMinor", "FileSize", "FileUUID", "StationUUID", "ForStationsUUID", "Comment", "FileName" })
            Assert.True(commit.ContainsKey(key), key);
        Assert.Equal("MVR_COMMIT", TypeOf(commit));

        var req = RequestMessage("f", "s");
        Assert.Equal("s", Assert.Single(Uuids(req, "FromStationUUID")));
        Assert.Equal("f", Str(req, "FileUUID"));
        Assert.Equal("MVR_LEAVE", TypeOf(LeaveMessage("s")));
        Assert.Equal("s", Str(LeaveMessage("s"), "FromStationUUID"));
    }

    [Fact]
    public void Readers_take_what_real_stations_send()
    {
        // numbers and bools as strings, the request's station as one string, LEAVE's as StationUUID, an older Files list
        var m = Parse(Encoding.UTF8.GetBytes(
            "{\"Type\":\"MVR_JOIN_RET\",\"OK\":\"true\",\"verMajor\":\"1\",\"StationUUID\":\"S\",\"FromStationUUID\":\"abc\"," +
            "\"Files\":[{\"FileUUID\":\"F\",\"FileSize\":\"42\"},{\"FileSize\":3},7]}"))!;
        Assert.True(Bool(m, "OK"));
        Assert.Equal(1, Int(m, "verMajor"));
        Assert.Equal(new[] { "abc" }, Uuids(m, "FromStationUUID"));
        var c = Assert.Single(CommitsOf(m));   // the one without a FileUUID and the number are skipped
        Assert.Equal(("F", "S", 42L), (c.FileUuid, c.StationUuid, c.FileSize));
        Assert.Equal("S", LeaverOf(Parse(Encoding.UTF8.GetBytes("{\"Type\":\"MVR_LEAVE\",\"StationUUID\":\"S\"}"))!));
        Assert.Empty(Uuids(Parse(Encoding.UTF8.GetBytes("{\"FromStationUUID\":\"\"}"))!, "FromStationUUID"));
        Assert.True(Same("A7669FF9-BD61-4486-AEA6-C190F8BA6B8C", "a7669ff9-bd61-4486-aea6-c190f8ba6b8c"));
        Assert.True(new Commit("f", "s", 0, "", "") { ForStations = new[] { "ME" } }.For("me"));
        Assert.False(new Commit("f", "s", 0, "", "") { ForStations = new[] { "other" } }.For("me"));

        Assert.Null(Parse(Encoding.UTF8.GetBytes("[1,2]")));
        Assert.Null(Parse(Encoding.UTF8.GetBytes("{\"Type\":")));
        Assert.Null(Parse(new byte[] { 0xFF, 0xFE, 0x7B }));
        Assert.Null(Parse(Encoding.UTF8.GetBytes(new string('[', 100) + new string(']', 100))));
    }

    [Fact]
    public void Pulled_files_get_safe_names()
    {
        MvrXchangeStation.Offer O(string name) => new(new Commit("0f5c6d1e-1111", "s", 1, name, ""), "Desk 1", null, false);
        Assert.Equal("Show.mvr", MvrXchangeStation.FileNameFor(O("Show.mvr")));
        Assert.Equal("evil.mvr", MvrXchangeStation.FileNameFor(O("..\\..\\Windows\\evil.mvr")));
        Assert.Equal("passwd.mvr", MvrXchangeStation.FileNameFor(O("/etc/passwd")));
        Assert.Equal("Desk 1 0f5c6d1e.mvr", MvrXchangeStation.FileNameFor(O("")));
        Assert.Equal("Desk 1 0f5c6d1e.mvr", MvrXchangeStation.FileNameFor(O("..")));
        Assert.Equal("a_b.mvr", MvrXchangeStation.FileNameFor(O("a*b")));
        Assert.Equal("Default", MvrXchangeStation.CleanGroup(" "));
        Assert.Equal("MyShow_2", MvrXchangeStation.CleanGroup("My Show_2!"));
    }

    // ---- mDNS ----------------------------------------------------------------------------------------

    [Fact]
    public void A_stations_announcement_reads_back_as_its_group_name_uuid_and_port()
    {
        var p = DnsSd.Announce(Service, "MyShow." + Service, "UnitSport-4aa291a1.local", 42424, IPAddress.Parse("192.168.1.50"),
            new[] { "StationName=Front of house", "StationUUID=4aa291a1-1a62-45fe-aabc-e90e5e2399a8" });
        var m = DnsSd.Parse(p)!;
        Assert.True(m.Response);
        var s = Assert.Single(DnsSd.Services(m, IPAddress.Loopback, Service));
        Assert.Equal(("MyShow", "Front of house", "4aa291a1-1a62-45fe-aabc-e90e5e2399a8", "192.168.1.50", 42424, false),
            (s.Group, s.StationName, s.StationUuid, s.Address.ToString(), s.Port, s.Gone));
        var bye = DnsSd.Services(DnsSd.Parse(DnsSd.Announce(Service, "MyShow." + Service, "h.local", 1, IPAddress.Loopback, Array.Empty<string>(), 0))!, IPAddress.Loopback, Service);
        Assert.True(Assert.Single(bye).Gone);

        var q = DnsSd.Parse(DnsSd.Query((Service, DnsSd.Ptr)))!;
        Assert.False(q.Response);
        Assert.Equal((Service, DnsSd.Ptr), (Assert.Single(q.Questions).Name, q.Questions[0].Type));

        Assert.Equal("Default", DnsSd.GroupOf("Default._mvrxchange._tcp.local.", Service));
        Assert.Equal("Default", DnsSd.GroupOf("Perastage on PC.Default._mvrxchange._tcp.local", Service));   // Perastage's station.group
        Assert.Null(DnsSd.GroupOf("_mvrxchange._tcp.local", Service));
        Assert.Null(DnsSd.GroupOf("x._http._tcp.local", Service));
    }

    [Fact]
    public void Compressed_names_read_and_malformed_ones_do_not_crash()
    {
        // a PTR answer whose target points back into the question's name (as responders compress)
        var b = new List<byte> { 0, 0, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0 };
        void Name(params string[] labels) { foreach (var l in labels) { b.Add((byte)l.Length); b.AddRange(Encoding.ASCII.GetBytes(l)); } }
        Name("_mvrxchange", "_tcp", "local"); b.Add(0); b.AddRange(new byte[] { 0, 12, 0, 1 });
        b.AddRange(new byte[] { 0xC0, 12, 0, 12, 0, 1, 0, 0, 0, 120 });
        b.AddRange(new byte[] { 0, 6 }); Name("Grp"); b.AddRange(new byte[] { 0xC0, 12 });
        var m = DnsSd.Parse(b.ToArray())!;
        Assert.Equal("Grp._mvrxchange._tcp.local", Assert.Single(m.Records).Target);
        var missing = new List<string>();
        Assert.Empty(DnsSd.Services(m, IPAddress.Loopback, Service, missing));   // no SRV yet: ask for it
        Assert.Equal("Grp._mvrxchange._tcp.local", Assert.Single(missing));

        var loop = new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 12, 0, 1 };
        Assert.Null(DnsSd.Parse(loop));
        Assert.Null(DnsSd.Parse(b.ToArray()[..^3]));
        Assert.Null(DnsSd.Parse(new byte[5]));
        var rnd = new Random(960);
        for (int i = 0; i < 2000; i++)
        {
            var junk = new byte[rnd.Next(0, 200)];
            rnd.NextBytes(junk);
            DnsSd.Parse(junk);   // never throws
        }
    }

    // ---- two stations on loopback --------------------------------------------------------------------

    private static async Task<MvrXchangeStation.Event> Next(MvrXchangeStation s, Func<MvrXchangeStation.Event, bool> want, int ms = 10000)
    {
        var seen = new List<string>();
        for (var until = DateTime.UtcNow.AddMilliseconds(ms); DateTime.UtcNow < until;)
        {
            while (s.Events.TryDequeue(out var e))
            {
                seen.Add(e.Line);
                if (want(e)) return e;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("no such event; heard: " + string.Join(" | ", seen));
    }

    private static MvrXchangeStation Station(string name, string folder)
    {
        var s = new MvrXchangeStation("Test", name, NewUuid()) { SaveFolder = folder };
        s.Start(IPAddress.Loopback, 0, discovery: false);
        return s;
    }

    [Fact]
    public async Task Two_stations_join_commit_and_pull_a_file_byte_identical()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mvrx-" + Guid.NewGuid().ToString("N")[..8]);
        var a = Station("Desk A", Path.Combine(dir, "a"));
        var b = Station("Desk B", Path.Combine(dir, "b"));
        try
        {
            var rnd = new Random(1);
            var early = new byte[200_000];
            rnd.NextBytes(early);
            a.CommitFile(early, "Early.mvr", "before anyone joined");

            // B joins A: A's JOIN_RET lists the early file
            b.Join(new IPEndPoint(IPAddress.Loopback, a.Port));
            await Next(b, e => e.Line.Contains("joined Desk A"));
            await Next(a, e => e.Line.Contains("Desk B (UnitSport) joined"));
            var offer = Assert.Single(b.Offers());
            Assert.Equal(("Early.mvr", 200_000L, "Desk A"), (offer.Commit.FileName, offer.Commit.FileSize, offer.StationName));

            b.Pull(offer);
            var got = await Next(b, e => e.Saved != null);
            Assert.Equal(early, File.ReadAllBytes(got.Saved!));
            Assert.EndsWith("Early.mvr", got.Saved);

            // A joins B too (as mDNS would make it), then commits a bigger one: B hears the COMMIT and pulls it
            a.Join(new IPEndPoint(IPAddress.Loopback, b.Port));
            await Next(a, e => e.Line.Contains("joined Desk B"));
            var big = new byte[3_000_000];
            rnd.NextBytes(big);
            Assert.Contains("to 1 station", a.CommitFile(big, "Late.mvr", "the stage moved"));
            var heard = await Next(b, e => e.Line.Contains("committed Late.mvr"));
            Assert.Contains("/concert xchange pull 2", heard.Line);
            b.Pull(b.Offers()[1]);
            got = await Next(b, e => e.Saved != null);
            Assert.Equal(big, File.ReadAllBytes(got.Saved!));

            // a file A never had: a REQUEST_RET, not a hang
            b.Pull(offer with { Commit = offer.Commit with { FileUuid = NewUuid() } });
            Assert.Contains("not available", (await Next(b, e => e.Line.Contains("did not send"))).Line);

            // B leaves: A marks it
            b.Stop();
            await Next(a, e => e.Line.Contains("Desk B left"));
            Assert.False(a.Peers().Single().Joined);
        }
        finally
        {
            a.Stop();
            b.Stop();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_station_survives_garbage_and_answers_the_next_peer()
    {
        var a = Station("Desk A", Path.GetTempPath());
        try
        {
            foreach (var junk in new[] { Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"), Package(0, 1, TypeJson, Encoding.UTF8.GetBytes("{nope")), new byte[3] })
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, a.Port);
                var stream = tcp.GetStream();
                await stream.WriteAsync(junk);
                tcp.Client.Shutdown(SocketShutdown.Send);
                var buf = new byte[64];
                tcp.ReceiveTimeout = 5000;
                Assert.Equal(0, stream.Read(buf));   // dropped, no answer
            }
            // a client that sends one JOIN per connection and reads the answer (BlenderDMX, libMVRgdtf)
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, a.Port);
            await WriteAsync(c.GetStream(), JoinMessage("Other", NewUuid(), Array.Empty<Commit>()), default);
            var ret = (await ReadAsync(c.GetStream(), default))!.Json!;
            Assert.Equal("MVR_JOIN_RET", TypeOf(ret));
            Assert.Equal(a.StationUuid, Str(ret, "StationUUID"));
            // and a second message on the same connection
            await WriteAsync(c.GetStream(), RequestMessage("", a.StationUuid), default);
            var none = (await ReadAsync(c.GetStream(), default))!.Json!;
            Assert.Equal(("MVR_REQUEST_RET", false), (TypeOf(none), Bool(none, "OK")));
        }
        finally { a.Stop(); }
    }

    // ---- WebSocket mode ------------------------------------------------------------------------------

    [Fact]
    public async Task WebSocket_mode_joins_pulls_and_serves_through_a_server()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
        using var http = new HttpListener();
        http.Prefixes.Add($"http://localhost:{port}/");
        http.Start();
        string dir = Path.Combine(Path.GetTempPath(), "mvrx-ws-" + Guid.NewGuid().ToString("N")[..8]);
        var a = new MvrXchangeStation("Test", "Desk A", NewUuid()) { SaveFolder = dir };
        var theirs = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray();
        var ours = Enumerable.Range(0, 50_000).Select(i => (byte)(i * 3)).ToArray();
        try
        {
            var server = Task.Run(async () =>
            {
                var ctx = await http.GetContextAsync();
                var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                async Task<(WebSocketMessageType, byte[])> Receive()
                {
                    var buf = new byte[1 << 20];
                    using var m = new MemoryStream();
                    WebSocketReceiveResult r;
                    do { r = await ws.ReceiveAsync(buf, default); m.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
                    return (r.MessageType, m.ToArray());
                }
                Task Send(JsonObject o) => ws.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, default);

                var join = Parse((await Receive()).Item2)!;
                Assert.Equal("MVR_JOIN", TypeOf(join));
                await Send(JoinMessage("Server", "server-uuid", new[] { new Commit("their-file", "desk-c", theirs.Length, "Theirs.mvr", "") }, answer: true));
                var req = Parse((await Receive()).Item2)!;
                Assert.Equal(("MVR_REQUEST", "their-file"), (TypeOf(req), Str(req, "FileUUID")));
                await ws.SendAsync(theirs, WebSocketMessageType.Binary, true, default);
                var commit = Parse((await Receive()).Item2)!;
                Assert.Equal("MVR_COMMIT", TypeOf(commit));
                await Send(Ret(CommitRet, true));
                await Send(RequestMessage(Str(commit, "FileUUID"), a.StationUuid));
                var (type, file) = await Receive();
                Assert.Equal(WebSocketMessageType.Binary, type);
                return file;
            });

            a.ConnectWebSocket(new Uri($"ws://localhost:{port}/"));
            await Next(a, e => e.Line.Contains("over WebSocket: 1 file"));
            var offer = Assert.Single(a.Offers());
            Assert.True(offer.ViaWebSocket);
            a.Pull(offer);
            var got = await Next(a, e => e.Saved != null);
            Assert.Equal(theirs, File.ReadAllBytes(got.Saved!));
            Assert.Contains("WebSocket server", a.CommitFile(ours, "Ours.mvr", "from the game"));
            Assert.Equal(ours, await server.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            a.Stop();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
