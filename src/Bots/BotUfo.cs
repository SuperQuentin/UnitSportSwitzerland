using Godot;
using UnitSport.Occasions;
using UnitSport.Player;

namespace UnitSport.Bots;

/// <summary>
/// A bot meeting a UFO sighting (#863, the bots row in docs/notes/bots/bot-capability-map.md):
/// the sense is the sightings' plans every peer holds (<see cref="UfoSightings.Nearest"/>, within
/// <see cref="SeeRange"/>, as a player would see the lamps). A beam over it, or about to reach it,
/// sends it running out from under the cone; otherwise it stops, looks up at the saucer and waves for
/// a few seconds, then gets on with its day. Called by a brain before its own goal; returns whether
/// it took the bot's intent this think.
/// </summary>
public static class BotUfo
{
    /// <summary>How far a bot notices a saucer, m.</summary>
    public const float SeeRange = 300f;
    /// <summary>It flees a beam it is within this of the edge of, m.</summary>
    public const float FleeMargin = 6f;
    /// <summary>It watches a new sighting this long, s, waving for the first part.</summary>
    public const double WatchSeconds = 8, WaveSeconds = 3.5;

    private const int Wave = 0;   // HumanMeshBuilder.EmoteTable

    /// <summary>Per bot: the sighting it last reacted to, and since when.</summary>
    private static readonly Dictionary<long, (int Sighting, double Since)> Memory = new();

    public static bool React(Bot bot, double now)
    {
        var body = bot.Body;
        if (UfoSightings.Instance?.Nearest(body.EyePosition, SeeRange) is not { } craft)
        {
            StopWave(bot);
            return false;
        }

        var pos = body.GlobalPosition;
        var away = new Vector3(pos.X - craft.At.X, 0f, pos.Z - craft.At.Z);
        float apart = away.Length();
        float depth = craft.At.Y + Avatar.UfoMeshBuilder.Hatch.Y - pos.Y;
        bool under = depth > 0f && depth < TractorBeamMath.Reach && apart < TractorBeamMath.RadiusAt(depth) + FleeMargin;
        // a saucer hovering low overhead is about to beam: run before it does
        if (under && (craft.Beam || craft.Height < 40f))
        {
            StopWave(bot);
            bot.Intent = BotIntent.Stand;
            bot.Intent.Wish = apart > 0.3f ? away / apart : new Vector3(Mathf.Cos(bot.N), 0f, Mathf.Sin(bot.N));
            bot.Intent.Run = true;
            return true;
        }

        if (!Memory.TryGetValue(bot.Id, out var seen) || seen.Sighting != craft.Sighting)
        {
            Memory[bot.Id] = seen = (craft.Sighting, now);
            GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" sees a saucer {apart:F0} m off");
        }
        double watched = now - seen.Since;
        if (watched > WatchSeconds)
        {
            StopWave(bot);
            return false;
        }
        bot.Intent = BotIntent.Stand;
        var to = craft.At - body.EyePosition;
        bot.Intent.FaceYaw = Mathf.Atan2(-to.X, -to.Z);
        bot.Intent.Pitch = Mathf.Asin(Mathf.Clamp(to.Normalized().Y, -1f, 1f));
        int wave = FootPlayer.EmoteDanceBase + Wave;
        if (watched < WaveSeconds && body.CanEmote) { if (body.DanceId != wave) body.DanceId = wave; }
        else StopWave(bot);
        return true;
    }

    private static void StopWave(Bot bot)
    {
        if (bot.Body.DanceId == FootPlayer.EmoteDanceBase + Wave) bot.Body.DanceId = 0;
    }

    /// <summary>A bot left the world: its memory goes.</summary>
    public static void Forget(Bot bot) => Memory.Remove(bot.Id);
}
