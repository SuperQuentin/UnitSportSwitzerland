using System.Threading.Tasks;
using Godot;
using UnitSport.Combat;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Player;

/// <summary>
/// <c>--auranet A|B|C</c> with <c>--connect</c> (driven by <c>tools/auranetcheck.sh</c>, #907, #914): aura
/// battles between clients, and a bot, through the real paths.
/// <list type="bullet">
/// <item>A challenges B with <c>/aura</c>; B accepts as E at A does (<see cref="AuraManager.AcceptFrom"/>).</item>
/// <item>Turn 1, A: plans stare down, slow walk, Siuuu and cuts on each downbeat, once C watches. B
/// sees A's copy strike the poses and the same verdict; C, a spectator, sees the battle on its screen
/// and cheers, which A's verdict counts. B breaks character on purpose (presses with nothing to hold
/// in): it laughed first, and A's verdict holds the bonus.</item>
/// <item>Turn 2, B answers (the answer bonus) while A keeps a straight face against any tempt; A
/// forges presses for B's turn, which the server refuses. Turn 3, B leaves its last cut: frozen.</item>
/// <item>Turn 4, A: a nine-move plan is refused, then A forfeits; both see B win.</item>
/// <item>C then adds a bot (admin), steps up to it and challenges it: the bot takes it at once,
/// plans, and cuts its own combo on the beat; C leaves.</item>
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
    private static readonly AuraMove[] Third = { AuraMove.Lasso, AuraMove.LeadRival, AuraMove.Bow };

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (_role == "A") await RunA(Me!);
        else if (_role == "B") await RunB(Me!);
        else await RunC(Me!);
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

        // turn 1: A performs the opening on the beat, once C watches (its cheers count)
        if (!await Heard("C", "watching", 40)) Fail("C never watched");
        if (!await Turn(v, 0, Opening, skipLast: false)) return;
        Expect(me.InAura, "A's AuraPose is set");
        var mine = v.History[0];
        GD.Print($"{Log} turn 1: {mine.Score.Total} ({string.Join(" ", mine.Grades)}), crowd {mine.Score.Crowd}, laugh {mine.Score.Composure}");
        Expect(mine.Score.Total > 0, $"A's opening scores ({mine.Score.Total})");
        // a headless client under load can hitch a frame or two: on the beat must not be a Miss
        Expect(Array.TrueForAll(mine.Grades, g => g is AuraGrade.Perfect or AuraGrade.Good or AuraGrade.Ok), "every cut on the beat lands");
        Expect(mine.Score.Crowd >= AuraRules.CrowdStep, $"C's cheers count for the crowd ({mine.Score.Crowd})");
        Expect(mine.Score.Composure == AuraRules.LaughBonus, $"B laughed first: A's verdict holds the bonus ({mine.Score.Composure})");
        Expect(v.LaughsB == 1, $"B's laugh is counted ({v.LaughsB})");
        Say($"turn1 {mine.Score.Total}");

        // turn 2, B's: forged presses; and a straight face against every tempt
        if (!Expect2(await Until(() => v.Turn == 1 && v.Phase == AuraPhase.Perform, 30), "turn 2: B performs")) return;
        Auras.RpcId(1, "Next", v.Id, 1, 0, ClockSync.ServerNow);
        Auras.RpcId(1, "Next", v.Id, 1, 0, ClockSync.ServerNow + 5.0);
        int tempts = 0;
        while (v.Turn == 1 && v.Phase == AuraPhase.Perform)
        {
            if (!double.IsNaN(v.TemptAt))
            {
                tempts++;
                await Seconds(0.3);
                Input.ActionPress(PlayerInput.AuraNext);
                await Seconds(0.05);
                Input.ActionRelease(PlayerInput.AuraNext);
            }
            await Seconds(0.02);
        }
        GD.Print($"{Log} kept a straight face against {tempts} tempts");
        if (!Expect2(await Until(() => v.History.Count >= 2, 20), "B's answer is judged")) return;
        Expect(v.History[1].Score.Answer >= 10, $"B's yawn and sunglasses answer the opening ({v.History[1].Score.Answer})");
        Expect(v.LaughsA == 0, $"A never laughed ({v.LaughsA}, {tempts} tempts)");

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
        if (!Expect2(await Until(() => Auras?.Pending.Count > 0, 20), "B is challenged")) return;
        long from = 0;
        foreach (var (peer, _) in Auras!.Pending) from = peer;
        Expect(Auras.ChallengedBy(from) && Auras.Prompt(from).StartsWith("Accept"), $"E at A would accept ('{Auras.Prompt(from)}')");
        Expect(Auras.AcceptFrom(from), "E at A takes the aura challenge");
        if (!Expect2(await Until(() => Auras.Current != null, 10), "B is in the battle")) return;
        var v = Auras.Current!;
        Expect(!v.IsA, "B is battler B");

        // A's turn: B breaks character on purpose, then watches A's copy strike the opening
        if (!Expect2(await Until(() => v.Turn == 0 && v.Phase == AuraPhase.Perform, 60), "turn 1: A performs")) return;
        Input.ActionPress(PlayerInput.AuraNext);
        await Seconds(0.05);
        Input.ActionRelease(PlayerInput.AuraNext);
        Expect(await Until(() => v.LaughsB == 1, 5), "pressing with nothing to hold in: B laughed first");
        Expect(await Until(() => me.AuraPose == (int)AuraStance.Laugh, 2), "B's body laughs");
        bool stare = await Until(() => Copy(v.Opponent) is { } a && AuraRules.MoveOf(a.AuraPose) is AuraMove.StareDown or AuraMove.SlowWalk, 6);
        bool siuuu = await Until(() => Copy(v.Opponent) is { } a && AuraRules.MoveOf(a.AuraPose) == AuraMove.Siuuu, 10);
        Expect(stare && siuuu, "A's copy strikes the opening, then the Siuuu");
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
        // the pair chains only between two cuts that landed (a hard move's window is narrow under load)
        var third = v.History[2];
        if (third.Grades[0] != AuraGrade.Miss && third.Grades[1] != AuraGrade.Miss)
            Expect(third.Score.Chain >= AuraRules.PairBonus(AuraMove.Lasso, AuraMove.LeadRival), "the lasso into the lead chains");
        else GD.Print($"{Log} the lasso or the lead missed ({string.Join(" ", third.Grades)}): no chain to check");

        Expect(await Until(() => v.Phase == AuraPhase.Done && v.Winner == v.Me, 60), "B wins by A's forfeit");
        Expect(await Until(() => Auras.Current == null, AuraMatch.DoneSeconds + 6), "the battle ends for B");
        Expect(await Until(() => Copy(v.Opponent) is { InAura: false }, 5), "A's copy is out of its aura pose");
        if (!await Heard("A", "done", 20)) Fail("A never finished");
    }

    /// <summary>C: a spectator who cheers, then a battle against a bot.</summary>
    private async Task RunC(FootPlayer me)
    {
        if (!Expect2(await Until(() => Auras?.Watched != null, 90), "C watches the battle nearby")) return;
        var w = Auras!.Watched!;
        Expect(w.NameA == "AuraA" && w.NameB == "AuraB", $"the spectator's screen names both ({w.NameA} vs {w.NameB})");
        Say("watching");
        if (!Expect2(await Until(() => Auras.Watched is { Turn: 0, Phase: AuraPhase.Perform }, 60), "C sees A perform")) return;
        if (DisplayServer.GetName() != "headless") GD.Print($"{Log} shot {Shot("C_watch")}");
        me.DanceId = FootPlayer.EmoteDanceBase + 1;   // Cheer, as the emote wheel plays it
        Expect(await Until(() => Auras.Watched is { } x && x.Plan.Length == 3 && x.Grades[0] != AuraGrade.Pending, 10), "the grades reach the spectator");
        await Until(() => Auras.Watched is not { Phase: AuraPhase.Perform }, 15);
        me.DanceId = 0;
        Expect(await Until(() => Auras.Watched is { Phase: AuraPhase.Verdict, LastTotal: > 0 }, 10), "the spectator sees the verdict");
        if (!await Heard("A", "done", 120)) { Fail("A never finished"); return; }
        Expect(await Until(() => Auras.Watched == null, 15), "the battle over, nothing left to watch");

        // a bot: added by an admin, stepped up to, challenged; it takes it and performs
        Chat?.Send("/login test");
        await Seconds(1.0);
        Chat?.Send("/bot add 1 hard");
        FootPlayer? bot = null;
        if (!Expect2(await Until(() => (bot = BotCopy()) != null && bot.BotName.Length > 0, 20), "a bot is here")) return;
        var at = bot!.GlobalPosition + (me.GlobalPosition - bot.GlobalPosition).Normalized() * 3f;
        me.GlobalPosition = at with { Y = Mathf.Max(at.Y, bot.GlobalPosition.Y) + 0.3f };
        await Seconds(1.0);
        Chat?.Send($"/aura {bot.BotName}");
        if (!Expect2(await Until(() => Auras.Current != null, 15), $"the bot {bot.BotName} takes the battle at once")) return;
        var v = Auras.Current!;
        if (!await Turn(v, 0, Opening, skipLast: false)) return;
        if (!Expect2(await Until(() => v.Turn == 1 && v.Phase == AuraPhase.Perform, 30), "the bot plans its turn")) return;
        Expect(await Until(() => AuraRules.MoveOf(bot.AuraPose) != AuraMove.None, 10), "the bot's body strikes its moves");
        if (!Expect2(await Until(() => v.History.Count >= 2, 30), "the bot's turn is judged")) return;
        var botTurn = v.History[1];
        GD.Print($"{Log} the bot performed {AuraManager.Combo(botTurn.Moves)}: {botTurn.Score.Total} ({string.Join(" ", botTurn.Grades)})");
        Expect(AuraRules.ValidatePlan(botTurn.Moves), "the bot planned a valid combo");
        Expect(Array.Exists(botTurn.Grades, g => g != AuraGrade.Miss), "the bot cut on the beat");
        Chat?.Send("/aura leave");
        Expect(await Until(() => Auras.Current == null, AuraMatch.DoneSeconds + 6), "the bot battle ends");
        Expect(await Until(() => bot.AuraPose == 0, 5), "the bot is out of its aura pose");
    }

    /// <summary>
    /// Plans <paramref name="plan"/> for <paramref name="turn"/> and performs it: Next pressed on
    /// each downbeat (the last left uncut when <paramref name="skipLast"/>), until its verdict arrives.
    /// </summary>
    private async Task<bool> Turn(AuraManager.View v, int turn, AuraMove[] plan, bool skipLast)
    {
        if (!Expect2(await Until(() => v.Turn == turn && v.Phase == AuraPhase.Plan && v.MyTurn, 45), $"turn {turn + 1}: my plan")) return false;
        await Seconds(1.0);
        // windowed (SHOTS=1): the planner as it opens, then the performance with its beat ring
        bool windowed = DisplayServer.GetName() != "headless";
        if (windowed) GD.Print($"{Log} shot {Shot($"{_role}_plan{turn + 1}")}");
        Auras!.SendPlan(plan);
        if (!Expect2(await Until(() => v.Plan.Length == plan.Length && v.Turn == turn, 10), $"turn {turn + 1}: the plan is taken")) return false;
        int presses = skipLast ? plan.Length - 1 : plan.Length;
        for (int i = 0; i < presses; i++)
        {
            double cut = AuraRules.CutAt(v.Plan, i, v.T0, v.Beat);
            // a frame before the downbeat, so the press lands within a frame of it
            await Until(() => ClockSync.ServerNow >= cut - 0.008, 30);
            if (windowed && i == 1) GD.Print($"{Log} shot {Shot($"{_role}_perform{turn + 1}")}");
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

    /// <summary>Player <paramref name="peer"/>'s copy here.</summary>
    private FootPlayer? Copy(long peer)
    {
        foreach (var s in PlayerSnapshot.Of(GetTree()))
            if (s.Player != Me && FootPlayer.NetId(s.Player.Name) == peer) return s.Player;
        return null;
    }

    private FootPlayer? BotCopy()
    {
        foreach (var s in PlayerSnapshot.Of(GetTree()))
            if (s.Player.Bot) return s.Player;
        return null;
    }
}
