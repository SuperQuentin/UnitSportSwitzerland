namespace UnitSport.Player;

/// <summary>What spends stamina (#1225): a rate per second, or a one-off cost.</summary>
public enum StaminaKind
{
    /// <summary>Sprinting on foot in a fight (out of one it is free).</summary>
    SprintCombat,
    /// <summary>Climbing a face, moving (per second at a full stick).</summary>
    ClimbMove,
    /// <summary>Holding still on a face.</summary>
    ClimbHold,
    /// <summary>A climb-jump along the face: one-off.</summary>
    ClimbJump,
    /// <summary>The sprint stroke, swimming.</summary>
    SwimSprint,
    /// <summary>A dive held with sprint.</summary>
    SwimDive,
    /// <summary>A pigeon's wing beats.</summary>
    PigeonFlap,
    /// <summary>A pigeon's boosted beats (Shift).</summary>
    PigeonFlapBoost,
    /// <summary>Reserved for a charged melee (#495's fights): one-off, not wired.</summary>
    Charged,
}

/// <summary>A food's lasting effect on the meter.</summary>
public enum StaminaBuff : byte
{
    None = 0,
    /// <summary>Hot chocolate: climbing costs x<see cref="Stamina.WarmClimbFactor"/>.</summary>
    Warm = 1,
    /// <summary>Fondue: +<see cref="Stamina.FedBonus"/> to the max, full at once.</summary>
    Fed = 2,
}

/// <summary>Where the body is resting, for the regen rate.</summary>
public enum StaminaRest
{
    /// <summary>In the air, climbing or swimming: no regen.</summary>
    None,
    /// <summary>Standing, walking or running on the floor out of a fight.</summary>
    Floor,
    /// <summary>Sitting on a stand.</summary>
    Seated,
    /// <summary>On a ground mount or in a vehicle.</summary>
    Riding,
    /// <summary>A pigeon walking or perched.</summary>
    Perched,
}

/// <summary>
/// The body's effort budget (#1225, <c>docs/plans/adventure-stamina.md</c>): spent by the muscles
/// (climbing, a climb-jump, a sprint stroke, a pigeon's flaps, sprinting in a fight), refilled
/// standing, never spent by equipment that flies on its own. Plain C# like <see cref="AirReserve"/>,
/// no Godot in it, linked into the unit tests (<c>StaminaTests</c>). Units are points; the HUD shows
/// a share. Owner only, never replicated.
/// </summary>
public struct Stamina
{
    /// <summary>The max with no upgrade.</summary>
    public const float Base = 100f;
    /// <summary>Points added to the max by one upgrade step (<c>"stamina"</c> <c>"+1"</c>).</summary>
    public const float StepPoints = 10f;
    /// <summary>Upgrade steps at most (200 points).</summary>
    public const int MaxSteps = 10;
    /// <summary>Added to the max while <see cref="StaminaBuff.Fed"/>.</summary>
    public const float FedBonus = 25f;
    /// <summary>Climbing costs this factor while <see cref="StaminaBuff.Warm"/>.</summary>
    public const float WarmClimbFactor = 0.75f;
    /// <summary>Climbing on crumbling rock (loose rock, scree) costs this factor more.</summary>
    public const float LooseFactor = 1.3f;
    /// <summary>Seconds without regen after the last spend.</summary>
    public const float RegenDelay = 1f;
    /// <summary>Winded until this share of the max is back.</summary>
    public const float WindedUntil = 0.25f;

    /// <summary>Regen per second by where the body rests.</summary>
    public const float RegenFloor = 25f, RegenSeated = 35f, RegenRiding = 15f, RegenPerched = 25f;

    /// <summary>Points per second of a rate (at a full stick).</summary>
    public static float Rate(StaminaKind kind) => kind switch
    {
        StaminaKind.SprintCombat => 12f,
        StaminaKind.ClimbMove => 9f,
        StaminaKind.ClimbHold => 1f,
        StaminaKind.SwimSprint => 8f,
        StaminaKind.SwimDive => 8f,
        StaminaKind.PigeonFlap => 5f,
        StaminaKind.PigeonFlapBoost => 10f,
        _ => 0f,
    };

