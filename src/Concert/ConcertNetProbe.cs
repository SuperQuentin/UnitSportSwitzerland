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
            // and its scene objects (#958): one truss piece's mesh, three pieces of it
            var box = new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 0.4f, 0), new Vector3(0, 0, 0.4f) };
            import.Rig.Meshes.Add(Mvr.RigData.Quantise(box, new[] { 0, 1, 2, 0, 2, 3, 0, 3, 1, 1, 3, 2 }, Colors.Silver));
            for (int i = 0; i < 3; i++)
                import.Rig.Objects.Add(new Mvr.RigData.ObjectRow { Mesh = 0, Xf = Mvr.RigData.X(new Transform3D(Basis.Identity, new Vector3(i * 2f, 12f, 0))) });
            Svc?.SendRig(0, import.Rig.Pack());
            Say($"rig {import.Rig.Fixtures.Count}");
            if (!await Heard("B", "rig ok", 30)) Fail("B never got the rig");
            Svc?.SendRig(0, Array.Empty<byte>());
            if (!await Heard("B", "own ok", 30)) Fail("B never got the set's own rig back");
        }
        Chat?.Send("/concert release");
        Expect(await Until(() => Svc?.SeedFor(0) == null, 10), "released: tonight's show is back");
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
            Expect(Svc?.RigFor(0) is { Objects.Count: 3, Meshes.Count: 1 } r && r.Meshes[0].TriangleCount == 4, $"with its scene objects ({Svc?.RigFor(0)?.Objects.Count} pieces of 3)");
            if (ConcertRig.Live.Count > 0)
                Expect(await Until(() => ConcertRig.Live.Values.Any(r => r.ObjectsDrawn == 3), 20), "and the stage draws them");
            Say("rig ok");
            Expect(await Until(() => Svc?.RigFor(0) == null, 20), "and goes when A gives the set its own rig back");
            Say("own ok");
        }
        else Fail("A never sent a rig");
        Expect(await Until(() => Svc?.SeedFor(0) == null, 20), "and the release");
    }
}
