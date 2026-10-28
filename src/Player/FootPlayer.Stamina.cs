using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// The stamina meter on the body (#1225, <c>docs/notes/player/stamina.md</c>): the rules
/// (<see cref="Stamina"/>) ticked once a physics step, spent by climbing, the sprint stroke, a
/// pigeon's flaps and sprinting in a fight, refilled standing. Owner only, never replicated: the
/// owner decides, as it does for the walk, the swim and the air reserve.
/// </summary>
public partial class FootPlayer
{
    /// <summary>Hurt by a player this recently (s): in a fight, sprinting costs.</summary>
    public const float CombatSeconds = 6f;

    private Stamina _stamina = Stamina.Full();
    private bool _wasWinded;

    /// <summary>The meter as it is now (owner only; a copy: spend through the body's own paths).</summary>
    public Stamina StaminaMeter => _stamina;

    /// <summary>The air's velocity the own glider flew in this step (#1225, <c>AirLiftField</c>): zero on foot. Probes and the variometer.</summary>
    public Vector3 AirNow { get; private set; }

    /// <summary><c>/stamina infinite</c> (admin online, free offline; a test hook): the meter never drains this session.</summary>
    public bool StaminaInfiniteCommand { get; set; }

    /// <summary>The player this peer plays (the last owner body to tick), for <see cref="StaminaCommand"/>.</summary>
    private static FootPlayer? LocalOwner;

