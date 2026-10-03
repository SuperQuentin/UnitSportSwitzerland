using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Vehicles;

namespace UnitSport.Player;

/// <summary>
/// <c>--exitcheck [password]</c>, offline or on a client of a loopback server (with the server's
/// <c>--admin-password</c>, so the vehicles it conjures are parked): getting out of a car and of
/// every truck and bus (#209), each twice — in the open, and with a wall either side of the door.
/// Walled in, the old exit tried "behind the door", which for a bus is inside it: the parked bus
/// was not in the physics yet, so the spot read clear, and the player was shoved onto its roof.
///
/// Fails when the player ends up on the vehicle, off the ground, or inside its box, or when a
/// truck's parked box is wider than its body (it used to be as wide as its mirrors). A bus's
/// driver (#162) gets up into its aisle instead: there it fails unless on the floor, under the roof,
/// then takes the wheel again and fails if the bus is lifted off the ground doing it (#323).
/// Read the <c>[exitcheck]</c> lines.
/// </summary>
public partial class ExitProbe : Node, Core.IOriginShiftAware
{
    public static bool Requested => CmdArgs.Has("--exitcheck");

    private static string? Password => CmdArgs.Value("--exitcheck", notFlag: true);

    private readonly System.Func<FootPlayer?> _local;
    private int _failed;
    /// <summary>The last patch used, and where the vehicle was got out of: world space, moved with the origin (offline, #185).</summary>
    private Vector3 _spot, _where;

    public void OnOriginShifted(Core.OriginShift shift)
    {
        _spot = shift.Point(_spot);
        _where = shift.Point(_where);
    }

    public ExitProbe(System.Func<FootPlayer?> local)
    {
        _local = local;
        Name = "ExitProbe";
    }

