using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>/movespeed</c> (#1485): an admin's movement speed factor, no upper cap. It scales the body's own
/// paths: walking, running, crawling, swimming, free climbing and creative flight (not vehicles, the
/// equipment rides or the spectator camera, which has its own wheel speed). Foot movement is the
/// owner's, so online the server decides and tells the owner (<c>ChatManager.SpeedFactorSet</c>); the
/// other peers see the body move through the usual snapshots.
/// </summary>
public partial class FootPlayer
{
    /// <summary>
    /// This screen's player's factor: offline from <c>/movespeed</c>, online as the server sent it. Kept
    /// off the body so a new body (a respawn) keeps it; back to 1 on leaving a server or losing admin rights.
    /// </summary>
    public static float OwnSpeedFactor { get; private set; } = 1f;

    public static void SetOwnSpeedFactor(float factor) => OwnSpeedFactor = Valid(factor);

    private float _speedFactor = 1f;

    /// <summary>
    /// The factor this body moves at: the local player's <see cref="OwnSpeedFactor"/> (none in a Battle
    /// Royale match, where everyone plays on equal terms), else the one the server set on it (a
    /// server-run bot's, or the server's record of a peer's, which <c>StaminaWatch</c> reads). 1 = normal.
    /// </summary>
    public float SpeedFactor
    {
        get => !Bot && !Npc && LocalOwner == this ? (Permissions.InMatch ? 1f : OwnSpeedFactor) : _speedFactor;
        set => _speedFactor = Valid(value);
    }

    private static float Valid(float factor) => factor > 0f && float.IsFinite(factor) ? factor : 1f;
}
