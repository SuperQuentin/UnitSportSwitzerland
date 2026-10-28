using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Occasions.Festivals.Desalpe;

/// <summary>
/// <c>--desalpenet a|b</c> with <c>--connect --festival test</c> (driven by <c>tools/desalpenetcheck.sh</c>, #1269 part 7): the Désalpe over a dedicated
/// server whose calendar reads 24 September 2027 (day 5 of the Désalpe, held at the fixture start, a 110 m route). Everything is asserted on the REMOTE peers.
/// Lea (A) dresses a cow at the alp and drives the herd down; Tom (B) stands 150 m away on the other side of the fair. B sees A's dress arrive over the wire
/// and the herd walk to the fair (the same place A sees it at), is refused when it tries to dress a cow or start the drive from where it stands, is not paid
/// for A's drive, and then, standing in the ring, gives its vote to A's herd and is paid. A sees the vote and is refused its own.
/// </summary>
public partial class DesalpeNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--desalpenet");

    public DesalpeNetProbe(ItemController items) : base(items, "desalpenet", "DN", "desalpenet_") { }
    public DesalpeNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("desalpe-2027") ?? new FestivalInstance();

    private void Teleport(double e, double n)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(e, n, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && DesalpeGame.Instance != null && DesalpeHerd.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        const string Id = DesalpeRules.FestivalId;
        var game = DesalpeGame.Instance!;
        var herd = DesalpeHerd.Instance!;
        Expect(await Until(() => Svc.RunningOf(Id) is { Day: 5, Site: not null } && Svc.DesalpeNow != null && herd.Count == DesalpeRules.Cows, 40), "the server runs the Désalpe on day 5 and this client sees the herd");
        Expect(await Until(() => Me is { } me && me.IsOnFloor(), 30), "the player stands on the ground");
        var site = Svc.RunningOf(Id)!.Site!;
        var route = Svc.DesalpeRouteNow()!;
        string? refused = null;
        Svc.Refused += why => refused = why;
        var scored = new List<int>();
        Svc.DesalpeScored += (s, _, _, _, _, _) => scored.Add(s);
        int voted = 0;
        Svc.DesalpeVoted += e => voted = e;

        if (_role == "A")
        {
            // dress a cow at the alp
            var (ce, cn) = herd.CowAt(2)!.Value;
            Teleport(ce + 1.6, cn);
            Expect(await Until(() => game.Dressing && game.NearCow >= 0, 10), "A stands by a cow of the herd");
            int cow = game.NearCow;
            DesalpeGame.TryInteract(Me!);
            Expect(await Until(() => herd.DressOf(cow) != 0, 10), $"the server dressed cow {cow} ({DesalpeModels.Describe(herd.DressOf(cow))})");
            Expect(await Until(() => Mine.Batzen == DesalpeRules.DressBatzen, 10), $"the dressing paid in A's own section ({Mine.Batzen})");
            Say($"dressed {cow} {herd.DressOf(cow)}");
            Expect(await Heard("B", "saw", 60), "B saw the dress");
            // start at the sign
            var (se, sn) = DesalpeGround.Sign(site, route);
            Teleport(se, sn);
            Expect(await Until(() => DesalpeGame.InteractionFor(Me!) is { } p && p.Text.Contains("Start the Alpabzug"), 10), "the prompt at the sign");
            DesalpeGame.TryInteract(Me!);
            Expect(await Until(() => herd.Phase == DesalpePhase.Driving, 10), "the herd is on its way (over the wire)");
            Say("driving");
            // behind the Leitkuh until it is over
            int guard = 0;
            while (herd.Phase == DesalpePhase.Driving && guard++ < 4000)
            {
                var (e, n) = herd.CowAt(0)!.Value;
                var (tx, ty) = route.Tangent(herd.Progress * route.Length);
                Teleport(e - tx * 12, n - ty * 12);
                await Seconds(0.4);
            }
            Expect(herd.Phase == DesalpePhase.Arrived, $"the herd came down ({herd.Phase})");
            Expect(await Until(() => scored.Count == 1, 15), "A was told the result over the wire");
            Expect(scored.Count == 1 && scored[0] >= 10 * 50, $"the score counts the cows ({scored.FirstOrDefault()})");
            Expect(await Until(() => Mine.Batzen > DesalpeRules.DressBatzen && Mine.Points >= scored[0], 15), $"A was paid for the drive ({Mine.Batzen} Batzen, {Mine.Points} points)");
            Expect(await Until(() => Svc.BoardOf(Id) is { } b && b.Points >= scored[0], 15), $"and its board holds the points ({Svc.BoardOf(Id)?.Points})");
            await Seconds(4);
            var (le, ln) = herd.CowAt(0)!.Value;
            Say($"leader {le:F1} {ln:F1}");
            // the vote: B gives it; A sees it, and its own is refused
            Expect(await Until(() => Svc.DesalpeNow is { Ring.Length: 1 } v && v.Ring[0].Votes == 1, 90), "A sees B's vote in the ring");
            Teleport(site.E + 6, site.N);
            await Seconds(0.5);
            refused = null;
            Svc.AskDesalpeVote(1);
            Expect(await Until(() => refused?.Contains("own herd") == true, 10), $"A's own herd is refused ({refused})");
            Say("done");
            Expect(await Heard("B", "done", 120), "B finished too");
        }
        else
        {
            // 150 m the other way from the alp: never a driver of the herd
            var (sx, sy) = route.Start;
            double len = Math.Sqrt(sx * sx + sy * sy);
            Teleport(site.E - sx / len * 150, site.N - sy / len * 150);
            Expect(await Heard("A", "dressed", 90), "A dressed a cow");
            var said = (await HeardStarting("A", "dressed", 1))!.Split(' ');
            int cow = int.Parse(said[1]);
            Expect(await Until(() => herd.DressOf(cow) != 0 && herd.DressOf(cow) == byte.Parse(said[2]), 10), $"B sees cow {cow}'s dress on the wire ({DesalpeModels.Describe(herd.DressOf(cow))})");
            Expect(Svc.DesalpeNow!.Dress[cow] != 0, "and in the state");
            Say("saw");
            // forged requests from where B stands
            refused = null;
            Svc.AskDesalpeDress(4, 1);
            Expect(await Until(() => refused?.Contains("Go to the cow") == true, 10), $"a dress from 150 m away is refused ({refused})");
            refused = null;
            Svc.AskDesalpeStart();
            Expect(await Until(() => refused?.Contains("alp sign") == true, 10), $"a start from 150 m away is refused ({refused})");
            refused = null;
            Svc.AskDesalpeVote(1);
            Expect(await Until(() => refused != null, 10), $"a vote with no herd in the ring is refused ({refused})");
            // watch the herd come down
            Expect(await Heard("A", "driving", 90), "A started the drive");
            Expect(await Until(() => herd.Phase == DesalpePhase.Driving, 10), "B sees the herd on its way");
            var (e0, n0) = herd.CowAt(0)!.Value;
            double startDist = Math.Sqrt((e0 - site.E) * (e0 - site.E) + (n0 - site.N) * (n0 - site.N));
            Expect(await Until(() => herd.Phase == DesalpePhase.Arrived, 400), "B sees the herd arrive at the fair");
            await Seconds(4);
            var (e1, n1) = herd.CowAt(0)!.Value;
            double endDist = Math.Sqrt((e1 - site.E) * (e1 - site.E) + (n1 - site.N) * (n1 - site.N));
            Expect(startDist > 60 && endDist < 25, $"the Leitkuh walked from {startDist:F0} m to {endDist:F0} m from the fair");
            string leader = (await HeardStarting("A", "leader ", 30))!;
            var parts = leader.Split(' ');
            double ae = Float(parts[1]), an = Float(parts[2]);
            Expect(Math.Sqrt((e1 - ae) * (e1 - ae) + (n1 - an) * (n1 - an)) < 1.5, $"B sees the Leitkuh where A sees it (within 1.5 m: {Math.Sqrt((e1 - ae) * (e1 - ae) + (n1 - an) * (n1 - an)):F2})");
            Expect(Mine.Batzen == 0 && scored.Count == 0, $"B was not paid for A's drive ({Mine.Batzen} Batzen)");
            // into the ring: vote for A's herd
            Teleport(site.E - 8, site.N);
            Expect(await Until(() => game.Voting && DesalpeGame.InteractionFor(Me!) is { } p && p.Text.Contains("Vote for Herd 1"), 15), $"the vote prompt in the ring ({DesalpeGame.InteractionFor(Me!)?.Text})");
            DesalpeGame.TryInteract(Me!);
            Expect(await Until(() => voted == 1, 10), "B's vote counted");
            Expect(await Until(() => Mine.Batzen == DesalpeRules.VoteBatzen && Mine.Points == DesalpeRules.VotePoints, 15), $"and paid, in B's own section ({Mine.Batzen}/{Mine.Points})");
            refused = null;
            Svc.AskDesalpeVote(1);
            Expect(await Until(() => refused?.Contains("voted already") == true, 10), $"a second vote is refused ({refused})");
            Say("done");
            Expect(await Heard("A", "done", 120), "A finished too");
        }
        await Finish(2.0);
    }
}