    public override void _Ready() => _ = Run();

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private static void Log(string what) => GD.Print($"[exitcheck] {what}");

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            // offline the world starts on the free camera: the player comes with the mode key
            if (me == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null) { Log("RESULT: FAIL no local player"); GetTree().Quit(1); return; }
        if (Password == "watch") { await Watch(me); return; }
        if (Password is { } pw && GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) is Net.ChatManager chat)
        {
            chat.Send($"/login {pw}");
            await Wait(1.5);
        }
        _spot = me.GlobalPosition;
        var kinds = new List<RideKind> { CarCatalog.All[0].Kind };
        foreach (var heavy in HeavyCatalog.All) kinds.Add(heavy.Kind);
        foreach (var kind in kinds)
            foreach (bool walled in new[] { false, true })
            {
                // each its own clear patch: online the last one's vehicle is still parked there
                if (FindSpot(me, _spot + new Vector3(30f, 0, 0)) is not { } clear) { Fail(Rideable.Create(kind)!.Label, "no clear spot"); continue; }
                _spot = clear;
                await Case(me, kind, walled, clear);
            }
        int exits = kinds.Count * 2;
        // up from the wheel of a city bus going along: it rolls on driverless, its driver aboard (#162)
        if (FindSpot(me, _spot + new Vector3(30f, 0, 0)) is { } road) { exits++; await Rolling(me, HeavyCatalog.All[2].Kind, road); }
        // E from outside a parked bus, both ways of the walkable boarding setting (#384)
        if (FindSpot(me, _spot + new Vector3(60f, 0, 0)) is { } outside) { exits++; await Outside(me, outside); }
        Log(_failed == 0 ? $"RESULT: ok, {exits} exits" : $"RESULT: FAIL {_failed} of {exits} exits");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    /// <summary>
    /// E from outside a parked city bus, both ways of "Get in buses and ships from outside" (#384):
    /// off, it does nothing (walk aboard instead); on (the default), it puts the player at the wheel.
    /// </summary>
    private async Task Outside(FootPlayer me, Vector3 at)
    {
        var kind = HeavyCatalog.All[2].Kind;
        var ride = Rideable.Create(kind)!;
        if (VehicleManager.Instance is not { } vehicles || me.Terrain?.Origin is not { } origin) { Fail("outside", "no vehicles"); return; }
        var state = new VehicleState(kind, origin.ToGlobal(at with { Y = Ground(me, at) }), 0f, Vector3.Zero, ride.MaxHealth,
            EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0);
        string? name = vehicles.Place(state, "veh_exit_outside");
        VehicleBody? bus = null;
        for (int i = 0; i < 100 && (bus = name == null ? null : vehicles.GetNodeOrNull<VehicleBody>(name)) is not { Posed: true }; i++) await Wait(0.1);
        if (bus == null) { Fail("outside", "the parked bus never came"); return; }
        // beside the front door, a little aft of it: ahead of it is its button, which E would press
        var spot = bus.ToGlobal(new Vector3(0, 0, ride.EntryPoint.Z + 1.1f)) + bus.GlobalTransform.Basis.X.Normalized() * (ride.ParkedBox.Size.X * 0.5f + 0.7f);
        var settings = GameSettings.Current;
        bool was = settings.BoardWalkableFromOutside;

        settings.BoardWalkableFromOutside = false;
        me.GlobalPosition = spot with { Y = Ground(me, spot) + 0.2f };
        me.Velocity = Vector3.Zero;
        await Wait(1.5);
        bool offered = VehicleReach.Find(me)?.Vehicle == bus;
        me.TryInteract();
        await Wait(1.5);
        if (offered || me.Ride != RideKind.OnFoot) Fail("outside", $"setting off: E from beside the door still {(offered ? "offers" : "takes")} the bus (ride {me.Ride})");
        else Log("ok   outside, setting off: E beside the bus's door does nothing; walk aboard and drive from inside");

        settings.BoardWalkableFromOutside = true;
        Log($"  outside: E would {VehicleReach.Find(me)?.Action ?? "do nothing"}; {me.GlobalPosition.DistanceTo(bus.ToGlobal(ride.EntryPoint)):F2} m from its entry, entry {ride.EntryPoint}");
        bool took = me.TryGetIn();
        for (int i = 0; i < 50 && me.Ride != kind; i++) await Wait(0.1);
        if (!took || me.Ride != kind || me.SeatIndex != 0) Fail("outside", $"setting on: E beside the door did not put the player at the wheel (ride {me.Ride})");
        else Log("ok   outside, setting on (the default): E beside the bus's door puts the player at the wheel");
        settings.BoardWalkableFromOutside = was;
    }

    /// <summary>
    /// The first open, level patch from <paramref name="from"/> on, 6 m steps east then a row
    /// north: nothing but the ground under any of 25 rays over a 20 m square (no house, tree or car),
    /// within a metre and a half of height.
    /// </summary>
    private Vector3? FindSpot(FootPlayer me, Vector3 from)
    {
        var space = me.GetWorld3D().DirectSpaceState;
        for (int row = 0; row < 20; row++)
            for (int col = 0; col < 40; col++)
            {
                var at = from + new Vector3(col * 6f, 0, -row * 25f);
                float low = float.MaxValue, high = float.MinValue;
                bool clear = true;
                for (int i = -2; i <= 2 && clear; i++)
                    for (int j = -2; j <= 2 && clear; j++)
                    {
                        var p = at + new Vector3(i * 5f, 0, j * 5f);
                        float g = Ground(me, p);
                        // anything solid, whatever this player collides with: aboard a bus it is the deck only
                        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(p with { Y = g + 30f }, p with { Y = g - 2f }, uint.MaxValue,
                            new Godot.Collections.Array<Rid> { me.GetRid() }));
                        clear = hit.Count > 0 && hit["position"].AsVector3().Y < g + 0.3f;
                        low = Mathf.Min(low, g);
                        high = Mathf.Max(high, g);
                    }
                if (clear && high - low < 1.5f && me.Terrain?.HasCollisionAt(at) == true) return at with { Y = Ground(me, at) };
            }
        return null;
    }

    private float Ground(FootPlayer me, Vector3 p) => me.Terrain != null && me.Terrain.TryGetHeight(p, out float g) ? g : p.Y;

    private async Task Case(FootPlayer me, RideKind kind, bool walled, Vector3 at)
    {
        var ride = Rideable.Create(kind)!;
        string name = $"{ride.Label}{(walled ? ", walled in" : "")}";
        me.GlobalPosition = at with { Y = Ground(me, at) + 1.5f };
        me.Rotation = Vector3.Zero;
        me.Velocity = Vector3.Zero;
        // standing still on the ground for half a second, not just touching it on the way down
        for (int i = 0, still = 0; i < 150 && still < 5; i++)
        {
            await Wait(0.1);
            still = me.IsOnFloor() && me.Velocity.Length() < 0.5f ? still + 1 : 0;
        }
        if (!me.SetRide(kind)) { Fail(name, $"SetRide refused (floor {me.IsOnFloor()}, indoors {me.Indoors}, speed {me.Velocity.Length():F1})"); return; }
        await Wait(1.5);

        // the body's half width: a truck's spec, a car's measured box
        float half = ride is Truck t ? t.Spec.Sections[0].Width * 0.5f : ride.ParkedBox.Size.X * 0.5f;
        var frame = me.GlobalTransform;
        var walls = new List<Node>();
        if (walled)
            foreach (float sx in new[] { 1f, -1f })
            {
                var wall = new StaticBody3D { Name = "ExitWall" };
                wall.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.8f, 3f, 4f) } });
                GetTree().Root.AddChild(wall);
                wall.GlobalTransform = frame * new Transform3D(Basis.Identity, new Vector3(sx * (half + 1f), 1.5f, ride.EntryPoint.Z));
                walls.Add(wall);
            }
        await Wait(0.2);
        _where = me.GlobalPosition;
        me.ExitVehicle();
        // online the server spawns the parked vehicle a round trip later (longer under load)
        VehicleBody? parked = null;
        for (int i = 0; i < 120 && parked == null; i++)
        {
            await Wait(0.1);
            foreach (var v in VehicleManager.Instance?.GetChildren().OfType<VehicleBody>() ?? Enumerable.Empty<VehicleBody>())
                if (v.Kind == kind && v.GlobalPosition.DistanceTo(_where) < 3f) parked = v;
        }
        await Wait(1.5);
        if (parked == null) { Fail(name, "no parked vehicle"); Clean(walls, null); return; }

        var feet = me.GlobalPosition;
        var local = parked.ToLocal(feet);
        var hull = parked.GetNodeOrNull<CollisionShape3D>("Hull");
        var size = (hull?.Shape as BoxShape3D)?.Size ?? Vector3.Zero;
        var centre = hull?.Position ?? Vector3.Zero;
        bool over = Mathf.Abs(local.X - centre.X) < size.X * 0.5f + 0.1f && Mathf.Abs(local.Z - centre.Z) < size.Z * 0.5f + 0.1f;
        float agl = feet.Y - Ground(me, feet);
        bool onTop = over && local.Y > 0.8f;
        string what = $"out at ({local.X:F2}, {local.Y:F2}, {local.Z:F2}) in its frame, {agl:F2} m over the ground; box {size.X:F2} x {size.Y:F2} x {size.Z:F2}";
        bool ok = !onTop && !over && agl < 0.8f;
        // a vehicle you can walk about in (#162): its driver gets up into the aisle, on its floor
        if (ride.Walkable)
        {
            float roof = centre.Y + size.Y * 0.5f;
            // on its floor: not under it, on the ground inside the bus, nor up on its roof
            bool aisle = over && local.Y > 0.15f && local.Y < roof - 1.5f && me.IsOnFloor();
            // or, the parked bus not here within the wait (a slow server), out by its door on the ground
            bool byDoor = !over && agl < 0.8f;
            ok = aisle || byDoor;
            onTop = over && !aisle && local.Y >= roof - 1.5f;
            what = (aisle ? "in the aisle: " : byDoor ? "the deck never came, out by the door: " : "") + what;
        }
        if (ride is Truck heavy && size.X > heavy.Spec.Sections[0].Width + 0.11f)
        {
            ok = false;
            what += $" WIDER than its {heavy.Spec.Sections[0].Width:F2} m body";
        }
        if (ok) Log($"ok   {name}: {what}");
        else Fail(name, (onTop ? "ON TOP: " : ride.Walkable ? "NOT IN THE AISLE: " : over ? "INSIDE: " : agl >= 0.8f ? "OFF THE GROUND: " : "") + what);
        if (ok && ride.Walkable && me.DeckOn != "" && await BackIn(me, name)) parked = null;   // driven again: it is the player's now
        Clean(walls, parked);
        await Wait(0.3);
    }

    /// <summary>
    /// <c>--exitcheck watch</c>, a second client: the boxes this peer gives the other player's
    /// trucks and buses, driven (the hull on its copy) and parked, against the body's width.
    /// </summary>
    private async Task Watch(FootPlayer me)
    {
        var seen = new HashSet<string>();
        int heavy = 0;
        void See(string key, RideKind kind, string what, Vector3 size)
        {
            if (HeavyCatalog.For(kind) is not { } spec || !seen.Add(key)) return;
            heavy++;
            float body = spec.Sections[0].Width;
            if (size.X > body + 0.11f) Fail(spec.Label, $"{what} box {size.X:F2} m wide, WIDER than its {body:F2} m body");
            else Log($"ok   {spec.Label}: {what} box {size.X:F2} x {size.Y:F2} x {size.Z:F2}");
        }
        for (double t = 0; t < 240; t += 0.25)
        {
            foreach (var p in GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>())
            {
                // keep up with it, 25 m off: far away its copy is relayed slowly, or not at all
                if (p != me && p.GlobalPosition.DistanceTo(me.GlobalPosition) > 60f)
                {
                    var near = p.GlobalPosition + new Vector3(0, 0, 25f);
                    me.GlobalPosition = near with { Y = Ground(me, near) + 1f };
                    me.Velocity = Vector3.Zero;
                }
                if (p != me && seen.Add($"{p.Name}~{p.Ride}"))
                    Log($"     player {p.Name} as {p.Ride}, {p.GlobalPosition.DistanceTo(me.GlobalPosition):F0} m off, visible {p.Visible}, hulls {string.Join(" ", p.GetChildren().OfType<CollisionShape3D>().Select(c => c.Name.ToString()))}");
                if (p != me && p.GetNodeOrNull<CollisionShape3D>("HullHigh") is { Shape: BoxShape3D high })
                    See($"{p.Name}:{p.Ride}", p.Ride, "driven (remote copy's upper hull)", high.Size);
            }
            foreach (var v in VehicleManager.Instance?.GetChildren().OfType<VehicleBody>() ?? Enumerable.Empty<VehicleBody>())
                if (v.GetNodeOrNull<CollisionShape3D>("Hull") is { Shape: BoxShape3D hull })
                    See(v.Name, v.Kind, "parked", hull.Size);
            await Wait(0.25);
        }
        if (heavy == 0) Fail("watch", "saw no truck or bus");
        Log(_failed == 0 ? $"RESULT: ok, {heavy} truck and bus boxes seen" : $"RESULT: FAIL {_failed} of {heavy} boxes");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    /// <summary>
    /// Drives a bus up to speed, gets up from the wheel: it rolls on driverless and the driver must
    /// stay aboard, carried, on its floor, the whole way (offline the origin shifts under them as it
    /// goes, #185: run with <c>--originstress 20</c>).
    /// </summary>
    private async Task Rolling(FootPlayer me, RideKind kind, Vector3 at)
    {
        string name = $"{Rideable.Create(kind)!.Label}, up from the wheel while it rolls";
        me.GlobalPosition = at with { Y = Ground(me, at) + 1.5f };
        me.Rotation = Vector3.Zero;
        me.Velocity = Vector3.Zero;
        for (int i = 0, still = 0; i < 150 && still < 5; i++)
        {
            await Wait(0.1);
            still = me.IsOnFloor() && me.Velocity.Length() < 0.5f ? still + 1 : 0;
        }
        if (!me.SetRide(kind)) { Fail(name, "SetRide refused"); return; }
        await Wait(1f);
        Input.ActionPress(PlayerInput.Throttle, 1f);
        for (int i = 0; i < 100 && me.GroundSpeed < 5f; i++) await Wait(0.1);
        Input.ActionRelease(PlayerInput.Throttle);
        float speed = me.GroundSpeed;
        me.ExitVehicle();
        float worst = 0f;
        string? lost = null;
        for (int i = 0; i < 60; i++)
        {
            await Wait(0.1);
            var bus = VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().Where(v => v.Kind == kind)
                .OrderBy(v => v.GlobalPosition.DistanceSquaredTo(me.GlobalPosition)).FirstOrDefault();
            if (bus == null) continue;
            var local = bus.ToLocal(me.GlobalPosition);
            if (i >= 20 && (me.DeckOn == "" || local.Y < 0.15f || local.Y > 1.5f || Mathf.Abs(local.X) > 1.4f))
            {
                lost ??= $"at {i * 0.1f:F1} s: ({local.X:F2}, {local.Y:F2}, {local.Z:F2}) in its frame, aboard '{me.DeckOn}'";
            }
            worst = Mathf.Max(worst, bus.Velocity.Length());
        }
        string what = $"got up at {speed * 3.6f:F0} km/h, the bus rolled at up to {worst * 3.6f:F0} km/h";
        if (lost == null) Log($"ok   {name}: {what}, aboard the whole way");
        else Fail(name, $"{what}; LOST {lost}");
    }

    /// <summary>
    /// Back at the wheel from the aisle (#323): the bus must stay on the ground, not be taken up and
    /// dropped. Every physics frame for two seconds, the driven body's height over the ground; then
    /// on foot again where it stands (the bus gone with it). False when it never took the wheel.
    /// </summary>
    private async Task<bool> BackIn(FootPlayer me, string name)
    {
        float aisle = me.GlobalPosition.Y - Ground(me, me.GlobalPosition);
        if (!me.TryGetIn()) { Fail(name, "back in: E at the wheel did nothing"); return false; }
        for (int i = 0; i < 300 && me.Ride == RideKind.OnFoot; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (me.Ride == RideKind.OnFoot) { Fail(name, "back in: never took the wheel"); return false; }
        float start = me.GlobalPosition.Y - Ground(me, me.GlobalPosition), high = float.MinValue;
        int at = 0;
        for (int frame = 0; frame < 120; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            float agl = me.GlobalPosition.Y - Ground(me, me.GlobalPosition);
            if (agl > high) { high = agl; at = frame; }
        }
        float settled = me.GlobalPosition.Y - Ground(me, me.GlobalPosition);
        string what = $"back in: {start:F2} m over the ground at once, {high:F2} m at frame {at}, {settled:F2} m settled (aisle {aisle:F2} m)";
        if (Mathf.Max(high, start) - settled > 0.25f) Fail(name, $"DROPPED {what}");
        else Log($"ok   {name}: {what}");
        // on foot where it stands; the next case conjures its own vehicle 30 m on
        me.Velocity = Vector3.Zero;
        for (int i = 0; i < 30 && !me.SetRide(RideKind.OnFoot); i++) await Wait(0.1);
        return true;
    }

    private void Fail(string name, string why)
    {
        _failed++;
        Log($"FAIL {name}: {why}");
    }

    private static void Clean(List<Node> walls, VehicleBody? parked)
    {
        foreach (var w in walls) w.QueueFree();
        // offline it can go; online it is the server's, and the next case is 40 m on anyway
        if (parked != null && parked.Multiplayer.MultiplayerPeer is null or OfflineMultiplayerPeer) parked.QueueFree();
    }
}
