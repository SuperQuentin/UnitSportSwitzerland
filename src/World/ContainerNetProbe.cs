using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.World;

/// <summary>
/// <c>--containernet A|B|C</c> on the <c>parking</c> fixture (<c>tools/containernetcheck.sh</c>, #689):
/// entity interest and object containers checked on the peers that did not do the thing.
/// <list type="bullet">
/// <item><b>A</b> wakes two dormant cars, gets into the first and parks it 8 m on (a client-owned
/// car, relayed by the server), drops an item, says where both stand, and waits for the script's go.
/// Then it stands 5 km away long enough for the server to put everything to sleep, comes back,
/// and wants each of them back exactly where it was, under its oid, and both bays still awake.</item>
/// <item><b>B</b> sees A's car and item at the lot, then from 5 km away has neither (and no vehicle
/// at all), and back at the lot has them again where they were.</item>
/// <item><b>C</b> joins a server restarted on the same containers (killed, not stopped: the crash
/// path), and wants A's car and item back where A left them, and both bays awake.</item>
/// </list>
/// A player 5 km off the fixture stands over nothing: the probe holds it in the air there.
/// </summary>
public partial class ContainerNetProbe : Node
{
    public static string? Role => CmdArgs.Value("--containernet");

    private readonly string _role;
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private double _t;
    private int _phase;
    private VehicleSlot? _slot1, _slot2;
    /// <summary>Where the probe keeps its player, LV95 and height; null = let it be.</summary>
    private (double E, double N, double Alt)? _hold;
    private long _carOid, _itemOid;
    private GlobalPos _carAt, _itemAt;
    private bool _claimed, _ok = true;

    /// <summary>How far the player goes to be out of everyone's interest and every container's reach, m.</summary>
    private const double Away = 5000;

    public ContainerNetProbe(string role, ChunkManager chunks, WorldOrigin origin)
    {
        Name = "ContainerNetProbe";
        _role = role.ToUpperInvariant();
        _chunks = chunks;
        _origin = origin;
        // after the player's own step: the hold has the last word on where it stands
        ProcessPhysicsPriority = 1000;
    }

    private static FootPlayer? Me(SceneTree tree) =>
        tree.GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>()
            .FirstOrDefault(p => p.IsMultiplayerAuthority() && FootPlayer.NetId(p.Name) is > 0);

    public override void _PhysicsProcess(double delta)
    {
        _t += delta;
        if (_t > 240) { Done($"FAILED (timeout in phase {_phase})"); return; }
        var me = Me(GetTree());
        if (me != null && _hold is { } h)
        {
            me.GlobalPosition = _origin.ToWorld(h.E, h.N, h.Alt);
            me.Velocity = Vector3.Zero;
        }
        if (me == null || DormantVehicles.Instance is not { } dormant || VehicleManager.Instance is not { } vehicles
            || DroppedItems.Instance is not { } items) return;

        if (_phase == 0)
        {
            if (_t < 4) return;
            var slots = dormant.Slots().OrderBy(s => s.NodeName, StringComparer.Ordinal).Take(2).ToList();
            if (slots.Count < 2) { if (_t > 60) Done("FAILED (no dormant cars in the lot)"); return; }
            (_slot1, _slot2) = (slots[0], slots[1]);
            Say($"slots {_slot1.Value.NodeName} and {_slot2.Value.NodeName}");
            _phase = 1;
            _t = 0;
            if (_role == "C") ExpectFromArgs();
            return;
        }
        switch (_role)
        {
            case "A": StepA(dormant, vehicles, items); break;
            case "B": StepB(dormant, vehicles, items); break;
            default: StepC(dormant, vehicles, items); break;
        }
    }

