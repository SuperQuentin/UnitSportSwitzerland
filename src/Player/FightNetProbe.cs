using System.Threading.Tasks;
using Godot;
using UnitSport.Combat;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>
/// <c>--fightnet A|B</c> with <c>--connect</c> (driven by <c>tools/fightnetcheck.sh</c>, #495): a fist
/// fight between two clients, through the real paths.
/// <list type="bullet">
/// <item>A challenges B with <c>/fight</c>; B accepts through <see cref="FightManager.Engage"/>, what E
/// at the challenger calls. Both must get the match.</item>
/// <item>At FIGHT! A steps in and jabs with the input actions pressed (screen-right is towards B: A is
/// on the left) until the round is over. A must see B's HP fall and win the round; B must see its own HP
/// fall and A's copy in the jab pose.</item>
/// <item>Round 2: A sends 30 strikes in one frame; the server's rate budget lets at most two land.
/// Then A forfeits, and both must see the match end with B the winner.</item>
/// </list>
/// </summary>
public partial class FightNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--fightnet");

    public FightNetProbe(ItemController items) : base(items, "fightnet", "FN", "fightnet_") { }
    public FightNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private static FightManager? Fights => FightManager.Client;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        Release();
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
        Chat?.Send("/fight FighterB");
        if (!Expect2(await Until(() => Fights?.Current != null, 20), "A is in the fight B accepted")) return;
        var v = Fights!.Current!;
        Expect(v.IsA, "the challenger is fighter A");
        if (!Expect2(await Until(() => v.Phase == FightPhase.Live, 10), "round 1: FIGHT!")) return;
        Expect(me.Fighting, "A's FightPose is set");

        // step in and jab until the round is over
        int theirs = v.TheirHp;
        for (int i = 0; i < 80 && v.Phase == FightPhase.Live; i++)
        {
            await Approach(me);
            Input.ActionPress(PlayerInput.FightPunch);
            await Seconds(0.05);
            Input.ActionRelease(PlayerInput.FightPunch);
            await Seconds(0.30);
        }
        GD.Print($"{Log} round 1: hp {v.MyHp}/{v.TheirHp}, phase {v.Phase} {v.End}");
        Expect(v.TheirHp < theirs || v.WinsA > 0, "A's jabs took B's HP down");
        Expect(await Until(() => v.WinsA == 1, 10), "A won round 1");
        Say("round1");

        // round 2: a burst of forged strikes, close enough to be in reach
        if (!Expect2(await Until(() => v.Round == 2 && v.Phase == FightPhase.Live, 15), "round 2: FIGHT!")) return;
        await Approach(me);
        int before = v.TheirHp;
        for (int i = 0; i < 30; i++) Fights.SendStrike(FightMove.Jab);
        await Seconds(1.5);
        int lost = before - v.TheirHp;
        GD.Print($"{Log} 30 jabs at once took {lost} HP");
        Expect(lost <= 2 * FightRules.Def(FightMove.Jab).Damage, $"the rate budget held a 30-jab burst to {lost} HP");

        Chat?.Send("/fight leave");
        Expect(await Until(() => v.Phase == FightPhase.Done && v.Winner == v.B, 10), "A's forfeit gives B the match");
        Expect(await Until(() => Fights.Current == null, FightMatch.DoneSeconds + 6), "the match ends for A");
        Expect(await Until(() => !me.Fighting, 3), "A's FightPose clears");
        Say("done");
    }

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        if (!Expect2(await Until(() => Fights?.Pending.Count > 0, 20), "B is challenged")) return;
        long from = 0;
        foreach (var (peer, _) in Fights!.Pending) from = peer;
        Expect(Fights.Prompt(from).StartsWith("Accept"), $"the prompt at A offers to accept ('{Fights.Prompt(from)}')");
        Fights.Engage(from);
        if (!Expect2(await Until(() => Fights.Current != null, 10), "B is in the fight")) return;
        var v = Fights.Current!;
        Expect(!v.IsA && v.Opponent == from, "B fights the challenger");

        // A's copy throws jabs, and they land here
        bool sawJab = await Until(() => Copy() is { } a && FightRules.MoveOf((byte)a.FightPose) == FightMove.Jab, 30);
        Expect(sawJab, "A's copy shows the jab pose");
        // windowed (SHOTS=1): the side-on camera and the HUD as B sees them
        if (DisplayServer.GetName() != "headless") GD.Print($"{Log} shot {Shot("B_fight")}");
        Expect(await Until(() => v.MyHp < FightRules.MaxHp || v.WinsA > 0, 20), "A's jabs land on B");
        if (!await Heard("A", "round1", 60)) { Fail("A never finished round 1"); return; }
        Expect(v.WinsA == 1, "B saw A win round 1");
        Expect(await Until(() => v.Phase == FightPhase.Done && v.Winner == me.GetMultiplayerAuthority(), 40), "B wins by A's forfeit");
        Expect(await Until(() => Fights.Current == null, FightMatch.DoneSeconds + 6), "the match ends for B");
        Expect(await Until(() => Copy() is { Fighting: false }, 5), "A's copy is out of its fight pose");
        if (!await Heard("A", "done", 20)) Fail("A never finished");
    }

    /// <summary>Holds the step towards the opponent (screen-right: A is on the left) until within jab reach.</summary>
    private async Task Approach(FootPlayer me)
    {
        for (int i = 0; i < 60; i++)
        {
            if (Copy() is not { } b) return;
            var d = b.GlobalPosition - me.GlobalPosition;
            d.Y = 0;
            if (d.Length() < 1.25f) break;
            Input.ActionPress(PlayerInput.MoveRight);
            await Seconds(0.05);
        }
        Input.ActionRelease(PlayerInput.MoveRight);
    }

    private static void Release()
    {
        Input.ActionRelease(PlayerInput.MoveRight);
        Input.ActionRelease(PlayerInput.FightPunch);
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
