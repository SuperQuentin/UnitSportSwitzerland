using Godot;
using UnitSport.BattleRoyale;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Items;

/// <summary>
/// <c>--pvpcheck A|B --pvpexpect medic</c> (<c>tools/mediccheck.sh</c>, #218): the medic armband over loopback, PvP on.
/// A rifle round from A hurts B (two non-medics); A's <c>/medic on</c> is refused for the cooldown; B's switch delay
/// is cancelled by moving and by A's round, then B stands still and wears it; A sees B's armband (replicated); A's
/// rounds and a forged Hit on B, and B's rounds and a forged Hit on A, hurt nobody; a Battle Royale takes B's
/// armband off at GO and refuses it, cancelling the match gives it back; <c>/medic off</c> is immediate and A hurts B again.
/// <c>--pvpexpect rejoin</c>: A again, same name: <c>/medic on</c> is still refused.
/// </summary>
public partial class PvpProbe
{
    private static string Expectation => RoleArg("--pvpexpect") ?? "";

    private bool Replied(string what) => _heard.Any(l => l.Contains(what));

    private async Task<bool> Reply(string what, double seconds) => await Until(() => Replied(what), seconds);

    private async Task RunMedicA(FootPlayer me)
    {
        me.LookYaw = 0f;
        var at = me.Global;
        string pos = Fmt($"posA {at.E:F2} {at.N:F2} {at.Alt:F2}");
        for (int tries = 0; tries < 60 && !Said("B", "ready"); tries++)
        {
            Say(pos);
            await Seconds(2.5);
        }
        var b = Body(me);
        if (!Said("B", "ready") || b == null) { Fail("B never joined"); return; }
        long peerB = FootPlayer.NetId(b.Name) ?? 0;
        await Near(me, b, 10f);

        await Fire(me, b, ItemId.Rifle, 1, "r1");                       // two non-medics
        Chat!.Send("/medic on");
        Expect(await Reply("Medic available in", 10), "A hit B, so A's /medic on is refused with the cooldown");

        await Step("B", "pending");
        await Fire(me, b, ItemId.Rifle, 1, "r2");                       // cancels B's pending armband
        await Step("B", "medic");
        Expect(await Until(() => b.Medic, 5), "B's armband is seen here (replicated)");
        await Seconds(1.0);
        Shot("medic_a_sees_b");
        int hits = Hits;
        await Fire(me, b, ItemId.Rifle, 2, "r3");
        Expect(Hits == hits, "no hit marker on a medic");
        Forge(me, b, peerB);
        Say("shot forged");

        float h = me.Health;
        await Step("B", "shot a");
        await Seconds(2.0);
        Expect(me.Health >= h - 0.6f, $"B, a medic, did not hurt A ({h:F1} -> {me.Health:F1})");
        Say("checked");

        // a Battle Royale takes the armband off, and cancelling it gives it back
        Chat.Send("/login medic");
        Expect(await Until(() => Permissions.IsAdmin, 10), "A is admin");
        Chat.Send("/br open 5 short");
        Expect(await Until(() => BrManager.Instance?.State.Phase == BrPhase.Lobby, 20), "a lobby");
        Chat.Send("/br join");
        Say("join");
        Expect(await Until(() => BrManager.Instance!.State.Entrants.Count >= 2, 30), "both in the lobby");
        Chat.Send("/br start");
        Expect(await Until(() => BrManager.Instance!.State.Phase == BrPhase.Playing, 90), "the match started");
        b = Body(me) ?? b;
        Expect(await Until(() => !b.Medic, 10), "GO took B's armband off (seen here)");
        await Step("B", "br refused");
        Chat.Send("/br cancel");
        Expect(await Until(() => BrManager.Instance!.State.Phase == BrPhase.Idle, 20), "the match is cancelled");
        Expect(await Until(() => (Body(me) ?? b).Medic, 15), "the armband is back after the match (seen here)");
        Say("br over");

        await Step("B", "off");
        b = Body(me) ?? b;
        await Near(me, b, 10f);
        await Fire(me, b, ItemId.Rifle, 1, "r4");                       // B took it off: hurt again
        await Step("B", "done");
    }

