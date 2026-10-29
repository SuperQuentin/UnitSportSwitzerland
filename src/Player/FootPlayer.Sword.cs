using System.Runtime.InteropServices;
using Godot;
using UnitSport.Audio;
using UnitSport.Combat;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Player;

/// <summary>
/// The sword in the hand (#1371, <c>docs/notes/combat/sword-combat.md</c>): the local player's <see cref="SwordMachine"/> (the
/// combo, the charge, the plunge, the dodge), the lunge and dodge steps, the soft and hard lock-on, the blow's trace against
/// the dummies and bodies this peer draws, and what the blow does to the body it lands on. Every peer draws a swing from
/// <see cref="SwordShown"/>, which the owner sets itself and the server's relay sets on the copies (<see cref="SwordSeen"/>).
/// <list type="bullet">
/// <item>Intent, not input: <see cref="SwordIntent"/> is the held buttons (<c>ItemController</c> fills it); the machine and the
/// steps below act on it and on the body's own state, never on a key. A bot's hands (<c>Bots/BotHands</c>) run the same machine
/// on the server.</item>
/// <item>Nothing here builds a mesh: the sword is the hand's rigid node, turned (<c>HeldItemVisual</c> reads <see cref="SwordArcNow"/>).</item>
/// </list>
/// </summary>
public partial class FootPlayer
{
    /// <summary>The sword is in the hand, on foot (every peer: <see cref="HeldItemId"/> is replicated).</summary>
    public bool SwordDrawn => HeldItemId == (int)ItemId.Sword && _ride == null && !Ragdolled;

    /// <summary>The buttons held this frame (the intent seam): the attack, the dodge. Set by <c>ItemController</c>, empty without a sword.</summary>
    public SwordInput SwordIntent;

    /// <summary>The local player's machine: the HUD reads its stamina, the probes its moves.</summary>
    public SwordMachine SwordState => _sword;

    /// <summary>The move this body is drawn in (the arc of the blade), <see cref="SwordMove.None"/> when at rest.</summary>
    public SwordMove SwordShown { get; private set; }

    /// <summary>Camera shake added by a landed blow, scaled by the screen-shake setting (decays by itself).</summary>
    public float SwordShake { get; private set; }

    /// <summary>When a dodge or a charge was last refused for want of stamina (game clock): the HUD blinks.</summary>
    public double SwordRefusedAt { get; private set; } = double.NegativeInfinity;

    /// <summary>A target is locked (hard lock-on).</summary>
    public bool SwordLocked => _swordLockSet;

    /// <summary>The yaw the body is held to while swinging; null when it faces as it walks.</summary>
    public float? SwordFaceYaw { get; private set; }

    /// <summary>The body turns to <see cref="SwordFaceYaw"/> at once (a spin slash's whole turn is the pose's).</summary>
    public bool SwordFaceSnap { get; private set; }

    private readonly SwordMachine _sword = new();
    private int _swordVariant, _swordSeed, _swordCount, _swordCode;
    private SwordMove _swordPoseMove;
    private float _swordWeight, _swordLungeDone;
    private double _swordShownAt;
    private float _swordShownHold;
    private float _swordYaw;
    private Vector3 _swordPush, _swordDodgeDir;
    private float _swordStagger;
    private bool _swordLockSet, _swordGather2;
    private SwordTargetKind _swordLockKind;
    private long _swordLockId;
    private readonly HashSet<long> _swordHit = new();
    private readonly List<SwordService.Reach> _swordReach = new();
    private readonly List<SwordRules.Candidate> _swordCands = new();

