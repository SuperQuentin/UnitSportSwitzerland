using Godot;
using UnitSport.Occasions.Birthdays;

namespace UnitSport.Bots;

/// <summary>
/// A bot meeting a player on their birthday (#1423, the bots row in docs/notes/bots/bot-capability-map.md): the sense is the list of
/// today's celebrants every peer holds (<see cref="BirthdayService.CelebrantBodies"/>), within <see cref="NoticeRange"/>. It walks up
/// to one it has not wished yet, wishes them through the server's own path (<see cref="BirthdayService.ServeWish"/>, the one a
/// player's E asks for), waves, and gets on with its day; it gives up after <see cref="GiveUpSeconds"/>. Called by a brain before its
/// own goal; returns whether it took the bot's intent this think.
/// </summary>
public static class BotBirthday
{
    /// <summary>How far a bot notices a birthday, m: a party hat and the chat line, at talking distance.</summary>
    public const float NoticeRange = 40f;
    /// <summary>It stops trying to reach one after this long, s.</summary>
    public const double GiveUpSeconds = 40, WaveSeconds = 2.5;

    private const int Wave = 0;   // HumanMeshBuilder.EmoteTable

    /// <summary>Per bot: who it is walking to, since when, and when it wished (0 while walking).</summary>
    private static readonly Dictionary<long, (long Peer, double Since, double Wished)> Memory = new();

    public static bool React(Bot bot, double now)
    {
        if (BirthdayService.Server is not { } birthdays) return false;
        var body = bot.Body;
        if (Memory.TryGetValue(bot.Id, out var m) && m.Wished > 0)
        {
            if (now - m.Wished < WaveSeconds) return true;
            if (body.DanceId == Player.FootPlayer.EmoteDanceBase + Wave) body.DanceId = 0;
            Memory.Remove(bot.Id);
            return false;
        }
        Player.FootPlayer? target = null;
        long peer = 0;
        float best = NoticeRange;
        foreach (var (p, b) in birthdays.CelebrantBodies())
        {
            if (birthdays.HasWished(bot.Id, p) || Memory.TryGetValue(bot.Id, out var old) && old.Peer == p && now - old.Since > GiveUpSeconds) continue;
            float d = body.GlobalPosition.DistanceTo(b.GlobalPosition);
            if (d < best) { best = d; target = b; peer = p; }
        }
        if (target == null) return false;
        if (!Memory.TryGetValue(bot.Id, out m) || m.Peer != peer) Memory[bot.Id] = m = (peer, now, 0);

        var to = target.GlobalPosition - body.GlobalPosition;
        to.Y = 0;
        bot.Intent = BotIntent.Stand;
        bot.Intent.FaceYaw = Mathf.Atan2(-to.X, -to.Z);
        if (to.Length() > BirthdayRules.WishReach - 1f)
        {
            bot.Intent.Wish = to.Normalized();
            return true;
        }
        if (birthdays.ServeWish(bot.Id, peer) == null)
        {
            GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" wished peer {peer} a happy birthday");
            if (body.CanEmote) body.DanceId = Player.FootPlayer.EmoteDanceBase + Wave;
        }
        Memory[bot.Id] = (peer, m.Since, now);
        return true;
    }

    /// <summary>A bot left the world: its memory goes.</summary>
    public static void Forget(Bot bot) => Memory.Remove(bot.Id);
}
