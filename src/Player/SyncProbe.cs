using System;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --synccheck [--at E,N]</c>
///
/// <para>
/// Does a remote player look like what its owner sees? An owner runs a scripted session — walk,
/// sprint, jump, slide, a leaning bike ride, a helicopter climb, a banking plane — and a MIRROR
/// player, given someone else's authority so it takes the remote path, is fed exactly what the real
/// synchronizer sends: every property in the owner's <c>ReplicationConfig</c>, copied every third
/// frame (20 Hz, a network's rate rather than the frame rate, so the in-between integration is
/// tested too). Each frame the mirror's drawn pose is compared with the owner's: the body visual's
/// transform, the on-foot hand (a proxy for pose and gait phase together) and the bike's crank.
/// Non-zero exit if any drifts past what one update interval explains. The car stage also flips the
/// NA6CE's headlights and soft top through the real key actions (#48): the mirror's must match the
/// owner's on every fresh frame, and both switches must have been seen on.
/// </para>
///
/// <para>
/// One process, no sockets: what is being checked is that the replicated state is COMPLETE and that
/// both sides derive the same picture from it. Transport is Godot's job and is not re-tested here.
/// </para>
/// </summary>
public partial class SyncProbe : Node
{
    public static bool Requested() => Array.IndexOf(OS.GetCmdlineUserArgs(), "--synccheck") >= 0;

    /// <summary>A network's update rate: time, not frames, or a 30 fps machine tests 10 Hz.</summary>
    private const double UpdateInterval = 0.05;
    private double _sinceUpdate = UpdateInterval;
    private static readonly Vector3 MirrorOffset = new(40f, 0f, 0f);
    // Fresh: the frame right after an update, when the mirror must match the owner outright — any
    // error there is state that is not replicated. Stale: the frames in between, where the mirror
    // runs on the last update and can only be as far off as one 50 ms interval plus a frame allows
    // (a plane rolling at 2.2 rad/s moves ~0.18 rad at 30 fps).
    private const float FreshErr = 0.02f, FreshCrank = 0.05f;
    private const float MaxBasisErr = 0.3f, MaxHandErr = 0.25f, MaxCrankErr = 0.25f;

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private FootPlayer? _owner, _mirror;
    private double _t, _wait;
    private int _stage = -1;
    private bool _done;

    // last frame's owner picture, which the mirror has just drawn its copy of
    private Transform3D? _ownerPose;
    private Transform3D? _ownerHand;
    private float? _ownerCrank;
    private RideKind _ownerKind;
    private float _ownerCadence;
    private double _lastDelta;

    private float _basisErr, _handErr, _crankErr, _freshBasis, _freshHand, _freshCrank;
    private int _samples, _freshSamples, _kindMismatch;
    private bool _copiedLastFrame;

    // the car's switches: the owner's last frame, fresh frames where the mirror's differed, and
    // which states the owner went through (1 top down, 2 lights on, 4 lights off again after that)
    private (bool Roof, bool Lights)? _ownerSwitches;
    private int _switchMismatch, _switchSeen, _switchPressed;
    private static readonly (double At, string Action)[] SwitchPresses =
        { (20.0, PlayerInput.RoofToggle), (20.5, PlayerInput.LightsToggle), (23.0, PlayerInput.LightsToggle) };
    private readonly System.Collections.Generic.Dictionary<string, (float Basis, float Hand, float Crank, int N, float Speed, int Poses)> _byStage = new();

    public SyncProbe(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
        // the owner's own body is only drawn in third person; the check needs it drawn
        GameSettings.Current.ThirdPerson = true;
        // after the owner (0), before the mirror (2): compare, snapshot, copy
        ProcessPriority = 1;
    }

