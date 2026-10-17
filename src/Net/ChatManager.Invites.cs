using Godot;

namespace UnitSport.Net;

/// <summary>
/// The chat's side of invites (#840): <c>/invites /accept /decline</c>, the party commands, and the
/// teleport requests anyone may send (<c>/tpa</c> to go to a player, <c>/tphere</c> to ask one over),
/// which the asked player accepts. Admin <c>/tp</c> and <c>/bring</c> need no asking.
/// </summary>
public partial class ChatManager
{
    public const string TpKind = "tp", TpHereKind = "tphere";
    /// <summary>A player waits this long between two teleport requests, s.</summary>
    public const double TpCooldown = 20;

    /// <summary>Server: invites (#840), wired by ServerWorld; the chat registers the teleport kinds on it.</summary>
    public InviteService? Invites
    {
        get => _invites;
        set
        {
            _invites = value;
            if (value == null) return;
            value.Register(new InviteKind
            {
                Id = TpKind, Label = "teleport", Seconds = 45, TellAccepted = false,
                // the sender goes to the one who accepted
                Accept = (peer, invite) => AnswerTeleport(invite.From, peer, invite.From),
            });
            value.Register(new InviteKind
            {
                Id = TpHereKind, Label = "teleport", Seconds = 45, TellAccepted = false,
                // the one who accepted goes to the sender
                Accept = (peer, invite) => AnswerTeleport(peer, invite.From, peer),
            });
        }
    }

    private InviteService? _invites;

    /// <summary>Server: the players online, past the handshake (invites go only to them).</summary>
    public long[] OnlinePeers => _registry?.Players.Select(p => p.PeerId).Where(id => id > 0).ToArray() ?? Array.Empty<long>();

    /// <summary>Server: parties (#840), wired by ServerWorld.</summary>
    public Parties? Parties { get; set; }

    private readonly Dictionary<long, ulong> _tpAskedAt = new();

    private void InviteCommand(long sender, string verb, string rest)
    {
        if (_invites == null)
        {
            ReplyTo(sender, "Invites are not running on this server.", ChatKind.Error);
            return;
        }
        if (sender == ConsolePeerId)
        {
            ReplyTo(sender, $"'/{verb}' needs a player.", ChatKind.Error);
            return;
        }
        switch (verb)
        {
            case "invites" or "accept" or "decline":
                ReplyTo(sender, _invites.Command(sender, verb, rest), ChatKind.Private);
                return;
            case "party":
                ReplyTo(sender, Parties?.Command(sender, rest) ?? "Parties are not running on this server.", ChatKind.Private);
                return;
            case "tpa" or "tphere":
                ReplyTo(sender, AskTeleport(sender, rest, here: verb == "tphere"), ChatKind.Private);
                return;
        }
    }

    /// <summary><c>/tpa</c> and <c>/tphere</c>: the request, as an invite the other player accepts.</summary>
    private string AskTeleport(long sender, string name, bool here)
    {
        if (name.Length == 0) return here ? "Usage: /tphere <player>" : "Usage: /tpa <player>";
        if (_registry?.FindByName(name) is not { } target) return $"No player matching '{name}'.";
        if (target.PeerId == sender) return "You are already there.";
        if (TeleportRefusal(sender, sender, target.PeerId) is { } why) return why;
        ulong now = Time.GetTicksMsec();
        if (_tpAskedAt.TryGetValue(sender, out ulong at) && now - at < TpCooldown * 1000)
            return $"Wait {TpCooldown - (now - at) / 1000.0:0} s before asking again.";
        string title = here ? $"{NameOf(sender)} asks you to teleport to them" : $"{NameOf(sender)} asks to teleport to you";
        string reply = _invites!.Send(here ? TpHereKind : TpKind, sender, target.PeerId, title, "", out var sent);
        if (sent != null) _tpAskedAt[sender] = now;
        return reply;
    }

    /// <summary>
    /// Why a teleport request between <paramref name="a"/> and <paramref name="b"/> is refused now, or null:
    /// neither may be in a Battle Royale match or a race (equal terms; a race has its own course).
    /// </summary>
    private string? TeleportRefusal(long asking, long a, long b)
    {
        foreach (long peer in new[] { a, b })
        {
            string who = peer == asking ? "You are" : $"{NameOf(peer)} is";
            if (BattleRoyale?.Playing(peer) == true) return $"{who} in a Battle Royale match.";
            if (Race?.RaceOf(peer) is > 0) return $"{who} in a race.";
        }
        return null;
    }

    /// <summary>An accepted request: <paramref name="mover"/> goes to <paramref name="to"/>. The reply is the accepter's.</summary>
    private string AnswerTeleport(long mover, long to, long accepter)
    {
        if (TeleportRefusal(accepter, mover, to) is { } why) return why;
        if (_players?.GetNodeOrNull<Player.FootPlayer>(to.ToString()) is not { } target) return $"{NameOf(to)} has no position yet.";
        if (_players.GetNodeOrNull<Player.FootPlayer>(mover.ToString()) == null) return $"{NameOf(mover)} is no longer here.";
        RpcId(mover, MethodName.ForceTeleport, target.Global.E, target.Global.N, NameOf(to));
        GD.Print($"[chat] teleport request: {mover} to {to}");
        if (mover == accepter)
        {
            ReplyTo(to, $"{NameOf(mover)} accepts and comes to you.", ChatKind.System);
            return $"Going to {NameOf(to)}.";
        }
        ReplyTo(mover, $"{NameOf(accepter)} accepts.", ChatKind.System);
        return $"{NameOf(mover)} is coming to you.";
    }
}
