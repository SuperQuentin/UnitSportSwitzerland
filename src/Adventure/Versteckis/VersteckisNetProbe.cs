using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Versteckis;

/// <summary>
/// <c>--vsnet a|b</c> with <c>--connect</c> (driven by <c>tools/versteckisnetcheck.sh</c>, #1268): two friends play a
/// two-round game of Versteckis on the hamlet with no bots; every assertion is about what reached the REMOTE peer.
/// <list type="number">
/// <item>A opens a game at the hamlet; B accepts the invite (as F2 would).</item>
/// <item>Round 1, A seeks: in Hide B's body is withheld on A (the server's <c>[vs] withheld</c>), and A's walk away from
/// the church door is put back.</item>
/// <item>B becomes a milk can, and A's copy of B draws a milk can.</item>
/// <item>A tags 2 m beside B (a miss, the cooldown), sends a forged tag 60 m away (refused), then tags B: caught on both.</item>
/// <item>Round 2, B seeks: B's copy of A draws a hay bale; A rings the bell (its disguise drops on B while it holds), +3 on
/// both peers' state; an Adventure request is refused while A is in the round; B then tags A.</item>
/// <item>The results, the release: A and B both get the <c>versteckis</c> section and are paid the same way (the script
/// compares the server's <c>[vs] paid</c> lines).</item>
/// </list>
/// </summary>
public partial class VersteckisNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--vsnet");
    private const string Password = "vspw";

    public VersteckisNetProbe(ItemController items) : base(items, "vsnet", "VS", "vsnet_") { }
    public VersteckisNetProbe() : this(null!) { }

    private static VersteckisManager Vs => VersteckisManager.Instance!;
    private VsState? S => Vs.Mine;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => VersteckisManager.Instance != null)) return;
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        // B says it is on; A, the first in, waits for it (B may join after A spoke)
        Say("ready");
        if (_role == "A") Expect(await Heard("B", "ready", 120), "B is on");
        if (_role == "A") await A();
        else await B();
        await Finish(5.0);
    }

    private FootPlayer? Body(long peer) => GetNodeOrNull<FootPlayer>("../Players/" + PlayerReplication.NodeName(peer));
    private long Other => S?.Entrants.FirstOrDefault(e => e.Peer != Vs.Me)?.Peer ?? 0;

    private async Task<bool> Phase(VsPhase phase, int round, double seconds) =>
        await Until(() => S is { } s && s.Phase == phase && s.Round == round, seconds);

    /// <summary>Walks the body in small steps, under a disguised walk's check (1 m a half second of game time).</summary>
    private async Task WalkTo(double e, double n, double step)
    {
        var me = Me!;
        for (int i = 0; i < 200; i++)
        {
            var g = me.Global;
            double de = e - g.E, dn = n - g.N, d = Math.Sqrt(de * de + dn * dn);
            if (d < 0.3) break;
            double s = Math.Min(d, step);
            me.GlobalPosition += new Vector3((float)(de / d * s), 0.1f, (float)(-dn / d * s));
            me.Velocity = Vector3.Zero;
            await Seconds(0.5);
        }
        await Seconds(0.6);
    }

    private async Task A()
    {
        if (!await SpeedUp(Password)) return;
        Chat!.Send("/vs open nearest 2 bots off");
        Expect(await Until(() => S is { Phase: VsPhase.Lobby, Kinds.Count: > 0 }, 60), $"A's game is open and its village read ({S?.Label})");
        Say("open");
        Expect(await Until(() => S is { } s && s.Humans == 2, 60), "B joined from the invite");
        Chat.Send("/vs start");
        Expect(await Until(() => S?.Phase == VsPhase.Gather, 15), "the game gathers on the square");
        Expect(await Until(() => Body(Other) != null, 15), "on the square A sees B");

        // round 1: A seeks; in Hide B is withheld from A and A is held at the door
        Expect(await Phase(VsPhase.Hide, 1, 30), "round 1: Hide");
        Expect(S!.Seeker == Vs.Me && Vs.MyEntry?.Role == VsRole.Seeker, "A seeks first (join order)");
        Expect(await Until(() => Vs.Hud?.Blind == true, 5), "A counts behind the black screen");
        Expect(await UntilHeld(() => Body(Other) == null, 1.0, 10), "B's body is withheld on A during Hide");
        var s = S!;
        var door = (s.DoorE, s.DoorN);
        Me!.GlobalPosition += new Vector3(12, 0.2f, 0);
        Expect(await Until(() => Me.Global.HorizontalDistanceTo(new GlobalPos(door.DoorE, door.DoorN, 0)) < 4, 15), "A's walk away from the door is put back");

        // Seek: A's copy of B is a milk can
        Expect(await Phase(VsPhase.Seek, 1, 60), "round 1: Seek");
        Expect(await Until(() => Body(Other) is { } b && VsDisguise.Of(b)?.Kind == VsProp.MilkCan, 20),
            $"A's copy of B draws a milk can ({(Body(Other) is { } bb ? VsDisguise.Of(bb)?.Kind.ToString() ?? "no disguise" : "no body")})");
        await Heard("B", "hidden", 30);
        var bAt = Body(Other)!.Global;
        await WalkTo(bAt.E - 2, bAt.N, 8);
        double missedBefore = Vs.MissedAt;
        Vs.ProbeTag(bAt.E - 1.0, bAt.N + 1.2, (float)bAt.Alt);
        Expect(await Until(() => Vs.MissedAt > missedBefore, 10), "a tag beside B misses (the server says so)");
        Vs.ProbeTag(bAt.E + 60, bAt.N, (float)bAt.Alt);
        await Seconds(VsRules.MissCooldown + 0.5);
        bAt = Body(Other)!.Global;
        Vs.ProbeTag(bAt.E, bAt.N, (float)bAt.Alt);
        Expect(await Until(() => S?.Find(Other) is { Caught: true } || S?.Phase == VsPhase.RoundEnd, 10), "A's tag catches B (judged by the server)");
        Expect(Vs.MyEntry?.Catches >= 1, "A is credited the catch");

        // round 2: B seeks, A hides as a hay bale and rings the bell
        Expect(await Phase(VsPhase.Hide, 2, 60), "round 2: Hide");
        Expect(S!.Seeker == Other, "B seeks the second round");
        Vs.Disguise(VsProp.HayBale);
        Expect(await Until(() => Vs.MyEntry?.Prop == VsProp.HayBale, 10), "A is a hay bale");
        Expect(await Phase(VsPhase.Seek, 2, 60), "round 2: Seek");
        Say("hidden");
        await Heard("B", "seen", 30);
        // the adventure gate is off for A in the round
        if (AdventureService.Instance is { } adv)
        {
            string? refused = null;
            adv.Refused += why => refused = why;
            adv.AskStart(ProbeQuests.Walk);
            Expect(await Until(() => refused != null, 10), $"an Adventure request is refused in the round ({refused})");
        }
        var bell = S!.Objectives.First(o => o.Site == VsSite.Bell);
        int bellIndex = S.Objectives.IndexOf(bell);
        await WalkTo(bell.E, bell.N - 1.0, 1.0);
        Expect(await Until(() => Vs.Offer?.Site == bellIndex, 5), $"the bell is offered ({Vs.Offer?.Prompt})");
        int before = Vs.MyEntry!.Score;
        Vs.ForceHold = true;
        Expect(Me!.TryInteract(), "E at the church bell");
        Expect(await Until(() => Vs.MyEntry?.Holding == bellIndex, 5), "the server has A holding the bell rope");
        Say("holding");
        Expect(await Until(() => S!.Objectives[bellIndex].Done, 20), "the bell is rung");
        Vs.ForceHold = false;
        Expect(await Until(() => Vs.MyEntry!.Score >= before + 3, 5), $"+3 for the ringer ({before} -> {Vs.MyEntry!.Score})");
        Say("rung");

        await EndAndPaid();
    }

    private async Task B()
    {
        Expect(await Heard("A", "open", 90), "A opened a game");
        var invites = InviteService.Client;
        Expect(await Until(() => invites?.Incoming.Any(v => v.Kind == VersteckisManager.InviteId) == true, 30), "B is invited");
        invites!.Accept(invites.Incoming.First(v => v.Kind == VersteckisManager.InviteId).Id);
        Expect(await Until(() => S != null, 15), "B is in A's game");

        Expect(await Phase(VsPhase.Hide, 1, 60), "round 1: Hide");
        Expect(Vs.MyEntry?.Role == VsRole.Hider, "B hides");
        Vs.Disguise(VsProp.MilkCan);
        Expect(await Until(() => Vs.MyEntry?.Prop == VsProp.MilkCan, 10), "B is a milk can");
        await WalkTo(S!.E, S.N - 22, 1.0);
        Expect(await Phase(VsPhase.Seek, 1, 60), "round 1: Seek");
        Say("hidden");
        Expect(await Until(() => Vs.MyEntry?.Caught == true || S?.Find(Vs.Me)?.Caught == true || S?.Phase == VsPhase.RoundEnd, 60), "B is caught (on B too)");

        // round 2: B seeks; A's copy draws a hay bale, then its body while it holds the bell
        Expect(await Phase(VsPhase.Hide, 2, 60), "round 2: Hide");
        Expect(Vs.MyEntry?.Role == VsRole.Seeker, "B seeks");
        Expect(await Phase(VsPhase.Seek, 2, 60), "round 2: Seek");
        await Heard("A", "hidden", 30);
        Expect(await Until(() => Body(Other) is { } a && VsDisguise.Of(a)?.Kind == VsProp.HayBale, 20), "B's copy of A draws a hay bale");
        Say("seen");
        Expect(await Heard("A", "holding", 60), "A holds the bell");
        Expect(await Until(() => Body(Other) is { } a && VsDisguise.Of(a) is not { Kind: not VsProp.None }, 5), "the bell breaks A's disguise on B");
        int aBefore = S!.Find(Other)!.Score;
        Expect(await Until(() => S!.Objectives.Any(o => o.Site == VsSite.Bell && o.Done), 20), "B's state has the bell rung");
        Expect(await Until(() => S!.Find(Other)!.Score >= aBefore + 3, 5), "and A's +3 on B's peer");
        await Heard("A", "rung", 30);
        var aAt = Body(Other)!.Global;
        await WalkTo(aAt.E, aAt.N - 1.5, 8);
        aAt = Body(Other)!.Global;
        Vs.ProbeTag(aAt.E, aAt.N, (float)aAt.Alt);
        Expect(await Until(() => S?.Phase is VsPhase.RoundEnd or VsPhase.Results, 15), "B catches A: the round ends");

        await EndAndPaid();
    }

    private async Task EndAndPaid()
    {
        long balance = Bank.Instance?.Balance ?? 0;
        Expect(await Until(() => S == null && !Vs.InGame, 90), "the game ends and this player is released");
        Expect(!Permissions.InMatch, "the round's lending is over");
        if (AdventureService.Instance is { } adv)
            Expect(await Until(() => adv.Mine.Get(VersteckisProgress.Section).Games >= 1, 15),
                $"the versteckis section reached this player ({adv.Mine.Get(VersteckisProgress.Section).Games} games)");
        Expect(await Until(() => (Bank.Instance?.Balance ?? 0) > balance, 15), $"paid into the account ({balance} -> {Bank.Instance?.Balance})");
    }
}
