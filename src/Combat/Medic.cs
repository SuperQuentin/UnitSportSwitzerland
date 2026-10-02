using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Combat;

/// <summary>
/// The medic armband (#218): a player who opts out of PvP. The server decides (<c>/medic on|off</c>,
/// the pause menu sends the same line), the owner shows it in <see cref="FootPlayer.Medic"/>
/// (a bit of the replicated outfit), and <see cref="Items.ItemEvents"/>' hit relay drops every hit
/// to or from a medic. Note <c>docs/notes/combat/medic-armband.md</c>.
/// </summary>
public static class Medic
{
    /// <summary>Server: the cooldowns, by identity. <c>--medic-cooldown s</c> and <c>--medic-delay s</c> set its times.</summary>
    public static readonly MedicLedger Ledger = new()
    {
        Cooldown = CmdArgs.Double("--medic-cooldown") ?? 300,
        Delay = CmdArgs.Double("--medic-delay") ?? 10,
    };

    /// <summary>How far a player putting the armband on may drift and still be standing still (m).</summary>
    public const float StillRadius = 1.5f;

    /// <summary>Server: a peer's identity (its player name), set by the chat's server half.</summary>
    public static Func<long, string?>? IdentityOf { get; set; }

    /// <summary>Server: whether a peer is in a running Battle Royale match, set by <c>BrManager</c>.</summary>
    public static Func<long, bool>? InMatch { get; set; }

    public static double Now => Time.GetTicksMsec() / 1000.0;

    private static readonly HashSet<long> Pending = new();
    private static readonly HashSet<long> Suspended = new();

    /// <summary>Whether a hit between these two bodies may hurt (both may be null: unknown counts as no medic).</summary>
    public static bool Hurts(FootPlayer? attacker, FootPlayer? victim) =>
        MedicLedger.Hurts(true, attacker?.Medic == true, victim?.Medic == true);

    /// <summary>Server: <paramref name="attacker"/> hit (or aimed a relayed hit at) a player; its owner is told the new cooldown.</summary>
    public static void Attacked(long attacker, FootPlayer? body)
    {
        if (IdentityOf?.Invoke(attacker) is not { } who) return;
        Ledger.Attacked(who, Now);
        body?.SendMedicState(attacker, body.Medic, Ledger.CooldownLeft(who, Now), 0);
    }

    /// <summary>Server: <c>/medic on|off</c> from <paramref name="peer"/>. Replies through <paramref name="reply"/>.</summary>
    public static async void Request(long peer, FootPlayer body, bool on, Action<string> reply)
    {
        string who = IdentityOf?.Invoke(peer) ?? peer.ToString();
        if (!on)
        {
            Pending.Remove(peer);
            body.SendMedicState(peer, false, Ledger.CooldownLeft(who, Now), 0);
            reply(body.Medic ? "Medic armband off: PvP applies to you again." : "You are not wearing the medic armband.");
            return;
        }
        if (body.Medic) { reply("You already wear the medic armband."); return; }
        if (Ledger.Refusal(who, InMatch?.Invoke(peer) == true, Now) is { } no)
        {
            body.SendMedicState(peer, false, Ledger.CooldownLeft(who, Now), 0);
            reply(no);
            return;
        }
        if (!Pending.Add(peer)) { reply("Already putting the medic armband on."); return; }
        double start = Now;
        var at = body.Global;
        body.SendMedicState(peer, false, 0, Ledger.Delay);
        reply($"Putting the medic armband on: stand still for {Ledger.Delay:F0} s.");
        string? fail = null;
        while (fail == null && Now - start < Ledger.Delay)
        {
            await body.ToSignal(body.GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
            if (!GodotObject.IsInstanceValid(body) || !body.IsInsideTree() || !Pending.Contains(peer)) { Pending.Remove(peer); return; }
            if (!(body.Global.DistanceTo(at) <= StillRadius)) fail = "you moved";
            else if (Ledger.HurtSince(peer, start)) fail = "you were hit";
            else if (Ledger.Refusal(who, InMatch?.Invoke(peer) == true, Now) is { } r) fail = r;
        }
        Pending.Remove(peer);
        body.SendMedicState(peer, fail == null, Ledger.CooldownLeft(who, Now), 0);
        reply(fail == null ? "Medic armband on: you cannot hurt players, and they cannot hurt you." : $"Medic armband cancelled: {fail}.");
        GD.Print($"[medic] peer {peer} armband {(fail == null ? "on" : "cancelled: " + fail)}");
    }

    /// <summary>Server: a Battle Royale match starts; its entrants' armbands come off until <see cref="Restore"/>.</summary>
    public static void Suspend(IEnumerable<long> peers, Node players)
    {
        foreach (long peer in peers)
        {
            Pending.Remove(peer);
            if (players.GetNodeOrNull<FootPlayer>(peer.ToString()) is not { Medic: true } body) continue;
            Suspended.Add(peer);
            body.SendMedicState(peer, false, 0, 0);
        }
    }

    /// <summary>Server: the match is over; whoever wore the armband before it gets it back.</summary>
    public static void Restore(Node players)
    {
        foreach (long peer in Suspended)
            players.GetNodeOrNull<FootPlayer>(peer.ToString())?.SendMedicState(peer, true, 0, 0);
        Suspended.Clear();
    }

    // ------------------------------------------------------------------------------------
    // client: what the owner last heard, for the pause menu
    // ------------------------------------------------------------------------------------

    /// <summary>Client: when (<see cref="Now"/>) the cooldown ends, and when a pending armband goes on.</summary>
    public static double CooldownEnds, PendingEnds;

    /// <summary>Client: the pause menu's line for <paramref name="me"/>.</summary>
    public static string MenuText(FootPlayer me) =>
        me.Medic ? "Medic armband: ON"
        : PendingEnds > Now ? $"Putting the armband on… {Math.Ceiling(PendingEnds - Now):F0} s"
        : CooldownEnds > Now ? MedicLedger.AvailableIn(CooldownEnds - Now)
        : "Medic armband: off";
}
