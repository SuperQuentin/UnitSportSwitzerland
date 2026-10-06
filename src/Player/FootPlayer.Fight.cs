using Godot;
using UnitSport.Combat;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Player;

/// <summary>
/// A fist fight (#495, <see cref="FightManager"/>): the local fighter's body on the arena line,
/// its moves and reactions, the side-on camera, and the replicated <see cref="FightPose"/> every
/// peer draws (<c>Avatar/HumanMeshBuilder.Fight.cs</c>).
/// <list type="bullet">
/// <item>Steps along the line through the arena's centre, square to the opponent; walls at
/// <see cref="FightRules.HalfLength"/>, never through the other body.</item>
/// <item>Keyboard and pad: left / right on the screen step (the camera is side-on), up jumps, down
/// crouches; punch, kick and the held guard are their own actions. VR keeps the head's view (no
/// camera swap, no body turn under the headset): the stick's forward is towards the opponent, a
/// real crouch crouches, B guards.</item>
/// <item>The server judges each strike; this side reports it when its active frames begin with
/// the opponent in reach as drawn here, and plays the stun, push and knockdown it is told.</item>
/// </list>
/// </summary>
public partial class FootPlayer
{
    /// <summary>
    /// What the fighter is doing (<see cref="FightStance"/>, or a move at <see cref="FightStance.MoveBase"/>);
    /// 0 when not fighting. The owner writes it; the server reads its copy for the guard.
    /// </summary>
    [Export] public int FightPose { get; set; }

    /// <summary>In a fist fight (on every peer): no items, emotes or interactions.</summary>
    public bool Fighting => FightPose != 0;

    private const float FightWalk = 2.3f, FightBackWalk = 1.8f, FightJump = 5.4f, FightMinGap = 0.8f;

    private readonly FightInput _fightInput = new();
    private FightManager? _fightHooked;
    private int _fightId, _fightRound = -1;
    private FootPlayer? _fightOpponent;
    private FightMove _fightMove;
    private double _fightMoveAt;
    private bool _fightSent;
    private FightKey _fightQueued;
    private double _fightQueuedAt = double.NegativeInfinity;
    private float _fightStun;
    private FightStance _fightStunAs;
    private float _fightPush;
    private bool _fightPunchHeld, _fightKickHeld, _fightUpHeld, _fightDownHeld;
    private int _fightStepHeld;
    private bool _fightWasVisible;
    private Vector3 _fightCamPos;
    private bool _fightCamSet;
    private float _fightCamSide;
    // drawing, every peer
    private int _fightDrawn = -1, _fightPrev = -1;
    private double _fightSince;
    private float _fightWeight;

    /// <summary>
    /// The local fighter's step, in place of the walk. False when not fighting (and on the way out
    /// of a fight, once: the pose cleared and the view handed back).
    /// </summary>
    private bool FightPhysics(float dt, bool onFloor)
    {
        if (Npc || _camera == null || FightManager.Client is not { } fights) return false;
        if (fights.Current is not { } v)
        {
            if (FightPose != 0) LeaveFight();
            return false;
        }
        if (v.Id != _fightId) EnterFight(fights, v);
        if (_fightOpponent != null && !IsInstanceValid(_fightOpponent)) _fightOpponent = null;
        _fightOpponent ??= GetParent()?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(v.Opponent));
        if (Origin is not { } origin) return false;

        var centre = origin.ToWorld(v.Centre);
        var axis = new Basis(Vector3.Up, v.Yaw) * Vector3.Forward;   // A to B
        float mySide = v.IsA ? -1f : 1f;
        double now = GameClock.Now;

        // a new round: both set down on their marks, facing each other
        if (v.Round != _fightRound)
        {
            _fightRound = v.Round;
            var mark = centre + axis * (mySide * FightRules.StartGap * 0.5f);
            float y = Terrain != null && Terrain.TryGetSurface(mark, out float g) ? g + 0.05f : GlobalPosition.Y;
            GlobalPosition = new Vector3(mark.X, Mathf.Max(y, mark.Y - 3f), mark.Z);
            Velocity = Vector3.Zero;
            _fightMove = FightMove.None;
            _fightStun = 0f;
            _fightPush = 0f;
            _fightInput.Reset();
        }

