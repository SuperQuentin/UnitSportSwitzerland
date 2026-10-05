using Godot;

namespace UnitSport.Player;

/// <summary>
/// Downed but not out (#475): in a squad match, a player whose health runs out while a team-mate
/// still stands goes DOWN instead of out. Flat on the ground, crawling, no items, rides or jumps,
/// bleeding out over <see cref="BleedSeconds"/>; any damage (a finishing shot, the zone) drains what
/// is left. A team-mate's revive (<see cref="ReviveInPlace"/>) stands them up where they lie;
/// bleeding out, being finished, or the server's word (<see cref="FinishDowned"/>: the whole team is
/// down) is the ordinary elimination, <see cref="Died"/> and all.
/// Replicated as <see cref="Down"/> = 2: hit tests skip only <see cref="Down"/> = 1, so a downed
/// body can still be shot.
/// </summary>
public partial class FootPlayer
{
    /// <summary>
    /// Asked when health runs out and <see cref="StayDown"/> holds: true makes the player
    /// <see cref="Downed"/> instead of out. A squad match sets it (a team-mate still standing).
    /// </summary>
    public static Func<FootPlayer, bool>? CanBeDowned;

    /// <summary>Raised on the owner when it goes down, with whoever did it (a peer id, 0 for none).</summary>
    public event Action<long>? WentDown;

    /// <summary>Down and crawling, waiting for a team-mate (<see cref="Down"/> = 2, replicated).</summary>
    public bool Downed => Down == 2;

    /// <summary>Seconds a downed player lasts untouched.</summary>
    public const float BleedSeconds = 30f;

    /// <summary>What is left of a downed player, 0..<see cref="MaxHealth"/>: it drains over <see cref="BleedSeconds"/> and with every hit.</summary>
    public float BleedLeft { get; private set; }

    /// <summary>A downed player's pace, m/s.</summary>
    public const float CrawlSpeed = 1.1f;

    /// <summary>Health on getting up.</summary>
    public const float RevivedHealth = 30f;

    private long _downedBy;
    private DamageCause _downedCause;

    /// <summary>Health ran out: down instead of out, when the game mode allows it. True when it did.</summary>
    private bool TryGoDown(long attacker, DamageCause cause)
    {
        if (Downed || Eliminated || StayDown?.Invoke(this) != true || CanBeDowned?.Invoke(this) != true) return false;
        if (_ride is { IsVehicle: true }) WreckVehicle();
        else if (_ride != null) ApplyRide(RideKind.OnFoot, Velocity);
        Down = 2;
        Health = 0f;
        BleedLeft = MaxHealth;
        _downedBy = attacker;
        _downedCause = cause;
        Announced?.Invoke("DOWN — A TEAM-MATE CAN REVIVE YOU", false);
        WentDown?.Invoke(attacker);
        return true;
    }

    /// <summary>Damage to a downed player: what is left drains; at nothing, out. The finisher takes the kill.</summary>
    private void HurtDowned(float amount, long attacker, DamageCause cause)
    {
        BleedLeft = Mathf.Max(0f, BleedLeft - amount);
        _sinceHurt = 0;
        Hurt?.Invoke(amount);
        if (attacker != 0) _downedBy = attacker;
        if (cause != DamageCause.Other) _downedCause = cause;
        if (BleedLeft <= 0f) BleedOut();
    }

    /// <summary>The bleeding, every frame on the owner.</summary>
    private void TickDowned(float dt)
    {
        if (!Downed) return;
        BleedLeft = Mathf.Max(0f, BleedLeft - MaxHealth / BleedSeconds * dt);
        if (BleedLeft <= 0f) BleedOut();
    }

    private void BleedOut()
    {
        if (!Downed) return;
        long killer = _downedBy;
        Die();   // StayDown holds: eliminated
        Died?.Invoke(killer, _downedCause);
    }

    /// <summary>A team-mate picked this player up (the server said so): on their feet where they lie.</summary>
    public void ReviveInPlace()
    {
        if (!Downed) return;
        Down = 0;
        BleedLeft = 0f;
        Health = RevivedHealth;
        _sinceHurt = 0;
        _stunTimer = 0.6f;   // a moment to get up
        Announced?.Invoke("REVIVED", false);
    }

    /// <summary>The server's word: the whole team is down, so this player is out now.</summary>
    public void FinishDowned() => BleedOut();
}