    private static readonly string[] Stages = { "walk", "sprint", "jump", "slide", "stand", "bike", "brake", "car", "heli", "plane" };
    private static readonly double[] StageEnd = { 3, 5, 6.5, 8, 9.5, 15.5, 18.5, 25.5, 31.5, 38.5 };
    private double _stageStart;
    private int _ownerPoseKind;

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        _wait += delta;
        if (_wait > 120) { GD.Print("[synccheck] TIMEOUT"); Finish(2); return; }
        if (_owner != null) return;

        var (e, n) = SpawnPoint.ParseTarget();
        var at = _origin.ToWorld(e, n, 0);
        if (!_chunks.TryGetHeight(at, out float g)) return;   // collision follows the player, who asks for it

        _owner = new FootPlayer { Name = "Owner", Terrain = _chunks };
        AddChild(_owner);
        _owner.GlobalPosition = new Vector3(at.X, g + 1f, at.Z);

        _mirror = new FootPlayer { Name = "Mirror", ProcessPriority = 2 };
        _mirror.SetMultiplayerAuthority(2);   // not us: it takes the remote path, as another peer's copy would
        AddChild(_mirror);
        GD.Print($"[synccheck] owner at LV95 {e:F0}/{n:F0}, ground {g:F1} m; mirror is authority {_mirror.GetMultiplayerAuthority()}");
    }

    public override void _Process(double delta)
    {
        if (_done || _owner == null || _mirror == null) return;
        if (!_owner.IsOnFloor() && _stage < 0) return;   // settle first
        _t += delta;
        Drive();

        // 1. what the mirror drew last frame, against what the owner showed then
        // A pose switch or a new mount reaches the mirror one update later, as over a network;
        // those frames are the latency, not a desync, and are left out of the in-between error.
        bool transition = _t - _stageStart < 0.5 || (_ownerKind == RideKind.OnFoot && _mirror.PoseKind != _ownerPoseKind);
        if (_ownerPose is { } pose && _mirror.Visual is { } mv && _mirror.Ride == _ownerKind && (!transition || _copiedLastFrame))
        {
            float be = BasisErr(pose, mv.Transform);
            float he = _ownerHand is { } oh && _mirror.HandLocal is { } mh ? oh.Origin.DistanceTo(mh.Origin) : 0f;
            // The crank turns during a frame on both sides, each on its own dt, so the two may be one
            // frame of cadence apart (more across a hitch frame); only what is beyond that counts.
            float turnedOn = _ownerCadence / 60f * Mathf.Tau * (float)Math.Max(delta, _lastDelta);
            float ce = _ownerCrank is { } oc && mv is Avatar.Cyclist mc
                ? Math.Max(0f, Mathf.Abs(Mathf.AngleDifference(oc, mc.CrankAngle)) - turnedOn)
                : _ownerCrank is { } os && mv is Avatar.CarRig mr ? Mathf.Abs(os - mr.SteerAngle) : 0f;
            var name = Stages[Math.Max(0, _stage)];
            var s = _byStage.GetValueOrDefault(name);
            _byStage[name] = (Math.Max(s.Basis, be), Math.Max(s.Hand, he), Math.Max(s.Crank, ce), s.N + 1,
                Math.Max(s.Speed, _owner.GroundSpeed), s.Poses | (1 << _owner.PoseKind));
            _basisErr = Math.Max(_basisErr, be);
            _handErr = Math.Max(_handErr, he);
            _crankErr = Math.Max(_crankErr, ce);
            _samples++;
            if (_copiedLastFrame)
            {
                _freshBasis = Math.Max(_freshBasis, be);
                _freshHand = Math.Max(_freshHand, he);
                _freshCrank = Math.Max(_freshCrank, ce);
                _freshSamples++;
            }
        }
        else if (_ownerPose != null && _mirror.Ride != _ownerKind && !_copiedLastFrame) _kindMismatch++;
        if (_copiedLastFrame && _ownerSwitches is { } sw && _mirror.Visual is Avatar.CarRig mirrorCar
            && (mirrorCar.RoofOpen != sw.Roof || mirrorCar.Headlights != sw.Lights))
            _switchMismatch++;

        // 2. the owner's picture now
        _lastDelta = delta;
        _ownerKind = _owner.Ride;
        _ownerPoseKind = _owner.PoseKind;
        _ownerPose = _owner.Visual?.Transform;
        _ownerHand = _owner.HandLocal;
        _ownerCrank = _owner.Visual switch
        {
            Avatar.Cyclist c => c.CrankAngle,
            Avatar.CarRig r => r.SteerAngle,   // a car's moving part: the front wheels
            _ => null,
        };
        _ownerCadence = _owner.Visual is Avatar.Cyclist cc ? cc.CadenceRpm : 0f;
        _ownerSwitches = _owner.Visual is Avatar.CarRig oc2 ? (oc2.RoofOpen, oc2.Headlights) : null;
        if (_ownerSwitches is { } now)
            _switchSeen |= (now.Roof ? 1 : 0) | (now.Lights ? 2 : 0) | (!now.Lights && (_switchSeen & 2) != 0 ? 4 : 0);

        // 3. the network: exactly the replicated properties, at 20 Hz
        _sinceUpdate += delta;
        _copiedLastFrame = _sinceUpdate >= UpdateInterval;
        if (_copiedLastFrame) { _sinceUpdate = 0; Replicate(); }

        if (_t > StageEnd[^1]) End();
    }

    private void Replicate()
    {
        var sync = _owner!.GetNode<MultiplayerSynchronizer>("Sync");
        foreach (var path in sync.ReplicationConfig.GetProperties())
        {
            var prop = path.GetConcatenatedSubNames();
            var value = _owner.Get(prop);
            if (prop == "position") value = _owner.Position + MirrorOffset;
            _mirror!.Set(prop, value);
        }
    }

    private static float BasisErr(Transform3D a, Transform3D b) =>
        Math.Max(Math.Max((a.Basis.X - b.Basis.X).Length(), (a.Basis.Y - b.Basis.Y).Length()),
            Math.Max((a.Basis.Z - b.Basis.Z).Length(), (a.Origin - b.Origin).Length()));

    private void Drive()
    {
        int stage = Array.FindIndex(StageEnd, end => _t < end);
        if (stage < 0) stage = StageEnd.Length - 1;
        if (stage != _stage) { _stage = stage; _stageStart = _t; Enter(Stages[stage]); }

        // held inputs per stage
        Hold(PlayerInput.MoveForward, _stage <= 3 || Stages[_stage] == "heli");   // "stand" lets the slide run out
        Hold(PlayerInput.Sprint, Stages[_stage] is "sprint" or "jump" or "slide");
        Hold(PlayerInput.FlyUp, Stages[_stage] == "heli" && _t < 20);
        Hold(PlayerInput.MoveRight, Stages[_stage] == "plane" && (_t % 4) < 2);
        Hold(PlayerInput.MoveLeft, Stages[_stage] == "plane" && (_t % 4) >= 2);
        Hold(PlayerInput.Jump, Stages[_stage] == "jump" && _t < 5.15);
        Hold(PlayerInput.CrouchSlide, Stages[_stage] == "slide" && _t > 6.6);

        // the car's switches are presses, not holds: through the event path, as a key would be
        if (_switchPressed < SwitchPresses.Length && _t >= SwitchPresses[_switchPressed].At)
        {
            var action = SwitchPresses[_switchPressed++].Action;
            Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = true });
            Input.ParseInputEvent(new InputEventAction { Action = action, Pressed = false });
        }
    }

    private void Enter(string stage)
    {
        GD.Print($"[synccheck] t={_t:F1}s {stage}");
        switch (stage)
        {
            case "bike":
                Mount(RideKind.RoadBike);
                _owner!.RideControls = () => new RideInput(1f, 0f, Mathf.Sin((float)_t * 1.3f) * 0.7f, false);
                break;
            case "brake":
                _owner!.RideControls = () => new RideInput(0f, 1f, 0f, false);
                break;
            case "car":
                // a drift car sliding: throttle, a weaving wheel and handbrake stabs. The NA6CE,
                // which has both a soft top and pop-ups for the switch presses.
                Mount(RideKind.OnFoot);
                Mount(CarCatalog.All.First(c => c.Label == "NA6CE Roadster").Kind);
                _owner!.RideControls = () => new RideInput(1f, 0f, Mathf.Sin((float)_t * 1.1f) * 0.8f, false,
                    Handbrake: _t % 2.5 < 0.35);
                break;
            case "heli":
                _owner!.RideControls = null;
                Mount(RideKind.Helicopter);
                break;
            case "plane":
                Mount(RideKind.OnFoot);
                Mount(RideKind.Plane);
                _owner!.DebugLaunch(_owner.GlobalPosition + Vector3.Up * 300f, new Vector3(0, 0, -50));
                break;
        }
    }

    private void Mount(RideKind kind)
    {
        // the probe's own business is what others see, not whether a mount is allowed here
        bool ok = _owner!.SetRide(kind);
        if (!ok) { _owner.DebugLaunch(_owner.GlobalPosition, Vector3.Zero); ok = _owner.SetRide(kind); }
        GD.Print($"[synccheck]   mount {kind}: {(ok ? "ok" : "REFUSED")}");
    }

    private static void Hold(string action, bool on)
    {
        if (on) Input.ActionPress(action); else Input.ActionRelease(action);
    }

    private void End()
    {
        foreach (var (name, s) in _byStage)
            GD.Print($"[synccheck] {name,-6} {s.N,4} frames  pose {s.Basis:F3}  hand {s.Hand:F3} m  crank {s.Crank:F3} rad"
                + $"  (owner up to {s.Speed:F1} m/s, on-foot poses {(s.Poses & 1) != 0}/{(s.Poses & 2) != 0}/{(s.Poses & 4) != 0} stride/air/tucked)");
        GD.Print($"[synccheck] fresh (frame after an update, {_freshSamples} frames): pose {_freshBasis:F4} (< {FreshErr}), "
            + $"hand {_freshHand:F4} m (< {FreshErr}), crank {_freshCrank:F3} rad (< {FreshCrank})");
        GD.Print($"[synccheck] car switches: owner went top down {(_switchSeen & 1) != 0}, lights on {(_switchSeen & 2) != 0}, "
            + $"lights off again {(_switchSeen & 4) != 0}; fresh frames where the mirror differed: {_switchMismatch} (must be 0)");
        bool ok = _switchSeen == 7 && _switchMismatch == 0 && _samples > 200 && _basisErr < MaxBasisErr && _handErr < MaxHandErr && _crankErr < MaxCrankErr
            && _freshBasis < FreshErr && _freshHand < FreshErr && _freshCrank < FreshCrank
            && _byStage.ContainsKey("bike") && _byStage.ContainsKey("plane");
        GD.Print($"[synccheck] max pose {_basisErr:F3} (< {MaxBasisErr}), hand {_handErr:F3} m (< {MaxHandErr}), "
            + $"crank {_crankErr:F3} rad beyond a frame of cadence (< {MaxCrankErr}), {_samples} frames, {_kindMismatch} frames waiting on a ride change");
        GD.Print(ok ? "[synccheck] RESULT: ok" : "[synccheck] RESULT: FAILED");
        Finish(ok ? 0 : 1);
    }

    private void Finish(int code)
    {
        foreach (var a in new[] { PlayerInput.MoveForward, PlayerInput.Sprint, PlayerInput.FlyUp, PlayerInput.MoveRight,
                     PlayerInput.MoveLeft, PlayerInput.Jump, PlayerInput.CrouchSlide })
            Input.ActionRelease(a);
        _done = true;
        GetTree().Quit(code);
    }
}