        // towards the opponent as drawn here; along the arena line when on top of each other
        var toThem = _fightOpponent is { } opp ? opp.GlobalPosition - GlobalPosition : axis * -mySide;
        toThem.Y = 0;
        float gap = toThem.Length();
        var forward = gap > 0.1f ? toThem / gap : axis * -mySide;
        if (!XR.XrSession.Active) Rotation = new Vector3(0, Mathf.Atan2(-forward.X, -forward.Z), 0);

        // ---- input --------------------------------------------------------------------
        bool control = v.Phase == FightPhase.Live || (v.Phase == FightPhase.FinishHim && v.Winner == v.Me);
        var stick = PlayerInput.Move;
        // which way "right" is: the screen's, side-on; in VR, the stick's forward is at them
        float toward = XR.XrSession.Active ? -stick.Y
            : stick.X * Mathf.Sign(_fightCamRight.Dot(forward) == 0 ? 1f : _fightCamRight.Dot(forward));
        bool up = XR.XrSession.Active ? PlayerInput.Held(PlayerInput.Jump) : stick.Y < -0.6f || PlayerInput.Held(PlayerInput.Jump);
        bool down = XR.XrSession.Active ? XR.XrPad.RealCrouch || stick.Y > 0.6f
            : stick.Y > 0.6f || (PlayerInput.LastDevice != InputDevice.Gamepad && PlayerInput.Held(PlayerInput.CrouchSlide));
        bool block = XR.XrSession.Active ? XR.XrPad.RightB : PlayerInput.Held(PlayerInput.FightBlock);
        bool punch = PlayerInput.Held(PlayerInput.FightPunch), kick = PlayerInput.Held(PlayerInput.FightKick);
        int step = toward > 0.4f ? 1 : toward < -0.4f ? -1 : 0;

        if (!control) up = down = block = punch = kick = false;
        if (down && !_fightDownHeld) _fightInput.Direction(FightKey.Down, now);
        if (step != 0 && step != _fightStepHeld) _fightInput.Direction(step > 0 ? FightKey.Forward : FightKey.Back, now);
        if (punch && !_fightPunchHeld) { _fightQueued = FightKey.Punch; _fightQueuedAt = now; }
        if (kick && !_fightKickHeld) { _fightQueued = FightKey.Kick; _fightQueuedAt = now; }
        bool jumpPressed = up && !_fightUpHeld;
        _fightPunchHeld = punch; _fightKickHeld = kick; _fightUpHeld = up; _fightDownHeld = down; _fightStepHeld = step;

        // ---- the move in progress -------------------------------------------------------
        if (_fightStun > 0f) _fightStun -= dt;
        var def = FightRules.Def(_fightMove);
        double into = now - _fightMoveAt;
        if (_fightMove != FightMove.None)
        {
            if (!_fightSent && into >= def.Startup)
            {
                _fightSent = true;
                // in reach as drawn here: the server checks its own copies again
                if (gap <= def.Reach + FightRules.BodyRadius) fights.SendStrike(_fightMove);
            }
            bool landedEarly = _fightMove == FightMove.JumpKick && onFloor && into > 0.1;
            if (into >= def.Total || landedEarly || _fightStun > 0f) _fightMove = FightMove.None;
        }
        // a buffered press starts when the hands are free (a jab's recovery may be cut by the next jab or the string's kick)
        bool free = _fightStun <= 0f && (_fightMove == FightMove.None
            || _fightMove == FightMove.Jab && into >= def.Startup + def.Active);
        if (control && free && now - _fightQueuedAt < 0.2 && !block)
        {
            bool finishing = v.Phase == FightPhase.FinishHim;
            var move = _fightInput.Press(_fightQueued, now, down && onFloor, !onFloor, finishing);
            _fightQueuedAt = double.NegativeInfinity;
            if (move != FightMove.None)
            {
                _fightMove = move;
                _fightMoveAt = now;
                _fightSent = false;
                def = FightRules.Def(move);
                PlayAt(GlobalPosition + Vector3.Up, Audio.SfxSynth.WhooshBank, def.Damage >= 9 ? -4f : -9f);
            }
        }

