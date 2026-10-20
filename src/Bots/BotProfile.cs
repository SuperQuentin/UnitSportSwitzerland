using System;
using System.Collections.Generic;

namespace UnitSport.Bots;

/// <summary>How hard a bot is (#837): one knob per match or per bot, read by its brain.</summary>
public enum BotDifficulty { Easy, Normal, Hard }

/// <summary>
/// Who a bot is and how it plays (#837): its name and the knobs its brain reads. Drawn from a seed,
/// as <c>AutoPilot.Temperament</c> draws a race NPC's skill, so a bot is the same bot on every run of
/// a match seed. Pure, unit-tested (<c>BotProfileTests</c>); nothing here knows a mode.
/// </summary>
/// <param name="ReactionSeconds">Seconds from first seeing a threat to acting on it.</param>
/// <param name="AimErrorDeg">Aim error at 50 m, degrees (a cone; grows with distance and movement in the brain).</param>
/// <param name="Boldness">0 cautious .. 1 bold: how readily it pushes a fight instead of taking cover.</param>
/// <param name="LootGreed">0 .. 1: how long it stays looting before moving on.</param>
public sealed record BotProfile(string Name, BotDifficulty Difficulty, float ReactionSeconds, float AimErrorDeg, float Boldness, float LootGreed)
{
    /// <summary>Swiss first names, the four languages mixed. A bot's tag also says BOT, so a name never passes for a player's.</summary>
    public static readonly string[] Names =
    [
        "Urs", "Reto", "Beat", "Heidi", "Vreni", "Ueli", "Res", "Sepp", "Trudi", "Fritz",
        "Nicolas", "Aurélie", "Joël", "Mathilde", "Loïc", "Chantal", "Yves", "Séverine", "Gaëlle", "Cédric",
        "Gian", "Ladina", "Curdin", "Mengia", "Flurin", "Seraina", "Duri", "Annina",
        "Luca", "Chiara", "Matteo", "Giulia", "Sandro", "Noemi", "Fabio", "Elena",
    ];

    /// <summary>
    /// The profile of bot <paramref name="n"/> in a match seeded <paramref name="seed"/>. Names are
    /// not repeated inside one match while the list lasts (<paramref name="taken"/>, filled here).
    /// </summary>
    public static BotProfile For(int seed, int n, BotDifficulty difficulty, ISet<string>? taken = null)
    {
        var rng = new Random(unchecked(seed * 7919 + n * 104729));
        string name = Names[rng.Next(Names.Length)];
        if (taken != null)
        {
            for (int i = 0; i < Names.Length && taken.Contains(name); i++)
                name = Names[(Array.IndexOf(Names, name) + 1) % Names.Length];
            if (taken.Contains(name)) name = $"{name} {n}";
            taken.Add(name);
        }
        float spread = (float)rng.NextDouble();   // 0..1: this bot's place inside its difficulty band
        return difficulty switch
        {
            BotDifficulty.Easy => new(name, difficulty, Lerp(0.9f, 1.4f, spread), Lerp(4f, 6f, spread), Lerp(0.1f, 0.4f, spread), Lerp(0.4f, 0.9f, spread)),
            BotDifficulty.Hard => new(name, difficulty, Lerp(0.2f, 0.4f, spread), Lerp(0.8f, 1.6f, spread), Lerp(0.5f, 0.9f, spread), Lerp(0.2f, 0.5f, spread)),
            _ => new(name, difficulty, Lerp(0.45f, 0.8f, spread), Lerp(2f, 3.5f, spread), Lerp(0.3f, 0.7f, spread), Lerp(0.3f, 0.7f, spread)),
        };
    }

    /// <summary>Parses "easy", "normal" or "hard" (any case); null when it is none of them.</summary>
    public static BotDifficulty? ParseDifficulty(string s) => s.ToLowerInvariant() switch
    {
        "easy" => BotDifficulty.Easy,
        "normal" => BotDifficulty.Normal,
        "hard" => BotDifficulty.Hard,
        _ => null,
    };

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
