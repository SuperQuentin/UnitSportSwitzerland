using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Chalanda;

/// <summary>
/// <c>--chalandacheck --chalandafast --festival test --date 2027-03-01 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c>
/// (quick tier, #1356): offline, this client plays the server's part, on its own scratch folder (<c>test_output/chalandacheck</c>, never the real adventure
/// or festival saves). The Chalandamarz (held at the fixture's start under <c>--festival test</c>) is on its one day. Through the real game: away from the stand
/// there is no prompt and a forged start or ring is refused; at the stand E begins the procession; walking along the loop E rings the Treichel on the beat
/// (the server grades the stamp: on the beat is Perfect, half a beat off is a Miss, twice on one beat or standing still or off the loop or from the future
/// scores nothing); the seven stations are passed in order and the stand finishes the march, which pays by the points (Batzen, party points, Nusstorte, a
/// Treichel); a run too fast for a body (the sprint limit back on) is no march; a bot is counted and never paid; the festival is on 1 March only.
/// Windowed it saves <c>test_output/chalanda_*.png</c>.
/// </summary>
public partial class ChalandaCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--chalandacheck");

    private readonly Func<FootPlayer?> _me;

    public ChalandaCheck(ItemController items, Func<FootPlayer?> me) : base(items, "chalandacheck", "", "chalanda_") => _me = me;
    public ChalandaCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static FestivalService Svc => FestivalService.Instance!;

    private void Teleport((double E, double N) at, double dn = 0)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(at.E, at.N + dn, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    private FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("chalandamarz-2027") ?? new FestivalInstance();

    /// <summary>Frame by frame until the server clock reaches <paramref name="beat"/>: the press then lands within a frame of it.</summary>
    private async Task<bool> ToBeat(long beat)
    {
        for (int i = 0; i < 2000 && ClockSync.ServerNow < ChalandaRules.BeatTime(beat); i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        return ClockSync.ServerNow >= ChalandaRules.BeatTime(beat);
    }

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && ChalandaGame.Instance != null && Me is { } me && me.IsOnFloor()
                && FestivalService.Instance.RunningOf(ChalandaRules.FestivalId) != null, 90))
        {
            Fail($"no Chalandamarz (service {FestivalService.Instance != null}, game {ChalandaGame.Instance != null}, body {Me?.Global})");
            return;
        }
        var game = ChalandaGame.Instance!;
        const string Id = ChalandaRules.FestivalId;
        var run = Svc.RunningOf(Id)!;
        var site = run.Site!;
        var local = AdventureService.LocalPeer;
        var told = new List<(FestivalService.ChalandaEvent What, int A, int Points, string Text)>();
        Svc.ChalandaTold += (w, a, p, t) => told.Add((w, a, p, t));
        string? refused = null;
        Svc.Refused += why => refused = why;

        Expect(run.Day == 1 && run.Length == 1, $"day 1 of 1 of the Chalandamarz ({run.Day}/{run.Length})");
        Expect(site is { Relocated: false }, $"held at the fixture's start ({site})");
        Expect(FestivalOccasion.SiteOf(Id) != null, "the occasion dresses the site (stand and banners)");
        Expect(FestivalCatalog.Get(Id)!.Problems().Count == 0, "the festival definition is sound");
        Expect(Mine.Batzen == 0, "a fresh player holds no Batzen");
        var stand = ChalandaRules.StandAt(site);
        var path = ChalandaRules.Path(site);
        var middle = (site.E, site.N);

        // ---- away from the stand: nothing offered, forged calls refused -----------------------------------
        Teleport(stand, 70);
        await Seconds(0.4);
        Expect(ChalandaGame.InteractionFor(Me!) == null && !ChalandaGame.TryInteract(Me!), "no prompt away from the stand, E does nothing");
        refused = null;
        Expect(Svc.ServeChalanda(local, FestivalService.ChalandaStart, 0) != null && refused?.Contains("Go to the Treichel stand") == true, $"a start from afar is refused ({refused})");
        refused = null;
        Expect(Svc.ServeChalanda(local, FestivalService.ChalandaRing, ClockSync.ServerNow) != null && refused?.Contains("not in the procession") == true, $"a ring before any march is refused ({refused})");
        Expect(Svc.ServeChalanda(local, 9, 0) != null, "a call that does not exist is refused");
        Expect(Mine.Batzen == 0 && Svc.ChalandaStarted == 0, "nothing started, nothing paid");

        // ---- the stand: E begins the procession -----------------------------------------------------------
        Teleport(stand, 2.0);
        Expect(await Until(() => game.NearStand, 5), "the stand is at hand");
        Expect(ChalandaGame.InteractionFor(Me!) is { } begin && begin.Text.Contains("Begin the procession"), $"the start is offered ({ChalandaGame.InteractionFor(Me!)?.Text})");
        Expect(ChalandaGame.TryInteract(Me!) && await Until(() => game.Marching, 5), "E begins the procession");
        Expect(Svc.ChalandaStarted == 1 && ChalandaGame.InteractionFor(Me!) is { } ring && ring.Text.Contains("Ring the Treichel"), "the server has a march and E now rings");
        Expect(game.NextStation == 0 && Svc.ChalandaFinished == 0, "standing at the stand finishes nothing: the stations come first");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(1.0);
            GD.Print($"[chalandacheck] shot {Shot("march")}");
        }

        // ---- ringing on the beat while walking toward the first station --------------------------------------
        double leg = ChalandaRules.Dist(path[0].E, path[0].N, stand);
        (double E, double N) Along(double metres) => (stand.E + (path[0].E - stand.E) * metres / leg, stand.N + (path[0].N - stand.N) * metres / leg);
        const int Rings = 16;
        long beat = ChalandaRules.NextBeat(ClockSync.ServerNow) + 1;
        for (int k = 0; k < Rings; k++, beat++)
        {
            Expect(await ToBeat(beat), $"beat {beat} came");
            Teleport(Along(0.8 * (k + 1)));
            ChalandaGame.TryInteract(Me!);
        }
        Expect(await Until(() => game.Points == Rings * ChalandaRules.PerfectPoints, 5), $"{Rings} rings on the beat while walking are {Rings * ChalandaRules.PerfectPoints} points ({game.Points}, grade {game.LastGrade})");
        Expect(game.LastGrade == 2 && Svc.ChalandaGoodRings == Rings && game.Pressed == Rings, "every ring was graded Perfect by the server");
        Expect(Svc.ChalandaSyncRings == 0, "no party, no step bonus");

        // half a beat off is a Miss; twice on one beat the second is nothing
        int points = game.Points;
        Expect(await ToBeat(beat), "next beat");
        Teleport(Along(0.8 * (Rings + 1)));
        Svc.AskChalandaRing(ChalandaRules.BeatTime(beat) - ChalandaRules.SecondsPerBeat / 2);
        Expect(await Until(() => game.LastGrade == 0, 5) && game.Points == points, $"a ring half a beat off scores nothing ({game.Points})");
        beat = ChalandaRules.NextBeat(ClockSync.ServerNow);
        Expect(await ToBeat(beat), "the beat after");
        Teleport(Along(0.8 * (Rings + 2)));
        Svc.AskChalandaRing(ChalandaRules.BeatTime(beat));
        Expect(await Until(() => game.LastGrade == 2 && game.Points == points + 3, 5), "and the next, on the beat, scores");
        Svc.AskChalandaRing(ChalandaRules.BeatTime(beat));
        Expect(await Until(() => game.LastGrade == 0 && game.Message.Contains("Once a beat"), 5) && game.Points == points + 3, $"twice on one beat is nothing ({game.Message})");
        refused = null;
        Expect(Svc.ServeChalanda(local, FestivalService.ChalandaRing, ClockSync.ServerNow + 10) != null && refused?.Contains("makes no sense") == true, $"a ring stamped in the future is refused ({refused})");
        Expect(Svc.ServeChalanda(local, FestivalService.ChalandaRing, double.NaN) != null, "and so is a NaN stamp");
        points = game.Points;

        // standing still is no march
        await Seconds(2.2);
        beat = ChalandaRules.NextBeat(ClockSync.ServerNow);
        await ToBeat(beat);
        ChalandaGame.TryInteract(Me!);
        Expect(await Until(() => game.Message.Contains("Keep walking"), 5) && game.Points == points, $"ringing on the spot scores nothing ({game.Message})");
        // off the loop is none either
        Teleport(middle);
        beat = ChalandaRules.NextBeat(ClockSync.ServerNow);
        await ToBeat(beat);
        ChalandaGame.TryInteract(Me!);
        Expect(await Until(() => game.Message.Contains("Back onto the loop"), 5) && game.Points == points, $"ringing in the village square scores nothing ({game.Message})");
        Expect(game.Marching, "still marching");

        // ---- the stations in order, then the stand --------------------------------------------------------------
        Teleport(path[3]);
        await Seconds(1.0);
        Expect(game.NextStation == 0, "station 4 first changes nothing: they come in order");
        for (int k = 0; k < ChalandaRules.Stations; k++)
        {
            Teleport(path[k]);
            Expect(await Until(() => game.NextStation == k + 1, 8), $"station {k + 1} is passed ({game.NextStation})");
        }
        points = game.Points;
        int before = Mine.Batzen, earned = Mine.Earned;
        Teleport(stand);
        Expect(await Until(() => told.Any(t => t.What == FestivalService.ChalandaEvent.Done), 8), "the stand finishes the procession");
        Expect(!game.Marching && Svc.ChalandaFinished == 1, "the march is over");
        var done = told.Last(t => t.What == FestivalService.ChalandaEvent.Done);
        int expected = ChalandaRules.Batzen(points);
        Expect(await Until(() => Mine.Earned == earned + expected, 5), $"the march earned {expected} Batzen for {points} points ({Mine.Earned - earned})");
        Expect(Mine.Batzen == before + expected && Mine.Points == points && Svc.CrewPoints(local, Id) == points, $"and the party points ({Mine.Points} / crew {Svc.CrewPoints(local, Id)})");
        Expect(await Until(() => Count(ItemId.EngadinerNusstorte) == ChalandaRules.PastryPieces && Count(ItemId.ChalandaTreichel) == 1, 5), "a Nusstorte and the Treichel are in the pack");
        Expect(done.Text.Contains(ChalandaRules.Rhyme), $"the rhyme is sung ({done.Text})");
        Expect(Mine.Paid.GetValueOrDefault("umzug:1") == expected, "inside the game's day cap");
        Expect((int)ItemId.ChalandaTreichel is >= 515 and <= 519, "the Treichel is in this festival's id block");

        // a march too fast for a body is no march (the sprint limit back on)
        FestivalService.ChalandaSpeedLimit = ChalandaRules.SprintSpeed;
        Teleport(stand, 1.5);
        await Until(() => game.NearStand, 5);
        ChalandaGame.TryInteract(Me!);
        await Until(() => game.Marching, 5);
        for (int k = 0; k < ChalandaRules.Stations; k++)
        {
            Teleport(path[k]);
            await Until(() => game.NextStation == k + 1, 8);
        }
        before = Mine.Batzen;
        Teleport(stand);
        Expect(await Until(() => told.Any(t => t.What == FestivalService.ChalandaEvent.Lost && t.Text.Contains("not a march")), 8), $"a teleport at the sprint limit is told it was not a march (running {game.Marching})");
        Expect(Mine.Batzen == before && Svc.ChalandaFinished == 1, "and pays nothing");
        FestivalService.ChalandaSpeedLimit = 1e9;

        // leaving the procession ends it
        Teleport(stand, 1.5);
        await Until(() => game.NearStand, 5);
        ChalandaGame.TryInteract(Me!);
        await Until(() => game.Marching, 5);
        Teleport(middle, ChalandaRules.LoopRadius + ChalandaRules.LeaveMargin + 20);
        Expect(await Until(() => !game.Marching && told.Any(t => t.What == FestivalService.ChalandaEvent.Lost && t.Text.Contains("left the procession")), 8), "walking far out leaves the procession");

        // ---- a bot is counted, never paid ----------------------------------------------------------------------------------
        const long Bot = -9;
        int bots = Svc.ChalandaBotActs, granted = Svc.GrantedBatzen;
        Expect(Svc.ServeChalanda(Bot, FestivalService.ChalandaStart, 0) == null && Svc.ServeChalanda(Bot, FestivalService.ChalandaRing, ClockSync.ServerNow) == null, "a bot begins and rings");
        Expect(Svc.ChalandaBotActs == bots + 2 && Svc.GrantedBatzen == granted && Svc.BatzenOf(Bot, Id) == 0, "counted, no Batzen granted");

        // ---- the dates ---------------------------------------------------------------------------------------------------
        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 3, 2));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "the Chalandamarz is over on 2 March");
        refused = null;
        Expect(Svc.ServeChalanda(local, FestivalService.ChalandaStart, 0) != null && refused?.Contains("not on") == true, $"nothing to march in then ({refused})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 2, 28));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "and not yet open on 28 February");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 3, 1));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 1, 15), "day 1 is 1 March");
        FestivalService.ChalandaSpeedLimit = ChalandaRules.SprintSpeed;
        await Finish(0);
    }
}
