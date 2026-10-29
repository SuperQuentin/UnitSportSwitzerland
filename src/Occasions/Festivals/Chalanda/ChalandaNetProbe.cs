using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Chalanda;

/// <summary>
/// <c>--chalandanet a|b</c> with <c>--connect --festival test --chalandafast</c> (driven by <c>tools/chalandanetcheck.sh</c>, #1356): the Chalandamarz over a
/// dedicated server whose calendar reads 1 March 2027 (day 1 of 1, held at the fixture start). Everything is asserted on the REMOTE peers. Lea (A) and
/// Tom (B) form a party. Tom, 150 m from the stand, is refused a start and a ring by the server. Both begin the procession at the stand (the server
/// answers each), then ring the Treichel on 14 beats of the shared march clock, each ring stamped with the beat and sent over the wire while walking
/// along the loop: the server grades them Perfect and, on every beat both rang, tells both they were in step and pays the bonus, so each client's points are
/// exactly 3 per ring plus 2 per beat in step. Both pass the seven stations (the server follows each position) and finish at the stand: each is paid in its own
/// section, finds the Nusstorte and the Treichel in its pack, and Lea sees the party's points on the shared board add up to both totals.
/// </summary>
public partial class ChalandaNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--chalandanet");

    public ChalandaNetProbe(ItemController items) : base(items, "chalandanet", "CN", "chalandanet_") { }
    public ChalandaNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("chalandamarz-2027") ?? new FestivalInstance();
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

    private async Task ToBeat(long beat)
    {
        for (int i = 0; i < 4000 && ClockSync.ServerNow < ChalandaRules.BeatTime(beat); i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && ChalandaGame.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        const string Id = ChalandaRules.FestivalId;
        const int Rings = 14;
        var game = ChalandaGame.Instance!;
        Expect(await Until(() => Svc.RunningOf(Id) is { Day: 1, Site: not null }, 40), "the server runs the Chalandamarz on day 1 and this client knows it");
        Expect(await Until(() => Me is { } me && me.IsOnFloor(), 30), "the player stands on the ground");
        var site = Svc.RunningOf(Id)!.Site!;
        var stand = ChalandaRules.StandAt(site);
        var path = ChalandaRules.Path(site);
        string? refused = null;
        Svc.Refused += why => refused = why;
        var told = new List<(FestivalService.ChalandaEvent What, int A, int Points, string Text)>();
        Svc.ChalandaTold += (w, a, p, t) => told.Add((w, a, p, t));
        double leg = ChalandaRules.Dist(path[0].E, path[0].N, stand);

        // the march: ring on 14 beats of the shared clock, stamped with the beat, walking along the first leg of the loop
        async Task MarchRings()
        {
            // step off first: the server counts a ring only for a body it has seen move within the last second and a half
            Teleport(stand.E + (path[0].E - stand.E) * 0.3 / leg, stand.N + (path[0].N - stand.N) * 0.3 / leg);
            await Seconds(0.6);
            long beat = ChalandaRules.NextBeat(ClockSync.ServerNow) + 2;
            for (int k = 0; k < Rings; k++, beat++)
            {
                await ToBeat(beat);
                double m = 0.8 * (k + 1);
                Teleport(stand.E + (path[0].E - stand.E) * m / leg, stand.N + (path[0].N - stand.N) * m / leg);
                Svc.AskChalandaRing(ChalandaRules.BeatTime(beat));
            }
            await Seconds(2.5);
        }

        async Task Stations()
        {
            for (int k = 0; k < ChalandaRules.Stations; k++)
            {
                Teleport(path[k].E, path[k].N);
                Expect(await Until(() => game.NextStation == k + 1, 12), $"the server passed station {k + 1} ({game.NextStation})");
            }
            Teleport(stand.E, stand.N);
            Expect(await Until(() => told.Any(t => t.What == FestivalService.ChalandaEvent.Done), 12), "the stand finishes the procession");
        }

        if (_role == "A")
        {
            Expect(await Heard("B", "ready", 120), "B is on");
            Chat!.Send("/party invite Tom");
            Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "A's party holds A and B");
            Say("party");
            Expect(await Heard("B", "refused", 150), "B was refused from afar");
            Teleport(stand.E + 2, stand.N);
            Expect(await Until(() => game.NearStand, 10), "A stands at the stand");
            await Seconds(1.0);   // the server learns where the body is from the movement packets: let it
            Say("atstand");
            Expect(await Heard("B", "atstand", 60), "B stands there too");
            Expect(ChalandaGame.TryInteract(Me!), "A takes a Treichel and sets off");
            Expect(await Until(() => game.Marching, 10), $"the server began A's procession (refused: {refused})");
            Say("started");
            Expect(await Heard("B", "started", 60), "B marches too");
            await MarchRings();
            Expect(await Until(() => game.SyncedRings >= 8, 10), $"A was told it was in step on {game.SyncedRings} beats");
            Expect(told.Any(t => t.What == FestivalService.ChalandaEvent.Synced && t.Text.Contains("In step with Tom")), "and with whom");
            int points = Rings * ChalandaRules.PerfectPoints + game.SyncedRings * ChalandaRules.SyncBonus;
            Expect(game.Points == points && game.LastGrade == 2, $"A's {game.Points} points are 3 a ring and 2 a beat in step ({points})");
            Say("rang");
            Expect(await Heard("B", "rang", 60), "B rang too");
            await Stations();
            int expected = ChalandaRules.Batzen(game.Points);
            Expect(await Until(() => Mine.Earned == expected && Mine.Points == game.Points, 10), $"A is paid in its own section ({Mine.Earned} for {Mine.Points} points, want {expected})");
            Expect(await Until(() => Count(ItemId.EngadinerNusstorte) == ChalandaRules.PastryPieces && Count(ItemId.ChalandaTreichel) == 1, 10), "A has the Nusstorte and the Treichel");
            Say($"pts{game.Points}");
            string? other = await HeardStarting("B", "pts", 90);
            int theirs = other != null && int.TryParse(other.AsSpan(3), out int v) ? v : -1;
            Expect(theirs > 0 && await Until(() => Svc.BoardOf(Id)?.Points == game.Points + theirs, 15), $"A's board holds the party's {game.Points}+{theirs} points ({Svc.BoardOf(Id)?.Points})");
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
            Teleport(stand.E + 150, stand.N + 150);
            await Seconds(1.0);
            Svc.AskChalandaStart();
            Expect(await Until(() => refused?.Contains("Go to the Treichel stand") == true, 10), $"a start from 150 m away is refused ({refused})");
            refused = null;
            Svc.AskChalandaRing(ClockSync.ServerNow);
            Expect(await Until(() => refused?.Contains("not in the procession") == true, 10), $"and so is a ring without a march ({refused})");
            Expect(Mine.Earned == 0, "nothing was paid");
            Say("refused");
            Expect(await Heard("A", "atstand", 60), "A is at the stand");
            Teleport(stand.E + 2.5, stand.N);
            Expect(await Until(() => game.NearStand, 10), "B stands at the stand");
            await Seconds(1.0);
            Say("atstand");
            Expect(await Heard("A", "started", 60), "A has set off");
            Expect(ChalandaGame.TryInteract(Me!), "B takes a Treichel and sets off");
            Expect(await Until(() => game.Marching, 10), $"the server began B's procession (refused: {refused})");
            Say("started");
            await MarchRings();
            Expect(await Until(() => game.SyncedRings >= 8, 10), $"B was told it was in step on {game.SyncedRings} beats");
            Expect(told.Any(t => t.What == FestivalService.ChalandaEvent.Synced && t.Text.Contains("In step with Lea")), "and with whom");
            int points = Rings * ChalandaRules.PerfectPoints + game.SyncedRings * ChalandaRules.SyncBonus;
            Expect(game.Points == points && game.LastGrade == 2, $"B's {game.Points} points are 3 a ring and 2 a beat in step ({points})");
            Say("rang");
            Expect(await Heard("A", "rang", 60), "A rang too");
            await Stations();
            int expected = ChalandaRules.Batzen(game.Points);
            Expect(await Until(() => Mine.Earned == expected && Mine.Points == game.Points, 10), $"B is paid in its own section ({Mine.Earned} for {Mine.Points} points, want {expected})");
            Expect(await Until(() => Count(ItemId.EngadinerNusstorte) == ChalandaRules.PastryPieces && Count(ItemId.ChalandaTreichel) == 1, 10), "B has the Nusstorte and the Treichel");
            Say($"pts{game.Points}");
            Expect(await Heard("A", "done", 120), "A saw the board");
        }
        await Finish(2.0);
    }
}
