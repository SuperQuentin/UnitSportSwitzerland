using Godot;
using UnitSport.Adventure;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.World;

namespace UnitSport.Occasions.Festivals.Fasnacht;

/// <summary>
/// <c>--fasnachtnet a|b</c> with <c>--connect --festival test --time 4.5</c> (driven by <c>tools/fasnachtnetcheck.sh</c>, #1357): Basel's Fasnacht over a dedicated
/// server whose calendar reads 15 February 2027 (day 1 of 3, held at the fixture start) and whose clock Lea, as admin, stops at half past four, so the Morgestraich is on. Everything is
/// asserted on the REMOTE peers. Both clients see the street lamps out (<c>Lamps</c> 0) and hear the Morgestraich. Tom (B), 150 m away first, is refused the stall, the
/// lantern and a seat by the server. Both take their Larve and Räppli at the stall (each in its own section), put the Larve on, and each sees the other's mask arrive on the
/// replicated body (Headwear 5-8). Both light the lantern and are paid 40 Batzen. Lea throws a handful of Räppli and Tom's own machine shows the paper (the event relayed by
/// the server). Lea sits at the Guggenmusik stage first (the drum), Tom second (the trumpet): each taps their whole part at the notes' times, the server grades both,
/// scores the din for two voices in step and pays each, and each is told its result in its own section.
/// </summary>
public partial class FasnachtNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--fasnachtnet");

    public FasnachtNetProbe(ItemController items) : base(items, "fasnachtnet", "FN", "fasnachtnet_") { }
    public FasnachtNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("baslerfasnacht-2027") ?? new FestivalInstance();

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

    private FootPlayer? Other() => Find(GetTree().Root);

    private FootPlayer? Find(Node n)
    {
        if (n is FootPlayer p && p != Me && !p.IsMultiplayerAuthority()) return p;
        foreach (var c in n.GetChildren())
            if (Find(c) is { } f) return f;
        return null;
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && FasnachtGame.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        const string Id = FasnachtRules.BaselId;
        var game = FasnachtGame.Instance!;
        Expect(await Until(() => Svc.RunningOf(Id) is { Day: 1, Site: not null }, 40), "the server runs Basel's Fasnacht on day 1 and this client knows it");
        Expect(await Until(() => Me is { } me && me.IsOnFloor(), 30), "the player stands on the ground");
        var site = Svc.RunningOf(Id)!.Site!;
        var stall = FasnachtRules.StallAt(site);
        var lantern = FasnachtRules.LanternAt(site);
        var stage = FasnachtRules.StageAt(site);
        string? refused = null;
        Svc.Refused += why => refused = why;
        var told = new List<(FestivalService.FasnachtEvent What, int A, string Text)>();
        Svc.FasnachtTold += (w, a, t) => told.Add((w, a, t));

        // the server's clock starts at ten and a dedicated server reads no --time: Lea, as admin, stops it at half past four, so the Morgestraich is on
        if (_role == "A")
        {
            if (!await LogIn("test")) return;
            Chat!.Send("/time speed 0");
            Chat.Send("/time set 04:30");
        }
        Expect(await Until(() => OccasionManager.Instance?.Atmosphere is { Atmosphere.Lamps: 0f }, 60), $"the street lamps are out at {DayNight.Instance?.Clock}");
        Expect(await Until(() => game.Morgestraichs >= 1, 10), "the Morgestraich's roll and saying were heard here");

        if (_role == "A")
        {
            Expect(await Heard("B", "ready", 120), "B is on");
            Expect(await Heard("B", "refused", 150), "B was refused from afar");
            Teleport(stall, 2.5);
            Expect(await Until(() => game.NearTarget == FasnachtGame.Near.Stall, 10), "A stands at the stall");
            await Seconds(1.0);   // the server learns where the body is from the movement packets: let it
            Expect(FasnachtGame.TryInteract(Me!), "A takes its Larve");
            Expect(await Until(() => Mine.Batzen == FasnachtRules.StallBatzen && Count(ItemId.Raeppli) == FasnachtRules.StallRaeppli, 10), $"A has its Räppli and its Batzen ({Mine.Batzen}, {Count(ItemId.Raeppli)})");
            var maskId = new[] { ItemId.LarveWaggis, ItemId.LarveDante, ItemId.LarveHarlekin, ItemId.LarveTeufel }.FirstOrDefault(i => Count(i) == 1);
            Expect(maskId != ItemId.None && _items.Inventory.Wear(SlotOf(maskId)), "A puts its Larve on");
            Say("masked");
            Expect(await Heard("B", "masked", 60), "B has its mask on");
            Expect(await Until(() => Other() is { } b && b.HeadwearId is >= 5 and <= 8, 30), $"B's Larve arrived on its body here ({Other()?.HeadwearId})");
            refused = null;
            Svc.AskFasnacht(FestivalService.FasnachtStall);
            Expect(await Until(() => refused?.Contains("today") == true, 10), $"a second Larve is refused ({refused})");
            // the Morgestraich
            Teleport(lantern, 2.5);
            Expect(await Until(() => game.NearTarget == FasnachtGame.Near.Lantern, 10), "A stands at the lantern");
            await Seconds(1.0);
            Expect(FasnachtGame.TryInteract(Me!), "A marches");
            Expect(await Until(() => Mine.Batzen == FasnachtRules.StallBatzen + FasnachtRules.MorgestraichBatzen, 10), $"A is paid the Morgestraich ({Mine.Batzen})");
            Say("marched");
            // Räppli: A throws, B's machine shows the paper
            Expect(await Heard("B", "papered", 60), "B is ready for the paper");
            int paper = RaeppliFx.Count;
            _items.UseSlot(Me!, SlotOf(ItemId.Raeppli));
            Expect(await Until(() => RaeppliFx.Count == paper + 1, 5), "A's own paper flew");
            Say("threw");
            // the Guggenmusik: A sits first, on the drum
            Teleport(stage, 2.5);
            Expect(await Until(() => game.NearTarget == FasnachtGame.Near.Stage, 10), "A stands at the stage");
            await Seconds(1.0);
            Say("atstage");
            Expect(await Heard("B", "atstage", 60), "B is at the stage too");
            Expect(FasnachtGame.TryInteract(Me!), "A asks for a seat");
            Expect(await Until(() => game.State == FasnachtGame.Phase.Playing, 10), $"A is seated (refused: {refused})");
            Expect(game.Part == GuggenPart.Drum, $"first to sit is the drum ({game.Part})");
            Say("seated");
            await PlayPart(game);
        }
        else
        {
            Say("ready");
            // forged: from 150 m away
            Teleport((stall.E + 150, stall.N + 150));
            await Seconds(1.0);
            Svc.AskFasnacht(FestivalService.FasnachtStall);
            Expect(await Until(() => refused?.Contains("Larven stall") == true, 10), $"the stall from 150 m away is refused ({refused})");
            refused = null;
            Svc.AskFasnacht(FestivalService.FasnachtLantern);
            Expect(await Until(() => refused?.Contains("lantern") == true, 10), $"and so is the lantern ({refused})");
            refused = null;
            Svc.AskGuggenStart();
            Expect(await Until(() => refused?.Contains("stage") == true, 10), $"and a seat ({refused})");
            Expect(Mine.Batzen == 0, "nothing was paid");
            Say("refused");
            Teleport(stall, 2.5);
            Expect(await Until(() => game.NearTarget == FasnachtGame.Near.Stall, 10), "B stands at the stall");
            await Seconds(1.0);
            Expect(FasnachtGame.TryInteract(Me!), "B takes its Larve");
            Expect(await Until(() => Mine.Batzen == FasnachtRules.StallBatzen && Count(ItemId.Raeppli) == FasnachtRules.StallRaeppli, 10), $"B has its Räppli and its Batzen ({Mine.Batzen}, {Count(ItemId.Raeppli)})");
            var maskId = new[] { ItemId.LarveWaggis, ItemId.LarveDante, ItemId.LarveHarlekin, ItemId.LarveTeufel }.FirstOrDefault(i => Count(i) == 1);
            Expect(maskId != ItemId.None && _items.Inventory.Wear(SlotOf(maskId)), "B puts its Larve on");
            Say("masked");
            Expect(await Heard("A", "masked", 60), "A has its mask on");
            Expect(await Until(() => Other() is { } a && a.HeadwearId is >= 5 and <= 8, 30), $"A's Larve arrived on its body here ({Other()?.HeadwearId})");
            Teleport(lantern, 2.5);
            Expect(await Until(() => game.NearTarget == FasnachtGame.Near.Lantern, 10), "B stands at the lantern");
            await Seconds(1.0);
            Expect(FasnachtGame.TryInteract(Me!), "B marches");
            Expect(await Until(() => Mine.Batzen == FasnachtRules.StallBatzen + FasnachtRules.MorgestraichBatzen, 10), $"B is paid the Morgestraich ({Mine.Batzen})");
            int paper = RaeppliFx.Count;
            Say("papered");
            Expect(await Heard("A", "threw", 60), "A threw a handful");
            Expect(await Until(() => RaeppliFx.Count == paper + 1, 10), "the paper of A's handful flew on B's machine (relayed by the server)");
            Teleport(stage, 2.5);
            Expect(await Until(() => game.NearTarget == FasnachtGame.Near.Stage, 10), "B stands at the stage");
            await Seconds(1.0);
            Say("atstage");
            Expect(await Heard("A", "seated", 60), "A has sat down");
            Expect(FasnachtGame.TryInteract(Me!), "B asks for a seat");
            Expect(await Until(() => game.State == FasnachtGame.Phase.Playing, 10), $"B is seated (refused: {refused})");
            Expect(game.Part == GuggenPart.Trumpet, $"the second to sit is the trumpet ({game.Part})");
            await PlayPart(game);
        }
        await Finish(2.0);
    }

    /// <summary>Taps the whole part at the notes' times, waits for the server's result and checks it: two voices in step, paid in this peer's own section.</summary>
    private async Task PlayPart(FasnachtGame game)
    {
        const string Id = FasnachtRules.BaselId;
        int before = Mine.Batzen, points = Mine.Points;
        game.Scripted = true;
        var notes = game.Chart.Of(game.Part);
        foreach (var note in notes)
        {
            double due = game.Chart.TimeOf(note);
            await Until(() => game.SongTime >= due, 12);
            game.Tap(due);
        }
        Expect(game.Taps.Count == notes.Length, $"{notes.Length} taps for the {game.Part} ({game.Taps.Count})");
        Expect(await Until(() => game.Result != null, 90), "the server graded the song");
        if (game.Result is not { } r) return;
        Expect(r.Voices == 2 && r.Sync100 >= 90 && r.Din100 >= 125, $"two voices in step: sync {r.Sync100} %, din {r.Din100 / 100.0:F2} ({r.Voices} voices)");
        Expect(r.Batzen > 0 && r.Points >= r.Max * 0.95, $"{r.Points} of {r.Max} points, +{r.Batzen} Batzen");
        // the milestones this song's points crossed pay their Batzen on top, outside the cap
        int bonus = FestivalCatalog.Get(Id)!.Milestones.Where(m => points < m.Points && m.Points <= points + r.BoardPoints).Sum(m => m.Batzen);
        Expect(await Until(() => Mine.Batzen == before + r.Batzen + bonus && Mine.Points == points + r.BoardPoints, 10), $"paid in this peer's own section (+{Mine.Batzen - before} Batzen with {bonus} of milestones, +{Mine.Points - points} points)");
        Expect(Svc.Mine.Instances.ContainsKey($"{Id}-2027"), "the book holds the instance");
        game.Scripted = false;
    }
}
