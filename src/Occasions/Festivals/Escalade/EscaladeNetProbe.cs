using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Escalade;

/// <summary>
/// <c>--escaladenet a|b</c> with <c>--connect --festival test --escaladefast</c> (driven by <c>tools/escaladenetcheck.sh</c>, #1351): the Escalade over a
/// dedicated server whose calendar reads 11 December 2027 (day 1 of 2, held at the fixture start). Everything is asserted on the REMOTE peers. Lea (A) and
/// Tom (B) form a party and stand at the Marmite. Tom, 150 m away first, is refused the Marmite by the server; back at it, Lea takes it (the server tells
/// both she holds it) and Tom takes the other side: both are paid in their own sections, both get the chocolate and are told the saying. Lea ladles at the
/// pot and the whole party is fed: Tom, who pressed nothing, finds a bowl in his pack and the Batzen in his own section. Tom runs the Course through
/// the gates (the server follows his position) and is paid; Lea sees the party's points on the shared board.
/// </summary>
public partial class EscaladeNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--escaladenet");

    public EscaladeNetProbe(ItemController items) : base(items, "escaladenet", "EN", "escaladenet_") { }
    public EscaladeNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("escalade-2027") ?? new FestivalInstance();
    private static InviteService Invites() => InviteService.Client!;

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

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && EscaladeGame.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        const string Id = EscaladeRules.FestivalId;
        var game = EscaladeGame.Instance!;
        Expect(await Until(() => Svc.RunningOf(Id) is { Day: 1, Site: not null }, 40), "the server runs the Escalade on day 1 and this client knows it");
        Expect(await Until(() => Me is { } me && me.IsOnFloor(), 30), "the player stands on the ground");
        var site = Svc.RunningOf(Id)!.Site!;
        var marmite = EscaladeRules.MarmiteAt(site);
        var pot = EscaladeRules.PotAt(site);
        var line = EscaladeRules.StartAt(site);
        string? refused = null;
        Svc.Refused += why => refused = why;
        var told = new List<(FestivalService.EscaladeEvent What, int A, int Ms, string Text)>();
        Svc.EscaladeTold += (w, a, ms, t) => told.Add((w, a, ms, t));
        void AtMarmite() => Teleport(marmite.E, marmite.N + 2.5);

        if (_role == "A")
        {
            Expect(await Heard("B", "ready", 120), "B is on");
            Chat!.Send("/party invite Tom");
            Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "A's party holds A and B");
            Say("party");
            Expect(await Heard("B", "refused", 150), "B was refused from afar");
            AtMarmite();
            Expect(await Until(() => game.NearTarget == FestivalService.EscaladeMarmite, 10), "A stands at the Marmite");
            await Seconds(1.0);   // the server learns where the body is from the movement packets: let it
            Say("atmarmite");
            Expect(await Heard("B", "atmarmite", 60), "B stands there too");
            Expect(EscaladeGame.TryInteract(Me!), "A takes the Marmite");
            Expect(await Until(() => told.Any(t => t.What == FestivalService.EscaladeEvent.Held), 10), $"the server told A it holds it (refused: {refused})");
            Say("held");
            const int Pay = EscaladeRules.SmashBatzen + EscaladeRules.SmashPairBatzen;
            Expect(await Until(() => told.Any(t => t.What == FestivalService.EscaladeEvent.Smashed && t.Text.Contains(EscaladeRules.Saying)), 20), "B joined: A hears the saying");
            Expect(await Until(() => Mine.Earned == Pay && Mine.Points == EscaladeRules.SmashPoints, 10), $"A is paid in its own section ({Mine.Earned}/{Mine.Points})");
            Expect(await Until(() => Count(ItemId.MarmiteChocolate) == EscaladeRules.ChocolatePieces, 10), "A has the chocolate");
            // the pot: A ladles, the whole party is fed
            Teleport(pot.E, pot.N + 2.0);
            Expect(await Until(() => game.NearTarget == FestivalService.EscaladePot, 10), "A stands at the pot");
            await Seconds(1.0);
            Say("atpot");
            Expect(await Heard("B", "atpot", 60), "B stands at the pot too");
            Expect(EscaladeGame.TryInteract(Me!), "A ladles");
            Expect(await Until(() => Count(ItemId.SoupeEscalade) == 1 && Mine.Earned == Pay + EscaladeRules.SoupeBatzen, 10), $"A has its bowl and its Batzen ({Mine.Earned})");
            Say("ladled");
            Expect(await Heard("B", "ran", 150), "B ran the Course");
            // the shared board holds everyone's points
            int points = 2 * EscaladeRules.SmashPoints + 2 * EscaladeRules.SoupePoints + (EscaladeRules.CourseBatzen + EscaladeRules.SpeedBatzen) * EscaladeRules.PointsPerBatzen;
            Expect(await Until(() => Svc.BoardOf(Id)?.Points == points, 15), $"A's board holds the party's {points} points ({Svc.BoardOf(Id)?.Points})");
            Say("done");
        }
        else
        {
            Say("ready");
            Expect(await Until(() => Invites().Incoming.Any(v => v.Kind == Parties.Kind), 60), "B is invited to A's party");
            if (Invites().Incoming.FirstOrDefault(v => v.Kind == Parties.Kind) is { } invite) Invites().Accept(invite.Id);
            Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "B's party holds A and B");
            Expect(await Heard("A", "party", 60), "A has its party");
            // forged: from 150 m away
            Teleport(marmite.E + 150, marmite.N + 150);
            await Seconds(1.0);
            Svc.AskEscalade(FestivalService.EscaladeMarmite);
            Expect(await Until(() => refused?.Contains("Go to the Marmite") == true, 10), $"the Marmite from 150 m away is refused ({refused})");
            refused = null;
            Svc.AskEscalade(FestivalService.EscaladePot);
            Expect(await Until(() => refused?.Contains("Go to the soup pot") == true, 10), $"and so is the pot ({refused})");
            Expect(Mine.Earned == 0, "nothing was paid");
            Say("refused");
            Expect(await Heard("A", "atmarmite", 60), "A is at the Marmite");
            AtMarmite();
            Expect(await Until(() => game.NearTarget == FestivalService.EscaladeMarmite, 10), "B stands at the Marmite");
            await Seconds(1.0);
            Say("atmarmite");
            Expect(await Heard("A", "held", 60), "A holds the Marmite");
            Expect(await Until(() => told.Any(t => t.What == FestivalService.EscaladeEvent.Held && t.Text.Contains("holds the Marmite")), 10), "the server told B that A holds it");
            Expect(EscaladeGame.TryInteract(Me!), "B takes the other side");
            const int Pay = EscaladeRules.SmashBatzen + EscaladeRules.SmashPairBatzen;
            Expect(await Until(() => told.Any(t => t.What == FestivalService.EscaladeEvent.Smashed && t.Text.Contains(EscaladeRules.Saying)), 15), $"B hears the saying (refused: {refused})");
            Expect(await Until(() => Mine.Earned == Pay && Mine.Points == EscaladeRules.SmashPoints, 10), $"B is paid in its own section ({Mine.Earned}/{Mine.Points})");
            Expect(await Until(() => Count(ItemId.MarmiteChocolate) == EscaladeRules.ChocolatePieces, 10), "B has the chocolate");
            refused = null;
            Svc.AskEscalade(FestivalService.EscaladeMarmite);
            Expect(await Until(() => refused?.Contains("today") == true, 10), $"a second smash is refused ({refused})");
            // the pot: B pressed nothing, and is fed
            Expect(await Heard("A", "atpot", 60), "A is at the pot");
            Teleport(pot.E + 1.0, pot.N + 2.0);
            await Seconds(1.0);
            Say("atpot");
            Expect(await Heard("A", "ladled", 60), "A ladled");
            Expect(await Until(() => Count(ItemId.SoupeEscalade) == 1 && Mine.Earned == Pay + EscaladeRules.SoupeBatzen, 10), $"B was fed by A's ladle ({Count(ItemId.SoupeEscalade)} bowl, {Mine.Earned} earned)");
            Expect(told.Any(t => t.What == FestivalService.EscaladeEvent.Ladled && t.Text.Contains("ladles you")), "B is told who ladled");
            refused = null;
            Svc.AskEscalade(FestivalService.EscaladePot);
            Expect(await Until(() => refused?.Contains("eaten") == true, 10), $"B's own ladle finds everyone fed ({refused})");
            // the Course
            Teleport(line.E, line.N + 1.5);
            Expect(await Until(() => game.NearTarget == FestivalService.EscaladeStart, 10), "B stands at the start line");
            await Seconds(1.0);
            Expect(EscaladeGame.TryInteract(Me!), "B starts the Course");
            Expect(await Until(() => game.Running, 10), $"the server started it (refused: {refused})");
            var path = EscaladeRules.Path(site);
            for (int k = 0; k < EscaladeRules.Gates; k++)
            {
                Teleport(path[k].E, path[k].N);
                Expect(await Until(() => game.NextGate == k + 1, 12), $"the server passed gate {k + 1} ({game.NextGate})");
            }
            Teleport(path[EscaladeRules.Gates].E, path[EscaladeRules.Gates].N);
            Expect(await Until(() => told.Any(t => t.What == FestivalService.EscaladeEvent.CourseDone), 12), "the line finishes the Course");
            int run = EscaladeRules.CourseBatzen + EscaladeRules.SpeedBatzen;
            Expect(await Until(() => Mine.Earned == Pay + EscaladeRules.SoupeBatzen + run, 10), $"B is paid the run ({Mine.Earned})");
            Say("ran");
            Expect(await Heard("A", "done", 60), "A finished too");
        }
        await Finish(2.0);
    }
}
