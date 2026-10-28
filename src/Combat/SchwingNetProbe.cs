using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions.Festivals;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>
/// <c>--schwingnet A|B</c> with <c>--connect</c> (driven by <c>tools/schwingnetcheck.sh</c>, #1269): Schwingen between two clients and a
/// server-run bot, through the real paths. Everything is asserted on the remote peer too.
/// <list type="bullet">
/// <item>A asks B with <c>/schwing</c>; B accepts. A kicks (Gammen) with the input action: B sees the telegraph and A's copy in the throw
/// pose; B braces with an <c>Act</c>, and both see the throw braced.</item>
/// <item>B stops answering: A throws until B is down, B lies in the sawdust and sees A's copy kneeling over it; A mashes the tug and wins;
/// both see A's copy brush and the bout end. A burst of forged acts is held by the server's budget (the script greps the server log).
/// This bout is off the festival: no Batzen.</item>
/// <item>At the Älplerfest's ring (<c>--festival test</c>): both press E at the ring (the queue), the server pairs them, A wins a
/// bout longer than the paid minimum and both are paid, on the remote peer's own book.</item>
/// <item>A alone in the queue is given a bot after <see cref="SchwingManager.FillSeconds"/>; A asks the bot with <c>/schwing bot</c>.</item>
/// </list>
/// </summary>
public partial class SchwingNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--schwingnet");

    public SchwingNetProbe(ItemController items) : base(items, "schwingnet", "SN", "schwingnet_") { }
    public SchwingNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private static SchwingManager? Bouts => SchwingManager.Client;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        Release();
        await Finish(2.0);
    }

    // ---- A -----------------------------------------------------------------------------------

    private async Task RunA(FootPlayer me)
    {
        if (!await SpeedUp("schwingnet")) return;
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        // ---- bout 1: off the festival ------------------------------------------------------
        Chat?.Send("/schwing SchwingB");
        if (!Expect2(await Until(() => Bouts?.Current != null, 20), "A is in the bout B accepted")) return;
        var v = Bouts!.Current!;
        Expect(v.IsA && !v.Festival, "the challenger is wrestler A, off the festival");
        if (!Expect2(await Until(() => v.Phase == SchwingPhase.Live, 10), "the bout is live")) return;
        Expect(await Until(() => me.InSchwing, 3), "A's SchwingPose is set");
        await Seconds(0.5);

        // a forged burst: only the budget's worth is taken (the script greps the server log for the refusals)
        for (int i = 0; i < 60; i++) Bouts.SendAct((int)SchwingLean.None, false, 0);
        await Seconds(1.2);

        // the first kick: B braces it
        Kick();
        Expect(await Until(() => v.Pending == SchwingThrow.Gammen, 5), "A's kick is a Gammen winding up");
        Expect(await Until(() => v.LastAnswer == SchwingAnswer.Braced, 5), "B braced it");
        Expect(v.BalanceA < 100, $"the braced throw cost the thrower balance ({v.BalanceA})");
        Say("braced");

        // B stops answering: kick until B is down
        for (int i = 0; i < 40 && v.Phase == SchwingPhase.Live; i++)
        {
            await Seconds(1.4);
            if (v.Pending == SchwingThrow.None && v.Phase == SchwingPhase.Live) Kick();
            await Seconds(0.1);
        }
        if (!Expect2(v.Phase == SchwingPhase.Ground, $"B went down to the ground work ({v.Phase}, balance {v.TheirBalance})")) return;
        Expect(v.IAmTop, "A is on top");
        Expect(await Until(() => me.SchwingPose == (int)SchwingStance.GroundTop, 3), "A kneels over the rival");
        // mash the tug until the pin
        for (int i = 0; i < 200 && v.Phase == SchwingPhase.Ground; i++)
        {
            Input.ActionPress(PlayerInput.FightPunch);
            await Seconds(0.06);
            Input.ActionRelease(PlayerInput.FightPunch);
            await Seconds(0.06);
        }
        Expect(await Until(() => v.Phase == SchwingPhase.Done, 10), "the bout ends");
        Expect(v.Winner == v.A && v.End == SchwingEnd.Won, $"A pinned B ({v.End}, winner {v.Winner})");
        Expect(await Until(() => Bouts.Current == null, SchwingRules.DoneSeconds + 6), "the bout is over for A");
        Expect(await Until(() => !me.InSchwing, 3), "A's SchwingPose clears");
        Expect(Booked("aelplerfest-2027").Batzen == 0, "an off-festival bout pays nothing");
        Say("bout1");

        // ---- bout 2: at the ring, paid ------------------------------------------------------
        if (!await Heard("B", "ring", 60)) { Fail("B never got to the ring"); return; }
        Expect(await Until(() => SchwingManager.RingNow() != null, 30), "the Älplerfest's ring is open");
        await ToRing(me);
        Bouts.EngageRing();
        Expect(await Until(() => Bouts.Queued, 5), "A is in the ring's queue");
        if (!Expect2(await Until(() => Bouts.Current is { Festival: true }, 20), "B and A are paired at the ring")) return;
        v = Bouts.Current!;
        if (!Expect2(await Until(() => v.Phase == SchwingPhase.Live, 10), "the ring bout is live")) return;
        // longer than the paid minimum (20 s of play), then throw until the pin
        await Seconds(SchwingManager.MinPaidSeconds + 2);
        for (int i = 0; i < 40 && v.Phase == SchwingPhase.Live; i++)
        {
            if (v.Pending == SchwingThrow.None) Kick();
            await Seconds(1.5);
        }
        for (int i = 0; i < 200 && v.Phase == SchwingPhase.Ground; i++)
        {
            Input.ActionPress(PlayerInput.FightPunch);
            await Seconds(0.06);
            Input.ActionRelease(PlayerInput.FightPunch);
            await Seconds(0.06);
        }
        Expect(await Until(() => v.Phase == SchwingPhase.Done, 10), "the ring bout ends");
        Expect(await Until(() => Booked("aelplerfest-2027").Batzen >= SchwingRules.Reward(true, false, false).Batzen, 10),
            $"the winner is paid Batzen on its own book ({Booked("aelplerfest-2027").Batzen})");
        Expect(Booked("aelplerfest-2027").Points >= SchwingRules.Reward(true, false, false).Points, "and party points");
        Expect(await Until(() => Bouts.Current == null, SchwingRules.DoneSeconds + 6), "the ring bout is over");
        Say("bout2");

        // ---- a bot fills the queue, and /schwing bot -----------------------------------------
        if (!await Heard("B", "idle", 60)) { Fail("B never went idle"); return; }
        await ToRing(me);
        Bouts.EngageRing();
        bool filled = await Until(() => Bouts.Current is { Festival: true }, SchwingManager.FillSeconds + 25);
        Expect(filled, "alone in the queue, A is given a bot after a while");
        if (filled)
        {
            v = Bouts.Current!;
            Expect(Bouts.Current.Opponent < 0, $"the opponent is a bot ({v.Opponent})");
            Expect(await Until(() => v.Phase == SchwingPhase.Live, 10), "the bot bout is live");
            // the bot plays: it leans and throws, so A's balance or the throw log moves within 40 s
            bool played = await Until(() => v.Pending != SchwingThrow.None || v.LastBy == v.Opponent || v.Phase == SchwingPhase.Ground || v.BalanceA < 100, 45);
            Expect(played, "the bot wrestles back");
            Expect(Copy(v.Opponent) is { SchwingPose: not 0 }, "the bot's body shows a Schwingen pose");
            Chat?.Send("/schwing leave");
            Expect(await Until(() => v.Phase == SchwingPhase.Done && v.Winner == v.Opponent, 10), "leaving gives the bot the bout");
            Expect(await Until(() => Bouts.Current == null, SchwingRules.DoneSeconds + 6), "the bot bout is over");
        }
        Chat?.Send("/schwing bot");
        bool bot = await Until(() => Bouts.Current is { Festival: false }, 10);
        Expect(bot, "/schwing bot asks the nearest bot, which accepts at once");
        if (bot)
        {
            await Seconds(3);
            Chat?.Send("/schwing leave");
            Expect(await Until(() => Bouts.Current == null, SchwingRules.DoneSeconds + 6), "that bout is over too");
        }
        Say("done");
    }

    // ---- B -----------------------------------------------------------------------------------

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        if (!Expect2(await Until(() => Bouts?.Current == null && InvitesPending(), 20), "B is asked")) return;
        Chat?.Send("/schwing accept");
        if (!Expect2(await Until(() => Bouts?.Current != null, 10), "B is in the bout")) return;
        var v = Bouts!.Current!;
        Expect(!v.IsA, "B wrestles the challenger");
        Expect(await Until(() => v.Phase == SchwingPhase.Live, 10), "the bout is live for B");

        // the telegraph is seen here, with A's copy in the throw pose; B braces it (Gammen: brace Left)
        bool saw = await Until(() => v.Pending == SchwingThrow.Gammen, 30);
        Expect(saw, "B sees A's Gammen winding up");
        Expect(Copy(v.A) is { } a && SchwingRules.ThrowOf(a.SchwingPose) == SchwingThrow.Gammen, "A's copy shows the throw pose");
        if (DisplayServer.GetName() != "headless") GD.Print($"{Log} shot {Shot("B_schwing")}");
        Bouts.SendAct((int)SchwingLean.Left, true, 0);
        Expect(await Until(() => v.LastAnswer == SchwingAnswer.Braced && v.LastBy == v.A, 5), "B sees its brace stop the throw");
        Expect(v.MyBalance >= 95, $"the brace cost B nothing ({v.MyBalance})");
        if (!await Heard("A", "braced", 30)) { Fail("A never saw the brace"); return; }
        Bouts.SendAct((int)SchwingLean.None, false, 0);

        // from here B stands there: A's kicks land
        Expect(await Until(() => v.MyBalance < 100, 20), "A's throws cost B balance");
        Expect(await Until(() => v.Phase == SchwingPhase.Ground, 60), "B goes down to the ground work");
        Expect(await Until(() => me.SchwingPose == (int)SchwingStance.GroundBottom || SchwingRules.Lying(me.SchwingPose), 5), "B lies in the sawdust");
        Expect(await Until(() => Copy(v.A) is { } t && t.SchwingPose == (int)SchwingStance.GroundTop, 5), "A's copy kneels over B");
        Expect(await Until(() => v.Phase == SchwingPhase.Done, 30), "the bout ends for B");
        Expect(v.Winner == v.A && v.End == SchwingEnd.Won, $"B sees A's pin ({v.End}, winner {v.Winner})");
        Expect(await Until(() => Copy(v.A) is { } w && w.SchwingPose == (int)SchwingStance.Brush, 5), "A's copy brushes the sawdust off");
        Expect(await Until(() => Bouts.Current == null, SchwingRules.DoneSeconds + 6), "the bout ends");
        if (!await Heard("A", "bout1", 60)) { Fail("A never finished bout 1"); return; }
        Expect(Booked("aelplerfest-2027").Batzen == 0, "B was paid nothing off the festival");

        // ---- the ring ------------------------------------------------------------------
        Say("ring");
        Expect(await Until(() => SchwingManager.RingNow() != null, 30), "the ring is open for B");
        await ToRing(me);
        Bouts.EngageRing();
        if (!Expect2(await Until(() => Bouts.Current is { Festival: true }, 25), "B is paired at the ring")) return;
        v = Bouts.Current!;
        Expect(await Until(() => v.Phase == SchwingPhase.Done, 150), "the ring bout ends for B");
        int loss = SchwingRules.Reward(false, false, false).Batzen;
        Expect(await Until(() => Booked("aelplerfest-2027").Batzen >= loss, 10), $"the loser is paid too, on its own book ({Booked("aelplerfest-2027").Batzen})");
        Expect(await Until(() => Bouts.Current == null, SchwingRules.DoneSeconds + 6), "the ring bout is over for B");
        Say("idle");
        if (!await Heard("A", "done", 120)) Fail("A never finished");
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>Walks the body to the ring's middle (the owner places it; the server reads the replicated position).</summary>
    private async Task ToRing(FootPlayer me)
    {
        if (SchwingManager.RingNow() is not { } ring || me.Origin is not { } origin) return;
        var at = origin.ToWorld(ring.E, ring.N, me.Global.Alt);
        me.GlobalPosition = new Vector3(at.X + (_role == "A" ? -1.2f : 1.2f), me.GlobalPosition.Y + 0.5f, at.Z);
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);
    }

    private static bool InvitesPending() => UnitSport.Net.InviteService.Client?.Incoming.Any(i => i.Kind == SchwingManager.InviteId) == true;

    private static FestivalInstance Booked(string instance) =>
        FestivalService.Instance?.Mine.Instances.GetValueOrDefault(instance) ?? new FestivalInstance();

    private static void Kick()
    {
        Input.ActionPress(PlayerInput.FightKick);
        _ = ReleaseLater();
    }

    private static async Task ReleaseLater()
    {
        await Task.Delay(120);
        Input.ActionRelease(PlayerInput.FightKick);
    }

    private static void Release()
    {
        Input.ActionRelease(PlayerInput.FightKick);
        Input.ActionRelease(PlayerInput.FightPunch);
    }

    private bool Expect2(bool ok, string what)
    {
        Expect(ok, what);
        return ok;
    }

    /// <summary>The copy of the wrestler <paramref name="peer"/> here (the other player, or a bot by its negative id).</summary>
    private FootPlayer? Copy(long peer)
    {
        foreach (var s in PlayerSnapshot.Of(GetTree()))
        {
            if (s.Player == Me || s.Player.IsMultiplayerAuthority() && !s.Player.Npc) continue;
            if (peer < 0 ? s.Player.Npc : !s.Player.Npc) return s.Player;
        }
        return null;
    }
}