        // ---- the body --------------------------------------------------------------------
        bool busy = _fightMove != FightMove.None || _fightStun > 0f || !control;
        float speed = 0f;
        if (onFloor && !busy && !down)
            speed = step > 0 ? FightWalk : step < 0 ? -FightBackWalk : 0f;
        if (block && onFloor) speed *= 0.35f;
        // the push from a blow, eased out
        _fightPush = Mathf.MoveToward(_fightPush, 0f, dt * 9f);
        var along = forward * (speed - _fightPush);
        var vel = Velocity;
        if (onFloor)
        {
            vel.X = along.X;
            vel.Z = along.Z;
            vel.Y = jumpPressed && !busy && !down ? FightJump : Mathf.Min(vel.Y, 0f);
        }
        else
            vel.Y -= Gravity * dt;
        // never into the other body
        if (gap < FightMinGap && new Vector3(vel.X, 0, vel.Z).Dot(forward) > 0f)
        {
            float into_ = new Vector3(vel.X, 0, vel.Z).Dot(forward);
            vel -= forward * into_;
        }
        Velocity = vel;
        MoveAndSlide();

        // the arena: back onto the line, inside its ends
        var rel = GlobalPosition - centre;
        rel.Y = 0;
        float a = Mathf.Clamp(rel.Dot(axis), -FightRules.HalfLength, FightRules.HalfLength);
        var side = rel - axis * rel.Dot(axis);
        var onLine = centre + axis * a + side * Mathf.Exp(-6f * dt);
        GlobalPosition = new Vector3(onLine.X, GlobalPosition.Y, onLine.Z);