    /// <summary>Points of a one-off cost.</summary>
    public static float Cost(StaminaKind kind) => kind switch
    {
        StaminaKind.ClimbJump => 18f,
        StaminaKind.Charged => 25f,
        _ => 0f,
    };

    /// <summary>Points left, 0..<see cref="Max"/>.</summary>
    public float Points;
    /// <summary>Upgrade steps, 0..<see cref="MaxSteps"/> (from the <c>"stamina"</c> progress section).</summary>
    public int Steps;
    /// <summary>Hit empty: no climb entry, no climb-jump, no sprint stroke, no flap until <see cref="WindedUntil"/> is back.</summary>
    public bool Winded;
    /// <summary>The rules say off (no Adventure here, creative mode, <c>--stamina off</c>, <c>/stamina infinite</c>): spends succeed and nothing drains.</summary>
    public bool Infinite;

    private float _sinceSpend;
    private float _warm, _fed;

    /// <summary>A full meter with <paramref name="steps"/> upgrades.</summary>
    public static Stamina Full(int steps = 0)
    {
        var s = new Stamina { Steps = Math.Clamp(steps, 0, MaxSteps), _sinceSpend = RegenDelay };
        s.Points = s.Max;
        return s;
    }

    /// <summary>The max now: base, the upgrades and a fondue's bonus.</summary>
    public readonly float Max => Base + StepPoints * Steps + (_fed > 0f ? FedBonus : 0f);

    /// <summary>Points over max, 0..1 (1 when infinite).</summary>
    public readonly float Share => Infinite ? 1f : Max <= 0f ? 0f : Points / Max;

    /// <summary>Nothing spent and nothing to refill.</summary>
    public readonly bool IsFull => Infinite || Points >= Max - 0.001f;

    /// <summary>Seconds of a food buff left, 0 when none.</summary>
    public readonly float BuffLeft(StaminaBuff kind) => kind switch
    {
        StaminaBuff.Warm => _warm,
        StaminaBuff.Fed => _fed,
        _ => 0f,
    };

    private readonly float Factor(StaminaKind kind, bool loose)
    {
        bool climb = kind is StaminaKind.ClimbMove or StaminaKind.ClimbHold or StaminaKind.ClimbJump;
        float f = 1f;
        if (climb && _warm > 0f) f *= WarmClimbFactor;
        if (climb && loose) f *= LooseFactor;
        return f;
    }

    /// <summary>What <see cref="Pay"/> would take for <paramref name="kind"/> now (the ring's pending band).</summary>
    public readonly float CostNow(StaminaKind kind, bool loose = false) => Infinite ? 0f : Cost(kind) * Factor(kind, loose);

    /// <summary>
    /// Spends a rate for <paramref name="dt"/> seconds, times <paramref name="scale"/> (a half stick
    /// climbs for half the cost). False when there is nothing to spend (empty or winded): the act does
    /// not happen. The step that empties the meter still succeeds and sets <see cref="Winded"/>.
    /// </summary>
    public bool Spend(StaminaKind kind, float dt, float scale = 1f, bool loose = false)
    {
        if (Infinite) return true;
        if (Winded || Points <= 0f) return false;
        float cost = Rate(kind) * Factor(kind, loose) * Math.Max(0f, scale) * dt;
        if (cost <= 0f) return true;
        Points -= cost;
        _sinceSpend = 0f;
        if (Points <= 0f)
        {
            Points = 0f;
            Winded = true;
        }
        return true;
    }

    /// <summary>Pays a one-off cost. False (nothing taken) when winded or short of it.</summary>
    public bool Pay(StaminaKind kind, bool loose = false)
    {
        if (Infinite) return true;
        float cost = CostNow(kind, loose);
        if (Winded || Points < cost) return false;
        Points -= cost;
        _sinceSpend = 0f;
        if (Points <= 0.001f)
        {
            Points = 0f;
            Winded = true;
        }
        return true;
    }