    /// <summary>
    /// Once a physics step before the walk: the push of a blow taken, and, for the local player, the machine, the facing, the
    /// steps and the trace. A bot's body only takes the push (its hands run its machine).
    /// </summary>
    private void SwordStep(float dt, bool onFloor)
    {
        if (!IsMultiplayerAuthority() || Npc && !Bot) return;
        SwordPushStep(dt);
        if (Bot || _camera == null) return;

        SwordShake = Mathf.MoveToward(SwordShake, 0f, dt * 0.12f);
        _swordStagger = Mathf.Max(0f, _swordStagger - dt);
        bool drawn = SwordDrawn && !Downed && !KnockedOut && !InDuel && !Eliminated;
        if (!drawn)
        {
            if (_sword.Busy || _sword.Charging || SwordShown != SwordMove.None) SwordPutAway();
            SwordFaceYaw = null;
            _swordLockSet = false;
            return;
        }

        double now = GameClock.Now;
        var input = SwordIntent;
        if (_swordStagger > 0f) { input.Attack = false; input.Dodge = false; }
        input.Airborne = !onFloor;
        input.Falling = !onFloor && Velocity.Y < -SwordRules.PlungeFall;
        input.Grounded = onFloor;
        _sword.Step(now, input);

        SwordLockStep(dt);
        // the charge first: letting go into the charged blow is one announcement (the blow's), not two
        if (_sword.ChargeChanged && (_sword.Charging || !_sword.Began))
        {
            SwordShown = _sword.Charging ? SwordMove.Charge : SwordMove.None;
            if (_sword.Charging) _swordPoseMove = SwordMove.Charge;
            _swordShownAt = now;
            _swordShownHold = 0f;
            if (_sword.Charging) PlayAt(GlobalPosition + Vector3.Up * 1.2f, SwordSounds.Charge, -8f);
            SwordService.Instance?.SendAct(_sword.Charging ? SwordMove.Charge : SwordMove.None, _swordYaw);
        }
        if (_sword.Began) SwordBegan(now);
        if (_sword.Busy)
        {
            var d = SwordRules.Def(_sword.Move);
            // the arc follows the machine's clock, so a hit-stop holds the blade too; a plunge's dive holds its raised pose
            _swordShownAt = _sword.Move == SwordMove.Plunge && _sword.Phase(now) == SwordPhase.Startup ? now : _sword.MoveAt;
            SwordMotion(dt, now, d);
        }
        else if (!_sword.Charging) SwordShown = SwordMove.None;

        // the blow: traced when its blade goes live, and once more late in the window against what moved into it
        if (_sword.ActiveBegan) { _swordGather2 = false; SwordTrace(_sword.Move); }
        else if (_sword.Live(now) && !_swordGather2 && _sword.Into(now) >= SwordRules.Def(_sword.Move).Startup + SwordRules.Def(_sword.Move).Active * 0.6)
        {
            _swordGather2 = true;
            SwordTrace(_sword.Move);
        }

        // the body turns to the blow's yaw while the hands are busy; the lock keeps it facing its target
        SwordFaceSnap = false;
        if (_sword.Busy && _sword.Move != SwordMove.Dodge)
        {
            // a spin slash turns the whole body: the pose's turn on top of the blow's yaw, set at once
            float spin = SwordPoses.Sample(_sword.Move, _swordVariant, _swordSeed, _sword.Progress(now)).Spin;
            SwordFaceYaw = _swordYaw + spin;
            SwordFaceSnap = spin != 0f;
        }
        else if (_sword.Charging) SwordFaceYaw = _swordYaw;
        else SwordFaceYaw = _swordLockSet && SwordLockYaw() is { } ly ? ly : null;
        if (_sword.Refused)
        {
            SwordRefusedAt = now;
            Core.PlayerInput.Rumble(0.15f, 0f, 0.05f);
        }
    }

    /// <summary>A knockback's push eased out, applied as a step (the walk's own motion goes on beside it).</summary>
    private void SwordPushStep(float dt)
    {
        if (_swordPush.LengthSquared() < 0.01f) { _swordPush = Vector3.Zero; return; }
        MoveAndCollide(_swordPush * dt);
        _swordPush *= Mathf.Exp(-6f * dt);
    }

