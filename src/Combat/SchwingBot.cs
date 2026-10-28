namespace UnitSport.Combat;

// Plain C#, no Godot: linked into the unit tests. A bot's decisions in Schwingen (#1269).

/// <summary>How well a bot wrestles, by its difficulty: the chance it reads a telegraph, how soon it reacts, how often it attacks, how fast it tugs.</summary>
public readonly record struct SchwingSkill(float Answer, double Reaction, float Aggression, double TugGap)
{
    public static SchwingSkill Of(int difficulty) => difficulty switch
    {
        0 => new(0.35f, 0.28, 0.25f, 0.24),
        2 => new(0.85f, 0.12, 0.6f, 0.11),
        _ => new(0.6f, 0.2, 0.4f, 0.16),
    };
}

/// <summary>What a bot decides: pure, so the tests can pin it; the server drives the bot's side through <c>ActFor</c> and <c>TugFor</c>.</summary>
public static class SchwingBot
{
    /// <summary>
    /// How a bot meets <paramref name="t"/>: with the skill's chance it reads the telegraph and counters (half the time) or braces;
    /// otherwise it stands there. <paramref name="roll"/> and <paramref name="pick"/> are uniform in 0..1.
    /// </summary>
    public static (SchwingLean Lean, bool Brace) Answer(SchwingThrow t, SchwingSkill skill, double roll, double pick)
    {
        var d = SchwingRules.Def(t);
        if (d.Throw == SchwingThrow.None || roll >= skill.Answer) return (SchwingLean.None, false);
        return pick < 0.5 ? (d.Counter, false) : (d.Brace, true);
    }

    /// <summary>The throw a bot tries: the buttons and stick a player would press, from two uniform rolls.</summary>
    public static SchwingThrow Attack(double kickRoll, double stickRoll)
    {
        var stick = stickRoll switch { < 0.3 => SchwingLean.None, < 0.5 => SchwingLean.Out, < 0.75 => SchwingLean.Left, _ => SchwingLean.Right };
        return SchwingRules.Choose(kickRoll < 0.3, stick);
    }

    /// <summary>Where it leans while nobody throws: nowhere half the time, else a random way.</summary>
    public static SchwingLean Wander(double roll) => roll switch
    {
        < 0.5 => SchwingLean.None,
        < 0.65 => SchwingLean.In,
        < 0.8 => SchwingLean.Out,
        < 0.9 => SchwingLean.Left,
        _ => SchwingLean.Right,
    };
}
