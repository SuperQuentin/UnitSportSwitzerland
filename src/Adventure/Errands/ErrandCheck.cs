using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// <c>--errandcheck --world fixture --pois fixture --givers fixture --systems ui,terrain,physics,adventure</c> (quick tier, #1267):
/// offline, this client plays the server's part on the fixture, whose eight givers stand round the start. The figures show within
/// 400 m; E at the post office opens the dialogue (three choices) and the prompt says whom it talks to. The mail run is taken:
/// three sacks in the pack, the card in the log; handed over at its three stops (by the Journal's own path), the cash is in the
/// account and the canton knows the player better. Refusals: a fourth card in a day ("Three cards a day"), a card from 20 m away,
/// and a <c>pk:</c> quest started through the Journal's own request. Two Swiss days on, the running cards expire and the cargo goes
/// back. Reputation levels grant their titles. The Errands tab opens. Progress in <c>test_output/errandcheck</c>, a scratch bank.
/// Windowed it saves <c>test_output/errand_*.png</c>.
/// </summary>
public partial class ErrandCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--errandcheck");
    private static bool Windowed => DisplayServer.GetName() != "headless";

    private readonly Func<FootPlayer?> _me;

    public ErrandCheck(ItemController items, Func<FootPlayer?> me) : base(items, "errandcheck", "", "errand_") => _me = me;
    public ErrandCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => ErrandService.Instance != null && AdventureService.Instance != null && GiverHost.Current.Count == 8 && Me is { } m && m.IsOnFloor(), 90))
        {
            Fail($"no errand service ({ErrandService.Instance != null}), eight givers ({GiverHost.Current.Count}) or player on the ground");
            return;
        }
        await Until(() => Me!.Down != 0, 3);
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        await Seconds(1);
        var svc = ErrandService.Instance!;
        var adventure = AdventureService.Instance!;
        var bank = Bank.Instance!;
        var mine = () => adventure.Mine;
        var errands = () => mine().Get(ErrandsProgress.Section);
        var quests = () => mine().Get(QuestsProgress.Section);
        var refusals = new List<string>();
        adventure.Refused += refusals.Add;
        var toasts = new List<string>();
        svc.Toast = toasts.Add;

        // the pack is saved between runs: start without cargo, so the counts below are this run's
        foreach (var cargo in new[] { ItemId.MailSack, ItemId.FerryCrate, ItemId.SupplyPack }) _items.Inventory.TakePlain(cargo, 999);

        var post = GiverHost.Current.All.First(g => g.Kind == GiverKind.Postmaster);
        var farmer = GiverHost.Current.All.First(g => g.Kind == GiverKind.Farmer);
        var senn = GiverHost.Current.All.First(g => g.Kind == GiverKind.Senn);
        Expect(post.Canton == "VS", "the fixture's givers are in the Valais");

        // the figures, the prompt, E
        await WalkTo(post.E, post.N - 2);
        Expect(await Until(() => ErrandGivers.Instance is { ShownCount: > 0 } g && g.Shown.Any(s => s.Id == post.Id), 5), "the post office's figure is shown");
        Expect(await Until(() => ErrandGivers.Instance?.InReach(Me!)?.Id == post.Id, 3), "the postmaster is within reach");
        var prompt = ErrandGivers.InteractionFor(Me!);
        Expect(prompt?.Text.Contains("Talk to") == true, $"the prompt names whom it talks to ('{prompt?.Text}')");
        Expect(Me!.TryInteract(), "E at the postmaster");
        var dialog = ErrandDialog.Instance!;
        Expect(await Until(() => dialog.IsOpen && dialog.Giver?.Id == post.Id, 2), "the dialogue opens");
        Expect(dialog.Choices.Count() == 3, $"three choices ({string.Join(" | ", dialog.Choices)})");
        Expect(dialog.Choices.First().StartsWith("1  Take the card"), "the card is the first choice");
        await Seconds(0.3);
        if (Windowed) GD.Print($"{Log} shot {Shot("dialog")}");
        dialog.Close();

        // the day's cards at the post office: the mail run and the supplies for the hut
        var cards = svc.CardsOf(post);
        Expect(cards.Count == 2, $"the post office has two cards today ({cards.Count})");
        var mail = cards.FirstOrDefault(c => c.Template == CardTemplate.MailRun);
        var supplies = cards.FirstOrDefault(c => c.Template == CardTemplate.HutSupplies);
        if (mail == null || supplies == null) { Fail($"expected the mail run and the hut supplies, got {string.Join(",", cards.Select(c => c.Template))}"); return; }

        // a card taken from too far away, and through the Journal's own start
        await Jump(post.E + 20, post.N);
        Expect(svc.Accept(AdventureService.LocalPeer, post.Id, mail.Id) is { } far && far.StartsWith("Go and see"), "a card from 20 m away is refused");
        Expect(adventure.Start(AdventureService.LocalPeer, mail.Id) == "Ask the giver.", "the Journal's own start refuses a given card");
        Expect(!quests().Active.ContainsKey(mail.Id), "and nothing runs");

        // the mail run
        await WalkTo(post.E, post.N - 2);
        long had = bank.Balance;
        Expect(svc.Accept(AdventureService.LocalPeer, post.Id, mail.Id) == null, "the mail run is taken");
        Expect(quests().Active.ContainsKey(mail.Id) && errands().Taken.Contains(mail.Id), "it runs, and counts as one of today's three");
        Expect(await Until(() => _items.Inventory.CountPlain(ItemId.MailSack) == 3, 3), "three mail sacks are in the pack");
        Expect(toasts.Any(t => t.StartsWith("Stamped")), "the stamp toast");
        Expect(svc.Accept(AdventureService.LocalPeer, post.Id, mail.Id) is { } again && again.Length > 0, "the same card is not taken twice");
        for (int i = 0; i < 3; i++)
        {
            var stop = (AreaTarget)mail.Quest.Steps[i].Target!;
            await WalkTo(stop.E, stop.N);
            adventure.AskDeliver(mail.Id, i);
            int left = 2 - i;
            Expect(await Until(() => _items.Inventory.CountPlain(ItemId.MailSack) == left, 3), $"stop {i + 1} ({stop.Name}): a sack handed over, {left} left");
        }
        Expect(await Until(() => quests().Done.ContainsKey(mail.Id), 3), "the card is done");
        Expect(await Until(() => bank.Balance == had + mail.Chf, 3), $"CHF {mail.Chf} are in the account ({had} -> {bank.Balance})");
        Expect(errands().Points("VS") == Postkarte.RepHome && errands().Done == 1, $"+{Postkarte.RepHome} in the Valais ({errands().Points("VS")})");

        // the second and third card, then a fourth
        await WalkTo(post.E, post.N - 2);
        Expect(svc.Accept(AdventureService.LocalPeer, post.Id, supplies.Id) == null, "the supplies are taken");
        Expect(await Until(() => _items.Inventory.CountPlain(ItemId.SupplyPack) == 1, 3), "one supply pack is in the pack");
        var farm = svc.CardsOf(farmer).First();
        await WalkTo(farmer.E, farmer.N - 2);
        Expect(svc.Accept(AdventureService.LocalPeer, farmer.Id, farm.Id) == null, "the farmer's request is taken (the third)");
        var alp = svc.CardsOf(senn).First();
        await WalkTo(senn.E, senn.N - 2);
        refusals.Clear();
        Expect(svc.Accept(AdventureService.LocalPeer, senn.Id, alp.Id) is { } cap && cap.StartsWith("Three cards a day"), "a fourth card is refused: three a day");
        Expect(refusals.Any(r => r.StartsWith("Three cards a day")), "and the owner is told");

        // the Errands tab
        Ui.AdventureLog.Open(ErrandsPage.TabId);
        await Seconds(0.8);
        if (Windowed) GD.Print($"{Log} shot {Shot("tab")}");

        // two Swiss days on: the running cards expire, their cargo goes back, today's three start again
        ErrandService.DayOffset = 2;
        Expect(await Until(() => !quests().Active.Keys.Any(Postkarte.IsCard), 5), "two days on, the running cards expired");
        Expect(await Until(() => _items.Inventory.CountPlain(ItemId.SupplyPack) == 0, 3), "the supply pack went back");
        Expect(toasts.Any(t => t.Contains("expired")), "the expiry is told");
        Expect(errands().Day == ErrandService.Today && errands().Taken.Count == 0, "the day's cards start again");
        ErrandService.DayOffset = 0;

        // reputation: levels and their titles
        adventure.Grant(AdventureService.LocalPeer, new Unlock("rep", "VS:+27"), "check");
        Expect(errands().Level("VS") == 2, $"30 points: Geschätzt ({errands().Points("VS")})");
        adventure.Grant(AdventureService.LocalPeer, new Unlock("rep", "VS:+30"), "check");
        Expect(errands().Level("VS") == 3 && errands().Flags.Contains("VS"), "60 points: Einheimisch, the Valais flag earned");
        adventure.Grant(AdventureService.LocalPeer, new Unlock("rep", "VS:+40"), "check");
        Expect(quests().Titles.Contains("Ehrenbürger von Valais"), "100 points: the title");
        Expect(toasts.Any(t => t.Contains("Ehrenbürger")), "and the level-up is told");

        // the section is saved with the rest
        adventure.SaveAll();
        await Seconds(0.5);
        Expect(File.Exists(adventure.FileOf(AdventureService.LocalKey)) && File.ReadAllText(adventure.FileOf(AdventureService.LocalKey)).Contains("\"errands\""), "the save file holds the errands section");
        await Finish(0.5);
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
