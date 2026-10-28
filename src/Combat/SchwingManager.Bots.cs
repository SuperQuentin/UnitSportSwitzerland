using Godot;
using UnitSport.Bots;
using UnitSport.Net;

namespace UnitSport.Combat;

/// <summary>
/// Bots in Schwingen (#1269): <c>/schwing &lt;bot&gt;</c> challenges one, which takes it at once, and the ring fills an odd count with
/// one. The server plays its side through the doors a player's RPCs open (<see cref="ActFor"/>, <see cref="TugFor"/>): it leans
/// at random, tries a throw by its skill's aggression, reads a telegraph by the skill's chance after the skill's reaction time, and tugs on
/// the ground at the skill's pace. Its brain is swapped for <see cref="SchwingHoldBrain"/> (on its mark, facing the rival) for the bout,
/// and its <c>SchwingPose</c> is set where the state is sent. A bot never earns Batzen or party points.
/// </summary>
public partial class SchwingManager
{
    /// <summary>A bot's side of a bout.</summary>
    public sealed class BotSide
    {
        public Bot Bot = null!;
        public IBotBrain Saved = null!;
        public SchwingHoldBrain Hold = new();
        public Vector3 Mark;
        public SchwingSkill Skill;
        public Random Rng = null!;
        public double WanderAt, AnswerAt = double.NaN, NextTug;
        /// <summary>Set once it has answered (or chosen not to) the telegraph under way, so it is not answered twice.</summary>
        public bool Answered;
    }

    private static long? BotPeerByName(string name)
    {
        if (BotDirector.Instance is not { } bots) return null;
        foreach (var b in bots.All)
            if (string.Equals(b.Profile.Name, name, StringComparison.OrdinalIgnoreCase)) return b.Id;
        return null;
    }

    private void BotsEnter(Match m)
    {
        m.BotA = BotEnter(m, m.Rules.A, -1f);
        m.BotB = BotEnter(m, m.Rules.B, 1f);
    }

    private BotSide? BotEnter(Match m, long id, float side)
    {
        if (!PlayerReplication.IsBotId(id) || BotDirector.Instance?.Get(id) is not { } bot) return null;
        var axis = new Basis(Vector3.Up, m.Yaw) * Vector3.Forward;
        var s = new BotSide
        {
            Bot = bot, Saved = bot.Brain, Skill = SchwingSkill.Of((int)bot.Profile.Difficulty),
            Rng = new Random(unchecked((int)id * 7919 + m.Id)),
            Mark = m.CentreWorld + axis * (side * SchwingRules.GripGap * 0.5f),
        };
        bot.Brain = s.Hold;
        s.Hold.Target = s.Mark;
        bot.Body.DanceId = 0;
        // set down on the mark at once
        bot.Body.GlobalPosition = new Vector3(s.Mark.X, bot.Body.GlobalPosition.Y, s.Mark.Z);
        GD.Print($"[schwing] #{m.Id} bot {bot.Profile.Name} ({bot.Profile.Difficulty}) wrestles");
        return s;
    }

    private void BotsLeave(Match m)
    {
        foreach (var s in new[] { m.BotA, m.BotB })
        {
            if (s == null || !GodotObject.IsInstanceValid(s.Bot.Body)) continue;
            if (s.Bot.Brain == s.Hold) s.Bot.Brain = s.Saved;
            s.Bot.Intent = default;
            s.Bot.Body.SchwingPose = 0;
        }
    }

    /// <summary>Server, every frame: each bot wrestler leans, throws, answers, tugs, and faces its rival.</summary>
    private void StepBots(Match m, double now)
    {
        if (m.BotA != null) StepBot(m, m.BotA, m.Rules.A, now);
        if (m.BotB != null) StepBot(m, m.BotB, m.Rules.B, now);
    }

    private void StepBot(Match m, BotSide s, long id, double now)
    {
        var r = m.Rules;
        var body = s.Bot.Body;
        if (!GodotObject.IsInstanceValid(body)) return;
        long rival = r.Other(id);
        if (r.Phase == SchwingPhase.Live)
        {
            if (r.Pending != SchwingThrow.None && r.ThrowBy == rival)
            {
                // a telegraph under way against it: read it after the reaction time, by chance, once
                if (!s.Answered && double.IsNaN(s.AnswerAt)) s.AnswerAt = now + s.Skill.Reaction * (0.7 + 0.6 * s.Rng.NextDouble());
                if (!s.Answered && now >= s.AnswerAt)
                {
                    s.Answered = true;
                    s.AnswerAt = double.NaN;
                    var (lean, brace) = SchwingBot.Answer(r.Pending, s.Skill, s.Rng.NextDouble(), s.Rng.NextDouble());
                    if (lean != SchwingLean.None || brace) ActFor(id, m.Id, (int)lean, brace, 0);
                }
            }
            else
            {
                s.Answered = false;
                s.AnswerAt = double.NaN;
                if (now >= s.WanderAt)
                {
                    s.WanderAt = now + 1.2 + 1.6 * s.Rng.NextDouble();
                    var lean = SchwingBot.Wander(s.Rng.NextDouble());
                    int th = 0;
                    // an attack now and then, when the grip allows
                    if (r.Pending == SchwingThrow.None && r.Grip(id) >= SchwingRules.GripNeeded + 5 && s.Rng.NextDouble() < s.Skill.Aggression)
                        th = (int)SchwingBot.Attack(s.Rng.NextDouble(), s.Rng.NextDouble());
                    ActFor(id, m.Id, (int)lean, false, th);
                }
            }
        }
        else if (r.Phase == SchwingPhase.Ground && now >= s.NextTug)
        {
            s.NextTug = now + s.Skill.TugGap * (0.8 + 0.4 * s.Rng.NextDouble());
            TugFor(id, m.Id);
        }

        // ---- the body: on its mark, facing the rival ----
        var other = id == r.A ? m.BodyB : m.BodyA;
        var toRival = other != null && GodotObject.IsInstanceValid(other) ? other.GlobalPosition - body.GlobalPosition : m.CentreWorld - body.GlobalPosition;
        toRival.Y = 0;
        var dir = toRival.LengthSquared() > 0.01f ? toRival.Normalized() : Vector3.Forward;
        s.Hold.Target = s.Mark;
        s.Hold.Yaw = Mathf.Atan2(-dir.X, -dir.Z);
        if ((s.Mark - body.GlobalPosition) with { Y = 0 } is var off && off.Length() > 2.5f)
            body.GlobalPosition = new Vector3(s.Mark.X, body.GlobalPosition.Y, s.Mark.Z);
    }
}

/// <summary>A bot in a bout: walks onto its mark and faces its yaw.</summary>
public sealed class SchwingHoldBrain : IBotBrain
{
    public Vector3 Target;
    public float Yaw;

    public double Think(Bot bot, double now)
    {
        var to = Target - bot.Body.GlobalPosition;
        to.Y = 0;
        float d = to.Length();
        bot.Intent.Wish = d > 0.15f ? to / d * Mathf.Min(1f, d * 1.5f) : Vector3.Zero;
        bot.Intent.Run = false;
        bot.Intent.FaceYaw = Yaw;
        bot.Intent.Pitch = 0f;
        bot.Intent.FireAt = null;
        return 0.05;
    }

    public string Status => "schwingen";
}
