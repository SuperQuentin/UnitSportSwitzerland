using Godot;
using UnitSport.Core;

namespace UnitSport.World;

/// <summary>
/// <c>--trafficcheck --mover walker</c>'s own part (#1033): every 5 s the walkers' crossings (onto a lit crossing, how many on a
/// red), waits (at a red kerb, on a two-stage crossing's island, at a zebra), zebras crossed and the cars that stood for them;
/// with <c>--at</c> walkers are spawned about that point too (<c>--walkers-near N</c> a second, 2 by default), so the junction under
/// test sees them. Fails if a walker ever stepped onto a lit crossing at red.
/// </summary>
public partial class TrafficProbe
{
    /// <summary>Car-seconds stood at a permissive turn for a walker on the crossing (#1046 with #1033).</summary>
    private double _carsForWalkers;

    private bool WalkersOn => _kind == MoverKind.Walker || GameSettings.Current.TrafficWalkers > 0;

    private readonly double _walkersNear = CmdArgs.Double("--walkers-near") ?? 2;

    partial void WalkersTick(double delta)
    {
        if (!WalkersOn) return;
        _carsForWalkers += _traffic.CarsWaitingForWalkers() * delta;
        // walkers where the junction under test is: a few a second within 60 m of --at, while there are fewer than wanted
        if (_stay && _placed && _kind == MoverKind.Walker && _walkersNear > 0 && (int)(_t * _walkersNear) != (int)((_t - delta) * _walkersNear)
            && _traffic.CountOf(MoverKind.Walker) < GameSettings.Current.TrafficWalkers)
            _traffic.SpawnWalkerNear(_spot, 5f, 60f);
        if ((int)(_t / 5) != (int)((_t - delta) / 5))
            GD.Print($"[trafficcheck] t={_t:F0}s walkers: {WalkerCounts()}");
    }

    private string WalkerCounts()
    {
        var (holding, standing) = _traffic.WalkersStill();
        return $"{_traffic.CountOf(MoverKind.Walker)} live ({holding} waiting, {standing} standing), onto lit crossings {_traffic.WalkerLitStarts} "
            + $"(on red {_traffic.WalkerRedStarts}, on the clearance amber {_traffic.WalkerAmberStarts}), waits at a red kerb {_traffic.WalkerLitWaits}, on an island {_traffic.WalkerIslandWaits}, "
            + $"at a zebra {_traffic.WalkerZebraWaits}; zebras crossed {_traffic.WalkerZebraCrossings}, cars stood for them {_traffic.ZebraCarSeconds:F0} s; "
            + $"unmarked crossed {_traffic.WalkerGapCrossings}, waits for a gap {_traffic.WalkerGapWaits}; "
            + $"cars stood at a turn for walkers {_carsForWalkers:F0} s";
    }

    partial void WalkersReport(ref string? failure)
    {
        if (!WalkersOn) return;
        GD.Print($"[trafficcheck] walkers: {WalkerCounts()}");
        if (failure is null && _traffic.WalkerRedStarts > 0)
            failure = $"{_traffic.WalkerRedStarts} walkers stepped onto a lit crossing at red";
    }
}
