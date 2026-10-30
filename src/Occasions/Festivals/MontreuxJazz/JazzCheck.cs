using Godot;
using UnitSport.Adventure;
using UnitSport.Concert;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.MontreuxJazz;

/// <summary>
/// <c>--montreuxcheck --festival test --date 2027-07-10 --jazzbpm 300 --festivalhour 21.5 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c>
/// (quick tier, #1394): offline, this client plays the server's part on its own scratch folder (<c>test_output/montreuxcheck</c>). Montreux Jazz (held at the fixture's
/// start under <c>--festival test</c>) is on its day 9 of 16, its songs a few seconds long at 300 BPM. The check plays the real game: away from the rail a
/// forged mark is refused and, with the sky off the evening, nothing is playing; at the rail E marks the listener (the Jazz pass comes once); a marked player who
/// stays for a whole song is paid when it ends (6 Batzen and 15 points), one who steps out of the audience before it ends, and one who lurks at 36 m, are paid
/// nothing; a call for the bis with only 8 Perfect claps of the player's own brings no band, with 4 more from a bot (a stranger, never paid) it does and pays
/// the player 10 Batzen; a clap off the beat does not count; the Jazz Night (day 8) pays half as much again; the closing night's last song adds the finale
/// bonus; a bot is counted and paid nothing; outside its dates the festival is off. Windowed it saves <c>test_output/montreux_*.png</c>.
/// </summary>
public partial class JazzCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--montreuxcheck");

    private readonly Func<FootPlayer?> _me;

    public JazzCheck(ItemController items, Func<FootPlayer?> me) : base(items, "montreuxcheck", "", "montreux_") => _me = me;
    public JazzCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static FestivalService Svc => FestivalService.Instance!;
    private const string Instance = "montreuxjazz-2027";

    private FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault(Instance) ?? new FestivalInstance();

    private void Teleport(double e, double n)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(e, n, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    /// <summary>A point of the audience: <paramref name="ahead"/> m past the stage front, <paramref name="across"/> m to the side.</summary>
    private void Stand(ResolvedSite site, float ahead, float across = 0f)
    {
        var (e, n) = MontreuxJazzRules.FromFrame(site.E, site.N, StadiumAzimuth, across, MontreuxJazzRules.FrontV + ahead);
        Teleport(e, n);
    }

    private static float StadiumAzimuth => Terrain.Stadiums.StadiumCatalog.MontreuxJazzOpenAir.AzimuthDeg;

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private JazzMoment Moment(ConcertEvent e) => MontreuxJazzRules.Moment(e, Net.ClockSync.ServerNow);

    /// <summary>Until the show's clock is at the start of a song (its first two beats): early enough to mark and still stand for 80 % of it.</summary>
    private Task<bool> UntilSongStart(ConcertEvent e, int? song = null) =>
        Until(() => Moment(e) is { Part: JazzPart.Song, BeatInPart: < 2.0 } m && (song == null || m.Song == song), 120);

    /// <summary>One clap on the ring's beat <paramref name="beat"/> of the call now on, stamped exactly on it; returns when the beat has passed.</summary>
    private async Task Clap(ConcertEvent e, int beat, bool bot = false)
    {
        var mom = Moment(e);
        double callStart = JazzSet.SecondsOf(JazzSet.CallStart(mom.Set, e.SetSongs), e.Bpm);
        double at = callStart + (JazzSet.CallLeadBeats + beat) * MontreuxJazzRules.BeatSeconds(e);
        double day = Math.Floor(Net.ClockSync.ServerNow / 86400.0) * 86400.0;
        double target = day + at;
        await Until(() => Net.ClockSync.ServerNow >= target, 10);
        if (bot) Svc.ServeMontreux(-9, FestivalService.MontreuxClap);
        else Svc.AskMontreux(FestivalService.MontreuxClap, target);
    }

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && JazzGame.Instance != null && Me is { } me && me.IsOnFloor()
                && FestivalService.Instance.RunningOf(MontreuxJazzRules.FestivalId) != null, 90))
        {
            Fail($"no Montreux Jazz (service {FestivalService.Instance != null}, game {JazzGame.Instance != null}, body {Me?.Global})");
            return;
        }
        var game = JazzGame.Instance!;
        const string Id = MontreuxJazzRules.FestivalId;
        var run = Svc.RunningOf(Id)!;
        var site = run.Site!;
        var local = AdventureService.LocalPeer;
        var e = FestivalService.MontreuxEvent(run);
        double spb = MontreuxJazzRules.BeatSeconds(e);

        Expect(run.Day == 9 && run.Length == 16, $"day 9 of 16 ({run.Day}/{run.Length})");
        Expect(site is { Relocated: false }, $"held at the fixture's start ({site})");
        Expect(FestivalCatalog.Get(Id)!.Problems().Count == 0 && !FestivalCatalog.Get(Id)!.Relocates, "the festival definition is sound and does not relocate");
        Expect(e.Genre == Genre.Jazz && e.SetSongs == 2 && Math.Abs(e.Bpm - 300f) < 0.01f, $"the night is a jazz night of two songs at 300 BPM ({e.Genre}, {e.SetSongs}, {e.Bpm})");
        Expect(Mine.Batzen == 0 && Count(ItemId.JazzPass) == 0, "a fresh player holds nothing");
        string? refused = null;
        Svc.Refused += why => refused = why;
        var told = new List<JazzEvent>();
        var lines = new List<string>();
        Svc.MontreuxTold += (what, _, _, text) => { told.Add(what); if (text.Length > 0) lines.Add(text); };

        // ---- away from the rail, and with the sky off the evening ------------------------------------------
        Stand(site, 60f);
        await Seconds(0.4);
        Expect(JazzGame.InteractionFor(Me!) == null && !JazzGame.TryInteract(Me!), "no prompt away from the rail, E does nothing");
        Expect(Svc.ServeMontreux(local, FestivalService.MontreuxMark) != null && refused?.Contains("rail") == true, $"a forged mark from afar is refused ({refused})");
        var (re, rn) = MontreuxJazzRules.Rail(site);
        Teleport(re, rn);
        Expect(await Until(() => game.AtRail, 5), "the rail is at hand");
        Expect(JazzGame.InteractionFor(Me!) is { } listen && listen.Text.Contains("Listen to the band"), $"the rail offers it ({JazzGame.InteractionFor(Me!)?.Text})");
        Svc.Hour = () => 12.0;
        refused = null;
        Expect(Svc.ServeMontreux(local, FestivalService.MontreuxMark) != null && refused?.Contains("Nothing is playing") == true, $"at noon nothing is playing ({refused})");
        Svc.Hour = () => 21.5;
        Expect(Count(ItemId.JazzPass) == 0, "no pass for a refused mark");

        // ---- a song: stay for all of it ----------------------------------------------------------------------
        Expect(await UntilSongStart(e, 0), "a set begins with its first song");
        Teleport(re, rn);
        await Seconds(0.3);
        Expect(JazzGame.TryInteract(Me!), "E marks the listener");
        Expect(await Until(() => Svc.MontreuxListening && Count(ItemId.JazzPass) == 1, 5), $"marked, and the Jazz pass is in the pack ({Count(ItemId.JazzPass)})");
        Expect(ItemDefs.Get(ItemId.JazzPass) is { } && (int)ItemId.JazzPass == 511, "the pass is item 511");
        Stand(site, 14f, 3f);
        double songSeconds = MontreuxJazzRules.SongSeconds(e);
        Expect(songSeconds is > 15 and < 25, $"a song lasts {songSeconds:F1} s at this tempo");
        int paid = Svc.MontreuxSongsPaid, before = Mine.Batzen;
        Expect(await Until(() => Svc.MontreuxSongsPaid == paid + 1, 40), $"the song paid when it ended ({Svc.MontreuxSongsPaid})");
        Expect(Mine.Batzen == before + MontreuxJazzRules.SongBatzen && Mine.Points == MontreuxJazzRules.SongPoints, $"6 Batzen and 15 points ({Mine.Batzen}, {Mine.Points})");
        Expect(told.Contains(JazzEvent.Song) && lines.Any(l => l.Contains("A song heard")), "and a toast said so");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(0.8);
            GD.Print($"[montreuxcheck] shot {Shot("listening")}");
        }

        // ---- the next song, out of the audience: nothing ---------------------------------------------------------
        Expect(await UntilSongStart(e, 1), "the second song begins");
        Stand(site, 36.5f);
        await Seconds(0.5);
        Expect(!MontreuxJazzRules.InZone(site, Me!.Global.E, Me.Global.N), "the lurker stands 36.5 m from the stage: outside the zone");
        paid = Svc.MontreuxSongsPaid; before = Mine.Batzen;
        Expect(await Until(() => Moment(e).Part != JazzPart.Song, 40), "the second song ends");
        await Seconds(0.6);
        Expect(Svc.MontreuxSongsPaid == paid && Mine.Batzen == before, $"a lurker outside the zone was paid nothing ({Mine.Batzen - before})");
        Expect(await Until(() => !Svc.MontreuxListening, 5), "and the mark went when the zone was left");

        // ---- the call: 8 Perfect claps alone are not 12 ------------------------------------------------------------
        Expect(await Until(() => Moment(e).Part == JazzPart.Call, 10), "the band leaves for the call");
        Stand(site, 10f);
        await Seconds(0.2);
        Expect(await Until(() => game.Calling, 5), "the clap ring is up in the audience");
        int claps = Svc.MontreuxClaps;
        int returned = Svc.MontreuxBisReturned, failed = Svc.MontreuxBisFailed;
        for (int b = 0; b < JazzSet.CallBeats; b++) await Clap(e, b);
        await Seconds(0.3);
        Expect(Svc.MontreuxClaps == claps + 8 && Svc.MontreuxMeter == 8, $"eight Perfect claps counted, the meter says {MontreuxJazzRules.MeterText(Svc.MontreuxMeter)} ({Svc.MontreuxClaps - claps})");
        before = Mine.Batzen;
        Expect(await Until(() => Moment(e).Part != JazzPart.Call, 10), "the call ends");
        Expect(await Until(() => told.Contains(JazzEvent.NoBis), 5) && Svc.MontreuxBisFailed == failed + 1 && Svc.MontreuxBisReturned == returned, $"the crowd was too quiet: no bis ({lines.LastOrDefault()})");
        Expect(Mine.Batzen == before, "and nothing was paid");

        // ---- the next set's call: the player's 8 and a bot's 4 make 12 ---------------------------------------------
        Expect(await Until(() => Moment(e).Part == JazzPart.Call, 80), "a second call comes");
        Stand(site, 10f);
        await Seconds(0.2);
        before = Mine.Batzen; int points = Mine.Points;
        int botActs = Svc.MontreuxBotActs;
        for (int b = 0; b < JazzSet.CallBeats; b++)
        {
            await Clap(e, b);
            if (b < 4) await Clap(e, b, bot: true);
        }
        await Seconds(0.3);
        Expect(Svc.MontreuxBotActs == botActs + 4, "four bot claps were counted");
        Expect(Svc.MontreuxMeter >= 8, $"the meter filled to {MontreuxJazzRules.MeterText(Svc.MontreuxMeter)} for this client's own 8");
        returned = Svc.MontreuxBisReturned; int bisPaid = Svc.MontreuxBisPaid;
        Expect(await Until(() => Moment(e).Part != JazzPart.Call, 10), "the call ends");
        Expect(await Until(() => Svc.MontreuxBisReturned == returned + 1 && Svc.MontreuxBisPaid == bisPaid + 1, 5), $"the band is back and the player is paid ({Svc.MontreuxBisPaid})");
        Expect(Mine.Batzen == before + MontreuxJazzRules.EncoreBatzen && Mine.Points == points + MontreuxJazzRules.EncorePoints, $"10 Batzen and 25 points for the bis ({Mine.Batzen - before}, {Mine.Points - points})");
        Expect(told.Contains(JazzEvent.Bis) && lines.Any(l => l.Contains("The band is back")), "and the toast said so");
        Expect(Svc.BatzenOf(-9, Id) == 0, "the bot, a stranger to the party, was never paid");

        // ---- a clap off the beat does not count, from afar is refused --------------------------------------------------
        // (the next set's call; the meter is the proof)
        Expect(await Until(() => Moment(e).Part == JazzPart.Call, 80), "a third call comes");
        await Until(() => Moment(e).BeatInPart > JazzSet.CallLeadBeats + 0.5, 5);
        claps = Svc.MontreuxClaps;
        double off = JazzSet.SecondsOf(JazzSet.CallStart(Moment(e).Set, e.SetSongs), e.Bpm) + (JazzSet.CallLeadBeats + 2.5) * spb;
        double dayStart = Math.Floor(Net.ClockSync.ServerNow / 86400.0) * 86400.0;
        await Until(() => Net.ClockSync.ServerNow >= dayStart + off, 10);
        Svc.AskMontreux(FestivalService.MontreuxClap, dayStart + off);
        await Seconds(0.2);
        Expect(Svc.MontreuxClaps == claps, "a clap between two beats is no Perfect clap");
        Stand(site, 80f);
        await Seconds(0.3);
        refused = null;
        Expect(Svc.ServeMontreux(local, FestivalService.MontreuxClap) is { } && Svc.MontreuxClaps == claps, "a clap from 80 m away is refused");
        await Until(() => Moment(e).Part != JazzPart.Call, 10);

        // ---- the Jazz Night pays half as much again -----------------------------------------------------------------------
        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 7, 9));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == MontreuxJazzRules.JazzNight, 15), "the date moves to day 8, the Jazz Night");
        run = Svc.RunningOf(Id)!;
        e = FestivalService.MontreuxEvent(run);
        Expect(e.SetSongs == 4 && MontreuxJazzRules.SongPay(run.Day) == 9, $"its set has four songs and a song pays 9 ({e.SetSongs})");
        Expect(await UntilSongStart(e), "a song begins");
        Teleport(re, rn);
        await Seconds(0.3);
        JazzGame.TryInteract(Me!);
        Expect(await Until(() => Svc.MontreuxListening, 5), "marked again");
        Stand(site, 12f, -4f);
        paid = Svc.MontreuxSongsPaid; before = Mine.Batzen;
        Expect(await Until(() => Svc.MontreuxSongsPaid == paid + 1, 40), "the Jazz Night's song paid");
        Expect(Mine.Batzen == before + 9, $"9 Batzen ({Mine.Batzen - before})");
        Expect(Count(ItemId.JazzPass) == 1, "and the pass came only once");

        // ---- the closing night: the last song adds the finale ----------------------------------------------------------------------
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 7, 17));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == MontreuxJazzRules.ClosingNight, 15), "the date moves to day 16, the closing concert");
        run = Svc.RunningOf(Id)!;
        e = FestivalService.MontreuxEvent(run);
        Expect(e.SetSongs == 6 && e.FinaleNight && e.ConfettiCannons == 2, $"a six-song set with the cannons ({e.SetSongs}, {e.FinaleNight})");
        // out of the audience while the grid of the new date settles: a listener still marked from day 8 would hear (and be paid for) the end of the set already on
        Stand(site, 60f);
        Expect(await Until(() => !Svc.MontreuxListening, 5), "the mark went with the audience");
        Expect(await UntilSongStart(e, 0), "the finale set begins");
        Teleport(re, rn);
        await Seconds(0.3);
        JazzGame.TryInteract(Me!);
        Expect(await Until(() => Svc.MontreuxListening, 5), "marked for the finale");
        Stand(site, 12f);
        before = Mine.Batzen;
        told.Clear(); lines.Clear();
        Expect(await Until(() => told.Contains(JazzEvent.Finale), 200), "the last song ended with the finale");
        // five songs of 6 and the sixth's 6 + 20
        Expect(Mine.Batzen == before + 6 * MontreuxJazzRules.SongBatzen + MontreuxJazzRules.FinaleBatzen, $"6 songs and the 20 Batzen finale ({Mine.Batzen - before})");
        Expect(Mine.Paid.GetValueOrDefault("listen:16") == 56 && Mine.Paid.GetValueOrDefault("listen:16") <= MontreuxJazzRules.ListenDayCap, $"under the day's cap of {MontreuxJazzRules.ListenDayCap} ({Mine.Paid.GetValueOrDefault("listen:16")})");

        // ---- a bot is counted and paid nothing --------------------------------------------------------------------------------------
        int granted = Svc.GrantedBatzen, acts = Svc.MontreuxBotActs;
        Expect(Svc.ServeMontreux(-9, FestivalService.MontreuxMark) == null && Svc.MontreuxBotActs == acts + 1, "a bot's mark is counted");
        Expect(Svc.GrantedBatzen == granted && Svc.BatzenOf(-9, Id) == 0, "and pays nothing");

        // ---- the dates ----------------------------------------------------------------------------------------------------------------
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 7, 18));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "the festival is over on 18 July");
        refused = null;
        Expect(Svc.ServeMontreux(local, FestivalService.MontreuxMark) != null && refused?.Contains("not on") == true, $"nothing to do then ({refused})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 7, 1));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "and not yet open on 1 July");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 7, 2));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 1, 15), "day 1 is 2 July 2027, the first Friday");
        await Finish(0);
    }
}
