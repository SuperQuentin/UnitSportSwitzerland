using Godot;
using UnitSport.Bots;
using UnitSport.Net;

namespace UnitSport.Combat;

/// <summary>
/// Bots in aura battles (#914): <c>/aura &lt;bot&gt;</c> challenges a bot, which takes it at once. The
/// server plays its side through the same doors a player's RPCs open: a plan from
/// <see cref="AuraRules.SuggestPlan(AuraContext?, float, int)"/> (a worse bot blunders more slots), each
/// Next cut at its downbeat plus a timing error (wider the worse it is), and a straight face kept
/// against a tempt with a chance by skill. Its brain is swapped for <see cref="AuraHoldBrain"/>
/// (on its mark, facing the rival) for the battle, and its <c>AuraPose</c> set here, which every
/// peer draws.
/// </summary>
public partial class AuraManager
{
    /// <summary>A bot's side of a battle.</summary>
    public sealed class BotSide
    {
        public Bot Bot = null!;
        public IBotBrain Saved = null!;
        public AuraHoldBrain Hold = new();
        public Vector3 Mark;
        public float Skill, Composure;
        /// <summary>Its cuts' timing error, in seconds (one standard deviation).</summary>
        public double Jitter;
        public Random Rng = null!;
        public double PlanAt = double.NaN;
        public int PlannedTurn = -1, PressTurn = -1;
        public double[] PressAt = Array.Empty<double>();
        public double TemptSeen = double.NaN, ComposeAt = double.NaN;
        public double LaughedAt = double.NegativeInfinity;
    }

    /// <summary>A bot's id by its name (bots are not chat peers), or null.</summary>
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
        var (skill, jitter, composure) = bot.Profile.Difficulty switch
        {
            BotDifficulty.Easy => (0.4f, 0.11, 0.55f),
            BotDifficulty.Hard => (0.95f, 0.03, 0.95f),
            _ => (0.7f, 0.06, 0.8f),
        };
        var axis = new Basis(Vector3.Up, m.Yaw) * Vector3.Forward;
        var s = new BotSide
        {
            Bot = bot, Saved = bot.Brain, Skill = skill, Jitter = jitter, Composure = composure,
            Rng = new Random(unchecked((int)id * 7919 + m.Id)),
            Mark = m.CentreWorld + axis * (side * AuraRules.StartGap * 0.5f),
        };
        bot.Brain = s.Hold;
        s.Hold.Target = s.Mark;
        bot.Body.DanceId = 0;
        GD.Print($"[aura] #{m.Id} bot {bot.Profile.Name} ({bot.Profile.Difficulty}) battles");
        return s;
    }

    private void BotsLeave(Match m)
    {
        foreach (var s in new[] { m.BotA, m.BotB })
        {
            if (s == null || !GodotObject.IsInstanceValid(s.Bot.Body)) continue;
            if (s.Bot.Brain == s.Hold) s.Bot.Brain = s.Saved;
            s.Bot.Intent = default;
            s.Bot.Body.AuraPose = 0;
        }
    }

    private void BotLaughed(Match m, long who)
    {
        if (BotOf(m, who) is { } s) s.LaughedAt = ClockSync.ServerNow;
    }

    private static BotSide? BotOf(Match m, long id) => id == m.Rules.A ? m.BotA : id == m.Rules.B ? m.BotB : null;

    /// <summary>Server, every frame: each bot battler plans, cuts, keeps a straight face, and shows its pose.</summary>
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
        bool performing = r.Performer == id;

        // ---- plan: a few seconds' thought, then the suggestion at its skill ----
        if (r.Phase == AuraPhase.Plan && performing && s.PlannedTurn != r.Turn)
        {
            if (double.IsNaN(s.PlanAt)) s.PlanAt = now + 2.0 + 2.0 * s.Rng.NextDouble();
            if (now >= s.PlanAt)
            {
                s.PlanAt = double.NaN;
                s.PlannedTurn = r.Turn;
                var plan = AuraRules.SuggestPlan(r.ContextFor(id), s.Skill, s.Rng.Next());
                var bytes = new byte[plan.Length];
                for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)plan[i];
                SubmitPlanFor(id, m.Id, r.Turn, bytes);
            }
        }

        // ---- perform: each cut at its downbeat plus the bot's timing error ----
        if (performing && r.Phase is AuraPhase.Countdown or AuraPhase.Perform && r.Plan.Length > 0)
        {
            if (s.PressTurn != r.Turn)
            {
                s.PressTurn = r.Turn;
                s.PressAt = new double[r.Plan.Length];
                for (int i = 0; i < r.Plan.Length; i++)
                    s.PressAt[i] = AuraRules.CutAt(r.Plan, i, r.T0, r.Beat) + s.Jitter * Gauss(s.Rng);
            }
            while (r.NextIndex < s.PressAt.Length && now >= s.PressAt[r.NextIndex])
                if (NextFor(id, m.Id, r.Turn, r.NextIndex, now, 0) == null) break;
        }

        // ---- the rival performs: a tempt is met with a straight face, by chance of skill ----
        if (!performing && r.Phase == AuraPhase.Perform)
        {
            if (!double.IsNaN(r.TemptAt) && r.TemptAt != s.TemptSeen)
            {
                s.TemptSeen = r.TemptAt;
                s.ComposeAt = s.Rng.NextDouble() < s.Composure ? r.TemptAt + 0.25 + 0.45 * s.Rng.NextDouble() : double.PositiveInfinity;
            }
            if (!double.IsNaN(s.ComposeAt) && now >= s.ComposeAt && !double.IsNaN(r.TemptAt))
            {
                s.ComposeAt = double.NaN;
                ComposeFor(id, m.Id, r.Turn, now, 0);
            }
        }

        // ---- the body: its pose, its mark, facing the rival ----
        int won = r.Phase != AuraPhase.Done || r.Winner == 0 ? 0 : r.Winner == id ? 1 : -1;
        bool laughing = now - s.LaughedAt < AuraBody.LaughSeconds;
        body.AuraPose = AuraBody.PoseFor(r.Phase, performing, won, r.Plan, r.T0, r.Beat, r.Grades, now, laughing, out float stepIn);
        var rival = id == r.A ? m.BodyB : m.BodyA;
        var toRival = rival != null && GodotObject.IsInstanceValid(rival) ? rival.GlobalPosition - body.GlobalPosition : m.CentreWorld - body.GlobalPosition;
        toRival.Y = 0;
        var dir = toRival.LengthSquared() > 0.01f ? toRival.Normalized() : Vector3.Forward;
        s.Hold.Target = s.Mark + (m.CentreWorld - s.Mark).Normalized() * (stepIn * 1.2f);
        s.Hold.Yaw = Mathf.Atan2(-dir.X, -dir.Z);
        // far off the mark (the start): set down on it
        if ((s.Hold.Target - body.GlobalPosition) with { Y = 0 } is var off && off.Length() > 2.5f)
            body.GlobalPosition = new Vector3(s.Hold.Target.X, body.GlobalPosition.Y, s.Hold.Target.Z);
    }

    private static double Gauss(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

/// <summary>A bot in an aura battle (#914): walks onto its target (the mark, or the slow walk's step) and faces its yaw.</summary>
public sealed class AuraHoldBrain : IBotBrain
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

    public string Status => "aura battle";
}
