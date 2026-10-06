using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Vehicles;

/// <summary>
/// <c>godot --headless --path . -- --connect 127.0.0.1 --chunks fixture:parking --parkingnet A|B</c>
/// — waking a dormant car (#499) over the network, for two clients on one server.
///
/// <para>
/// Waking is authority and replicated state, so the root <c>CLAUDE.md</c> wants it checked on the
/// <b>remote</b> peer, not just the one that did it. <b>A</b> finds a dormant car, wakes it, and
/// waits for the real vehicle to appear under its deterministic name. <b>B</b> joins afterwards and
/// must see the same vehicle, under the same name, standing in the same bay — and must have stopped
/// drawing its own dormant copy of that slot, or the car would be there twice.
/// </para>
/// </summary>
public partial class ParkingNetProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly bool _wakes;
    private double _t;
    private int _phase;
    private VehicleSlot? _slot;

    public ParkingNetProbe(string mode, ChunkManager chunks, WorldOrigin origin)
    {
        Name = "ParkingNetProbe";
        _chunks = chunks;
        _origin = origin;
        _wakes = mode.StartsWith("A", StringComparison.OrdinalIgnoreCase);
    }

    public static string? Mode() => CmdArgs.Value("--parkingnet");

    private string Tag => _wakes ? "A" : "B";

    public override void _PhysicsProcess(double delta)
    {
        _t += delta;
        if (_t > 150) { Say("RESULT: FAILED (timeout)"); Finish(2); return; }
        if (DormantVehicles.Instance is not { } dormant) return;

        switch (_phase)
        {
            case 0:   // find a dormant car of the fixture lot
            {
                if (_t < 5) return;
                if (Find(dormant) is not { } slot)
                {
                    if (_t > 60) { Say("RESULT: FAILED (no dormant car in the lot)"); Finish(1); }
                    return;
                }
                _slot = slot;
                Say($"sees {(dormant.IsAwake(slot) ? "woken" : "dormant")} {slot.NodeName} at bay {slot.Ordinal}");
                _phase = 1;
                _t = 0;
                return;
            }
            case 1:
            {
                var slot = _slot!.Value;
                if (_wakes)
                {
                    // A asks once, then waits for the server to put the real vehicle there
                    if (_t < 1) return;
                    if (_t < 1.2) { dormant.Wake(slot); return; }
                }

                var live = VehicleManager.Instance?.GetNodeOrNull<VehicleBody>(slot.NodeName);
                if (live == null)
                {
                    // B simply waits: A's wake reaches it as a spawn plus the "woken" broadcast
                    if (_t > (_wakes ? 25 : 90))
                    {
                        Say($"RESULT: FAILED ({slot.NodeName} never arrived)");
                        Finish(1);
                    }
                    return;
                }

                float off = _origin.ToWorld(slot.E, slot.N, slot.Height).DistanceTo(live.GlobalPosition);
                bool gone = dormant.IsAwake(slot);
                Say($"{slot.NodeName} is a real {live.Kind} here, {off:F2} m from its bay, dormant copy gone: {gone}");

                bool ok = off < 3f && gone;
                Say(ok ? "RESULT: ok" : $"RESULT: FAILED (off {off:F2} m, dormant copy gone {gone})");
                Finish(ok ? 0 : 1);
                return;
            }
        }
    }

    /// <summary>
    /// The first car of the lot, by walking the bays the tiles serve: for A the first dormant one,
    /// for B the first one woken or not, which is the one A woke. B used to look for the first
    /// DORMANT car as well, which only found A's because B's fleet was drawn before its join
    /// snapshot arrived; since fleets follow the player (#552) B knows the car is awake from the
    /// start, as it should, and the first dormant car is the next bay.
    /// </summary>
    private VehicleSlot? Find(DormantVehicles dormant)
    {
        if (_chunks.Source is not { } source) return null;
        var loaded = new List<(TileId Id, int Stride)>();
        _chunks.ListTiles(loaded);

        foreach (var (id, _) in loaded)
        {
            var tile = source.LoadRoadsAsync(id).GetAwaiter().GetResult();
            if (tile is null || tile.Parking.Count == 0) continue;
            foreach (var bay in tile.Parking)
            {
                var at = _origin.ToWorld(id.MinE + bay.X, id.MaxN - bay.Z, bay.Y);
                if (dormant.Nearest(at, awakeToo: !_wakes) is { } s) return s;
            }
        }
        return null;
    }

    private void Say(string what) => GD.Print($"[parkingnet {Tag}] {what}");

    private void Finish(int code)
    {
        SetPhysicsProcess(false);
        GetTree().Quit(code);
    }
}