    private void StepA(DormantVehicles dormant, VehicleManager vehicles, DroppedItems items)
    {
        var s1 = _slot1!.Value;
        var s2 = _slot2!.Value;
        switch (_phase)
        {
            case 1:   // wake both
                if (_t < 1.2) { dormant.Wake(s1); dormant.Wake(s2); return; }
                if (vehicles.GetNodeOrNull<VehicleBody>(s1.NodeName) is not { } car1 || vehicles.GetNodeOrNull(s2.NodeName) == null)
                {
                    if (_t > 30) Done("FAILED (the slots never woke)");
                    else if (_t % 2 < 1.0 / 60) { dormant.Wake(s1); dormant.Wake(s2); }
                    return;
                }
                // in, and parked 8 m on: a vehicle this client simulates, the server relays
                if (!_claimed)
                {
                    _claimed = true;
                    vehicles.Claim(car1, state => vehicles.Park(state with
                    {
                        Position = new GlobalPos(s1.E + 8, s1.N, s1.Height + 0.3),
                        Velocity = Vector3.Zero, EngineOn = false,
                    }));
                }
                Next();
                return;
            case 2:   // the parked car, at rest
                if (Mine(vehicles) is not { } parked) { if (_t > 30) Done("FAILED (the parked car never came)"); return; }
                if (_t < 4) return;
                _carOid = parked.Oid;
                _carAt = parked.Global;
                items.Drop(new ItemStack(ItemId.WaterBottle, 1), _origin.ToWorld(s1.E, s1.N + 6, s1.Height + 0.5), Vector3.Zero, Vector3.Zero, Vector3.Zero);
                Next();
                return;
            case 3:   // the dropped item, at rest
                if (items.Items.FirstOrDefault(i => !i.Proxy && i.Owner == Multiplayer.GetUniqueId()) is not { Settled: true } dropped)
                {
                    if (_t > 30) Done("FAILED (the dropped item never settled)");
                    return;
                }
                if (_t < 3) return;
                _itemOid = dropped.Oid;
                _itemAt = dropped.Capture().Position;
                Say(FormattableString.Invariant($"PARKED car {_carOid} {_carAt.E:F3} {_carAt.N:F3} {_carAt.Alt:F3} item {_itemOid} {_itemAt.E:F3} {_itemAt.N:F3} {_itemAt.Alt:F3}"));
                Say("ready: waiting for the go");
                Next();
                return;
            case 4:   // the script says B is done
                if (CmdArgs.Value("--containernet-go") is not { } go || !System.IO.File.Exists(go)) return;
                Say("go: standing 5 km away for the containers");
                _hold = (s1.E + Away, s1.N, s1.Height + 2);
                Next();
                return;
            case 5:   // away: nothing near, so everything sleeps on the server
                if (_t < 20) return;
                Say("back at the lot");
                _hold = (s1.E, s1.N - 20, s1.Height + 2);
                Next();
                return;
            case 6:
                if (_t < 8) return;
                CheckBack(dormant, vehicles, items, "woken from its container");
                Done(_ok ? "ok" : "FAILED");
                return;
        }
    }

