using Godot;
using UnitSport.Combat;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.Player;

/// <summary>
/// An aura battle (#907, <see cref="AuraManager"/>): the local battler stands on its mark facing the
/// rival, presses <see cref="PlayerInput.AuraNext"/> to cut from move to move on the downbeats, and
/// replicates <see cref="AuraPose"/>, which every peer draws (<c>Avatar/HumanMeshBuilder.Aura.cs</c>).
/// <list type="bullet">
/// <item>The walk is locked: both stand <see cref="AuraRules.StartGap"/> apart on the arena line.
/// The slow walk steps the performer towards the rival and back over the move.</item>
/// <item>The pose is a pure function of the battle's state and the shared clock: planning, waiting,
/// the move the schedule is in, a stumble after a missed cut, the verdict's victory or defeat.</item>
/// <item>The camera stands behind the waiting battler's shoulder, on the performer, for both: the
/// phone-filmed look of the real battles. VR keeps the head's view.</item>
/// </list>
/// </summary>
public partial class FootPlayer
{
    /// <summary>
    /// What the battler is doing (<see cref="AuraStance"/>, or a move at <see cref="AuraStance.MoveBase"/>);
    /// 0 when not in a battle. The owner writes it; nothing scores from it.
    /// </summary>
    [Export] public int AuraPose { get; set; }

    /// <summary>In an aura battle (on every peer).</summary>
    public bool InAura => AuraPose != 0;

    /// <summary>In a fist fight or an aura battle (on every peer): no items, emotes or interactions.</summary>
    public bool InDuel => FightPose != 0 || AuraPose != 0 || SchwingPose != 0;

    /// <summary>How far the slow walk steps towards the rival.</summary>
    private const float AuraWalkReach = 1.2f;

    private int _auraId;
    private FootPlayer? _auraRival;
    private bool _auraNextHeld, _auraStruck;
    private bool _auraWasVisible;
    private Vector3 _auraCamPos;
    private bool _auraCamSet;
    // drawing, every peer
    private int _auraDrawn = -1, _auraPrev = -1;
    private double _auraSince;
    private float _auraWeight;