    /// <summary>
    /// One step: buffs run down, and after <see cref="RegenDelay"/> without a spend the meter refills
    /// at the rate of <paramref name="rest"/>. Winded ends at <see cref="WindedUntil"/> of the max.
    /// </summary>
    public void Step(float dt, StaminaRest rest)
    {
        if (_warm > 0f) _warm = Math.Max(0f, _warm - dt);
        if (_fed > 0f)
        {
            _fed = Math.Max(0f, _fed - dt);
            if (_fed <= 0f) Points = Math.Min(Points, Max);   // the bonus goes with it
        }
        if (Infinite)
        {
            Points = Max;
            Winded = false;
            return;
        }
        _sinceSpend += dt;
        if (_sinceSpend >= RegenDelay)
        {
            float rate = RegenRate(rest);
            if (rate > 0f) Points = Math.Min(Max, Points + rate * dt);
        }
        if (Winded && Points >= Max * WindedUntil) Winded = false;
    }

    /// <summary>Regen per second while resting so.</summary>
    public static float RegenRate(StaminaRest rest) => rest switch
    {
        StaminaRest.Floor => RegenFloor,
        StaminaRest.Seated => RegenSeated,
        StaminaRest.Riding => RegenRiding,
        StaminaRest.Perched => RegenPerched,
        _ => 0f,
    };

    /// <summary>Food: points back at once (clamped to the max). Returns the points actually added.</summary>
    public float Restore(float points)
    {
        if (points <= 0f) return 0f;
        float before = Points;
        Points = Math.Min(Max, Points + points);
        if (Winded && Points >= Max * WindedUntil) Winded = false;
        return Points - before;
    }

    /// <summary>
    /// Food: a lasting effect for <paramref name="seconds"/>. One of each kind at a time; a new one
    /// restarts its timer. <see cref="StaminaBuff.Fed"/> fills the meter at once.
    /// </summary>
    public void Buff(StaminaBuff kind, float seconds)
    {
        switch (kind)
        {
            case StaminaBuff.Warm:
                _warm = Math.Max(0f, seconds);
                break;
            case StaminaBuff.Fed:
                _fed = Math.Max(0f, seconds);
                Points = Max;
                Winded = false;
                break;
        }
    }

    /// <summary>The upgrade steps from the progress section; a raise refills the meter (the feedback).</summary>
    public void SetSteps(int steps, bool refill = true)
    {
        int was = Steps;
        Steps = Math.Clamp(steps, 0, MaxSteps);
        if (refill && Steps > was) { Points = Max; Winded = false; }
        else Points = Math.Min(Points, Max);
    }

    /// <summary>Back to full (a respawn, a ride's rest).</summary>
    public void Refill()
    {
        Points = Max;
        Winded = false;
    }

    // ---- when it applies: one place, pure ---------------------------------------------------------

    /// <summary>
    /// The meter counts (not infinite): the Adventure layer is active for this player, it is not in
    /// creative mode, the server did not say <c>--stamina off</c>, and no <c>/stamina infinite</c>.
    /// </summary>
    public static bool Applies(bool adventure, bool creative, bool serverOn, bool infiniteCommand) =>
        adventure && !creative && serverOn && !infiniteCommand;

    /// <summary>
    /// Free climbing is on: where the Adventure layer is (never in a Battle Royale match), or in
    /// creative mode outside a match. Elsewhere the movement is today's (mantle, wall jump).
    /// </summary>
    public static bool ClimbingAllowed(bool adventure, bool creative, bool inMatch) =>
        !inMatch && (adventure || creative);

    /// <summary>
    /// The most a climb may plausibly gain between floors (the server's <c>StaminaWatch</c>):
    /// <c>1.5 x (max + 75) / 9 x 1.2 + 4</c> m, a full ring, the best food and climb-jumps, plus slack.
    /// </summary>
    public static float PlausibleClimbM(float max) => 1.5f * (max + 75f) / Rate(StaminaKind.ClimbMove) * 1.2f + 4f;
}