    /// <summary>
    /// <c>/stamina [infinite | on | full]</c> on this screen (#1225): free offline, an admin's online (an
    /// honest client's rule; the server's <c>StaminaWatch</c> logs what it could not have climbed). The
    /// line to show, or null when the text is not this command.
    /// </summary>
    public static string? StaminaCommand(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("/stamina", StringComparison.OrdinalIgnoreCase)) return null;
        if (LocalOwner is not { } me || !IsInstanceValid(me)) return "No body to tire.";
        string arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        var m = me._stamina;
        string state = $"Stamina {m.Points:F0}/{m.Max:F0} ({m.Steps} steps){(m.Infinite ? ", not counted here" : "")}.";
        if (arg == "") return state;
        if (Permissions.Online && !Permissions.IsAdmin) return "'/stamina " + arg + "' is an admin's online.";
        switch (arg)
        {
            case "infinite": me.StaminaInfiniteCommand = true; return "Stamina is infinite this session.";
            case "on": me.StaminaInfiniteCommand = false; return "Stamina counts again.";
            case "full": me._stamina.Refill(); return "Stamina full.";
            default: return "Usage: /stamina [infinite | on | full]";
        }
    }

    /// <summary>Hit empty (owner only): the ring blinks orange, a panting breath plays.</summary>
    public event Action? WindedStarted;

    /// <summary>The max went up (an upgrade step arrived): the ring refills as the feedback.</summary>
    public event Action? StaminaRaised;

    /// <summary>
    /// In a fight: an aura battle or a fist fight, or hurt by a player in the last
    /// <see cref="CombatSeconds"/>. Sprinting costs then.
    /// </summary>
    public bool InCombat => InDuel || (LastAttacker != 0 && GameClock.Now - LastAttackedAt < CombatSeconds);

    /// <summary>
    /// The Adventure layer is on for this body (the shared gate, <c>AdventureService.Active</c>): the
    /// system runs and the player is not in a Battle Royale round. A client knows the match from
    /// <see cref="Permissions.InMatch"/>; the server asks the service about its bots.
    /// </summary>
    public bool AdventureOn
    {
        get
        {
            if (Adventure.AdventureService.Instance is not { } a) return false;
            if (Bot || Npc) return !(a.InMatch?.Invoke(PeerOf) ?? false);
            return !Permissions.InMatch;
        }
    }

    /// <summary>The peer id this body is named after (players are named by it), 0 for others.</summary>
    private long PeerOf => long.TryParse(Name, out long id) ? id : 0;

    /// <summary>Free climbing is on here (<see cref="Stamina.ClimbingAllowed"/>): Adventure or creative, never in a match.</summary>
    public bool ClimbingAllowed => Stamina.ClimbingAllowed(AdventureOn, _creative, Permissions.InMatch);

    /// <summary>The meter counts here (<see cref="Stamina.Applies"/>); false = infinite.</summary>
    public bool StaminaApplies => Stamina.Applies(AdventureOn, _creative, Permissions.ServerStamina, StaminaInfiniteCommand);

    /// <summary>Food (<c>ItemController</c>): points back. The points actually added.</summary>
    public float RestoreStamina(float points) => _stamina.Restore(points);

    /// <summary>Food: a lasting effect (hot chocolate, fondue).</summary>
    public void BuffStamina(StaminaBuff kind, float seconds) => _stamina.Buff(kind, seconds);

    /// <summary>Whether food with <paramref name="points"/> or a buff would change the meter now (food is refused at full health only when it would not).</summary>
    public bool StaminaWouldTake(float points, StaminaBuff buff) =>
        !_stamina.Infinite && ((points > 0f && !_stamina.IsFull) || buff != StaminaBuff.None);

    /// <summary>The upgrade steps (the <c>"stamina"</c> section); a raise refills the ring.</summary>
    public void SetStaminaSteps(int steps)
    {
        if (steps == _stamina.Steps) return;
        bool raised = steps > _stamina.Steps;
        _stamina.SetSteps(steps);
        if (raised) StaminaRaised?.Invoke();
    }

    /// <summary>Checks: the meter set to this many points (clamped), as if spent.</summary>
    internal void DebugSetStamina(float points)
    {
        _stamina.Points = Mathf.Clamp(points, 0f, _stamina.Max);
        _stamina.Winded = points <= 0f;
    }

    /// <summary>XR hand climbing (<c>XrClimb</c>): points per metre gained, the arms working harder than on the stick.</summary>
    public const float XrClimbPerMetre = 9f;

    /// <summary>
    /// XR hand climbing (#1225): holding on costs 1 /s and each metre gained <see cref="XrClimbPerMetre"/>.
    /// False when empty: the hands let go. True always where the meter does not count.
    /// </summary>
    public bool SpendXrClimb(float dt, float metresUp)
    {
        if (_stamina.Infinite) return true;
        bool held = _stamina.Spend(StaminaKind.ClimbHold, dt);
        if (metresUp > 0f) held &= _stamina.Spend(StaminaKind.ClimbHold, 1f, metresUp * XrClimbPerMetre);
        return held && !_stamina.Winded;
    }

    /// <summary>Spends a rate (the body's own paths call it). False: not enough, the act does not happen.</summary>
    private bool SpendStamina(StaminaKind kind, float dt, float scale = 1f, bool loose = false) =>
        _stamina.Spend(kind, dt, scale, loose);

    /// <summary>
    /// Once a physics step (owner and server-run bodies): the rules decide whether the meter counts,
    /// the upgrade steps follow the progress section, and it refills where the body rests.
    /// </summary>
    private void TickStamina(float dt, bool onFloor)
    {
        if (_ride is not Flyer) AirNow = Vector3.Zero;
        if (!Bot && !Npc) LocalOwner = this;
        bool infinite = !StaminaApplies;
        if (infinite != _stamina.Infinite)
        {
            _stamina.Infinite = infinite;
            if (infinite) _stamina.Refill();
        }
        if (!Bot && !Npc && Adventure.StaminaUpgrades.MySteps is int steps) SetStaminaSteps(steps);
        // linked or belayed (#1266): set by the owner from its link and the partner's pose
        _stamina.Step(dt, StaminaRestNow(onFloor), CompanyNow?.Invoke() ?? StaminaCompany.None);
        bool winded = _stamina.Winded;
        if (winded && !_wasWinded) WindedStarted?.Invoke();
        _wasWinded = winded;
    }

    private StaminaRest StaminaRestNow(bool onFloor)
    {
        if (RidingWith != 0) return StaminaRest.Riding;
        if (Sitting) return StaminaRest.Seated;
        if (_ride is Pigeon) return Pigeon.ModeOf(_flight) == PigeonFlight.Mode.Air ? StaminaRest.None : StaminaRest.Perched;
        if (_ride is Flyer) return StaminaRest.None;
        if (_ride != null) return StaminaRest.Riding;
        if (_climbing || _swimming || _mantling) return StaminaRest.None;
        return onFloor ? StaminaRest.Floor : StaminaRest.None;
    }
}
