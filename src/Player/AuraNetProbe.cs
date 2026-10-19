using System.Threading.Tasks;
using Godot;
using UnitSport.Combat;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Player;

/// <summary>
/// <c>--auranet A|B</c> with <c>--connect</c> (driven by <c>tools/auranetcheck.sh</c>, #907): an aura
/// battle between two clients, through the real paths.
/// <list type="bullet">
/// <item>A challenges B with <c>/aura</c>; B accepts with <c>/aura accept</c>. Both must get the battle.</item>
/// <item>Turn 1, A: plans stare down, slow walk, Siuuu and presses <see cref="PlayerInput.AuraNext"/>
/// on each downbeat. B must see A's copy in those poses, and both the same verdict, above zero.</item>
/// <item>Turn 2, B answers: yawn, sunglasses, point to sky, on the beat; its verdict must hold the
/// answer bonus. Meanwhile A forges a press for B's turn: the server refuses it.</item>
/// <item>Turn 3, B again (round 2 opens with B): it never cuts its last move, which the server must
/// call frozen, a Miss.</item>
/// <item>Turn 4, A: a nine-move plan is refused (still planning), then A forfeits; both must see B win,
/// the battle end, and the poses clear.</item>
/// </list>
/// </summary>
public partial class AuraNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--auranet");

    public AuraNetProbe(ItemController items) : base(items, "auranet", "AN", "auranet_") { }
    public AuraNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private static AuraManager? Auras => AuraManager.Client;

    private static readonly AuraMove[] Opening = { AuraMove.StareDown, AuraMove.SlowWalk, AuraMove.Siuuu };
    private static readonly AuraMove[] Answer = { AuraMove.Yawn, AuraMove.Sunglasses, AuraMove.PointToSky };
    private static readonly AuraMove[] Third = { AuraMove.Lasso, AuraMove.Mewing, AuraMove.ArmsCrossed };

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        Input.ActionRelease(PlayerInput.AuraNext);
        await Finish(2.0);
    }

    private async Task RunA(FootPlayer me)
    {
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }
        Chat?.Send("/aura AuraB");
        if (!Expect2(await Until(() => Auras?.Current != null, 20), "A is in the battle B accepted")) return;
        var v = Auras!.Current!;
        Expect(v.IsA, "the challenger is battler A");

        // turn 1: A performs the opening on the beat
        if (!await Turn(v, 0, Opening, skipLast: false)) return;
        Expect(me.InAura, "A's AuraPose is set");
        var mine = v.History[0];
        GD.Print($"{Log} turn 1: {mine.Score.Total} ({string.Join(" ", mine.Grades)})");
        Expect(mine.Score.Total > 0, $"A's opening scores ({mine.Score.Total})");
        Expect(Array.TrueForAll(mine.Grades, g => g is AuraGrade.Perfect or AuraGrade.Good), "every cut on the beat is Perfect or Good");
        Say($"turn1 {mine.Score.Total}");

        // turn 2, B's: a forged press for B's turn from A
        if (!Expect2(await Until(() => v.Turn == 1 && v.Phase == AuraPhase.Perform, 30), "turn 2: B performs")) return;
        Auras.RpcId(1, "Next", v.Id, 1, 0, ClockSync.ServerNow);
        Auras.RpcId(1, "Next", v.Id, 1, 0, ClockSync.ServerNow + 5.0);
        bool sawB = await Until(() => Copy() is { } b && AuraRules.MoveOf(b.AuraPose) != AuraMove.None, 10);
        Expect(sawB, "B's copy shows its moves");
        if (!Expect2(await Until(() => v.History.Count >= 2, 20), "B's answer is judged")) return;
        Expect(v.History[1].Score.Answer >= 10, $"B's yawn and sunglasses answer the opening ({v.History[1].Score.Answer})");

        // turn 3, B's again; turn 4, A's: a nine-move plan is refused, then A walks away
        if (!Expect2(await Until(() => v.Turn == 3 && v.Phase == AuraPhase.Plan && v.MyTurn, 45), "turn 4: A plans")) return;
        Expect(v.History.Count >= 3 && v.History[2].Grades[^1] == AuraGrade.Miss, "B's uncut last move was called frozen");
        var nine = new byte[9];
        for (int i = 0; i < nine.Length; i++) nine[i] = (byte)AuraMove.StandTall;
        Auras.RpcId(1, "SubmitPlan", v.Id, 3, nine);
        await Seconds(1.0);
        Expect(v.Phase == AuraPhase.Plan && v.Turn == 3, "the nine-move plan was refused: still planning");
        Chat?.Send("/aura leave");
        Expect(await Until(() => v.Phase == AuraPhase.Done && v.Winner == v.B, 10), "A's forfeit gives B the battle");
        Expect(await Until(() => Auras.Current == null, AuraMatch.DoneSeconds + 6), "the battle ends for A");
        Expect(await Until(() => !me.InAura, 3), "A's AuraPose clears");
        Say("done");
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        if (!Expect2(await Until(() => InviteService.Client?.IncomingCount > 0, 20), "B is challenged")) return;
        Chat?.Send("/aura accept");
        if (!Expect2(await Until(() => Auras?.Current != null, 10), "B is in the battle")) return;
        var v = Auras!.Current!;
        Expect(!v.IsA, "B is battler B");

        // A's turn: its copy strikes each move of the opening, in order
        if (!Expect2(await Until(() => v.Turn == 0 && v.Phase == AuraPhase.Perform, 30), "turn 1: A performs")) return;
        bool stare = await Until(() => Copy() is { } a && AuraRules.MoveOf(a.AuraPose) == AuraMove.StareDown, 6);
        bool siuuu = await Until(() => Copy() is { } a && AuraRules.MoveOf(a.AuraPose) == AuraMove.Siuuu, 10);
        Expect(stare && siuuu, "A's copy does the stare down, then the Siuuu");
        if (DisplayServer.GetName() != "headless") GD.Print($"{Log} shot {Shot("B_watch")}");
        if (!Expect2(await Until(() => v.History.Count >= 1, 20), "A's turn is judged")) return;
        if (!await Heard("A", $"turn1 {v.History[0].Score.Total}", 10)) Fail("A and B disagree on A's score");

        // turn 2: B answers on the beat
        if (!await Turn(v, 1, Answer, skipLast: false)) return;
        var answer = v.History[1].Score;
        GD.Print($"{Log} turn 2: {answer.Total}, answer {answer.Answer}");
        Expect(answer.Answer >= 10, $"B's verdict holds the answer bonus ({answer.Answer})");

        // turn 3 (round 2 opens with B): the last move never cut
        if (!await Turn(v, 2, Third, skipLast: true)) return;
        Expect(v.History[2].Grades[^1] == AuraGrade.Miss, "the uncut last move is a Miss");

        Expect(await Until(() => v.Phase == AuraPhase.Done && v.Winner == v.Me, 60), "B wins by A's forfeit");
        Expect(await Until(() => Auras.Current == null, AuraMatch.DoneSeconds + 6), "the battle ends for B");
        Expect(await Until(() => Copy() is { InAura: false }, 5), "A's copy is out of its aura pose");
        if (!await Heard("A", "done", 20)) Fail("A never finished");
    }

    /// <summary>
    /// Plans <paramref name="plan"/> for <paramref name="turn"/> and performs it: Next pressed on
    /// each downbeat (the last left uncut when <paramref name="skipLast"/>), until its verdict arrives.
    /// </summary>
    private async Task<bool> Turn(AuraManager.View v, int turn, AuraMove[] plan, bool skipLast)
    {
        if (!Expect2(await Until(() => v.Turn == turn && v.Phase == AuraPhase.Plan && v.MyTurn, 45), $"turn {turn + 1}: my plan")) return false;
        await Seconds(0.5);
        Auras!.SendPlan(plan);
        if (!Expect2(await Until(() => v.Plan.Length == plan.Length && v.Turn == turn, 10), $"turn {turn + 1}: the plan is taken")) return false;
        int presses = skipLast ? plan.Length - 1 : plan.Length;
        for (int i = 0; i < presses; i++)
        {
            double cut = AuraRules.CutAt(v.Plan, i, v.T0);
            // a frame before the downbeat, so the press lands within a frame of it
            await Until(() => ClockSync.ServerNow >= cut - 0.008, 30);
            Input.ActionPress(PlayerInput.AuraNext);
            await Until(() => v.NextIndex > i, 1);
            Input.ActionRelease(PlayerInput.AuraNext);
            await Seconds(0.05);
        }
        return Expect2(await Until(() => v.History.Count > turn, 20), $"turn {turn + 1}: the verdict");
    }

    private bool Expect2(bool ok, string what)
    {
        Expect(ok, what);
        return ok;
    }

    /// <summary>The other player's copy here.</summary>
    private FootPlayer? Copy()
    {
        foreach (var s in PlayerSnapshot.Of(GetTree()))
            if (s.Player != Me && !s.Player.IsMultiplayerAuthority() && !s.Player.Npc) return s.Player;
        return null;
    }
}
