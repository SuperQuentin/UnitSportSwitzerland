using Godot;
using UnitSport.Adventure;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.World;

namespace UnitSport.Occasions.Festivals.Fasnacht;

/// <summary>
/// <c>--fasnachtcheck --festival test --date 2027-02-04 --world fixture --chunks fixture:flat --systems ui,physics,occasions,adventure</c> (quick tier, #1357): offline,
/// this client plays the server's part, on its own scratch folder (<c>test_output/fasnachtcheck</c>, never the real adventure or festival saves). Luzern's Fasnacht
/// (held at the fixture's start under <c>--festival test</c>) is on its day 1 of 6 (4 February 2027, easter-52). The check walks the games through the real game: the
/// festival's own mask while nothing is worn, away from the stall and the stage a forged use is refused, the stall's mask and Räppli once a day, the mask worn,
/// ten thrown handfuls (paper on this machine, one play paid, a flood refused), a Guggenmusik song of three voices (the player on the drum, two bots) tapped frame by frame
/// through <see cref="FasnachtGame"/> and paid with the din for voices in step, a forged and an early song refused, a bot counted and never paid, day 2, then Basel's
/// (15 February): the Morgestraich only between 04:00 and 05:30 of day 1, the street lamps out then (<c>Lamps</c> 0) and the lantern's Batzen once. Windowed it saves
/// <c>test_output/fasnacht_*.png</c>.
/// </summary>
public partial class FasnachtCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--fasnachtcheck");

    private readonly Func<FootPlayer?> _me;

    public FasnachtCheck(ItemController items, Func<FootPlayer?> me) : base(items, "fasnachtcheck", "", "fasnacht_") => _me = me;
    public FasnachtCheck() : this(null!, () => null) { }

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

    private int SlotOf(ItemId id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) return i;
        return -1;
    }

    private FestivalInstance Mine(string id) => Svc.Mine.Instances.GetValueOrDefault($"{id}-2027") ?? new FestivalInstance();

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => FestivalService.Instance != null && FasnachtGame.Instance != null && Me is { } me && me.IsOnFloor()
                && FestivalService.Instance.RunningOf(FasnachtRules.FestivalId) != null, 90))
        {
            Fail($"no Fasnacht (service {FestivalService.Instance != null}, game {FasnachtGame.Instance != null}, body {Me?.Global})");
            return;
        }
        var game = FasnachtGame.Instance!;
        const string Id = FasnachtRules.FestivalId;
        var run = Svc.RunningOf(Id)!;
        var site = run.Site!;
        var local = AdventureService.LocalPeer;
        string? refused = null;
        Svc.Refused += why => refused = why;
        var told = new List<(FestivalService.FasnachtEvent What, int A, string Text)>();
        Svc.FasnachtTold += (w, a, t) => told.Add((w, a, t));

        Expect(run.Day == 1 && run.Length == 6, $"day 1 of 6 of Luzern's Fasnacht ({run.Day}/{run.Length})");
        Expect(site is { Relocated: false }, $"held at the fixture's start ({site})");
        Expect(FestivalOccasion.SiteOf(Id) != null, "the occasion dresses the site (stall, stage)");
        foreach (var id in new[] { FasnachtRules.FestivalId, FasnachtRules.BaselId })
            Expect(FestivalCatalog.Get(id)!.Problems().Count == 0, $"{id}'s definition is sound");
        Expect(Mine(Id).Batzen == 0, "a fresh player holds no Batzen");
        var stall = FasnachtRules.StallAt(site);
        var stage = FasnachtRules.StageAt(site);

        // ---- the festival's own mask, while nothing is worn ---------------------------------------------------------
        Expect(await Until(() => Me!.HeadwearId == (int)Headwear.LarveFritschi, 10), $"Luzern's Fritschi is worn while the Fasnacht runs ({Me!.HeadwearId})");

        // ---- away from everything: nothing offered, forged uses refused --------------------------------------------------
        Teleport(stall, 70);
        await Seconds(0.4);
        Expect(FasnachtGame.InteractionFor(Me!) == null && !FasnachtGame.TryInteract(Me!), "no prompt away from the ground, E does nothing");
        refused = null;
        Expect(Svc.ServeFasnacht(local, FestivalService.FasnachtStall) != null && refused?.Contains("Larven stall") == true, $"the stall from afar is refused ({refused})");
        refused = null;
        Expect(Svc.ServeFasnacht(local, FestivalService.FasnachtLantern) != null && refused?.Contains("Basel") == true, $"Luzern has no Morgestraich lantern ({refused})");
        refused = null;
        Expect(Svc.ServeGuggenStart(local, out _) is { } far && far.Contains("stage") && refused != null, $"a seat from afar is refused ({refused})");
        Expect(Svc.ServeFasnacht(local, 9) != null, "a target that does not exist is refused");
        Expect(Mine(Id).Batzen == 0, "nothing was paid");

        // ---- the Larven stall ------------------------------------------------------------------------------------------------
        Teleport(stall, 2.5);
        Expect(await Until(() => game.NearTarget == FasnachtGame.Near.Stall, 5), $"the stall is at hand ({game.NearTarget})");
        Expect(FasnachtGame.InteractionFor(Me!) is { } offer && offer.Text.Contains("Larve"), $"the stall is offered ({FasnachtGame.InteractionFor(Me!)?.Text})");
        Expect(FasnachtGame.TryInteract(Me!), "E uses the stall");
        Expect(await Until(() => Mine(Id).Batzen == FasnachtRules.StallBatzen, 5), $"the stall paid {FasnachtRules.StallBatzen} Batzen ({Mine(Id).Batzen})");
        Expect(Mine(Id).Points == FasnachtRules.StallPoints, $"and the points ({Mine(Id).Points})");
        Expect(await Until(() => Count(ItemId.Raeppli) == FasnachtRules.StallRaeppli, 5), $"and the Räppli ({Count(ItemId.Raeppli)})");
        int masks = new[] { ItemId.LarveWaggis, ItemId.LarveDante, ItemId.LarveHarlekin, ItemId.LarveTeufel }.Sum(Count);
        Expect(masks == 1, $"and exactly one Larve ({masks})");
        Expect(told.Any(t => t.What == FestivalService.FasnachtEvent.Stall && t.Text.Contains("Larve")), "the stall says so");
        refused = null;
        Expect(Svc.ServeFasnacht(local, FestivalService.FasnachtStall) != null && refused?.Contains("today") == true, $"only once a day ({refused})");
        Expect(Svc.FasnachtStalls == 1, "one stall on the server");
        Expect((int)ItemId.Raeppli is >= 505 and <= 509 && (int)ItemId.LarveTeufel is >= 505 and <= 509 && (int)Headwear.LarveFritschi is >= 5 and <= 10, "the items are in 505-509 and the masks in Headwear 5-10");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(1.0);
            GD.Print($"[fasnachtcheck] shot {Shot("stall")}");
        }

        // ---- the mask, worn ------------------------------------------------------------------------------------------------------
        var maskId = new[] { ItemId.LarveWaggis, ItemId.LarveDante, ItemId.LarveHarlekin, ItemId.LarveTeufel }.First(i => Count(i) == 1);
        Expect(_items.Inventory.Wear(SlotOf(maskId)), "the Larve goes on the head");
        Expect(await Until(() => Me!.HeadwearId == (int)OccasionHats.ForItem(maskId) && Me.HeadwearId is >= 5 and <= 8, 10), $"the figure wears {maskId} (headwear {Me!.HeadwearId})");

        // ---- Räppli: ten handfuls make a play ---------------------------------------------------------------------------------------
        int paper = RaeppliFx.Count, before = Mine(Id).Batzen, points = Mine(Id).Points;
        int bag = SlotOf(ItemId.Raeppli);
        Expect(bag >= 0, "a bag of Räppli is in the pack");
        for (int k = 0; k < FasnachtRules.ThrowsPerPlay; k++)
        {
            _items.UseSlot(Me!, bag);
            await Seconds(0.45);
        }
        Expect(RaeppliFx.Count - paper == FasnachtRules.ThrowsPerPlay, $"paper flew ten times on this machine ({RaeppliFx.Count - paper})");
        Expect(Count(ItemId.Raeppli) == FasnachtRules.StallRaeppli - FasnachtRules.ThrowsPerPlay, $"ten handfuls were used up ({Count(ItemId.Raeppli)} left)");
        Expect(await Until(() => Svc.FasnachtThrows == FasnachtRules.ThrowsPerPlay, 5), $"the server counted ten throws ({Svc.FasnachtThrows})");
        Expect(await Until(() => Mine(Id).Batzen == before + FasnachtRules.RaeppliBatzen && Mine(Id).Points == points + FasnachtRules.RaeppliPoints, 5), $"the tenth paid {FasnachtRules.RaeppliBatzen} Batzen ({Mine(Id).Batzen - before})");
        Expect(told.Any(t => t.What == FestivalService.FasnachtEvent.Raeppli), "and says so");
        // a flood: the same instant twice
        int throws = Svc.FasnachtThrows;
        Svc.ServeFasnacht(local, FestivalService.FasnachtRaeppli);
        Expect(Svc.ServeFasnacht(local, FestivalService.FasnachtRaeppli) is { } fast && fast.Contains("fast") && Svc.FasnachtThrows == throws + 1, "two throws in the same instant count once");
        if (DisplayServer.GetName() != "headless")
        {
            _items.UseSlot(Me!, bag);
            await Seconds(0.7);
            GD.Print($"[fasnachtcheck] shot {Shot("raeppli")}");
        }

        // ---- the Guggenmusik: three voices ---------------------------------------------------------------------------------------------
        Teleport(stage, 2.5);
        Expect(await Until(() => game.NearTarget == FasnachtGame.Near.Stage, 5), $"the stage is at hand ({game.NearTarget})");
        Expect(FasnachtGame.InteractionFor(Me!) is { } band && band.Text.Contains("Guggenmusik"), $"the band is offered ({FasnachtGame.InteractionFor(Me!)?.Text})");
        before = Mine(Id).Batzen;
        points = Mine(Id).Points;
        Expect(FasnachtGame.TryInteract(Me!), "E asks for a seat");
        Expect(await Until(() => game.State == FasnachtGame.Phase.Playing, 5), "the player is seated");
        Expect(game.Part == GuggenPart.Drum && game.Chart.Bpm is >= 120 and <= 140, $"on the drum at {game.Chart.Bpm} BPM ({game.Part})");
        game.Scripted = true;
        refused = null;
        Expect(Svc.ServeGuggenSong(local, [0.5]) != null && refused?.Contains("not over") == true, $"a song sent at once is refused ({refused})");
        var rng = new Random(1357);
        Expect(Svc.ServeGuggenBot(-1, 1.0, rng) == null && Svc.ServeGuggenBot(-2, 1.0, rng) == null, "two bots take the trumpet and the sousaphone");
        Expect(Svc.GuggenVoices == 3, $"the band has three voices ({Svc.GuggenVoices})");
        Expect(Svc.ServeGuggenBot(-3, 1.0, rng) == null && Svc.GuggenVoices == 4, "a fourth voice opens a song of its own, the band stays three");
        // play the drum frame by frame, at the notes' times
        var notes = game.Chart.Of(GuggenPart.Drum);
        foreach (var note in notes)
        {
            double due = game.Chart.TimeOf(note);
            await Until(() => game.SongTime >= due, 8);
            game.Tap(due);   // at the note's own time: the screen's grading is then the whole of the timing the frame rate could blur
        }
        Expect(game.Taps.Count == notes.Length, $"{notes.Length} taps, one per drum note ({game.Taps.Count})");
        var mine = GuggenRules.Grade(game.Chart, GuggenPart.Drum, game.Taps);
        Expect(mine.Points >= mine.Max * 0.9 && mine.Extra == 0, $"the screen's own grading: {mine.Points} of {mine.Max}");
        Expect(await Until(() => game.Result != null, 30), "the song was graded");
        var result = game.Result!.Value;
        Expect(result.Voices == 3 && result.Sync100 >= 90 && result.Din100 >= 150, $"three voices in step: sync {result.Sync100} %, din {result.Din100 / 100.0:F2} ({result.Voices} voices)");
        Expect(result.Batzen > 0 && result.Batzen <= GuggenRules.BatzenMax, $"paid {result.Batzen} Batzen");
        // the song's points (640) take the party past the first milestone (300), which pays its 20 Batzen on top, outside the cap
        Expect(await Until(() => Mine(Id).Batzen == before + result.Batzen + 20 && Mine(Id).Points == points + result.BoardPoints, 5), $"in the book: Batzen {Mine(Id).Batzen - before}, points {Mine(Id).Points - points}");
        Expect(Mine(Id).Milestones.SequenceEqual([0]) && Mine(Id).Paid.GetValueOrDefault("guggen:1") == result.Batzen, "the first milestone paid, and the song's own Batzen are inside the game's day cap");
        Expect(Svc.GuggenPaid.Count(p => p.Peer < 0) == 2 && Svc.GuggenPaid.Where(p => p.Peer < 0).All(p => p.Result.Batzen == 0), "the two bots were graded and paid nothing");
        Expect(game.Taps.Count > 0 && FasnachtSounds.Snare.Variants.Length > 0 && FasnachtSounds.Trumpet.Variants.Length > 0, "the instruments sound");
        if (DisplayServer.GetName() != "headless") GD.Print($"[fasnachtcheck] shot {Shot("guggen")}");
        game.Scripted = false;
        await Until(() => game.Idle, 5);
        refused = null;
        Expect(Svc.ServeGuggenSong(local, [1.0]) != null && refused?.Contains("no seat") == true, $"a song with no seat is refused ({refused})");

        // ---- a bot is counted, never paid --------------------------------------------------------------------------------------------------
        const long Bot = -9;
        int bots = Svc.FasnachtBotActs, granted = Svc.GrantedBatzen;
        Expect(Svc.ServeFasnacht(Bot, FestivalService.FasnachtStall) == null && Svc.ServeFasnacht(Bot, FestivalService.FasnachtRaeppli) == null, "a bot takes a mask and throws Räppli");
        Expect(Svc.FasnachtBotActs == bots + 2 && Svc.GrantedBatzen == granted && Svc.BatzenOf(Bot, Id) == 0, "counted, no Batzen granted");

        // ---- a new day pays again; the dates -------------------------------------------------------------------------------------------------
        OccasionManager.Instance!.OverrideDate(new DateOnly(2027, 2, 5));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 2, 15), "5 February is day 2");
        Teleport(stall, 2.5);
        await Until(() => game.NearTarget == FasnachtGame.Near.Stall, 5);
        before = Mine(Id).Batzen;
        Expect(Svc.ServeFasnacht(local, FestivalService.FasnachtStall) == null && await Until(() => Mine(Id).Batzen == before + FasnachtRules.StallBatzen, 5), $"the stall pays again on day 2 ({Mine(Id).Batzen - before})");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 2, 9));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 6, 15), "9 February is the last day, day 6");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 2, 10));
        Expect(await Until(() => Svc.RunningOf(Id) == null, 15), "Luzern's Fasnacht is over on 10 February");
        refused = null;
        Expect(Svc.ServeFasnacht(local, FestivalService.FasnachtStall) != null && refused?.Contains("not on") == true, $"nothing at the stall then ({refused})");

        // ---- Basel's: the Morgestraich ------------------------------------------------------------------------------------------------------------
        const string Basel = FasnachtRules.BaselId;
        var sky = DayNight.Instance!;
        sky.HourOverride = 10;
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 2, 15));
        Expect(await Until(() => Svc.RunningOf(Basel)?.Day == 1, 15), "15 February is day 1 of Basel's Fasnacht");
        Expect(Svc.RunningOf(Basel)!.Length == 3 && FestivalCatalog.Get(Basel)!.TokenCap == 300, "three days, token cap 300");
        var lantern = FasnachtRules.LanternAt(Svc.RunningOf(Basel)!.Site!);
        Teleport(lantern, 2.5);
        Expect(await Until(() => game.NearTarget == FasnachtGame.Near.Lantern, 5), $"the lantern is at hand ({game.NearTarget})");
        Expect(await Until(() => OccasionManager.Instance!.Atmosphere is null, 5) && game.Morgestraichs == 0, "by day the lamps are on and there is no Morgestraich");
        refused = null;
        Expect(Svc.ServeFasnacht(local, FestivalService.FasnachtLantern) != null && refused?.Contains("four in the morning") == true, $"at ten o'clock the lantern says wait ({refused})");
        sky.HourOverride = 4.5;
        Expect(await Until(() => OccasionManager.Instance!.Atmosphere is { Atmosphere.Lamps: 0f }, 5), "at half past four the street lamps are out (Lamps 0)");
        Expect(await Until(() => game.Morgestraichs == 1, 5), "and the roll and the saying are heard once");
        before = Mine(Basel).Batzen;
        Expect(FasnachtGame.InteractionFor(Me!) is { } march && march.Text.Contains("Morgestraich"), $"the lantern is offered ({FasnachtGame.InteractionFor(Me!)?.Text})");
        Expect(FasnachtGame.TryInteract(Me!) && await Until(() => Mine(Basel).Batzen == before + FasnachtRules.MorgestraichBatzen, 5), $"the Morgestraich paid {FasnachtRules.MorgestraichBatzen} Batzen ({Mine(Basel).Batzen - before})");
        Expect(Mine(Basel).Points == FasnachtRules.MorgestraichPoints && Svc.FasnachtLanterns == 1, "and the points");
        refused = null;
        Expect(Svc.ServeFasnacht(local, FestivalService.FasnachtLantern) != null && refused?.Contains("today") == true, $"only once ({refused})");
        if (DisplayServer.GetName() != "headless")
        {
            await Seconds(1.0);
            GD.Print($"[fasnachtcheck] shot {Shot("morgestraich")}");
        }
        sky.HourOverride = 5.6;
        Expect(await Until(() => OccasionManager.Instance!.Atmosphere is null, 5), "at 05:36 the lamps are back");
        sky.HourOverride = null;
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 2, 16));
        Expect(await Until(() => Svc.RunningOf(Basel)?.Day == 2, 15), "16 February is day 2");
        sky.HourOverride = 4.5;
        refused = null;
        Expect(Svc.ServeFasnacht(local, FestivalService.FasnachtLantern) != null && refused?.Contains("first day") == true, $"the Morgestraich is day 1's only ({refused})");
        sky.HourOverride = null;
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 2, 17));
        Expect(await Until(() => Svc.RunningOf(Basel)?.Day == 3, 15), "17 February is the last day");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 2, 18));
        Expect(await Until(() => Svc.RunningOf(Basel) == null, 15), "Basel's is over on 18 February");
        OccasionManager.Instance.OverrideDate(new DateOnly(2027, 2, 4));
        Expect(await Until(() => Svc.RunningOf(Id)?.Day == 1, 15), "and Luzern's day 1 is 4 February again");
        await Finish(0);
    }
}
