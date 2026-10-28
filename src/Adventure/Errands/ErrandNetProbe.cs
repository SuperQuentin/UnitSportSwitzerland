using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// <c>--errandnet lead|follow</c> with <c>--connect</c> (driven by <c>tools/errandnetcheck.sh</c>, #1267): a dedicated server on the
/// flat fixture with the fixture POIs and givers, and two clients, Lea (<c>lead</c>) and Tom (<c>follow</c>, its own <c>user://</c>).
/// What each reads is what reached it from the other, the remote peer:
/// <list type="number">
/// <item>both compute the same cards for the cell (the offers hash Lea says is the one Tom computes);</item>
/// <item>Lea and Tom are a party; Lea takes the mail run at the post office (the server checks where her body stands), shares it;
/// Tom sees the shared card and joins it from where he stands: the card runs for both, with three sacks each;</item>
/// <item>both hand the sacks over at the three stops; the second to finish pays both the friend bonus: each hears
/// "Together with ..." and holds the bonus in the <c>errands</c> section;</item>
/// <item>Tom takes two more cards and is refused a fourth ("Three cards a day").</item>
/// </list>
/// </summary>
public partial class ErrandNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--errandnet");

    public ErrandNetProbe(ItemController items) : base(items, "errandnet", "ER", "errandnet_") { }
    public ErrandNetProbe() : this(null!) { }

    private static ErrandService Svc => ErrandService.Instance!;
    private static AdventureService Adventure => AdventureService.Instance!;
    private ErrandsProgress Errands => Adventure.Mine.Get(ErrandsProgress.Section);
    private QuestsProgress Quests => Adventure.Mine.Get(QuestsProgress.Section);
    private readonly List<string> _toasts = new(), _refusals = new();

    public override async void _Ready()
    {
        _role = Role ?? "LEAD";
        if (!await Joined(150, () => ErrandService.Instance != null && AdventureService.Instance != null && GiverHost.Current.Count == 8)) return;
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        Expect(await Until(() => Adventure.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it");
        var was = Svc.Toast;
        Svc.Toast = t => { was?.Invoke(t); _toasts.Add(t); };
        Adventure.Refused += _refusals.Add;
        if (_role == "LEAD") await Lead(); else await Follow();
        await Finish(6.0);
    }

    private static (int E, int N) Cell => Postkarte.CellOf(PoiCatalogue.FixtureE, PoiCatalogue.FixtureN);

    private static string OffersHash()
    {
        var cards = Svc.CardsIn(Cell.E, Cell.N);
        return string.Join("-", cards.Select(c => $"{c.Slot}{(int)c.Template}c{c.Chf}h{c.Hash & 0xFFF}")) + $"x{cards.Count}";
    }

    private Giver PostOffice => GiverHost.Current.All.First(g => g.Kind == GiverKind.Postmaster);
    private Offer Mail => Svc.CardsOf(PostOffice).First(c => c.Template == CardTemplate.MailRun);

    private async Task Lead()
    {
        Expect(await Heard("FOLLOW", "ready", 150), "Tom is on");
        string hash = OffersHash();
        Expect(Svc.CardsIn(Cell.E, Cell.N).Count >= 4, $"the cell has cards ({hash})");
        Say("offers " + hash);
        Chat!.Send("/party invite Tom");
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "Lea's party holds Lea and Tom");
        Say("party");

        // the post office: the server checks where the body stands
        await Jump(PostOffice.E, PostOffice.N - 2);
        await Seconds(1.5);
        var mail = Mail;
        Svc.AskAccept(PostOffice.Id, mail.Id);
        Expect(await Until(() => Quests.Active.ContainsKey(mail.Id), 15), "the server gave Lea the mail run");
        Expect(await Until(() => _items.Inventory.CountPlain(ItemId.MailSack) == 3, 15), "three sacks reached her pack");
        Expect(await Until(() => Errands.Taken.Contains(mail.Id), 10), "and the errands section counts it");
        Svc.AskShare(mail.Id);
        Say("shared");
        Expect(await Heard("FOLLOW", "joined", 60), "Tom joined her card");

        await Deliver(mail);
        Expect(await Until(() => Quests.Done.ContainsKey(mail.Id), 15), "Lea's card is done");
        Say("done");
        // Tom finishes second: the bonus reaches Lea from his hand-over
        Expect(await Until(() => Errands.BonusPaid.Contains(mail.Id), 60), "Tom's finish paid Lea the friend bonus");
        Expect(_toasts.Any(t => t.StartsWith("Together with Tom")), $"Lea heard it ({string.Join("; ", _toasts.TakeLast(3))})");
        Expect(Errands.Together == 1 && Errands.Points("VS") >= Postkarte.RepHome + 1, $"+1 reputation more, together 1 ({Errands.Points("VS")})");
        Say("bonus");
        Expect(await Heard("FOLLOW", "capped", 90), "Tom met his cap");
    }

    private async Task Follow()
    {
        // Lea may join the chat after the first line: say it again until she answers
        for (int i = 0; i < 40 && !await Heard("LEAD", "offers ", 3); i++) Say("ready");
        string theirs = (await HeardStarting("LEAD", "offers ", 30)) ?? "";
        Expect(theirs.StartsWith("offers ") && theirs["offers ".Length..] == OffersHash(), $"the same cards on the remote peer ({OffersHash()} / {theirs})");
        Expect(await Until(() => InviteService.Client?.Incoming.Any(v => v.Kind == Parties.Kind) == true, 90), "Tom is invited to Lea's party");
        if (InviteService.Client?.Incoming.FirstOrDefault(v => v.Kind == Parties.Kind) is { } party) InviteService.Client.Accept(party.Id);
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 20), "Tom's party holds both");
        var mail = Mail;
        Expect(await Heard("LEAD", "shared", 120), "Lea shared the card");
        Expect(await Until(() => Svc.SharedCards.Any(s => s.Card == mail.Id), 15), "the shared card reached Tom's list");
        Expect(_toasts.Any(t => t.Contains("shared a card")), "with a toast");
        long lea = Svc.SharedCards.First(s => s.Card == mail.Id).From;
        Svc.AskJoin(mail.Id, lea);
        Expect(await Until(() => Quests.Active.ContainsKey(mail.Id), 15), "Tom joined the card from where he stood");
        Expect(await Until(() => _items.Inventory.CountPlain(ItemId.MailSack) == 3, 15), "with his own three sacks");
        Say("joined");
        Expect(await Heard("LEAD", "done", 240), "Lea finished first");

        await Deliver(mail);
        Expect(await Until(() => Quests.Done.ContainsKey(mail.Id), 15), "Tom's card is done");
        Expect(await Until(() => Errands.BonusPaid.Contains(mail.Id), 15), "the friend bonus is in Tom's section too");
        Expect(_toasts.Any(t => t.StartsWith("Together with Lea")), $"Tom heard it ({string.Join("; ", _toasts.TakeLast(3))})");
        Expect(Errands.Together == 1, "together 1 on Tom");

        // two more cards at the post office, then the cap
        Expect(await Heard("LEAD", "bonus", 60), "Lea has her side");
        await Jump(PostOffice.E, PostOffice.N - 2);
        await Seconds(1.5);
        var supplies = Svc.CardsOf(PostOffice).First(c => c.Template == CardTemplate.HutSupplies);
        Svc.AskAccept(PostOffice.Id, supplies.Id);
        Expect(await Until(() => Quests.Active.ContainsKey(supplies.Id), 15), "Tom took the supplies (the second)");
        var farmer = GiverHost.Current.All.First(g => g.Kind == GiverKind.Farmer);
        await Jump(farmer.E, farmer.N - 2);
        await Seconds(1.5);
        Svc.AskAccept(farmer.Id, Svc.CardsOf(farmer).First().Id);
        Expect(await Until(() => Errands.Taken.Count == 3, 15), "and the farmer's request (the third)");
        var senn = GiverHost.Current.All.First(g => g.Kind == GiverKind.Senn);
        await Jump(senn.E, senn.N - 2);
        await Seconds(1.5);
        _refusals.Clear();
        Svc.AskAccept(senn.Id, Svc.CardsOf(senn).First().Id);
        Expect(await Until(() => _refusals.Any(r => r.StartsWith("Three cards a day")), 15), $"the server refused a fourth card ({string.Join("; ", _refusals)})");
        Say("capped");
    }

    /// <summary>The three stops, in order: walk to each, hand a sack over, wait for the server to take it.</summary>
    private async Task Deliver(Offer mail)
    {
        for (int i = 0; i < 3; i++)
        {
            var stop = (AreaTarget)mail.Quest.Steps[i].Target!;
            await Jump(stop.E, stop.N);
            await Seconds(1.5);
            Adventure.AskDeliver(mail.Id, i);
            int left = 2 - i;
            Expect(await Until(() => _items.Inventory.CountPlain(ItemId.MailSack) == left, 15), $"stop {i + 1} ({stop.Name}): the server took a sack, {left} left");
        }
    }

    private async Task Jump(double e, double n)
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
            await Seconds(0.4);
        }
    }
}