    /// <summary>
    /// The local battler's body, in place of the walk. False when not in a battle (and on the way
    /// out of one, once: the pose cleared and the view handed back).
    /// </summary>
    private bool AuraPhysics(float dt, bool onFloor)
    {
        if (Npc || _camera == null || AuraManager.Client is not { } auras) return false;
        if (auras.Current is not { } v)
        {
            if (AuraPose != 0) LeaveAura();
            return false;
        }
        if (v.Id != _auraId) EnterAura(v);
        if (_auraRival != null && !IsInstanceValid(_auraRival)) _auraRival = null;
        _auraRival ??= GetParent()?.GetNodeOrNull<FootPlayer>(PlayerReplication.NodeName(v.Opponent));
        if (Origin is not { } origin) return false;

        var centre = origin.ToWorld(v.Centre);
        var axis = new Basis(Vector3.Up, v.Yaw) * Vector3.Forward;   // A to B
        float mySide = v.IsA ? -1f : 1f;
        var toRival = axis * -mySide;
        var mark = centre + axis * (mySide * AuraRules.StartGap * 0.5f);
        double now = ClockSync.ServerNow;

        // ---- Next: the one timed key; in VR, striking the next move's pose for real (#914) ----
        bool next = PlayerInput.Held(PlayerInput.AuraNext);
        bool struck = v.MyTurn && StruckNextPose(v, now);
        // the rival performs: the same key keeps a straight face against a tempt (#914)
        if (next && !_auraNextHeld && !v.MyTurn && auras.PressCompose() is { } held)
        {
            PlayAt(GlobalPosition + Vector3.Up, held ? Audio.SfxSynth.TickBank : Audio.SfxSynth.OofBank, held ? -10f : -6f);
            if (!held) PlayerInput.Rumble(0.4f, 0.4f, 0.15f);
        }
        if ((next && !_auraNextHeld || struck) && v.MyTurn && auras.PressNext() is { } grade)
        {
            switch (grade)
            {
                case AuraGrade.Perfect:
                    PlayAt(GlobalPosition + Vector3.Up, Audio.SfxSynth.TickBank, 0f);
                    PlayerInput.Rumble(0.3f, 0f, 0.05f);
                    break;
                case AuraGrade.Miss:
                    PlayAt(GlobalPosition + Vector3.Up, Audio.SfxSynth.OofBank, -6f);
                    PlayerInput.Rumble(0.6f, 0.5f, 0.2f);
                    break;
                default:
                    PlayAt(GlobalPosition + Vector3.Up, Audio.SfxSynth.TickBank, -8f);
                    break;
            }
        }
        _auraNextHeld = next;

        // ---- the body: on the mark, facing the rival; the slow walk steps in and back ---------
        int won = v.Phase != AuraPhase.Done || v.Winner == 0 ? 0 : v.Winner == v.Me ? 1 : -1;
        var pose = AuraBody.PoseFor(v.Phase, v.MyTurn, won, v.Plan, v.T0, v.Beat, v.Grades, now, v.Laughing(v.Me, now), out float stepIn);
        var at = mark + toRival * (stepIn * AuraWalkReach);
        var rel = at - GlobalPosition;
        rel.Y = 0;
        var vel = Velocity;
        vel.X = rel.X * 8f;
        vel.Z = rel.Z * 8f;
        vel.Y = onFloor ? Mathf.Min(vel.Y, 0f) : vel.Y - Gravity * dt;
        // far off the mark (the start, a teleport): set down on it
        if (rel.Length() > 2.5f)
        {
            float y = Terrain != null && Terrain.TryGetSurface(at, out float g) ? g + 0.05f : GlobalPosition.Y;
            GlobalPosition = new Vector3(at.X, Mathf.Max(y, at.Y - 3f), at.Z);
            vel = Vector3.Zero;
        }
        Velocity = vel;
        MoveAndSlide();

        var face = _auraRival is { } rival ? rival.GlobalPosition - GlobalPosition : toRival;
        face.Y = 0;
        if (face.LengthSquared() < 0.01f) face = toRival;
        if (!XR.XrSession.Active) Rotation = new Vector3(0, Mathf.Atan2(-face.X, -face.Z), 0);

        AuraPose = pose;
        return true;
    }

    /// <summary>
    /// VR (#914): the hands strike the pose of the move after the one on now (or drop the last
    /// pose: hands down) inside the cut's window, a beat before its downbeat to the end of Ok. Only
    /// the moment the pose is struck counts, so a pose already held must be dropped and struck again.
    /// </summary>
    private bool StruckNextPose(AuraManager.View v, double now)
    {
        if (!XR.XrSession.Active || VrHands is not { } hands || v.Phase != AuraPhase.Perform || v.NextIndex >= v.Plan.Length)
        {
            _auraStruck = false;
            return false;
        }
        double cut = AuraRules.CutAt(v.Plan, v.NextIndex, v.T0, v.Beat);
        var next = v.NextIndex + 1 < v.Plan.Length ? v.Plan[v.NextIndex + 1] : AuraMove.None;
        var g = AuraBody.Classify(hands.Right.X, hands.Right.Y, hands.Right.Z, hands.Left.X, hands.Left.Y, hands.Left.Z);
        bool on = AuraBody.Strikes(next, g);
        bool rising = on && !_auraStruck;
        _auraStruck = on;
        return rising && now >= cut - v.Beat && now <= cut + AuraRules.OkWindow;
    }

    private void EnterAura(AuraManager.View v)
    {
        _auraId = v.Id;
        _auraRival = null;
        _auraCamSet = false;
        _auraNextHeld = PlayerInput.Held(PlayerInput.AuraNext);
        _auraWasVisible = _walker?.Visible ?? true;
        DanceId = 0;
    }

    private void LeaveAura()
    {
        AuraPose = 0;
        _auraId = 0;
        _auraRival = null;
        if (_walker != null) _walker.Visible = _auraWasVisible;
        _viewYaw = Rotation.Y;
        _pivotY = float.NaN;
    }