    private void StepB(DormantVehicles dormant, VehicleManager vehicles, DroppedItems items)
    {
        var s1 = _slot1!.Value;
        switch (_phase)
        {
            case 1:   // A's car and item reach B, relayed by the server
                if (Theirs(vehicles) is not { } car || items.Items.FirstOrDefault(i => !i.Proxy && i.Owner != Multiplayer.GetUniqueId()) is not { Settled: true } item)
                {
                    if (_t > 90) Done("FAILED (A's car and item never reached B)");
                    return;
                }
                if (_t < 3) return;
                _carOid = car.Oid;
                _carAt = car.Global;
                _itemOid = item.Oid;
                _itemAt = item.Capture().Position;
                Expect(dormant.IsAwake($"{s1.Owner}|{s1.Ordinal}"), "the bay A's car left is empty here too");
                Say($"sees A's car {car.Name} and item {item.Name}; going 5 km away");
                _hold = (s1.E + Away, s1.N, s1.Height + 2);
                Next();
                return;
            case 2:   // out of everyone's range: none of it exists here
                if (_t < 6) return;
                int cars = vehicles.GetChildren().OfType<VehicleBody>().Count(), drops = items.Items.Count(i => !i.Proxy);
                Expect(cars == 0 && drops == 0, $"5 km away: {cars} vehicles and {drops} items here (want none)");
                _hold = (s1.E, s1.N - 30, s1.Height + 2);
                Next();
                return;
            case 3:   // back: they come again, where they were
                if (_t < 6) return;
                var again = vehicles.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Oid == _carOid);
                var back = items.Items.FirstOrDefault(i => i.Oid == _itemOid);
                Expect(again != null && again.Global.DistanceTo(_carAt) < 0.05, $"back at the lot: A's car {(again == null ? "missing" : FormattableString.Invariant($"{again.Global.DistanceTo(_carAt):F3} m off"))}");
                Expect(back != null && back.Capture().Position.DistanceTo(_itemAt) < 0.05, $"back at the lot: A's item {(back == null ? "missing" : "there")}");
                Done(_ok ? "ok" : "FAILED");
                return;
        }
    }

    private void StepC(DormantVehicles dormant, VehicleManager vehicles, DroppedItems items)
    {
        // the server restarted: the containers woke as C arrived
        if (_t < 10) return;
        CheckBack(dormant, vehicles, items, "after the server restarted");
        Done(_ok ? "ok" : "FAILED");
    }

    private void CheckBack(DormantVehicles dormant, VehicleManager vehicles, DroppedItems items, string when)
    {
        var s1 = _slot1!.Value;
        var s2 = _slot2!.Value;
        var car = vehicles.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Oid == _carOid);
        var item = items.Items.FirstOrDefault(i => i.Oid == _itemOid);
        Expect(car != null && car.Global.DistanceTo(_carAt) < 0.05,
            $"{when}: car {_carOid} {(car == null ? "missing" : FormattableString.Invariant($"{car.Name}, {car.Global.DistanceTo(_carAt):F3} m from where it was parked"))}");
        Expect(item != null && item.Capture().Position.DistanceTo(_itemAt) < 0.05,
            $"{when}: item {_itemOid} {(item == null ? "missing" : FormattableString.Invariant($"{item.Name}, {item.Capture().Position.DistanceTo(_itemAt):F3} m from where it fell"))}");
        Expect(vehicles.GetChildren().OfType<VehicleBody>().Count(v => v.Oid == _carOid) == 1, $"{when}: the car exactly once");
        Expect(vehicles.GetNodeOrNull(s2.NodeName) != null, $"{when}: the second woken car {s2.NodeName} is there");
        Expect(dormant.IsAwake($"{s1.Owner}|{s1.Ordinal}") && dormant.IsAwake($"{s2.Owner}|{s2.Ordinal}"), $"{when}: both bays stay awake (no dormant copy, no second wake)");
    }

    /// <summary>C knows what A left from the script: <c>--containernet-expect carOid,E,N,Alt,itemOid,E,N,Alt</c>.</summary>
    private void ExpectFromArgs()
    {
        var p = (CmdArgs.Value("--containernet-expect") ?? "").Split(',');
        if (p.Length != 8) { Done("FAILED (no --containernet-expect)"); return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _carOid = long.Parse(p[0], inv);
        _carAt = new GlobalPos(double.Parse(p[1], inv), double.Parse(p[2], inv), double.Parse(p[3], inv));
        _itemOid = long.Parse(p[4], inv);
        _itemAt = new GlobalPos(double.Parse(p[5], inv), double.Parse(p[6], inv), double.Parse(p[7], inv));
    }

    private VehicleBody? Mine(VehicleManager vehicles) =>
        vehicles.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Owner == Multiplayer.GetUniqueId());

    private VehicleBody? Theirs(VehicleManager vehicles) =>
        vehicles.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Owner > 0 && v.Owner != Multiplayer.GetUniqueId());

    private void Next()
    {
        _phase++;
        _t = 0;
    }

    private void Expect(bool ok, string what)
    {
        Say($"{(ok ? "ok  " : "FAIL")} {what}");
        _ok &= ok;
    }

    private void Say(string what) => GD.Print($"[containernet {_role}] {what}");

    private void Done(string result)
    {
        Say($"RESULT: {result}");
        SetPhysicsProcess(false);
        GetTree().Quit(result == "ok" ? 0 : 1);
    }
}
