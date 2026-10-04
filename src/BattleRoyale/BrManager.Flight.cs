using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.BattleRoyale;

/// <summary>
/// The cargo plane on a client (#207). Every client flies its own copy along <see cref="BrFlight"/>
/// from the shared clock. An entrant boards at GO: the body is held in the hold
/// (<see cref="FootPlayer.Carrier"/>) and hidden, and a chase camera circles the plane with the look.
/// E jumps once the doors are open over the zone's first circle, into a wingsuit at the ramp with the plane's
/// speed (Space in the air then opens the parachute, as on any base jump). When the doors close,
/// whoever is still aboard is pushed out. The server records each jump so every peer shows the body again.
/// No "ground too near" push: right after the jump across the country, the ground a client holds under
/// the plane is a placeholder until the real tiles stream in; the server's altitude keeps it clear.
/// </summary>
public partial class BrManager
{
    private BrPlane? _plane;
    private Camera3D? _planeCam;
    private bool _aboard;
    private readonly HashSet<long> _hidden = new();

    /// <summary>The local player is in the plane's hold.</summary>
    public bool Aboard => _aboard;

    /// <summary>Wingsuit exit speed cap (m/s): the plane's cruise, or less on a fast test pace.</summary>
    private const float ExitSpeed = 55f;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Board()
    {
        if (_server || LocalPlayer() is not { } me || Origin == null) return;
        EnterMatch(me);
        if (_state.Flight is not { } flight) return;
        _aboard = true;
        me.Carrier = Hold;
        var frame = PlaneFrame(flight, ClockSync.ServerNow);
        me.LookYaw = frame.Yaw;
        me.LookPitch = -0.3f;
        GD.Print(FormattableString.Invariant($"[br] aboard the plane to {_state.AreaName}, doors open in {flight.OpensAt - ClockSync.ServerNow:F0} s"));
    }

    /// <summary>The plane at a server time: world position, yaw (nose along the line), velocity.</summary>
    public (Vector3 At, float Yaw, Vector3 Velocity) PlaneFrame(BrFlight flight, double now)
    {
        var z = flight.At(now);
        var at = Origin!.ToWorld(_state.AreaE + z.X, _state.AreaN + z.Y, flight.Altitude);
        float yaw = Mathf.Atan2(-flight.Dir.X, flight.Dir.Y);
        var velocity = now < flight.GoneAt ? new Vector3(flight.Dir.X, 0, -flight.Dir.Y) * flight.Speed : Vector3.Zero;
        return (at, yaw, velocity);
    }

    /// <summary>Where the hold keeps the body: inside the fuselage.</summary>
    private (Vector3 At, float Yaw, Vector3 Velocity)? Hold()
    {
        if (!_aboard || _state.Flight is not { } flight || Origin == null) return null;
        var f = PlaneFrame(flight, ClockSync.ServerNow);
        return (f.At + Vector3.Down * 1.2f, f.Yaw, f.Velocity);
    }

    /// <summary>
    /// E in the hold: out of the ramp into a wingsuit. Refused (with the time to wait) before the doors
    /// open; <paramref name="pushed"/> when the doors close.
    /// </summary>
    public bool JumpOut(bool pushed = false)
    {
        if (!_aboard || LocalPlayer() is not { } me || _state.Flight is not { } flight) return false;
        double now = ClockSync.ServerNow;
        if (!pushed && !flight.DoorsOpen(now))
        {
            me.Announce(now < flight.OpensAt ? $"DOORS OPEN IN {flight.OpensAt - now:F0} S" : "DOORS CLOSED", false);
            return false;
        }
        var f = PlaneFrame(flight, now);
        var exit = f.At + new Basis(Vector3.Up, f.Yaw) * BrPlane.Ramp + Vector3.Down * 2f;
        var forward = f.Velocity.LengthSquared() > 1f ? f.Velocity.Normalized() : new Basis(Vector3.Up, f.Yaw) * Vector3.Forward;
        var velocity = forward * Math.Min(flight.Speed, ExitSpeed) + Vector3.Down * 4f;
        _aboard = false;
        StopPlaneCamera();
        me.Leap(exit, velocity, RideKind.Wingsuit);
        Effect(Audio.SfxSynth.Whoosh, 2f, 0.8f);
        me.Camera.Current = true;
        RpcId(1, MethodName.Jump);
        var (e, n) = Origin!.ToLv95(exit);
        float clear = me.Terrain is { } t && t.TryGetHeight(me.GlobalPosition, out float g) ? me.GlobalPosition.Y - g : float.NaN;
        GD.Print(FormattableString.Invariant($"[br] {(pushed ? "pushed out" : "jumped")} at {e:F0}/{n:F0}, {exit.Y:F0} m ({clear:F0} m above the ground), {now - flight.ClosesAt:F1} s from the doors closing"));
        return true;
    }

