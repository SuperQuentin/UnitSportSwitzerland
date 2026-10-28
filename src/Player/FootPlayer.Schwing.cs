using Godot;
using UnitSport.Combat;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Player;

/// <summary>
/// Schwingen (#1269, <see cref="SchwingManager"/>): the local wrestler stands on its mark in the grip, facing the rival; the move stick
/// leans, the guard key braces, the punch and kick keys throw (the stick picks which) and, on the ground, mash the tug.
/// The server works out the pose of each wrestler and this body replicates it as <see cref="SchwingPose"/>, which every peer draws
/// (<c>Avatar/HumanMeshBuilder.Schwing.cs</c>).
/// <list type="bullet">
/// <item>Keyboard and pad: stick right is towards the rival on the side-on screen, up and down lean round it. VR keeps the head's view:
/// the stick's forward is towards the rival, sideways leans round, B braces, the triggers throw. Gripping the belt for real is a
/// later target.</item>
/// <item>The walk is locked: both stand <see cref="SchwingRules.GripGap"/> apart on the bout's line.</item>
/// </list>
/// </summary>
public partial class FootPlayer
{
    /// <summary>
    /// What the wrestler is doing (<see cref="SchwingStance"/>, or a throw at <see cref="SchwingStance.ThrowBase"/>); 0 when not wrestling.
    /// The owner writes the pose the server sent; nothing scores from it.
    /// </summary>
    [Export] public int SchwingPose { get; set; }

    /// <summary>Wrestling (on every peer).</summary>
    public bool InSchwing => SchwingPose != 0;

    private int _schwingId;
    private FootPlayer? _schwingRival;
    private SchwingManager? _schwingHooked;
    private bool _schwingWasVisible, _schwingPunchHeld, _schwingKickHeld, _schwingCamSet;
    private int _schwingLean = -1;
    private bool _schwingBrace;
    private double _schwingSentAt = double.NegativeInfinity;
    private Vector3 _schwingCamPos;
    private float _schwingCamSide;
    private Vector3 _schwingCamRight = Vector3.Right;
    // drawing, every peer
    private int _schwingDrawn = -1, _schwingPrev = -1;
    private double _schwingSince;
    private float _schwingWeight;

    /// <summary>The local wrestler's body, in place of the walk. False when not wrestling (and on the way out, once: pose cleared, view handed back).</summary>
    private bool SchwingPhysics(float dt, bool onFloor)
    {
        if (Npc || _camera == null || SchwingManager.Client is not { } bouts) return false;
        if (bouts.Current is not { } v)
        {
            if (SchwingPose != 0) LeaveSchwing();
            return false;
        }
        if (v.Id != _schwingId) EnterSchwing(bouts, v);
        if (_schwingRival != null && !IsInstanceValid(_schwingRival)) _schwingRival = null;
        _schwingRival ??= GetParent()?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(v.Opponent));
        if (Origin is not { } origin) return false;

        var centre = origin.ToWorld(v.Centre);
        var axis = new Basis(Vector3.Up, v.Yaw) * Vector3.Forward;   // A to B
        float mySide = v.IsA ? -1f : 1f;
        var toRival = axis * -mySide;
        var mark = centre + axis * (mySide * SchwingRules.GripGap * 0.5f);
        var face = _schwingRival is { } rival ? rival.GlobalPosition - GlobalPosition : toRival;
        face.Y = 0;
        if (face.LengthSquared() < 0.01f) face = toRival;
        face = face.Normalized();

        // ---- input: lean, brace, throw, tug ---------------------------------------------------
        bool live = v.Phase == SchwingPhase.Live;
        bool ground = v.Phase == SchwingPhase.Ground;
        var stick = PlayerInput.Move;
        float toward = XR.XrSession.Active ? -stick.Y
            : stick.X * Mathf.Sign(_schwingCamRight.Dot(face) == 0 ? 1f : _schwingCamRight.Dot(face));
        float round = XR.XrSession.Active ? -stick.X : -stick.Y;
        var lean = live ? SchwingRules.LeanOf(toward, round) : SchwingLean.None;
        bool brace = live && (XR.XrSession.Active ? XR.XrPad.RightB : PlayerInput.Held(PlayerInput.FightBlock));
        bool punch = PlayerInput.Held(PlayerInput.FightPunch), kick = PlayerInput.Held(PlayerInput.FightKick);
        bool punchEdge = punch && !_schwingPunchHeld, kickEdge = kick && !_schwingKickHeld;
        _schwingPunchHeld = punch;
        _schwingKickHeld = kick;
        double now = GameClock.Now;
        int throwId = 0;
        if (live && (punchEdge || kickEdge) && v.Pending == SchwingThrow.None)
            throwId = (int)SchwingRules.Choose(kickEdge && !punchEdge, lean);
        if (ground && (punchEdge || kickEdge)) bouts.SendTug();
        if (live && (throwId != 0 || (((int)lean != _schwingLean || brace != _schwingBrace) && now - _schwingSentAt >= 0.06)))
        {
            _schwingLean = (int)lean;
            _schwingBrace = brace;
            _schwingSentAt = now;
            bouts.SendAct((int)lean, brace, throwId);
        }
        if (!live && !ground) _schwingLean = -1;

