using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// <c>--crevassenet lead|follow</c> with <c>--connect</c> (driven by <c>tools/crevassenetcheck.sh</c>, #1372): a server on the flat
/// fixture and two clients, Lea (<c>lead</c>) and Tom (<c>follow</c>), a pretend glacier box on Lea. What Tom reads is what
/// reached him from Lea, the remote peer:
/// <list type="number">
/// <item>Lea, unroped, steps on a hidden strip: Tom sees her copy in <c>PoseCrevasse</c>, 10 m under him; she climbs out and Tom
/// sees her stand on the ground again;</item>
/// <item>Lea and Tom rope up, a second strip: Tom, standing, sees the rope hold her 3 m under the lip; he hauls with the held-E
/// path (<c>HaulTarget</c>, <c>AskHaul</c>) and Lea comes out;</item>
/// </list>
/// The script then reads the server's log: the stamina watch skipped Lea's body down the crevasse and flagged no climb.
/// </summary>
public partial class CrevasseNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--crevassenet");

    public CrevasseNetProbe(ItemController items) : base(items, "crevassenet", "CR", "crevassenet_") { }
    public CrevasseNetProbe() : this(null!) { }

    private static TogetherService Svc => TogetherService.Instance!;
    private static AdventureService Adventure => AdventureService.Instance!;
    private Vector3 _wish;
    private Vector3 _across;

    public override async void _Ready()
    {
        _role = Role ?? "LEAD";
        if (!await Joined(150, () => TogetherService.Instance != null && AdventureService.Instance != null)) return;
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        Expect(await Until(() => Adventure.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it");
        Me!.WalkControls = () => (_wish, false);
        if (_role == "LEAD") await Lead(); else await Follow();
        Me.WalkControls = null;
        await Finish(6.0);
    }

    private long OtherPeer() => Parties.Client!.Members.First(m => m.Peer != Multiplayer.GetUniqueId()).Peer;

    /// <summary>Two crevasse cells round the course's start: the same on both peers (pure), 40 m apart or more.</summary>
    private static List<(double E, double N, double Angle)> Cells()
    {
        var (e0, n0) = SpawnPoint.ParseTarget();
        var found = new List<(double E, double N, double Angle)>();
        for (double e = e0 - 70; e < e0 + 70 && found.Count < 2; e += Crevasses.Cell)
            for (double n = n0 - 70; n < n0 + 70 && found.Count < 2; n += Crevasses.Cell)
                if (Crevasses.InCell(e, n) is { } c && found.All(f => Math.Abs(f.E - c.E) + Math.Abs(f.N - c.N) > 40)) found.Add(c);
        return found;
    }

    private Vector3 World(double e, double n) => Me!.Origin!.ToWorld(e, n, Me.Global.Alt) + Vector3.Up * 0.5f;

    /// <summary>Lea's spot: 3 m to the side of the strip; sets the world direction straight across it.</summary>
    private Vector3 Side((double E, double N, double Angle) c)
    {
        double se = -Math.Sin(c.Angle), sn = Math.Cos(c.Angle);
        var from = World(c.E - se * 3, c.N - sn * 3);
        _across = ((World(c.E + se * 3, c.N + sn * 3) - from) with { Y = 0 }).Normalized();
        return from;
    }

    /// <summary>Tom's spot: 5 m along the strip and 3 m off it, standing on the surface.</summary>
    private Vector3 Partner((double E, double N, double Angle) c) =>
        World(c.E - Math.Cos(c.Angle) * 5 - Math.Sin(c.Angle) * 3, c.N - Math.Sin(c.Angle) * 5 + Math.Cos(c.Angle) * 3);

    // ---- Lea ---------------------------------------------------------------------------------------

    private async Task Lead()
    {
        Expect(await Heard("FOLLOW", "ready", 90), "Tom is on");
        Chat!.Send("/party invite Tom");
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "Lea's party holds Lea and Tom");
        var me = Me!;
        var (e0, n0) = SpawnPoint.ParseTarget();
        TogetherService.GlacierBox = (e0, n0, 150);
        var cells = Cells();
        if (cells.Count < 2) { Fail("two crevasse cells round the start"); return; }
        Say("cells");

        // 1. unroped
        Expect(await Heard("FOLLOW", "at0", 60), "Tom stands by the first strip");
        float surface = me.GlobalPosition.Y;
        me.DebugLaunch(Side(cells[0]), Vector3.Zero);
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(0.8);
        surface = me.GlobalPosition.Y;
        _wish = _across;
        Expect(await Until(() => Svc.InSlot, 6), "Lea's bridge breaks: the slot is open");
        _wish = Vector3.Zero;
        Expect(await Until(() => me.IsOnFloor() && me.GlobalPosition.Y < surface - 8f, 8), $"she lands on the ice floor ({me.GlobalPosition.Y - surface:F1} m)");
        Say("fell");
        Expect(await Heard("FOLLOW", "saw-fall", 30), "Tom saw her fall");
        await Seconds(1.0);
        me.DebugSetStamina(me.StaminaMeter.Max);
        _wish = _across;
        Expect(await Until(() => !Svc.InSlot, 45), $"Lea climbs out ({me.GlobalPosition.Y - surface:F1} m, climbing {me.IsClimbing})");
        _wish = Vector3.Zero;
        Expect(await Until(() => me.IsOnFloor() && !me.InCrevasse, 5), "the ground is solid under her again");
        Say("out");
        Expect(await Heard("FOLLOW", "saw-out", 30), "Tom saw her stand on the ground again");

        // 2. roped: both stand by the second strip first, then the rope (a teleport while tied would be pulled back)
        Expect(await Heard("FOLLOW", "at1", 60), "Tom stands by the second strip");
        me.DebugLaunch(Side(cells[1]), Vector3.Zero);
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(0.8);
        Chat.Send("/rope Tom");
        Expect(await Until(() => Svc.Links.Count == 1 && Svc.Links.ModeOf(Multiplayer.GetUniqueId()) == LinkMode.Rope, 30), "roped up with Tom");
        Say("roped");
        surface = me.GlobalPosition.Y;
        int caught = Svc.CrevassesCaught;
        _wish = _across;
        Expect(await Until(() => Svc.InSlot, 6), "the second bridge breaks");
        _wish = Vector3.Zero;
        Expect(Svc.CrevassesCaught == caught + 1, "Tom's rope holds the fall");
        Expect(await Until(() => Svc.Hanging(Multiplayer.GetUniqueId()), 4), "Lea hangs on the rope");
        await Seconds(1.5);
        float depth = surface - me.GlobalPosition.Y;
        Expect(depth > 1.5f && depth < 4.5f, $"{depth:F1} m under the lip, not on the floor");
        Say("hanging");
        Expect(await Heard("FOLLOW", "saw-hang", 30), "Tom saw her hang");
        Expect(await Heard("FOLLOW", "hauling", 30), "Tom hauls");
        Expect(await Until(() => !Svc.InSlot, 40), $"Lea is hauled out ({surface - me.GlobalPosition.Y:F1} m under the lip)");
        await Seconds(1.5);
        Expect(me.IsOnFloor() && !me.InCrevasse, "standing on solid ground");
        Say("hauled");
        Expect(await Heard("FOLLOW", "saw-rescue", 30), "Tom saw the rescue");
        Chat.Send("/rope off");
        Say("done");
    }

    // ---- Tom ---------------------------------------------------------------------------------------

    private async Task Follow()
    {
        var me = Me!;
        Say("ready");
        Expect(await Until(() => InviteService.Client?.Incoming.Any(v => v.Kind == Parties.Kind) == true, 90), "Tom is invited to Lea's party");
        if (InviteService.Client?.Incoming.FirstOrDefault(v => v.Kind == Parties.Kind) is { } party) InviteService.Client.Accept(party.Id);
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 20), "Tom's party holds both");
        Expect(await Heard("LEAD", "cells", 60), "Lea has her glacier");
        var cells = Cells();
        if (cells.Count < 2) { Fail("two crevasse cells round the start"); return; }
        long lea = OtherPeer();

        // 1. unroped: Tom stands near, Lea falls
        me.DebugLaunch(Partner(cells[0]), Vector3.Zero);
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(1.0);
        float ground = me.GlobalPosition.Y;
        Say("at0");
        Expect(await Until(() => Svc.Body(lea) is { PoseKind: FootPlayer.PoseCrevasse }, 40), "Tom sees Lea's copy in the crevasse pose");
        Expect(await Until(() => Svc.Body(lea) is { } b && b.GlobalPosition.Y < ground - 8f, 15),
            $"and 10 m under him ({(Svc.Body(lea) is { } l ? l.GlobalPosition.Y - ground : 0):F1} m)");
        Say("saw-fall");
        Expect(await Until(() => Svc.Body(lea) is { } b && b.PoseKind != FootPlayer.PoseCrevasse, 70), "Tom sees her pose go back to the walk's");
        Expect(await Until(() => Svc.Body(lea) is { } b && b.GlobalPosition.Y > ground - 1.5f, 10), "Lea stands on the ground again on Tom's screen");
        await Heard("LEAD", "out", 30);
        Say("saw-out");

        // 2. roped
        me.DebugLaunch(Partner(cells[1]), Vector3.Zero);
        await Until(() => me.IsOnFloor(), 5);
        await Seconds(1.0);
        ground = me.GlobalPosition.Y;
        Say("at1");
        Expect(await Until(() => InviteService.Client?.Incoming.Any(v => v.Kind == TogetherService.RopeKind) == true, 60), "Lea asks to rope up");
        if (InviteService.Client?.Incoming.FirstOrDefault(v => v.Kind == TogetherService.RopeKind) is { } rope) InviteService.Client.Accept(rope.Id);
        Expect(await Until(() => Svc.Links.Count == 1, 20), "the rope link is in Tom's book");
        await Heard("LEAD", "roped", 30);
        Expect(await Until(() => Svc.Body(lea) is { PoseKind: FootPlayer.PoseCrevasse }, 40), "Tom sees Lea fall into the second crevasse");
        Expect(await Heard("LEAD", "hanging", 30), "Lea hangs");
        Expect(await Until(() => Svc.Body(lea) is { } b && ground - b.GlobalPosition.Y is > 1.5f and < 4.5f, 10),
            $"Tom sees her hang 3 m under the lip ({(Svc.Body(lea) is { } l2 ? ground - l2.GlobalPosition.Y : 0):F1} m)");
        Say("saw-hang");
        Expect(await Until(() => Svc.HaulTarget(me) == lea, 10), "the prompt offers Tom the haul (E held at the rope)");
        Svc.AskHaul(true);
        Say("hauling");
        Expect(await Heard("LEAD", "hauled", 60), "Lea is out");
        Svc.AskHaul(false);
        Expect(await Until(() => Svc.Body(lea) is { } b && b.PoseKind != FootPlayer.PoseCrevasse && b.GlobalPosition.Y > ground - 1.5f, 15), "Tom sees Lea back on the surface");
        Say("saw-rescue");
        Expect(await Heard("LEAD", "done", 60), "Lea is done");
    }
}
