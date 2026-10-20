using System.Net.Sockets;
using System.Threading.Tasks;
using Godot;
using UnitSport.Concert.Protocols;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Concert;

/// <summary>
/// <c>--concertnet A|B</c> with <c>--connect</c> (driven by <c>tools/concertnetcheck.sh</c>, #885): an
/// operator hosts a set and runs it from a controller, another player gets it.
/// <list type="bullet">
/// <item>A logs in (admin), hosts set 7 at the nearest stadium, turns its show input on (Art-Net and
/// OSC), then plays a controller: ArtDmx packets to its own port 6454 at 30 Hz, then an OSC blackout
/// to port 9000.</item>
/// <item>B must hear the set announced (seed 7, A its operator), receive the relayed universes with
/// the bytes A's controller sent, and get the blackout cue.</item>
/// <item>Then A pulls a rig over MVR-xchange (#960, loopback) from a station of its own and commits
/// ours back; B must get the pulled rig.</item>
/// </list>
/// </summary>
public partial class ConcertNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--concertnet");

    public ConcertNetProbe(ItemController items) : base(items, "concertnet", "CN", "concertnet_") { }
    public ConcertNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private const int Seed = 7;
    private static ConcertService? Svc => ConcertService.Instance;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (_role == "A") await RunA(); else await RunB();
        await Finish(2.0);
    }

    private async Task RunA()
    {
        Chat?.Send("/login test");
        await Seconds(1.5);
        Say("hello");
        if (!await Heard("B", "ready", 60)) { Fail("B never got ready"); return; }
        Chat?.Send($"/concert host {Seed}");
        Expect(await Until(() => Svc?.SeedFor(0) == Seed, 10), $"the server confirms set {Seed} hosted");
        Chat?.Send("/concert input artnet on");
        Chat?.Send("/concert input osc on");
        await Seconds(0.5);
        Expect(Svc?.Input is { ArtNetOn: true, OscOn: true }, "the show input listens (Art-Net 6454, OSC 9000)");

        // a controller: Art-Net to our own port, 30 frames a second for four seconds
        using var udp = new UdpClient();
        var slots = new byte[512];
        for (int frame = 0; frame < 120; frame++)
        {
            slots[0] = (byte)frame;
            slots[1] = 0xAB;
            slots[511] = 0xCD;
            var packet = ArtNet.EncodeDmx(0, (byte)frame, slots);
            udp.Send(packet, packet.Length, "127.0.0.1", ArtNet.Port);
            await Seconds(1.0 / 30);
        }
        Expect(Svc?.Input?.ArtNetPackets >= 100, $"the input read the controller's packets ({Svc?.Input?.ArtNetPackets})");
        var blackout = Osc.Encode("/concert/blackout", 1f);
        udp.Send(blackout, blackout.Length, "127.0.0.1", Osc.DefaultPort);
        if (!await Heard("B", "got", 30)) Fail("B never got the show");

        // an MVR rig on the set (#941): set 13's patch through the file format, sent as the operator
        var mvr = Mvr.MvrFile.Write(Mvr.RigData.FromPatch(new StagePlan(ConcertPlanner.Plan(Terrain.Stadiums.StadiumCatalog.Geneva, 13)).Patch, "net"), "net");
        var import = Mvr.MvrFile.Read(mvr, "netcheck.mvr", out string? error);
        Expect(import != null, $"A reads the MVR ({error})");
        if (import != null)
        {
            Svc?.SendRig(0, import.Rig.Pack());
            Say($"rig {import.Rig.Fixtures.Count}");
            if (!await Heard("B", "rig ok", 30)) Fail("B never got the rig");
            Svc?.SendRig(0, Array.Empty<byte>());
            if (!await Heard("B", "own ok", 30)) Fail("B never got the set's own rig back");
        }
        await Xchange();
        Chat?.Send("/concert release");
        Expect(await Until(() => Svc?.SeedFor(0) == null, 10), "released: tonight's show is back");
    }

    /// <summary>
    /// MVR-xchange (#960) on loopback: the game's station joins a station of the probe's own, pulls the
    /// rig it offers (saved, imported, sent to the set: B must get it), commits ours back, and leaves.
    /// </summary>
    private async Task Xchange()
    {
        Chat?.Send("/concert xchange on NetCheck local");
        var game = ConcertMvrTool.Station;
        Expect(game is { Listening: true }, "the game's MVR-xchange station listens on loopback");
        if (game == null) return;
        var desk = new Mvr.MvrXchangeStation("NetCheck", "Probe desk", Mvr.MvrXchange.NewUuid()) { SaveFolder = OS.GetUserDataDir() + "/concert/xchange_probe" };
        desk.Start(System.Net.IPAddress.Loopback, 0, discovery: false);
        try
        {
            var mvr = Mvr.MvrFile.Write(Mvr.RigData.FromPatch(new StagePlan(ConcertPlanner.Plan(Terrain.Stadiums.StadiumCatalog.Geneva, 21)).Patch, "xchange"), "xchange");
            desk.CommitFile(mvr, "NetCheck xchange.mvr", "set 21 from a lighting desk");
            Chat?.Send($"/concert xchange join 127.0.0.1:{desk.Port}");
            Expect(await Until(() => game.Offers().Length == 1, 10), $"the game joins the desk and sees its file ({game.Offers().Length} offered)");
            int n = Mvr.MvrFile.Read(mvr, "x.mvr", out _)?.Rig.Fixtures.Count ?? -1;
            Chat?.Send("/concert xchange pull 1");
            Say($"xrig {n}");
            if (!await Heard("B", "xrig ok", 30)) Fail("B never got the rig pulled over MVR-xchange");
            Svc?.SendRig(0, Array.Empty<byte>());
            if (!await Heard("B", "xown ok", 30)) Fail("B never got the set's own rig back after the pull");

            // the desk joins back, as mDNS would make it: a JOIN alone gives no address to reach the game at
            desk.Join(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, game.Port));
            Expect(await Until(() => desk.Peers().Any(p => p.Joined && p.EndPoint?.Port == game.Port), 10), "the desk joins the game back");
            Chat?.Send("/concert xchange commit from the net check");
            Expect(await Until(() => desk.Offers().Length == 1, 10), $"the desk hears the game's commit ({desk.Offers().Length})");
            if (desk.Offers().FirstOrDefault() is { } ours)
            {
                desk.Pull(ours);
                string? saved = null;
                string heard = "";
                Expect(await Until(() => { while (desk.Events.TryDequeue(out var e)) { saved ??= e.Saved; heard = e.Line; } return saved != null; }, 15), $"the desk pulls the game's rig ({heard})");
                string sent = System.IO.Path.Combine(ConcertMvrTool.FolderPath, ours.Commit.FileName);
                Expect(saved != null && System.IO.File.Exists(sent) && System.IO.File.ReadAllBytes(saved).AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(sent)),
                    $"byte for byte ({ours.Commit.FileName}, {ours.Commit.FileSize} bytes)");
            }
            Chat?.Send("/concert xchange off");
            Expect(ConcertMvrTool.Station == null && await Until(() => desk.Peers().All(p => !p.Joined), 10), "off: the game leaves the group");
        }
        finally { desk.Stop(); }
    }

    private async Task RunB()
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        Expect(await Until(() => Svc?.SeedFor(0) == Seed, 30), $"the set A hosts is announced (seed {Svc?.SeedFor(0)})");
        Expect(await Until(() => Svc?.FramesReceived >= 20, 30), $"A's universes arrive relayed ({Svc?.FramesReceived} frames)");
        var last = Svc?.LastFrame;
        Expect(last is { Length: 512 } && last[1] == 0xAB && last[511] == 0xCD, "with the bytes A's controller sent");
        Expect(await Until(() => Svc?.CuesOf(0) is { Blackout: true }, 20), "the OSC blackout reaches this peer as a cue");
        Say("got");

        // the rig A imports reaches this peer, and goes with A's "own rig"
        if (await HeardStarting("A", "rig ", 30) is { } said && int.TryParse(said[4..], out int n))
        {
            Expect(await Until(() => Svc?.RigFor(0)?.Fixtures.Count == n, 20), $"A's imported rig arrives ({Svc?.RigFor(0)?.Fixtures.Count} of {n} fixtures, from {Svc?.RigFor(0)?.Source})");
            Say("rig ok");
            Expect(await Until(() => Svc?.RigFor(0) == null, 20), "and goes when A gives the set its own rig back");
            Say("own ok");
        }
        else Fail("A never sent a rig");
        // the rig A pulls over MVR-xchange (#960) reaches this peer the same way
        if (await HeardStarting("A", "xrig ", 60) is { } x && int.TryParse(x[5..], out int m))
        {
            Expect(await Until(() => Svc?.RigFor(0)?.Fixtures.Count == m, 20), $"A's rig pulled over MVR-xchange arrives ({Svc?.RigFor(0)?.Fixtures.Count} of {m} fixtures)");
            Say("xrig ok");
            Expect(await Until(() => Svc?.RigFor(0) == null, 20), "and goes again");
            Say("xown ok");
        }
        else Fail("A never pulled a rig over MVR-xchange");
        Expect(await Until(() => Svc?.SeedFor(0) == null, 20), "and the release");
    }
}