    /// <summary>A move began (the machine's say): its facing, its announcement to the server, its sound.</summary>
    private void SwordBegan(double now)
    {
        var move = _sword.Move;
        _swordSeed = _swordCount++ & 15;
        _swordLungeDone = 0f;
        // the set a combo swing is drawn from: the advancing one on the move, the planted one standing
        _swordVariant = SwordPoses.VariantFor(MathX.FlatLength(Velocity) > 2f || Core.PlayerInput.Move.LengthSquared() > 0.09f);
        if (move is SwordMove.Charged or SwordMove.Plunge) _swordVariant = 0;
        if (move == SwordMove.Dodge)
        {
            // along the stick; with the stick still, back from where the body faces
            var stick = Core.PlayerInput.Move;
            var view = new Basis(Vector3.Up, _viewYaw);
            var wish = view * new Vector3(stick.X, 0, stick.Y);
            _swordDodgeDir = wish.LengthSquared() > 0.04f ? wish.Normalized() : GlobalBasis.Z with { Y = 0 };
            _swordDodgeDir = _swordDodgeDir.LengthSquared() > 0.01f ? _swordDodgeDir.Normalized() : Vector3.Back;
            _swordYaw = Rotation.Y;
            // a roll forward, a backstep or a sidestep: by the dash against where the body faces
            _swordVariant = SwordPoses.DodgeVariant(_swordDodgeDir.Dot(-GlobalBasis.Z), _swordDodgeDir.Dot(GlobalBasis.X));
            PlayAt(GlobalPosition + Vector3.Up, SwordSounds.Dodge, -5f);
        }
        else
        {
            _swordYaw = SwordAim(move);
            _swordHit.Clear();
            _swordGather2 = false;
            bool heavy = move is SwordMove.Slash5 or SwordMove.Charged or SwordMove.Plunge;
            PlayAt(GlobalPosition + Vector3.Up * 1.3f, heavy ? SwordSounds.Heavy : SwordSounds.Swish, heavy ? -3f : -6f);
            if (move == SwordMove.Plunge) SoftLanding = true;   // the blow is the landing's work
        }
        SwordShown = _swordPoseMove = move;
        _swordShownAt = now;
        _swordShownHold = move == SwordMove.Plunge ? 0.6f : 0f;
        _swordCode = SwordPoses.Code(_swordVariant, _swordSeed);
        SwordService.Instance?.SendAct(move, _swordYaw, _swordCode);
    }

    /// <summary>
    /// Where a blow faces: at the locked target; else the target nearest the view inside the soft lock's cone (the swing turns
    /// to it); else where the view looks.
    /// </summary>
    private float SwordAim(SwordMove move)
    {
        float view = _viewYaw;
        if (_swordLockSet && SwordLockYaw() is { } locked) return locked;
        if (SwordService.Instance is not { } svc) return view;
        _swordReach.Clear();
        svc.Targets(this, _swordReach);
        _swordCands.Clear();
        for (int i = 0; i < _swordReach.Count; i++)
            _swordCands.Add(new SwordRules.Candidate(_swordReach[i].Position.X, _swordReach[i].Position.Z, i));
        float fx = -Mathf.Sin(view), fz = -Mathf.Cos(view);
        int best = SwordRules.Best(CollectionsMarshal.AsSpan(_swordCands), GlobalPosition.X, GlobalPosition.Z, fx, fz,
            SwordRules.SoftRange, SwordRules.SoftConeDeg);
        if (best < 0) return view;
        var p = _swordReach[(int)_swordCands[best].Id].Position;
        return Mathf.Atan2(-(p.X - GlobalPosition.X), -(p.Z - GlobalPosition.Z));
    }

    /// <summary>The yaw towards the locked target, or null when it is gone (the lock lets go).</summary>
    private float? SwordLockYaw()
    {
        if (SwordService.Instance?.PositionOf(_swordLockKind, _swordLockId) is not { } p) { _swordLockSet = false; return null; }
        var d = p - GlobalPosition;
        if (d.X * d.X + d.Z * d.Z > SwordRules.HardRange * SwordRules.HardRange * 1.7f) { _swordLockSet = false; return null; }
        return Mathf.Atan2(-d.X, -d.Z);
    }

    /// <summary>The hard lock: the camera eases round to the target, never past a body behind.</summary>
    private void SwordLockStep(float dt)
    {
        if (!_swordLockSet || SwordLockYaw() is not { } yaw) return;
        float off = Mathf.Abs(Mathf.AngleDifference(_viewYaw, yaw));
        if (off > 2.2f) { _swordLockSet = false; return; }   // it went behind: let go
        _viewYaw = Mathf.LerpAngle(_viewYaw, yaw, MathX.Damp(5f, dt));
    }

