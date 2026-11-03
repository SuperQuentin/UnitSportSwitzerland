using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Construction;

namespace UnitSport.Player;

/// <summary>
/// Working a building site's crane (#618). A tower crane is climbed: E at the ladder's foot, up
/// the mast (forward / back, Jump lets go, as every ladder), and into the cab at the top, where
/// the controls are taken. Any crane, a self-erecting one too, is worked from the ground with the
/// radio remote in the box on its mast. Either way the server grants the crane to one player at a
/// time (<see cref="CraneService"/>), and E lets go of it again.
///
/// <para>
/// The levers are the excavator's actions in the ISO crane pattern: left stick (WASD) slews and
/// runs the trolley, right stick (↑ ↓) hoists; the cab's two joysticks in VR. <c>couple</c> (H,
/// D-pad ←, a poke on the cab's console in VR) hooks a pallet on or sets it down;
/// <c>camera_toggle</c> (V, R3) switches to the camera on the trolley. While carried, the body has
/// no physics of its own (<see cref="Carrier"/>).
/// </para>
/// </summary>
public partial class FootPlayer
{
    private enum CraneSeat : byte { None, Ladder, Cab, Remote }

    private CraneSeat _craneAt;
    private SiteCranes.Live? _craneLive;
    private string? _craneKey;
    private float _craneClimb;
    private CranePose _cranePose;
    private CraneLevers _craneSpeed;
    private bool _craneAsking, _hookView;

    /// <summary>A climber or an operator is this close to the ladder's foot or the remote's box to take it, m (flat).</summary>
    public const float CraneReach = 1.6f;
    private const float CraneClimbSpeed = 2.2f;

    /// <summary>For probes: the ladder's input (+1 up, −1 down) instead of the keys; null: the keys.</summary>
    public float? ForceCraneClimb { get; set; }

    /// <summary>Working a crane's controls, in its cab or on its remote: the sticks are its levers.</summary>
    public bool CraneWorking => _craneAt is CraneSeat.Cab or CraneSeat.Remote;
    /// <summary>In a crane's cab (the VR cab controls' context).</summary>
    public bool CraneInCab => _craneAt == CraneSeat.Cab;
    /// <summary>On a crane's ladder.</summary>
    public bool OnCraneLadder => _craneAt == CraneSeat.Ladder;
    /// <summary>How far up the ladder, m over the base.</summary>
    public float CraneClimb => _craneClimb;
    /// <summary>The crane being climbed or worked, or null.</summary>
    public string? CraneKey => _craneKey;
    /// <summary>Looking through the trolley's camera.</summary>
    public bool CraneHookView => _hookView;

    /// <summary>
    /// The crane part within reach on foot: a tower crane's ladder or any crane's remote box, the
    /// nearer, and what E would do there. Allocates nothing.
    /// </summary>
    private (SiteCranes.Live Crane, bool Ladder)? CraneInReach()
    {
        if (_ride != null || Indoors || _carried) return null;
        (SiteCranes.Live, bool)? best = null;
        float bestDist = CraneReach;
        var at = GlobalPosition;
        foreach (var live in SiteCranes.All.Values)
        {
            var foot = live.Foot;
            if (MathF.Abs(foot.X - at.X) > 8f || MathF.Abs(foot.Z - at.Z) > 8f || MathF.Abs(foot.Y - at.Y) > 4f) continue;
            if (live.HasCab)
            {
                float d = Flat(live.LadderFoot, at);
                if (d < bestDist) { bestDist = d; best = (live, true); }
            }
            float r = Flat(live.AtRemote.At, at);
            if (r < bestDist) { bestDist = r; best = (live, false); }
        }
        return best;
    }

    private static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>The prompt at a crane's foot, or in its cab, or null (#618).</summary>
    public string? CranePrompt() => CraneInteraction()?.Text;

    /// <summary>How much of a ladder's foot the outline takes, m.</summary>
    private const float LadderOutline = 3f;

    /// <summary>The prompt at a crane's foot, or on its ladder, and the ladder or the remote's box to outline (#1286); or null.</summary>
    public Items.Interaction? CraneInteraction()
    {
        if (_craneAt == CraneSeat.Ladder) return new(InputHints.Format("{move_forward} / {move_back}: climb · {jump}: let go"), default);
        if (CraneWorking || CraneInReach() is not { } near) return null;
        return near.Ladder
            ? new(InputHints.Prompt(PlayerInput.InteractMount, "Climb the crane"),
                Items.Outline.Box(null, near.Crane.LadderFrame(LadderOutline), new Vector3(0.56f, LadderOutline, 0.06f)))
            : new(InputHints.Prompt(PlayerInput.InteractMount, "Take the crane's remote"),
                Items.Outline.Box(null, near.Crane.RemoteFrame, new Vector3(0.36f, 0.44f, 0.36f)));
    }

