using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// <c>--worldeventcheck --world fixture --pois fixture --givers fixture --systems ui,terrain,physics,adventure</c> (quick tier, #1339):
/// offline, this client plays the server's part. Each of the four events is staged near the player and driven to its end with
/// scripted walking and the real help path (<see cref="WorldEvents.AskHelp"/>): a forest fire is beaten out cell by cell, somebody
/// is dug out of an avalanche, a lost hiker is found, talked to and walked to a goal, a runaway cow is driven home by standing
/// behind it. Each pays its francs and five points in the canton, counts in the progress and ends the drawing. A busy player
/// (a Versteckis game, a Battle Royale round) has no event come near. Then the lost hiker's Postkarte: taken at the hut, the hiker
/// found at the pass, brought down, the card done and paid. Windowed it saves <c>test_output/worldevent_*.png</c>.
/// </summary>
public partial class WorldEventCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--worldeventcheck");
    private static bool Windowed => DisplayServer.GetName() != "headless";

    private readonly Func<FootPlayer?> _me;

    public WorldEventCheck(ItemController items, Func<FootPlayer?> me) : base(items, "worldeventcheck", "", "worldevent_") => _me = me;
    public WorldEventCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => WorldEvents.Instance != null && ErrandService.Instance != null && AdventureService.Instance != null && GiverHost.Current.Count == 8 && Me is { } m && m.IsOnFloor(), 90))
        {
            Fail("no world events, errand service or player on the ground");
            return;
        }
        await Until(() => Me!.Down != 0, 3);
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        await Seconds(1);
        var events = WorldEvents.Instance!;
        var adventure = AdventureService.Instance!;
        var bank = Bank.Instance!;
        var errands = () => adventure.Mine.Get(ErrandsProgress.Section);
        var toasts = new List<string>();
        events.Toast = toasts.Add;
        // a checkerboard of 16 m squares is the forest: every point is near an edge
        events.ForestOverride = (e, n) => ((long)Math.Floor(e / 16) + (long)Math.Floor(n / 16)) % 2 == 0;
        // the save is kept between runs: a hiker card left running by an earlier run is given up, and its hiker goes home
        foreach (string card in adventure.Mine.Get(QuestsProgress.Section).Active.Keys.Where(Postkarte.IsCard).ToList()) adventure.Abandon(AdventureService.LocalPeer, card);
        Expect(await Until(() => events.RunningCount == 0, 60), "no event is left over from an earlier run");
        var start = Me!.Global;
        double e0 = start.E, n0 = start.N;
        Expect(WorldEvents.Mode == WorldEventMode.Normal, "the default mode is normal");
        WorldEvents.Mode = WorldEventMode.Off;   // no chance event comes in between: the forced ones below still plan

        // a player in a Versteckis game or a round has no event come near
        events.Busy = _ => true;
        events.Force(WorldEventKind.ForestFire);
        await Seconds(3);
        Expect(events.RunningCount == 0, "a busy player has no event planned near");
        events.Busy = _ => false;
        Expect(await Until(() => events.RunningCount == 1, 10), "a free player gets the forced fire");
        int fire = events.Running.First().Id;
        Expect(await Until(() => events.ViewCount == 1, 5), "the fire is drawn");
        Expect(events.ProbeOf(fire)?.Count == FireSim.Start, $"six cells burn ({events.ProbeOf(fire)?.Count})");
        Expect(toasts.Any(t => t.Contains("Smoke over the forest")), "the plan is told");
        if (Windowed) GD.Print($"{Log} shot {Shot("fire")}");
        long cash = bank.Balance;
        int rep = errands().Points("VS");
        int fires = errands().Events.GetValueOrDefault("ForestFire");
        // beat it out, nearest cell first
        for (int guard = 0; guard < 80 && events.ProbeOf(fire) != null; guard++)
        {
            var cells = events.Targets(fire);
            var me = Me!.Global;
            var next = cells.OrderBy(c => (c.E - me.E) * (c.E - me.E) + (c.N - me.N) * (c.N - me.N)).First();
            await WalkTo(next.E + 1, next.N);
            events.AskHelp(fire, EventAct.BeatStart, next.Key);
            await Until(() => events.Targets(fire).All(c => c.Key != next.Key), 6);
            events.AskHelp(fire, EventAct.BeatStop, next.Key);
        }
        Expect(await Until(() => events.ProbeOf(fire) == null, 5), "the fire is out");
        Expect(await Until(() => bank.Balance == cash + 40, 3), $"CHF 40 are in the account ({cash} -> {bank.Balance})");
        Expect(errands().Points("VS") == rep + WorldEventRules.RepPoints, $"+{WorldEventRules.RepPoints} in the Valais ({errands().Points("VS")})");
        Expect(errands().Events.GetValueOrDefault("ForestFire") == fires + 1, "the fire is counted");
        Expect(await Until(() => events.ViewCount == 0, 3), "the drawing is gone");
        Expect(toasts.Any(t => t.Contains("The fire is out")), "and the end is told");

        // an avalanche: probe, dig
        await Jump(e0, n0);
        cash = bank.Balance;
        rep = errands().Points("VS");
        int slides = errands().Events.GetValueOrDefault("Avalanche");
        int avalanche = events.Stage(WorldEventKind.Avalanche, e0 + 60, n0, seed: 2);
        Expect(await Until(() => events.ViewCount == 1, 5), "the debris cone is drawn");
        var spot = events.Targets(avalanche).First();
        Expect(WorldEvents.OwnsGather == false, "no dig prompt far from the cone");
        await WalkTo(spot.E + 1, spot.N);
        Expect(await Until(() => WorldEvents.OwnsGather, 3), "next to the buried the dig prompt is up and owns the gather hold");
        events.AskHelp(avalanche, EventAct.DigStart, spot.Key);
        await Seconds(3);
        Expect(events.ProbeOf(avalanche) is { Progress: > 0.25f and < 0.5f }, $"three seconds of digging is a third ({events.ProbeOf(avalanche)?.Progress})");
        Expect(await Until(() => events.ProbeOf(avalanche) == null, 12), "dug out in about eight seconds");
        Expect(await Until(() => bank.Balance == cash + 60, 3), $"CHF 60 are in the account ({cash} -> {bank.Balance})");
        Expect(errands().Points("VS") == rep + WorldEventRules.RepPoints && errands().Events.GetValueOrDefault("Avalanche") == slides + 1, "five points and a count");
        // a dig stops when the body leaves
        await Jump(e0, n0);
        int second = events.Stage(WorldEventKind.Avalanche, e0 + 60, n0, seed: 2);
        spot = events.Targets(second).First();
        await WalkTo(spot.E + 1, spot.N);
        events.AskHelp(second, EventAct.DigStart, spot.Key);
        await Seconds(1);
        await WalkTo(spot.E + 30, spot.N);
        await Seconds(1);
        float before = events.ProbeOf(second)?.Progress ?? 0;
        await Seconds(2);
        Expect(events.ProbeOf(second) is { } p2 && Math.Abs(p2.Progress - before) < 0.001f, "digging stops when the body walks away");
        Expect(events.Help(AdventureService.LocalPeer, Me!.Global, second, EventAct.DigStart, spot.Key) is { } far && far.Length > 0, "a dig from 30 m is refused");
        await WalkTo(spot.E + 1, spot.N);
        events.AskHelp(second, EventAct.DigStart, spot.Key);
        Expect(await Until(() => events.ProbeOf(second) == null, 14), "and goes on when it comes back");

        // a lost hiker: found, followed, brought to the goal
        await Jump(e0, n0);
        cash = bank.Balance;
        rep = errands().Points("VS");
        int hiker = events.Stage(WorldEventKind.LostHiker, e0, n0 + 80, goalE: e0, goalN: n0 + 200);
        Expect(await Until(() => events.ViewCount == 1, 5), "the hiker is drawn");
        await WalkTo(e0 + 50, n0 + 80);
        Expect(!WorldEvents.TryInteract(Me!), "50 m away: nobody to talk to");
        await WalkTo(e0 + 2, n0 + 80);
        Expect(await Until(() => WorldEvents.InteractionFor(Me!)?.Text.Contains("Talk to the lost hiker") == true, 3), "the prompt names the hiker");
        Expect(WorldEvents.TryInteract(Me!), "E at the hiker");
        Expect(await Until(() => events.ProbeOf(hiker)?.Found == true, 3), "the hiker is found");
        await Seconds(1);
        var h0 = events.ProbeOf(hiker)!.Value;
        await Escort(e0, n0 + 200);
        Expect(await Until(() => events.ProbeOf(hiker) == null, 10), "the hiker reached the goal");
        Expect(await Until(() => bank.Balance == cash + 40, 3), $"CHF 40 are in the account ({cash} -> {bank.Balance})");
        Expect(errands().Points("VS") == rep + WorldEventRules.RepPoints, "five points");

        // a runaway cow: stand behind it and it walks home
        await Jump(e0, n0);
        cash = bank.Balance;
        int cow = events.Stage(WorldEventKind.RunawayCow, e0 + 100, n0, seed: 1);
        Expect(await Until(() => events.ViewCount == 1, 5), "the cow is drawn");
        var home = (E: e0 + 100, N: n0);
        await Seconds(10);
        var lost = events.ProbeOf(cow)!.Value;
        double away = Math.Sqrt((lost.E - home.E) * (lost.E - home.E) + (lost.N - home.N) * (lost.N - home.N));
        Expect(away > 50, $"a loose cow walks away from its herd ({away:F0} m)");
        for (int guard = 0; guard < 120 && events.ProbeOf(cow) is { } c; guard++)
        {
            double dx = c.E - home.E, dn = c.N - home.N, len = Math.Sqrt(dx * dx + dn * dn);
            await Jump(c.E + dx / len * 3, c.N + dn / len * 3);
        }
        Expect(events.ProbeOf(cow) == null, "the cow is home");
        Expect(await Until(() => bank.Balance == cash + 30, 3), $"CHF 30 are in the account ({cash} -> {bank.Balance})");

        // left alone, an event ends by itself and pays nothing
        await Jump(e0, n0);
        cash = bank.Balance;
        int lone = events.Stage(WorldEventKind.RunawayCow, e0 + 100, n0, seed: 5, lifeS: 20);
        Expect(await Until(() => events.ProbeOf(lone) == null, 40), "an event nobody helps ends by itself");
        Expect(bank.Balance == cash, "and pays nothing");
        Expect(toasts.Any(t => t.Contains("found its own way home")), "the world's end is told");

        // the lost hiker's Postkarte
        await HikerCard(events, adventure, bank, errands);
        await Finish(0.5);
    }

    /// <summary>The hut warden's lost hiker card: taken at the hut, the hiker at the pass found, brought down, paid.</summary>
    private async Task HikerCard(WorldEvents events, AdventureService adventure, Bank bank, Func<ErrandsProgress> errands)
    {
        var svc = ErrandService.Instance!;
        var hut = GiverHost.Current.All.First(g => g.Kind == GiverKind.HutWarden);
        Offer? card = null;
        for (int day = 0; day < 60 && card == null; day++)
        {
            ErrandService.DayOffset = day;
            card = svc.CardsOf(hut).FirstOrDefault(c => c.Template == CardTemplate.LostHiker);
        }
        if (card?.Hiker is not { } where) { Fail("no lost hiker card at the hut in 60 days"); return; }
        Expect(true, $"day {ErrandService.DayOffset}: '{card.Quest.Title}' at the hut");
        var quests = () => adventure.Mine.Get(QuestsProgress.Section);
        await Jump(hut.E, hut.N - 2);
        long cash = bank.Balance;
        Expect(svc.Accept(AdventureService.LocalPeer, hut.Id, card.Id) == null, "the card is taken");
        Expect(await Until(() => events.RunningCount == 1 && events.Running.First().Kind == WorldEventKind.LostHiker, 5), "its hiker sits where the card says");
        int id = events.Running.First().Id;
        Expect(events.Running.First() is var r && Math.Abs(r.E - where.E) < 1 && Math.Abs(r.N - where.N) < 1, "at the pass");
        Expect(await Until(() => events.ViewCount == 1, 5), "and is drawn");
        await Jump(where.E + 2, where.N);
        Expect(WorldEvents.TryInteract(Me!), "E at the hiker");
        Expect(await Until(() => quests().Active[card.Id].Step == 1, 3), "finding the hiker is the card's first step");
        await Seconds(1);
        GD.Print($"{Log} escorting to {where.GoalE:F0}/{where.GoalN:F0}");
        await Escort(where.GoalE, where.GoalN);
        GD.Print($"{Log} escorted");
        Expect(await Until(() => quests().Done.ContainsKey(card.Id), 15), "the hiker at the hut finishes the card");
        Expect(await Until(() => bank.Balance == cash + card.Chf, 3), $"CHF {card.Chf} are in the account ({cash} -> {bank.Balance})");
        Expect(await Until(() => events.ProbeOf(id) == null, 5), "the hiker's event is over");
        ErrandService.DayOffset = 0;
    }

    /// <summary>Walks to a point at 1.5 m/s with the hiker behind: slower than a walk-to-spot, so the hiker keeps up.</summary>
    private async Task Escort(double e, double n)
    {
        var me = Me!;
        for (int guard = 0; guard < 400; guard++)
        {
            var g = me.Global;
            double de = e - g.E, dn = n - g.N, d = Math.Sqrt(de * de + dn * dn);
            if (d < 3) return;
            double step = Math.Min(d, 1.5);
            me.GlobalPosition += new Vector3((float)(de / d * step), 0.1f, (float)(-dn / d * step));
            me.Velocity = Vector3.Zero;
            await Seconds(1);
        }
    }

    private async Task WalkTo(double e, double n)
    {
        var me = Me!;
        while (true)
        {
            var g = me.Global;
            double de = e - g.E, dn = n - g.N, d = Math.Sqrt(de * de + dn * dn);
            if (d < 0.5) break;
            double step = Math.Min(d, 20);
            me.GlobalPosition += new Vector3((float)(de / d * step), 0.2f, (float)(-dn / d * step));
            me.Velocity = Vector3.Zero;
            await Seconds(0.5);
        }
    }

    private async Task Jump(double e, double n)
    {
        var me = Me!;
        var g = me.Global;
        me.GlobalPosition += new Vector3((float)(e - g.E), 0.2f, (float)-(n - g.N));
        me.Velocity = Vector3.Zero;
        await Seconds(0.5);
    }
}