    /// <summary>
    /// Locks onto the target nearest the view within <see cref="SwordRules.HardRange"/>, or lets go. Called by the lock
    /// action (and a bot, if it ever wants one).
    /// </summary>
    public void SwordToggleLock()
    {
        if (_swordLockSet) { _swordLockSet = false; return; }
        if (SwordService.Instance is not { } svc) return;
        _swordReach.Clear();
        svc.Targets(this, _swordReach);
        _swordCands.Clear();
        for (int i = 0; i < _swordReach.Count; i++)
            _swordCands.Add(new SwordRules.Candidate(_swordReach[i].Position.X, _swordReach[i].Position.Z, i));
        float fx = -Mathf.Sin(_viewYaw), fz = -Mathf.Cos(_viewYaw);
        int best = SwordRules.Best(CollectionsMarshal.AsSpan(_swordCands), GlobalPosition.X, GlobalPosition.Z, fx, fz, SwordRules.HardRange, 80f);
        if (best < 0) return;
        var r = _swordReach[(int)_swordCands[best].Id];
        _swordLockSet = true;
        _swordLockKind = r.Kind;
        _swordLockId = r.Id;
    }

    /// <summary>Where the lock is, for the HUD's marker (world); null without one.</summary>
    public Vector3? SwordLockPoint() =>
        _swordLockSet && SwordService.Instance?.PositionOf(_swordLockKind, _swordLockId) is { } p ? p + Vector3.Up * 1.1f : null;

    /// <summary>The lunge of a blow and the dodge's dash, as steps along the ground this frame.</summary>
    private void SwordMotion(float dt, double now, SwordMoveDef d)
    {
        var step = Vector3.Zero;
        if (_sword.Move == SwordMove.Dodge)
        {
            float k = Mathf.Clamp(1f - (float)_sword.Into(now) / d.Total, 0f, 1f);
            step = _swordDodgeDir * (SwordRules.DodgeSpeed * k * dt);
        }
        else if (SwordPoses.Lunge(_sword.Move, _swordVariant) is var metres and > 0f)
        {
            // a step per swing, 0.3 to 1.2 m, eased out over the strike, never into a target already at arm's length
            float done = SwordPoses.LungeDone(_sword.Move, _sword.Progress(now));
            var fwd = new Vector3(-Mathf.Sin(_swordYaw), 0, -Mathf.Cos(_swordYaw));
            if (done > _swordLungeDone && !SwordTargetClose(fwd)) step = fwd * (metres * (done - _swordLungeDone));
            _swordLungeDone = Mathf.Max(_swordLungeDone, done);
        }
        else if (_sword.Move == SwordMove.Plunge && _sword.Phase(now) == SwordPhase.Startup)
            Velocity = new Vector3(0, -SwordRules.PlungeSpeed, 0);
        if (step != Vector3.Zero) MoveAndCollide(step);
    }

    private bool SwordTargetClose(Vector3 fwd)
    {
        if (SwordService.Instance is not { } svc) return false;
        foreach (var s in PlayerSnapshot.Of(GetTree()))
        {
            if (s.Player == this || s.Player.Npc && !s.Player.Bot) continue;
            var o = s.Player.GlobalPosition - GlobalPosition;
            o.Y = 0;
            if (o.LengthSquared() < 1.1f * 1.1f && o.Normalized().Dot(fwd) > 0.3f) return true;
        }
        foreach (var t in svc.Dummies.Values)
        {
            var d = t.GlobalPosition - GlobalPosition;
            d.Y = 0;
            if (d.LengthSquared() < 1.3f * 1.3f && d.Normalized().Dot(fwd) > 0.3f) return true;
        }
        return false;
    }

    /// <summary>Traces a blow whose blade is live against the targets this peer draws and names each one reached.</summary>
    private void SwordTrace(SwordMove move)
    {
        if (!SwordRules.IsBlow(move) || SwordService.Instance is not { } svc) return;
        var d = SwordRules.Def(move);
        _swordReach.Clear();
        svc.Targets(this, _swordReach);
        float fx = -Mathf.Sin(_swordYaw), fz = -Mathf.Cos(_swordYaw);
        foreach (var r in _swordReach)
        {
            if (_swordHit.Count >= SwordGuard.MaxTargets) break;
            var rel = r.Position - GlobalPosition;
            if (!SwordRules.InCone(0, 0, fx, fz, rel.X, rel.Z, d.Reach, d.ConeDeg, out _) || !SwordRules.InHeight(rel.Y)) continue;
            long key = r.Kind == SwordTargetKind.Dummy ? -1_000_000 - r.Id : r.Id;
            if (_swordHit.Add(key)) svc.SendHit(move, r.Kind, r.Id);
        }
    }