    private async Task RunMedicB(FootPlayer me)
    {
        if (!await Until(() => _heard.Any(l => l.Contains("PV A posA")), 150)) { Fail("A never reported"); return; }
        var parts = _heard.First(l => l.Contains("PV A posA")).Split("posA ")[1].Split(' ');
        _a = new GlobalPos(Lv95(parts[0]), Lv95(parts[1]), Lv95(parts[2]));
        _items.Inventory.Select(0);
        await StandAt(me, 10f);
        Say("ready");

        float h = me.Health;
        Expect(await HealthAfter(me, "r1", h - 26f), $"two non-medics: A's round hurt B ({h:F1} -> {me.Health:F1})");

        // the delay: moving cancels it
        Chat!.Send("/medic on");
        Expect(await Reply("stand still", 10), "the armband takes a delay");
        me.GlobalPosition += new Vector3(3f, 0f, 0f);
        Expect(await Reply("cancelled: you moved", 10), "moving cancels it");
        await StandAt(me, 10f);
        // and a player's hit cancels it
        Chat.Send("/medic on");
        await Until(() => _heard.Count(l => l.Contains("stand still")) >= 2, 10);
        Say("pending");
        h = me.Health;
        Expect(await HealthAfter(me, "r2", h - 26f), $"A's round during the delay hurt B ({h:F1} -> {me.Health:F1})");
        Expect(await Reply("cancelled: you were hit", 10), "a hit cancels it");
        // standing still, unhurt: on
        Chat.Send("/medic on");
        Expect(await Reply("Medic armband on", 30), "B wears the armband after the delay");
        Expect(await Until(() => me.Medic, 5), "B's own Medic flag");
        Say("medic");
        h = me.Health;
        await Until(() => Said("A", "shot forged"), 60);
        await Seconds(2.0);
        Expect(me.Health >= h - 0.6f, $"neither A's rounds nor a forged Hit hurt B, a medic ({h:F1} -> {me.Health:F1})");

        // B shoots A: nothing
        var a = Body(me);
        if (a == null) { Fail("A's body is not here"); return; }
        _items.Inventory.Select(SlotOf(ItemId.Rifle));
        await Fire(me, a, ItemId.Rifle, 1, null);
        Forge(me, a, FootPlayer.NetId(a.Name) ?? 0);
        Say("shot a");
        await Step("A", "checked");

        // the Battle Royale
        await Step("A", "join");
        Chat.Send("/br join");
        Expect(await Until(() => !me.Medic, 120), "GO took the armband off");
        Chat.Send("/medic on");
        Expect(await Reply("No medic armband in a Battle Royale match", 10), "refused in a match");
        Say("br refused");
        Expect(await Until(() => me.Medic, 60), "the armband is back after the match");
        await Step("A", "br over");

        Chat.Send("/medic off");
        Expect(await Until(() => !me.Medic, 5), "taking it off is immediate");
        await StandAt(me, 10f);
        Say("off");
        h = me.Health;
        Expect(await HealthAfter(me, "r4", h - 26f), $"after /medic off A's round hurts B again ({h:F1} -> {me.Health:F1})");
        Say("done");
    }

    /// <summary>A hand-made Hit event, as a modified client could send it: the server's relay must drop it.</summary>
    private static void Forge(FootPlayer me, FootPlayer victim, long peer)
    {
        var dir = (victim.GlobalPosition - me.GlobalPosition).Normalized();
        ItemEvents.Instance!.Send(ItemEventKind.Hit, victim.GlobalPosition + Vector3.Up * 1.2f, dir,
            new PlayerHits.Hit(peer, 26f, ItemId.Rifle, false).Pack());
    }

    /// <summary>A back under the same name: the cooldown is kept by identity, so <c>/medic on</c> is still refused.</summary>
    private async Task RunRejoin()
    {
        Chat!.Send("/medic on");
        Expect(await Reply("Medic available in", 10), "the cooldown survived the rejoin");
    }
}
