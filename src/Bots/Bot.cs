using Godot;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Bots;

/// <summary>
/// What a brain asks the body for this tick (#837). The body reads it through its seams, never
/// <c>PlayerInput</c>: <see cref="FootPlayer.WalkControls"/> (wish, run), <see cref="FootPlayer.FootActions"/>
/// (jump, crouch), and the facing (<see cref="FootPlayer.LookYaw"/> with <see cref="FootPlayer.SquareToView"/>).
/// </summary>
public struct BotIntent
{
    /// <summary>World-space walk wish, length up to 1 (0 = stand).</summary>
    public Vector3 Wish;
    public bool Run;
    public bool Jump;
    public bool Crouch;
    /// <summary>Face this yaw whatever the feet do (aiming, looking round); null = face where it walks.</summary>
    public float? FaceYaw;
    /// <summary>The view's pitch when <see cref="FaceYaw"/> is set (radians, + up).</summary>
    public float Pitch;

    public static BotIntent Stand => default;
}

/// <summary>
/// One server-run bot (#837): its body, who it is, what it carries, and the brain that drives it.
/// Mode-free: the mode (<c>BrBots</c>, later others) chooses the brain and listens to its events
/// through <see cref="BotDirector"/>.
/// </summary>
public sealed class Bot
{
    public Bot(long id, int n, FootPlayer body, BotProfile profile)
    {
        Id = id;
        N = n;
        Body = body;
        Profile = profile;
    }

    /// <summary>Its entrant id, negative (<c>PlayerReplication.BotId</c>); the body is named after it.</summary>
    public long Id { get; }
    /// <summary>Its slot in the director (1..999).</summary>
    public int N { get; }
    public FootPlayer Body { get; }
    public BotProfile Profile { get; }
    /// <summary>What it carries: server-side, never saved (bots have no client), its death box when it falls.</summary>
    public Inventory Inventory { get; } = new() { Persist = false };
    public BotIntent Intent;
    public IBotBrain Brain { get; set; } = new WanderBrain();
    /// <summary>Server clock of its next think (the brain's own rate, slower far from every human).</summary>
    public double NextThink;
    /// <summary>Whether a human is near enough to see it act (the near level of detail).</summary>
    public bool Watched;
    public bool Alive => GodotObject.IsInstanceValid(Body) && !Body.Eliminated;
}

/// <summary>
/// A bot's mind for one mode (#837): reads the bot (and later its senses), writes its
/// <see cref="Bot.Intent"/>. One per mode (<see cref="WanderBrain"/> now; <c>BrBrain</c>, <c>PvpBrain</c>,
/// <c>RaceBrain</c>, <c>CoopBrain</c> later); the body, inventory and senses are shared.
/// </summary>
public interface IBotBrain
{
    /// <summary>Seconds until it wants to think again (the director slows this far from humans).</summary>
    double Think(Bot bot, double now);
}