    /// <summary>The sword is put away (or the body went down): the hands let go of everything.</summary>
    private void SwordPutAway()
    {
        bool had = _sword.Charging || _sword.Busy;
        _sword.Reset();
        SwordShown = SwordMove.None;
        if (had) SwordService.Instance?.SendAct(SwordMove.None, _swordYaw);
    }

    // ---- what a blow does to this body -------------------------------------------------------------------------

    /// <summary>
    /// A blow of <paramref name="move"/> from <paramref name="attacker"/> landed on this body (the server's word, on the victim's
    /// own machine): the health, the push, the rocking, the shake. Nothing inside a dodge's window.
    /// </summary>
    public void SwordHurt(long attacker, float damage, Vector3 knock, SwordMove move)
    {
        if (_sword.Invulnerable(GameClock.Now)) return;
        ShotHit(damage, attacker);
        SwordKnocked(knock);
        bool heavy = move is SwordMove.Slash5 or SwordMove.Charged or SwordMove.Plunge;
        Flinch(knock, heavy ? 1.1f : 0.6f);
        SwordShake = Mathf.Max(SwordShake, heavy ? 0.03f : 0.015f);
        Core.PlayerInput.Rumble(0.6f, heavy ? 0.8f : 0.4f, heavy ? 0.3f : 0.12f);
    }

    /// <summary>
    /// Pushed by a blow (<paramref name="knock"/>, metres per second, world axes): a step back eased out, a stagger in which no
    /// blow or dodge starts, and a hard blow breaks the swing it landed in.
    /// </summary>
    public void SwordKnocked(Vector3 knock)
    {
        knock.Y = 0;
        _swordPush += knock;
        _swordStagger = Mathf.Clamp(0.12f + knock.Length() * 0.03f, 0.12f, 0.45f);
        if (knock.Length() >= 5f && !Bot && (_sword.Busy || _sword.Charging)) SwordPutAway();
    }

    /// <summary>A dash (a bot's dodge): <paramref name="velocity"/> in metres per second along the ground, eased out like a push, with no stagger.</summary>
    public void SwordDash(Vector3 velocity) => _swordPush += new Vector3(velocity.X, 0, velocity.Z);

    /// <summary>
    /// This body's blow landed (the server's word on the swinger's machine): the hands stop for the move's hit-stop, the screen
    /// shakes with the damage and the pad rumbles.
    /// </summary>
    public void SwordLanded(SwordMove move, bool down)
    {
        var d = SwordRules.Def(move);
        _sword.Freeze(GameClock.Now, d.HitStop * (down ? 1.6f : 1f));
        SwordShake = Mathf.Max(SwordShake, (0.004f + d.Damage * 0.0005f) * (move is SwordMove.Slash5 or SwordMove.Plunge ? 1.7f : 1f));
        Core.PlayerInput.Rumble(0.3f + d.Damage * 0.01f, 0.2f + d.Damage * 0.015f, 0.06f + d.HitStop);
    }

    // ---- drawing, every peer -----------------------------------------------------------------------------------

    /// <summary>
    /// The server's relay of another body's move (<see cref="SwordService.Acted"/>): drawn from now, with its sound. A
    /// <see cref="SwordMove.None"/> clears it.
    /// </summary>
    public void SwordSeen(SwordMove move, float yaw, int code = 0)
    {
        SwordShown = move;
        if (move != SwordMove.None) { _swordPoseMove = move; _swordCode = code; _swordVariant = SwordPoses.VariantOf(code); _swordSeed = SwordPoses.SeedOf(code); }
        _swordShownAt = GameClock.Now;
        _swordShownHold = move == SwordMove.Plunge ? 0.6f : 0f;
        if (move is SwordMove.None or SwordMove.Dodge) { if (move == SwordMove.Dodge) PlayAt(GlobalPosition + Vector3.Up, SwordSounds.Dodge, -5f); return; }
        if (move == SwordMove.Charge) { PlayAt(GlobalPosition + Vector3.Up * 1.2f, SwordSounds.Charge, -8f); return; }
        bool heavy = move is SwordMove.Slash5 or SwordMove.Charged or SwordMove.Plunge;
        PlayAt(GlobalPosition + Vector3.Up * 1.3f, heavy ? SwordSounds.Heavy : SwordSounds.Swish, heavy ? -3f : -6f);
    }