    /// <summary>
    /// E / Y: at a crane's foot, onto its ladder or onto its remote; working one, let go of it.
    /// True when it did something.
    /// </summary>
    private bool TryCrane()
    {
        if (CraneWorking)
        {
            LeaveCraneControls();
            return true;
        }
        if (_craneAt != CraneSeat.None || CraneInReach() is not { } near) return false;
        if (near.Ladder)
        {
            _craneLive = near.Crane;
            _craneKey = near.Crane.Key;
            _craneAt = CraneSeat.Ladder;
            _craneClimb = 0f;
            ClimbStep = 0;
            ShowWhileCarried = true;
            CarriedPose = 2;
            Carrier = HoldOnCrane;
            GD.Print($"[crane] {Name} climbs {_craneKey}");
        }
        else TakeCraneControls(near.Crane, CraneSeat.Remote);
        return true;
    }

    /// <summary>Asks the server for the crane; granted, the controls are this player's from its current pose.</summary>
    private void TakeCraneControls(SiteCranes.Live crane, CraneSeat where)
    {
        if (_craneAsking || CraneService.Instance is not { } service) return;
        _craneAsking = true;
        service.RequestOperate(crane.Key, why =>
        {
            _craneAsking = false;
            if (why != null)
            {
                CraneService.Say(why);
                return;
            }
            if (!IsInstanceValid(crane.Owner) || _ride != null)
            {
                service.Release();
                return;
            }
            _craneLive = crane;
            _craneKey = crane.Key;
            _craneAt = where;
            _cranePose = crane.Last with { Loaded = false };
            _craneSpeed = default;
            ShowWhileCarried = true;
            CarriedPose = 0;
            Carrier = HoldOnCrane;
            GD.Print($"[crane] {Name} works {crane.Key} from the {(where == CraneSeat.Cab ? "cab" : "remote")}");
        });
    }

    /// <summary>Lets go of the crane: from the cab back onto the ladder's top, from the remote onto the ground.</summary>
    private void LeaveCraneControls()
    {
        CraneService.Instance?.Release();
        HookView(false);
        GD.Print($"[crane] {Name} lets go of {_craneKey}");
        if (_craneAt == CraneSeat.Cab && _craneLive != null)
        {
            _craneAt = CraneSeat.Ladder;
            _craneClimb = CranePlans.LadderTop(_craneLive.Data.Spot);
            CarriedPose = 2;
            return;
        }
        OffCrane(_craneLive is { } live && IsInstanceValid(live.Owner) ? live.AtRemote.At : GlobalPosition);
    }

    /// <summary>Off the crane altogether, standing at <paramref name="at"/>.</summary>
    private void OffCrane(Vector3 at)
    {
        if (CraneWorking) CraneService.Instance?.Release();
        HookView(false);
        _craneAt = CraneSeat.None;
        _craneLive = null;
        _craneKey = null;
        if (Carrier == HoldOnCrane) Release(at, Vector3.Zero);
    }

    /// <summary>The carrier while on a crane: up and down its ladder, in its cab, or at its remote.</summary>
    private (Vector3 At, float Yaw, Vector3 Velocity)? HoldOnCrane()
    {
        if (_craneLive is not { } live || !IsInstanceValid(live.Owner))
        {
            // the tile went: let go where we are
            OffCrane(GlobalPosition);
            return null;
        }
        float dt = (float)GetPhysicsProcessDeltaTime();
        if (CraneWorking && CraneService.Instance?.Mine != live.Key)
        {
            // the server let go of it for us (a lost answer, a reconnect): off the controls
            LeaveCraneControls();
            if (_craneAt == CraneSeat.None) return null;
        }
        switch (_craneAt)
        {
            case CraneSeat.Ladder:
            {
                float top = CranePlans.LadderTop(live.Data.Spot);
                float input = ForceCraneClimb ?? (PlayerInput.Held(PlayerInput.MoveForward) ? 1 : 0) - (PlayerInput.Held(PlayerInput.MoveBack) ? 1 : 0);
                if (ForceCraneClimb == null && Input.IsActionJustPressed(PlayerInput.Jump))
                {
                    // let go: a fall from wherever the hands were
                    var (from, _) = live.OnLadder(_craneClimb);
                    OffCrane(from + (live.LadderFoot - live.Foot).Normalized() * 0.3f);
                    return null;
                }
                _craneClimb = Mathf.Clamp(_craneClimb + input * CraneClimbSpeed * dt, 0f, top);
                ClimbStep = Mathf.FloorToInt(_craneClimb / 0.35f);
                if (_craneClimb <= 0f && input < 0)
                {
                    OffCrane(live.LadderFoot);
                    return null;
                }
                // at the cab's door: the controls, if nobody else has them
                if (_craneClimb >= top - 0.01f && input > 0 && !_craneAsking) TakeCraneControls(live, CraneSeat.Cab);
                var (at, yaw) = live.OnLadder(_craneClimb);
                return (at, yaw, Vector3.Up * input * CraneClimbSpeed);
            }
            case CraneSeat.Cab:
            {
                WorkCrane(live, dt);
                var (at, yaw) = live.InCab(_cranePose);
                return (at, yaw, Vector3.Zero);
            }
            case CraneSeat.Remote:
            {
                WorkCrane(live, dt);
                var (at, yaw) = live.AtRemote;
                return (at, yaw, Vector3.Zero);
            }
        }
        return null;
    }

