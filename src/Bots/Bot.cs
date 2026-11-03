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
    /// <summary>Grab the face it walks into and climb it (#1225): a body never climbs by pushing alone.</summary>
    public bool Climb;
    /// <summary>Face this yaw whatever the feet do (aiming, looking round); null = face where it walks.</summary>
    public float? FaceYaw;
    /// <summary>The view's pitch when <see cref="FaceYaw"/> is set (radians, + up).</summary>
    public float Pitch;
    /// <summary>Fire (or stab) at this world point this tick, if the weapon is ready (<see cref="BotHands.Fire"/>).</summary>
    public Vector3? FireAt;
    /// <summary>Swing the sword at this world point (<see cref="BotHands.Swing"/>); held, it chains the combo.</summary>
    public Vector3? SwordAt;
    /// <summary>Dodge away from this world point this tick (<see cref="BotHands.Dodge"/>).</summary>
    public Vector3? DodgeFrom;
    /// <summary>
    /// In the air: whether it wants the parachute open (else the wingsuit). The body's flight controls
    /// turn the wish into the deploy or cut-away press only when the ride differs, never a held key.
    /// </summary>
    public bool Canopy;
    /// <summary>The flight stick in the air: a canopy's turn (x, + right) and brakes (y, + brake); a wingsuit's override.</summary>
    public Vector2 Steer;

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
    /// <summary>The hands' state (<see cref="BotHands"/>): when the weapon is ready again, the barrel's heat and since when.</summary>
    public double NextShotAt;
    /// <summary>The sword's machine (#1371): the combo, the dodge and their stamina, stepped by <see cref="BotHands.Tick"/>; the same rules as a player's.</summary>
    public Combat.SwordMachine Sword { get; } = new();
    public double SwordWantUntil, DodgeWantUntil;
    public Vector3 SwordTarget, DodgeAway;
    public bool SwordPulse, SwordTraced;
    public int SwordSwings;
    public float Heat;
    public double HeatAt;
    /// <summary>What it perceives (<see cref="BotSenses"/>): the only way its brain learns about the world.</summary>
    public BotSenses Senses { get; } = new();
    /// <summary>Its walk (<see cref="BotSkills.MoveTo"/>).</summary>
    public BotNav Nav { get; } = new();
    /// <summary>Its drive (<see cref="BotDriving"/>): the route, the pilot.</summary>
    public BotDrive Drive { get; } = new();
    /// <summary>The world it acts in: the physics space and the bodies there (set by the director).</summary>
    public BotWorld World { get; set; } = null!;
    /// <summary>A timed use under way (<see cref="BotHands.Use"/>): what, and the clock it applies at (0 = none).</summary>
    public ItemId UseItem;
    public double UseUntil;
    public bool Alive => GodotObject.IsInstanceValid(Body) && !Body.Eliminated;
}

/// <summary>
/// What a brain may reach of the world (#837): the physics space for its senses and its walk, and
/// the bodies there for its senses to look at. Nothing else: a brain never reads the world directly.
/// </summary>
public sealed class BotWorld
{
    public BotWorld(Node3D players) => Players = players;

    public Node3D Players { get; }
    public PhysicsDirectSpaceState3D Space => Players.GetWorld3D().DirectSpaceState;

    public System.Collections.Generic.IEnumerable<FootPlayer> Bodies()
    {
        foreach (var n in Players.GetChildren()) if (n is FootPlayer p) yield return p;
    }
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

    /// <summary>What it is up to, for <c>--botlog</c>'s status lines.</summary>
    string Status => "";
}