    /// <summary>
    /// The blade's pose this frame, in the hand's frame: (yaw, pitch, roll) radians about the grip and the metres it reaches
    /// forward. False when the sword is at rest. <c>HeldItemVisual</c> turns the rigid node with it.
    /// </summary>
    public bool SwordArcNow(out float yaw, out float pitch, out float roll, out float forward)
    {
        yaw = pitch = roll = forward = 0f;
        if (!SwordDrawn || SwordShown is SwordMove.None or SwordMove.Dodge) return false;
        var d = SwordRules.Def(SwordShown);
        float t = SwordShown == SwordMove.Charge ? 0f : (float)Math.Max(0.0, GameClock.Now - _swordShownAt - _swordShownHold) / Math.Max(0.01f, d.Total);
        if (t > 1.02f)
        {
            // a copy's blow is over (the owner's machine ends its own)
            if (!IsMultiplayerAuthority()) SwordShown = SwordMove.None;
            return false;
        }
        (yaw, pitch, roll, forward) = SwordRules.Arc(SwordShown, t);
        // the hand carries the arc now (the pose): the blade only whips a little at the wrist, but held down for a plunge
        if (SwordShown is >= SwordMove.Slash1 and <= SwordMove.Slash5 or SwordMove.Charged)
        {
            yaw *= 0.4f; pitch *= 0.4f; roll *= 0.4f; forward *= 0.4f;
        }
        return true;
    }

    /// <summary>
    /// 0..1: how bright the blade's trail is now. Only through a blow's active frames and a little after, on every peer; the
    /// shape (mirrored for a backhand, upright for an overhead blow) is <c>HeldItemVisual</c>'s.
    /// </summary>
    public float SwordTrailNow(out SwordMove move)
    {
        move = SwordShown;
        if (!SwordDrawn || !SwordRules.IsBlow(SwordShown) || SwordShown == SwordMove.Plunge && GameClock.Now - _swordShownAt - _swordShownHold < 0) return 0f;
        var d = SwordRules.Def(SwordShown);
        float p = (float)Math.Max(0.0, GameClock.Now - _swordShownAt - _swordShownHold) / Math.Max(0.01f, d.Total);
        var (_, tA, tS, _) = SwordPoses.Times(SwordShown, _swordSeed);
        float to = tS + (1f - tS) * 0.25f;
        if (p < tA || p > to) return 0f;
        float x = (p - tA) / Math.Max(0.001f, to - tA);
        return Mathf.Sin(Mathf.Pi * Mathf.Min(1f, x * 0.9f + 0.05f));
    }

    /// <summary>The variant of the swing being drawn (the trail's mirror, the probes).</summary>
    public int SwordVariantShown => _swordVariant;

    /// <summary>
    /// The sword's whole-body pose (every peer): the move being drawn, its variant and seed, how far through it. Blends in over a
    /// tenth of a second and out over a quarter, so walking does not snap. Null when nothing is drawn.
    /// </summary>
    private Avatar.DanceParams? StepSwordPose(float dt)
    {
        bool active = SwordDrawn && SwordShown != SwordMove.None;
        _swordWeight = Mathf.MoveToward(_swordWeight, active ? 1f : 0f, dt * (active ? 10f : 4f));
        if (_swordWeight <= 0.001f) { _swordWeight = 0f; return null; }
        double now = GameClock.Now;
        var move = _swordPoseMove;
        float p;
        if (move == SwordMove.Charge) p = Mathf.Clamp((float)((now - _swordShownAt) / SwordRules.ChargeMax), 0f, 0.999f);
        else if (SwordShown == SwordMove.None) p = 0.999f;
        else
        {
            var d = SwordRules.Def(move);
            p = Mathf.Clamp((float)(now - _swordShownAt - _swordShownHold) / Mathf.Max(0.01f, d.Total), 0f, 0.999f);
            // a copy's blow is over: it settles into the stance and out
            if (!IsMultiplayerAuthority() && p >= 0.999f) SwordShown = SwordMove.None;
        }
        return new Avatar.DanceParams(Audio.Cd.MusicStyle.Pop, Avatar.HumanMeshBuilder.SwordMoves, 0f, p,
            Avatar.HumanMeshBuilder.SwordCode(move, _swordVariant, _swordSeed), _swordWeight);
    }
}
