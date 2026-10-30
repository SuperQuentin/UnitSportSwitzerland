using Godot;
using UnitSport.Adventure;
using UnitSport.Concert;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.MontreuxJazz;

/// <summary>
/// <c>--montreuxnet a|b</c> with <c>--connect --festival test --jazzbpm 240 --festivalhour 21.5</c> (driven by <c>tools/montreuxnetcheck.sh</c>, #1394): Montreux Jazz over
/// a dedicated server whose calendar reads 10 July 2027 (day 9 of 16, held at the fixture start), its songs a few seconds long. Everything is asserted on the REMOTE
/// peers. Lea (A) and Tom (B) form a party, stand at the stage-front rail and mark themselves as listening (the Jazz pass arrives in each pack); both hear the same song to
/// its end and each is paid the group yield (7 or 8 Batzen, +25 % for the friend) and the same 15 points into their own sections, the party board holding both. At the
/// call for the bis A claps beats 0-5 and B beats 2-7 on the shared clock (stamped on the beat, as the client sends them): the crowd meter fills to 12, B reads A's claps
/// on its own screen, the server brings the band back and pays each of them the yield on 10 Batzen (12 or 13) and 25 points. A forged mark from 150 m and a clap
/// from afar are refused by the server.
/// </summary>
public partial class JazzNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--montreuxnet");

    private const string Instance = "montreuxjazz-2027";

    public JazzNetProbe(ItemController items) : base(items, "montreuxnet", "MJ", "montreuxnet_") { }
    public JazzNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault(Instance) ?? new FestivalInstance();

    private void Teleport(double e, double n)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(e, n, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private static InviteService Invites() => InviteService.Client!;

    private static JazzMoment Moment(ConcertEvent e) => MontreuxJazzRules.Moment(e, ClockSync.ServerNow);

    private async Task Clap(ConcertEvent e, int beat)
    {
        var mom = Moment(e);
        double target = Math.Floor(ClockSync.ServerNow / 86400.0) * 86400.0
            + JazzSet.SecondsOf(JazzSet.CallStart(mom.Set, e.SetSongs), e.Bpm) + (JazzSet.CallLeadBeats + beat) * MontreuxJazzRules.BeatSeconds(e);
        await Until(() => ClockSync.ServerNow >= target, 10);
        Svc.AskMontreux(FestivalService.MontreuxClap, target);
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && JazzGame.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        const string Id = MontreuxJazzRules.FestivalId;
        Expect(await Until(() => Svc.RunningOf(Id) is { Day: 9, Site: not null }, 40), "the server runs Montreux Jazz on day 9 and this client knows it");
        Expect(await Until(() => Me is { } me && me.IsOnFloor(), 30), "the player stands on the ground");
        var run = Svc.RunningOf(Id)!;
        var site = run.Site!;
        var e = FestivalService.MontreuxEvent(run);
        var told = new List<(JazzEvent What, int A, int B, string Text)>();
        Svc.MontreuxTold += (what, a, b, text) => told.Add((what, a, b, text));
        string? refused = null;
        Svc.Refused += why => refused = why;
        var (re, rn) = MontreuxJazzRules.Rail(site);
        bool lea = _role == "A";

        // a party of the two
        Say("ready");
        Expect(await Heard(lea ? "B" : "A", "ready", 120), "the other is on");
        if (lea)
        {
            Chat!.Send("/party invite Tom");
            Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "A's party holds A and B");
        }
        else
        {
            Expect(await Until(() => Invites().Incoming.Any(v => v.Kind == Parties.Kind), 40), "B is invited to A's party");
            if (Invites().Incoming.FirstOrDefault(v => v.Kind == Parties.Kind) is { } invite) Invites().Accept(invite.Id);
            Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "B's party holds A and B");
        }
        Say("party");
        Expect(await Heard(lea ? "B" : "A", "party", 60), "both are in the party");

        // forged requests first: from 150 m the server refuses both
        Teleport(site.E + 150, site.N + 150);
        await Seconds(1.2);
        refused = null;
        Svc.AskMontreux(FestivalService.MontreuxMark);
        Expect(await Until(() => refused?.Contains("rail") == true, 10), $"a mark from 150 m is refused by the server ({refused})");
        Expect(Count(ItemId.JazzPass) == 0 && !Svc.MontreuxListening, "no pass, not marked");

        // at the rail, a metre apart, before the set's song begins: the server knows where the bodies are
        Teleport(re, rn + (lea ? 0.6 : -0.6));
        await Seconds(1.5);
        Expect(await Until(() => JazzGame.Instance!.AtRail, 10), "the rail is at hand");
        Expect(await Until(() => Moment(e) is { Part: JazzPart.Song, Song: 0, BeatInPart: < 2.0 }, 120), "the first song of a set begins");
        JazzGame.TryInteract(Me!);
        Expect(await Until(() => Svc.MontreuxListening && Count(ItemId.JazzPass) == 1, 10), $"the server marked this listener and the Jazz pass arrived in the pack (refused: {refused})");

        // the song: stand for all of it, paid with the yield for the friend
        Expect(await Until(() => told.Any(t => t.What == JazzEvent.Song), 40), $"the server told this client its song was paid ({string.Join(" | ", told.Select(t => t.Text))})");
        var song = told.First(t => t.What == JazzEvent.Song);
        Expect(song.B is 7 or 8 && song.Text.Contains("1 friend"), $"6 Batzen with a friend: 7 or 8 ({song.B}: {song.Text})");
        Expect(await Until(() => Mine.Batzen == song.B && Mine.Points == MontreuxJazzRules.SongPoints, 10), $"it reached this player's own section: {Mine.Batzen} Batzen, {Mine.Points} points");
        Expect(await Until(() => Svc.BoardOf(Id) is { Points: 2 * MontreuxJazzRules.SongPoints } b && b.Members.Length == 2, 20),
            $"the party board holds both members' points ({Svc.BoardOf(Id)?.Points})");
        Say("song");
        Expect(await Heard(lea ? "B" : "A", "song", 60), "the other was paid for the same song");

        // the second song, then the call
        int batzen = Mine.Batzen;
        Expect(await Until(() => told.Count(t => t.What == JazzEvent.Song) == 2, 40), "the second song was paid too");
        Expect(await Until(() => Moment(e).Part == JazzPart.Call, 20), "the band leaves for the call");
        batzen = Mine.Batzen;
        int points = Mine.Points;
        // A claps beats 0-5, B beats 2-7: neither alone is 12
        int from = lea ? 0 : 2;
        for (int b = from; b < from + 6; b++) await Clap(e, b);
        Expect(await Until(() => Svc.MontreuxMeter == 12, 10), $"the meter reads 12 of 12 on this screen: the friend's claps came over the wire too ({Svc.MontreuxMeter})");
        Expect(await Until(() => told.Any(t => t.What == JazzEvent.Bis), 15), $"the band came back ({string.Join(" | ", told.TakeLast(2).Select(t => t.Text))})");
        var bis = told.Last(t => t.What == JazzEvent.Bis);
        Expect(bis.Text.Contains("The band is back"), $"told so ({bis.Text})");
        Expect(await Until(() => Mine.Batzen - batzen is 12 or 13 && Mine.Points - points == MontreuxJazzRules.EncorePoints, 10),
            $"10 Batzen with a friend (12 or 13) and 25 points, in this player's section ({Mine.Batzen - batzen}, {Mine.Points - points})");

        // a clap from afar is refused
        Teleport(site.E + 150, site.N + 150);
        await Seconds(1.2);
        Svc.AskMontreux(FestivalService.MontreuxClap, ClockSync.ServerNow);
        await Seconds(1.0);
        Expect(Mine.Batzen - batzen is 12 or 13, "a clap from 150 m changes nothing: no more Batzen");
        Say("done");
        Expect(await Heard(lea ? "B" : "A", "done", 150), "the other finished too");
        await Finish(2.0);
    }
}