        // ---- the body: on its mark, facing the rival ------------------------------------------
        var rel = mark - GlobalPosition;
        rel.Y = 0;
        var vel = Velocity;
        vel.X = rel.X * 8f;
        vel.Z = rel.Z * 8f;
        vel.Y = onFloor ? Mathf.Min(vel.Y, 0f) : vel.Y - Gravity * dt;
        // far off the mark (the start, a teleport): set down on it
        if (rel.Length() > 2.5f)
        {
            float y = Terrain != null && Terrain.TryGetSurface(mark, out float g) ? g + 0.05f : GlobalPosition.Y;
            GlobalPosition = new Vector3(mark.X, Mathf.Max(y, mark.Y - 3f), mark.Z);
            vel = Vector3.Zero;
        }
        Velocity = vel;
        MoveAndSlide();
        if (!XR.XrSession.Active) Rotation = new Vector3(0, Mathf.Atan2(-face.X, -face.Z), 0);

        SchwingPose = v.MyPose;
        return true;
    }

    private void EnterSchwing(SchwingManager bouts, SchwingManager.View v)
    {
        _schwingId = v.Id;
        _schwingRival = null;
        _schwingCamSet = false;
        _schwingCamSide = 0f;
        _schwingLean = -1;
        _schwingBrace = false;
        _schwingPunchHeld = PlayerInput.Held(PlayerInput.FightPunch);
        _schwingKickHeld = PlayerInput.Held(PlayerInput.FightKick);
        _schwingWasVisible = _walker?.Visible ?? true;
        if (_schwingHooked != bouts)
        {
            if (_schwingHooked != null) _schwingHooked.ThrownEvent -= OnSchwingThrown;
            bouts.ThrownEvent += OnSchwingThrown;
            _schwingHooked = bouts;
        }
        DanceId = 0;
    }

    private void LeaveSchwing()
    {
        SchwingPose = 0;
        _schwingId = 0;
        _schwingRival = null;
        if (_walker != null) _walker.Visible = _schwingWasVisible;
        // the chase camera picks up from behind the body as it now faces
        _viewYaw = Rotation.Y;
        _pivotY = float.NaN;
    }

    /// <summary>A throw wound up or answered: the whoosh as it starts, the thump (or the tick of a brace) and the rumble as it ends.</summary>
    private void OnSchwingThrown(long by, SchwingThrow t, SchwingAnswer answer)
    {
        if (SchwingManager.Client?.Current is not { } v) return;
        bool mine = by == v.Me;
        var at = GlobalPosition + Vector3.Up * 1.2f;
        if (answer == SchwingAnswer.None)
        {
            PlayAt(at, SchwingSounds.Grunt, -6f);
            if (!mine) PlayerInput.Rumble(0.25f, 0.1f, 0.12f);
            return;
        }
        switch (answer)
        {
            case SchwingAnswer.Braced:
                PlayAt(at, SchwingSounds.Slap, -2f);
                PlayerInput.Rumble(0.3f, 0.2f, 0.1f);
                break;
            case SchwingAnswer.Countered:
                PlayAt(at, SchwingSounds.Slap, 1f);
                PlayerInput.Rumble(0.4f, 0.3f, 0.15f);
                break;
            default:
                PlayAt(at, SchwingSounds.Thud, answer == SchwingAnswer.Down ? 3f : 0f);
                CameraShake = answer == SchwingAnswer.Down ? 0.03f : 0.015f;
                PlayerInput.Rumble(mine ? 0.5f : 0.7f, answer == SchwingAnswer.Down ? 0.8f : 0.4f, 0.2f);
                break;
        }
    }

    /// <summary>
    /// The bout's view, from <c>_Process</c>: side-on, both wrestlers framed, the local one on the left as in the fist fight; in VR
    /// the head keeps its own view. True while it owns the camera.
    /// </summary>
    private bool SchwingView(float dt)
    {
        if (!InSchwing || SchwingManager.Client?.Current is not { } v) return false;
        CameraShake = Mathf.MoveToward(CameraShake, 0f, dt * 0.12f);
        if (XR.XrSession.Active)
        {
            Rotation = new Vector3(0, _viewYaw, 0);
            ApplyFootPose();
            SeeBodyFromEye(true);
            return true;
        }
        if (_walker != null) _walker.Visible = true;
        ApplyFootPose();
        SeeBodyFromEye(false);
        if (_camera == null) return true;

        var other = _schwingRival?.GlobalPosition ?? GlobalPosition - Basis.Z * 2f;
        var mid = (GlobalPosition + other) * 0.5f;
        var perp = new Basis(Vector3.Up, v.Yaw) * Vector3.Right;
        if (_schwingCamSide == 0f) _schwingCamSide = v.IsA ? 1f : -1f;
        const float distance = 4.2f;
        var target = mid + Vector3.Up * 0.95f;
        var wanted = target + perp * (_schwingCamSide * distance) + Vector3.Up * 0.5f;
        float reach = ArmReach(target, wanted, SelfExclude, 0.25f, 1f, 0.15f, out float through, out var lensAcross);
        var at = target.Lerp(wanted, reach);
        _schwingCamPos = !_schwingCamSet || _schwingCamPos.DistanceTo(at) > 6f ? at : _schwingCamPos.Lerp(at, MathX.Damp(6f, dt));
        _schwingCamSet = true;
        var lens = Transform3D.Identity.Translated(_schwingCamPos).LookingAt(target, Vector3.Up);
        if (CameraShake > 0f)
        {
            float t = (float)GameClock.Now;
            lens = lens.Rotated(lens.Basis.Z, CameraShake * Mathf.Sin(t * 61f));
            lens.Origin = _schwingCamPos;
        }
        _camera.GlobalTransform = reach > through ? lensAcross * lens : lens;
        _schwingCamRight = new Vector3(lens.Basis.X.X, 0, lens.Basis.X.Z).Normalized();
        return true;
    }

    /// <summary>
    /// The wrestling pose to draw (every peer), or null: the replicated <see cref="SchwingPose"/> through the dance layer. A throw plays
    /// from when this copy saw it start, over a second; a stance loops; a change flows over 120 ms.
    /// </summary>
    private Avatar.DanceParams? StepSchwingPose(float dt)
    {
        int pose = SchwingPose;
        _schwingWeight = Mathf.MoveToward(_schwingWeight, pose != 0 ? 1f : 0f, dt * 5f);
        double now = GameClock.Now;
        if (pose != 0 && pose != _schwingDrawn)
        {
            _schwingPrev = _schwingDrawn;
            _schwingDrawn = pose;
            _schwingSince = now;
        }
        if (_schwingWeight <= 0.001f || _schwingDrawn < 0)
        {
            _schwingDrawn = _schwingPrev = -1;
            _schwingWeight = 0f;
            return null;
        }
        float el = (float)(now - _schwingSince);
        float phase = SchwingRules.ThrowOf(_schwingDrawn) != SchwingThrow.None ? Mathf.Clamp(el / 1.0f, 0f, 0.999f)
            : (SchwingStance)_schwingDrawn is SchwingStance.Down or SchwingStance.Stumble
                ? Mathf.Clamp(el / 0.6f, 0f, 0.999f)
                : (el / 1.5f) % 1f;
        float blend = Mathf.Clamp(el / 0.12f, 0f, 1f);
        int prev = _schwingPrev > 0 ? Avatar.HumanMeshBuilder.SchwingMoves + _schwingPrev : -1;
        return new Avatar.DanceParams(Audio.Cd.MusicStyle.Pop, Avatar.HumanMeshBuilder.SchwingMoves + _schwingDrawn,
            0f, phase, 0, _schwingWeight, prev, blend);
    }

    /// <summary>E at the sawdust ring while the Schwingen is open (#1269): join the queue, or give the place up. Online only.</summary>
    public Interaction? RingInteraction()
    {
        if (SchwingManager.Client is not { } bouts || !NetLink.Online(this) || bouts.Current != null || Ride != RideKind.OnFoot || InDuel || Downed
            || !SchwingRing.At(this)) return null;
        return new(bouts.Queued ? "Leave the queue" : "Wrestle: Schwingen", default, InBar: true);
    }

    private bool TryJoinRing()
    {
        if (RingInteraction() == null) return false;
        SchwingManager.Client!.EngageRing();
        return true;
    }
}