    /// <summary>Every frame on a client: the plane, the hold, the bodies still aboard.</summary>
    private void FlightTick(FootPlayer? me)
    {
        var flight = _state.Flight;
        double now = ClockSync.ServerNow;

        // the plane, for everyone while it flies (spectators and free-roamers see it go over too)
        if (flight is { } fl && now < fl.GoneAt && Origin != null)
        {
            if (_plane == null) AddChild(_plane = new BrPlane());
            var f = PlaneFrame(fl, now);
            _plane.Fly(f.At, f.Yaw);
        }
        else if (_plane != null)
        {
            _plane.QueueFree();
            _plane = null;
        }

        // the hold
        if (_aboard)
        {
            if (flight is not { } fa || me == null || !InMatch || _state.Phase != BrPhase.Playing) LeaveHold(me);
            else if (now >= fa.ClosesAt) JumpOut(pushed: true);
            else PlaneCamera(me, fa, now);
        }

        // the others still aboard stay hidden in the hold (this client's own body: FootPlayer.Carried)
        foreach (var e in _state.Entrants)
        {
            if (e.Peer == Me || GetNodeOrNull<FootPlayer>("../Players/" + e.Peer) is not { } body) continue;
            body.Stowed = flight is { } fh && !e.Jumped && now < fh.ClosesAt + 3;
            if (body.Stowed) _hidden.Add(e.Peer);
            else _hidden.Remove(e.Peer);
        }
    }

    /// <summary>Behind and above the plane, turned round it by the look.</summary>
    private void PlaneCamera(FootPlayer me, BrFlight flight, double now)
    {
        if (_planeCam == null) AddChild(_planeCam = new Camera3D { Name = "PlaneCam", Fov = 70, Far = 30000, TopLevel = true });
        if (!_planeCam.Current) _planeCam.Current = true;
        var f = PlaneFrame(flight, now);
        float pitch = Mathf.Clamp(me.LookPitch, -1.1f, 0.35f);
        var offset = new Basis(Vector3.Up, me.LookYaw) * new Basis(Vector3.Right, pitch) * new Vector3(0, 0, 60f);
        _planeCam.GlobalPosition = f.At + Vector3.Up * 6f + offset;
        _planeCam.LookAt(f.At + Vector3.Up * 3f, Vector3.Up);
    }

    private void StopPlaneCamera()
    {
        if (_planeCam is { Current: true }) _planeCam.Current = false;
    }

    /// <summary>Out of the hold without a jump (the match ended or was called off): the body is let go where it is.</summary>
    private void LeaveHold(FootPlayer? me)
    {
        _aboard = false;
        StopPlaneCamera();
        if (me == null) return;
        me.Carrier = null;   // the body shows again on its next physics step
        me.Camera.Current = true;
    }

    /// <summary>Back to showing every body (the match is over).</summary>
    private void ShowHidden()
    {
        foreach (long peer in _hidden)
            if (GetNodeOrNull<FootPlayer>("../Players/" + peer) is { } body) body.Stowed = false;
        _hidden.Clear();
    }
}
