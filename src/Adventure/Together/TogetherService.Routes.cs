using Godot;
using UnitSport.Core;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// Two-player routes (#1266 part 4): the leg-up (a linked friend crouched still offers a step; a jump beside it is a free
/// 2.6 m climb-jump, no stamina, allowed winded), and the rope team's seal (<c>SealKind.Roped</c>: a roped pair within
/// 8 m of the chest; the two-handle gate is the rewards' own seal). Offering a step is the only new state: the owner tells
/// the server on a change, the server tells the offerer's rope or hand partners.
/// </summary>
public partial class TogetherService
{
    /// <summary>A leg-up lifts this high, m.</summary>
    public const float LegUpHeight = 2.6f;
    /// <summary>The partner offering the step within this, m.</summary>
    public const float LegUpReach = 1.0f;

    // who offers a step now (every copy keeps what it was told)
    private readonly HashSet<long> _steps = new();

    partial void ReadyRoutes()
    {
        if (!Authority || Rewards.RewardService.Instance is not { } rewards) return;
        rewards.RopedAt = RopedAt;
    }

    /// <summary>Authority: a roped pair, one of them <paramref name="peer"/>, both adventuring and within <paramref name="reach"/> m of a point.</summary>
    public bool RopedAt(long peer, double e, double n, float reach)
    {
        if (Links.ModeOf(peer) != LinkMode.Rope || !_adventure.Active(peer) || !Within(peer, e, n, reach)) return false;
        long a = Links.LeaderOf(peer), b = Links.FollowerOf(peer);
        return a != 0 && _adventure.Active(a) && Within(a, e, n, reach) || b != 0 && _adventure.Active(b) && Within(b, e, n, reach);
    }

    private bool Within(long peer, double e, double n, float reach) =>
        PositionOf(peer) is { } p && (p.E - e) * (p.E - e) + (p.N - n) * (p.N - n) <= reach * reach;

    /// <summary>Client: the local player roped to a friend, both within <paramref name="reach"/> m of a point (a rope team's chest unseals).</summary>
    public bool RopedHere(double e, double n, float reach)
    {
        long self = Self;
        if (Links.ModeOf(self) != LinkMode.Rope || Me?.Invoke() is not { } me) return false;
        var g = me.Global;
        if ((g.E - e) * (g.E - e) + (g.N - n) * (g.N - n) > reach * reach) return false;
        return Near(Links.LeaderOf(self)) || Near(Links.FollowerOf(self));

        bool Near(long other) => other != 0 && Body(other)?.Global is { } o && (o.E - e) * (o.E - e) + (o.N - n) * (o.N - n) <= reach * reach;
    }

    /// <summary>Whether <paramref name="peer"/> offers a step now (checks).</summary>
    public bool OffersStep(long peer) => _steps.Contains(peer);

    /// <summary>An owned body crouched still while linked offers a step: told on a change only.</summary>
    private void Offer(long peer, bool on)
    {
        if (_steps.Contains(peer) == on) return;
        if (Authority) ServeOffer(peer, on);
        else if (peer == Self) RpcId(1, MethodName.RequestStep, on);
    }

    /// <summary>The offer of a step, checked (linked, adventuring) and told to the offerer's partners.</summary>
    public void ServeOffer(long peer, bool on)
    {
        if (!Authority) return;
        if (on && (!Links.IsLinked(peer) || !_adventure.Active(peer))) on = false;
        if (on) _steps.Add(peer);
        else if (!_steps.Remove(peer)) return;
        GD.Print($"[together] {Who(peer)} {(on ? "offers a step" : "stands up")}");
        foreach (long p in Links.Chain(peer))
            if (p != peer && Connected(p)) RpcId(p, MethodName.TellStep, peer, on);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestStep(bool on) { if (_server) ServeOffer(Multiplayer.GetRemoteSenderId(), on); }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TellStep(long peer, bool on)
    {
        if (on) _steps.Add(peer);
        else _steps.Remove(peer);
    }

    /// <summary>
    /// A body's jump off the floor: a leg-up when a link partner offers a step within <see cref="LegUpReach"/> m. True means
    /// the jump is the leg-up's (the body lifts <see cref="LegUpHeight"/> m, no stamina, winded or not).
    /// </summary>
    private bool LegUpFor(Owned o)
    {
        if (!o.Hooked || o.Body is not { } body) return false;
        foreach (long p in Links.Chain(o.Peer))
        {
            if (p == o.Peer || !_steps.Contains(p) || Body(p) is not { } b) continue;
            var d = b.GlobalPosition - body.GlobalPosition;
            if (d.X * d.X + d.Z * d.Z <= LegUpReach * LegUpReach && Mathf.Abs(d.Y) < 0.8f)
            {
                GD.Print($"[together] {Who(o.Peer)} gets a leg-up from {Who(p)}");
                return true;
            }
        }
        return false;
    }

    /// <summary>The owned body's crouch (keys for the own one, its foot actions for a check's or a bot's body).</summary>
    private static bool Crouching(FootPlayer body, bool own) =>
        own ? PlayerInput.Held(PlayerInput.CrouchSlide) : body.FootActions?.Invoke().Crouch == true;
}