    /// <summary>
    /// The battle's view, from <c>_Process</c>: behind the waiting battler's shoulder, on the
    /// performer (between turns, side-on to both). VR keeps the head's view. True while it owns the camera.
    /// </summary>
    private bool AuraView(float dt)
    {
        if (!InAura || AuraManager.Client?.Current is not { } v) return false;
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

        var other = _auraRival?.GlobalPosition ?? GlobalPosition - Basis.Z * AuraRules.StartGap;
        bool performing = v.Phase is AuraPhase.Countdown or AuraPhase.Perform or AuraPhase.Plan;
        // the performer is the subject; the waiting one's shoulder is in the foreground
        var subject = !performing ? (GlobalPosition + other) * 0.5f : v.MyTurn ? GlobalPosition : other;
        var watcher = !performing ? subject : v.MyTurn ? other : GlobalPosition;
        var line = subject - watcher;
        line.Y = 0;
        if (line.LengthSquared() < 0.01f) line = new Basis(Vector3.Up, v.Yaw) * Vector3.Forward;
        line = line.Normalized();
        var right = line.Cross(Vector3.Up);
        var target = subject + Vector3.Up * 1.1f;
        var wanted = !performing
            ? target + right * 5.5f + Vector3.Up * 0.6f
            : watcher - line * 1.6f + right * 0.55f + Vector3.Up * 1.55f;
        float reach = ArmReach(target, wanted, SelfExclude, 0.25f, 1f, 0.15f, out float through, out var lensAcross);
        var at = target.Lerp(wanted, reach);
        _auraCamPos = !_auraCamSet || _auraCamPos.DistanceTo(at) > 8f ? at : _auraCamPos.Lerp(at, MathX.Damp(4f, dt));
        _auraCamSet = true;
        var lens = Transform3D.Identity.Translated(_auraCamPos).LookingAt(target, Vector3.Up);
        _camera.GlobalTransform = reach > through ? lensAcross * lens : lens;
        return true;
    }

    /// <summary>
    /// The aura pose to draw (every peer), or null: the replicated <see cref="AuraPose"/> through the
    /// dance layer. A move plays from when this copy saw it start, over its beats; a stance loops;
    /// a change flows over 80 ms.
    /// </summary>
    private Avatar.DanceParams? StepAuraPose(float dt)
    {
        int pose = AuraPose;
        _auraWeight = Mathf.MoveToward(_auraWeight, pose != 0 ? 1f : 0f, dt * 5f);
        double now = GameClock.Now;
        if (pose != 0 && pose != _auraDrawn)
        {
            _auraPrev = _auraDrawn;
            _auraDrawn = pose;
            _auraSince = now;
        }
        if (_auraWeight <= 0.001f || _auraDrawn < 0)
        {
            _auraDrawn = _auraPrev = -1;
            _auraWeight = 0f;
            return null;
        }
        float el = (float)(now - _auraSince);
        var move = AuraRules.MoveOf(_auraDrawn);
        // the battle's beat (#914): the music's when it keeps one, as this peer knows it
        double beatLen = AuraManager.Client?.BeatHere ?? AuraRules.BeatSeconds;
        float phase = move != AuraMove.None
            ? AuraRules.IsDance(move)
                // a dance is drawn on the bar, as it is danced to music
                ? (float)(el / (4 * beatLen) % 1.0)
                : Mathf.Clamp(el / (float)(AuraRules.Def(move).Beats * beatLen), 0f, 0.999f)
            : (AuraStance)_auraDrawn == AuraStance.Stumble
                ? Mathf.Clamp(el / (float)beatLen, 0f, 0.999f)
                : (el / 2.4f) % 1f;
        float beat = (float)(ClockSync.ServerNow / beatLen % 1.0);
        float blend = Mathf.Clamp(el / 0.08f, 0f, 1f);
        int prev = _auraPrev > 0 ? Avatar.HumanMeshBuilder.AuraMoves + _auraPrev : -1;
        // a move the dances already have (the Griddy) runs on the shared beat; the rest on its progress
        return new Avatar.DanceParams(Audio.Cd.MusicStyle.HipHop, Avatar.HumanMeshBuilder.AuraMoves + _auraDrawn,
            beat, phase, (int)(el / (4 * beatLen)), _auraWeight, prev, blend);
    }
}