        FightPose = PoseNow(v, onFloor, down, block);
        return true;
    }

    /// <summary>The camera's screen-right, for the step input: set by the fight camera each frame.</summary>
    private Vector3 _fightCamRight = Vector3.Right;

    private int PoseNow(FightManager.View v, bool onFloor, bool down, bool block)
    {
        bool lost = v.Winner != 0 && v.Winner != v.Me;
        switch (v.Phase)
        {
            case FightPhase.RoundOver when v.End is FightEnd.Ko or FightEnd.Perfect:
                return (int)(lost ? FightStance.Down : FightStance.Victory);
            case FightPhase.FinishHim when lost:
                return (int)FightStance.Dazed;
            case FightPhase.Done:
                return (int)(v.Winner == 0 ? FightStance.Stand : lost ? FightStance.Down : FightStance.Victory);
        }
        if (_fightStun > 0f) return (int)_fightStunAs;
        if (_fightMove != FightMove.None) return FightRules.PoseOf(_fightMove);
        if (!onFloor) return (int)FightStance.Air;
        if (down) return (int)(block ? FightStance.GuardLow : FightStance.Crouch);
        return (int)(block ? FightStance.GuardHigh : FightStance.Stand);
    }

    private void EnterFight(FightManager fights, FightManager.View v)
    {
        _fightId = v.Id;
        _fightRound = -1;
        _fightOpponent = null;
        _fightCamSet = false;
        _fightCamSide = 0f;
        _fightWasVisible = _walker?.Visible ?? true;
        if (_fightHooked != fights)
        {
            if (_fightHooked != null) _fightHooked.StruckEvent -= OnFightStruck;
            fights.StruckEvent += OnFightStruck;
            _fightHooked = fights;
        }
        DanceId = 0;
    }

    private void LeaveFight()
    {
        FightPose = 0;
        _fightId = 0;
        _fightMove = FightMove.None;
        _fightStun = 0f;
        _fightOpponent = null;
        if (_walker != null) _walker.Visible = _fightWasVisible;
        // the chase camera picks up from behind the body as it now faces
        _viewYaw = Rotation.Y;
        _pivotY = float.NaN;
    }

    /// <summary>A strike the server judged: the victim's stun, push and knockdown; the attacker's rumble.</summary>
    private void OnFightStruck(long attacker, FightMove move, StrikeOutcome outcome)
    {
        if (FightManager.Client?.Current is not { } v || outcome is StrikeOutcome.Refused or StrikeOutcome.Whiff) return;
        var def = FightRules.Def(move);
        var at = (_fightOpponent is { } o && attacker == v.Me ? o.GlobalPosition : GlobalPosition) + Vector3.Up * 1.3f;
        PlayAt(at, outcome == StrikeOutcome.Blocked ? Audio.SfxSynth.TickBank : Audio.SfxSynth.ImpactBank,
            outcome == StrikeOutcome.Blocked ? -2f : def.Knockdown ? 3f : 0f);
        if (attacker == v.Me)
        {
            PlayerInput.Rumble(outcome == StrikeOutcome.Hit ? 0.5f : 0.2f, outcome == StrikeOutcome.Hit ? 0.3f : 0f, 0.08f);
            return;
        }
        bool blocked = outcome == StrikeOutcome.Blocked;
        _fightMove = FightMove.None;
        _fightStun = blocked ? def.BlockStun : def.HitStun;
        _fightStunAs = blocked ? FightStance.BlockStun : def.Knockdown ? FightStance.Down : FightStance.HitStun;
        // the push, as a speed eased out over ~0.15 s
        _fightPush = (blocked ? def.Push * 0.5f : def.Push) * 6f;
        if (move == FightMove.Finisher && !blocked)
            Velocity += new Vector3(0, 7f, 0);
        if (!blocked)
        {
            if (_fightOpponent is { } opp) Flinch(GlobalPosition - opp.GlobalPosition, def.Knockdown ? 1.2f : 0.6f);
            CameraShake = def.Knockdown ? 0.03f : 0.015f;
            if (def.Knockdown) PlayAt(GlobalPosition + Vector3.Up, Audio.SfxSynth.OofBank, 0f);
            PlayerInput.Rumble(0.6f, def.Knockdown ? 0.8f : 0.4f, def.Knockdown ? 0.3f : 0.12f);
        }
    }

    /// <summary>
    /// The fight's view, from <c>_Process</c>: side-on, both fighters framed, the local one on the
    /// left as P1 is; in VR the head keeps its own view. True while it owns the camera.
    /// </summary>
    private bool FightView(float dt)
    {
        if (!Fighting || FightManager.Client?.Current is not { } v) return false;
        // nothing else sets the shake while the items are off: a blow's dies away here
        CameraShake = Mathf.MoveToward(CameraShake, 0f, dt * 0.12f);
        if (XR.XrSession.Active)
        {
            Rotation = new Vector3(0, _viewYaw, 0);
            ApplyFootPose();
            return true;
        }
        if (_walker != null) _walker.Visible = true;
        ApplyFootPose();
        if (_camera == null) return true;

        var other = _fightOpponent?.GlobalPosition ?? GlobalPosition - Basis.Z * 2f;
        var mid = (GlobalPosition + other) * 0.5f;
        var across = other - GlobalPosition;
        across.Y = 0;
        float gap = Mathf.Max(across.Length(), 0.5f);
        var perp = new Basis(Vector3.Up, v.Yaw) * Vector3.Right;
        // the side the camera stands on is picked once, so the local fighter starts on the left
        // (screen right points at the opponent), and it never swaps when they jump over each other
        if (_fightCamSide == 0f) _fightCamSide = (v.IsA ? 1f : -1f);
        float distance = Mathf.Clamp(2.6f + gap * 0.75f, 3.6f, 8f);
        var target = mid + Vector3.Up * 1.05f;
        var wanted = target + perp * (_fightCamSide * distance) + Vector3.Up * 0.35f;
        // walls and trees: the same pull-in as the chase camera
        float reach = ArmReach(target, wanted, SelfExclude, 0.25f, 1f, 0.15f, out float through, out var lensAcross);
        var at = target.Lerp(wanted, reach);
        _fightCamPos = !_fightCamSet || _fightCamPos.DistanceTo(at) > 6f ? at : _fightCamPos.Lerp(at, MathX.Damp(8f, dt));
        _fightCamSet = true;
        var lens = Transform3D.Identity.Translated(_fightCamPos).LookingAt(target, Vector3.Up);
        if (CameraShake > 0f)
        {
            float t = (float)Time.GetTicksMsec() / 1000f;
            lens = lens.Rotated(lens.Basis.Z, CameraShake * Mathf.Sin(t * 61f));
            lens.Origin = _fightCamPos;
        }
        _camera.GlobalTransform = reach > through ? lensAcross * lens : lens;
        _fightCamRight = new Vector3(lens.Basis.X.X, 0, lens.Basis.X.Z).Normalized();
        return true;
    }

    /// <summary>
    /// The fight pose to draw (every peer), or null: the replicated <see cref="FightPose"/> through
    /// the dance layer. A move plays from when this copy saw it start, over its frame data; a stance
    /// loops; a change flows over 80 ms.
    /// </summary>
    private Avatar.DanceParams? StepFightPose(float dt)
    {
        int pose = FightPose;
        _fightWeight = Mathf.MoveToward(_fightWeight, pose != 0 ? 1f : 0f, dt * 5f);
        double now = GameClock.Now;
        if (pose != 0 && pose != _fightDrawn)
        {
            _fightPrev = _fightDrawn;
            _fightDrawn = pose;
            _fightSince = now;
        }
        if (_fightWeight <= 0.001f || _fightDrawn < 0)
        {
            _fightDrawn = _fightPrev = -1;
            _fightWeight = 0f;
            return null;
        }
        float el = (float)(now - _fightSince);
        var move = FightRules.MoveOf((byte)_fightDrawn);
        float phase = move != FightMove.None ? Mathf.Clamp(el / FightRules.Def(move).Total, 0f, 0.999f)
            : (FightStance)_fightDrawn is FightStance.HitStun or FightStance.BlockStun or FightStance.Down
                ? Mathf.Clamp(el / 0.45f, 0f, 0.999f)
                : (el / 1.2f) % 1f;
        float blend = Mathf.Clamp(el / 0.08f, 0f, 1f);
        int prev = _fightPrev > 0 ? Avatar.HumanMeshBuilder.FightMoves + _fightPrev : -1;
        return new Avatar.DanceParams(Audio.Cd.MusicStyle.Pop, Avatar.HumanMeshBuilder.FightMoves + _fightDrawn,
            0f, phase, 0, _fightWeight, prev, blend);
    }

    /// <summary>
    /// The other player on foot the local player is looking at, close enough to challenge (or to
    /// answer): within 3.5 m, inside a narrow cone of the camera's view, nearest the centre.
    /// </summary>
    public FootPlayer? PointedFighter()
    {
        if (_camera == null || Ride != RideKind.OnFoot || Fighting || Downed) return null;
        var look = -_camera.GlobalTransform.Basis.Z;
        look.Y = 0;
        if (look.LengthSquared() < 1e-4f) return null;
        look = look.Normalized();
        FootPlayer? best = null;
        float bestAngle = 0.4f;
        foreach (var s in PlayerSnapshot.Of(GetTree()))
        {
            var p = s.Player;
            if (p == this || p.Npc || s.Ride != RideKind.OnFoot || p.Down != 0 || p.Fighting || !p.Visible) continue;
            var d = s.Pos - GlobalPosition;
            d.Y = 0;
            float dist = d.Length();
            if (dist > 3.5f || dist < 0.05f) continue;
            float angle = look.AngleTo(d / dist);
            if (angle < bestAngle)
            {
                bestAngle = angle;
                best = p;
            }
        }
        return best;
    }

    /// <summary>E at another player: challenge, or accept its challenge. Online only.</summary>
    private bool TryEngageFighter()
    {
        if (FightManager.Client is not { } fights || !NetLink.Online(this) || PointedFighter() is not { } p) return false;
        if (NetId(p.Name) is not { } peer || peer <= 0) return false;
        fights.Engage(peer);
        return true;
    }
}