    /// <summary>One physics step of the levers, the hook button and the view, sent to everyone through the service.</summary>
    private void WorkCrane(SiteCranes.Live live, float dt)
    {
        if (CraneService.Instance is not { } service) return;
        var hold = service.MyHold;
        var levers = Npc ? default : new CraneLevers(
            PlayerInput.Strength(PlayerInput.ArmSlewLeft) - PlayerInput.Strength(PlayerInput.ArmSlewRight),
            PlayerInput.Strength(PlayerInput.ArmStickOut) - PlayerInput.Strength(PlayerInput.ArmStickIn),
            PlayerInput.Strength(PlayerInput.ArmBoomUp) - PlayerInput.Strength(PlayerInput.ArmBoomDown));
        _cranePose = CraneControl.Step(live.Job, _cranePose, levers, ref _craneSpeed, HookFloor(live, hold), dt);
        service.Steer(_cranePose);
        if (Input.IsActionJustPressed(PlayerInput.Couple)) WorkHook();
        if (Input.IsActionJustPressed(PlayerInput.CameraToggle)) HookView(!_hookView);
    }

    /// <summary>
    /// The lowest the hook's origin may go now, tile-local: onto what is under the hook block, or
    /// with a pallet on, onto what is under the pallet — or while its slings are still slack, onto
    /// the pallet's top where it stands.
    /// </summary>
    private float HookFloor(SiteCranes.Live live, CraneService.Hold? hold)
    {
        float lift = live.Owner.GlobalPosition.Y;
        var hook = live.HookWorld(_cranePose);
        if (hold == null || hold.Carrying == 0)
            return live.FloorUnder(hook + Vector3.Down * (CraneControl.HookBottom - 0.05f)) - lift + CraneControl.HookBottom;
        if (!float.IsNaN(hold.Rest)) return hold.Rest + Pallets.Height + CraneControl.HookBottom;
        float under = CraneControl.HungAt(_cranePose.HookY, float.NaN) + lift;
        return live.FloorUnder(new Vector3(hook.X, under + 0.05f, hook.Z)) - lift + CraneControl.SlingHang;
    }

    /// <summary>What <c>couple</c> would do with the hook now: take a pallet (its id), set one down (where), or nothing.</summary>
    public (string? Take, Vector3? SetAt) HookAction()
    {
        if (!CraneWorking || _craneLive is not { } live || CraneService.Instance?.MyHold is not { } hold) return (null, null);
        var hook = live.HookWorld(_cranePose);
        if (hold.Carrying == 0)
        {
            foreach (var (id, pallet) in PalletNode.All)
                if (!pallet.Taken && pallet.IsVisibleInTree() && CraneControl.Hooks(hook, pallet.GlobalPosition)) return (id, null);
            return (null, null);
        }
        float lift = live.Owner.GlobalPosition.Y;
        float under = CraneControl.HungAt(_cranePose.HookY, hold.Rest) + lift;
        float surface = live.FloorUnder(new Vector3(hook.X, under + 0.05f, hook.Z));
        return CraneControl.SetsDown(under, surface) ? (null, new Vector3(hook.X, surface, hook.Z)) : (null, null);
    }

    /// <summary><c>couple</c>: hook the pallet under the hook on, or set the hook's pallet down on what is under it.</summary>
    private void WorkHook()
    {
        if (PalletService.Instance is not { } pallets || CraneService.Instance?.MyHold is not { } hold) return;
        var (take, setAt) = HookAction();
        if (take != null)
        {
            GD.Print($"[crane] {Name} hooks {take} on");
            pallets.RequestTake(take, _ => { });
        }
        else if (setAt is { } at && Pallets.LoadCarried(hold.Carrying) is { } load && Origin is { } origin)
        {
            GD.Print(FormattableString.Invariant($"[crane] {Name} sets a pallet down at {at.Y:F2}"));
            // its runners along the jib, as it hung
            pallets.RequestDrop(load, origin.ToGlobal(at), _cranePose.Yaw + PalletNode.CarriedYaw(false), _ => { });
        }
    }

    /// <summary>The trolley's camera on or off: the player's own camera is given back when off.</summary>
    private void HookView(bool on)
    {
        if (on == _hookView) return;
        _hookView = on;
        if (on && !XR.XrSession.Active && _craneLive is { } live && IsInstanceValid(live.Owner)) live.HookCamera().Current = true;
        else if (!on && Camera != null && IsInstanceValid(Camera)) Camera.Current = true;
        if (!on && _craneLive?.Camera is { } cam && IsInstanceValid(cam)) cam.Current = false;
    }
}
