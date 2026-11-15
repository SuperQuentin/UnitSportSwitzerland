using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;
using UnitSport.World.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// <c>--colonynetcheck A|B</c> with <c>--connect</c> (driven by <c>tools/colonynetcheck.sh</c>, #2000): a server hosting the
/// Moon builds Artemis Base round its players and runs its crew as bots on its own collision (the interiors system's
/// rooms, #2005), and its airlocks (<see cref="ColonyDoors"/>); everything is judged on the clients.
/// <list type="bullet">
/// <item>Both, admins, take the fly camera to Artemis Base (<c>/goto</c>) and step in on the plateau between two spines;
/// their own colony draws no figures (the server's bots are the crew).</item>
/// <item>A watches the crew 60 s: the colonists' copies stand on the modules' floors in the air, walk through the rooms and
/// tubes (one at least 3 m), and never pass between the air and the outside but at an airlock (through a wall).</item>
/// <item>B watches the colonist the server sends on an EVA (<c>--colonisteva</c> on the server): it goes out through an
/// airlock whose outer door B sees open (the cycle sent by the server), and stands outside on the plateau.</item>
/// </list>
/// </summary>
public partial class ColonyNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--colonynetcheck");

    public ColonyNetProbe(ItemController items) : base(items, "colonynet", "CN", "colonynet_") { }

    public ColonyNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private static readonly MoonColony Base = MoonColonies.Find("artemis")!;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ProcessPriority = 1000;
        if (!await Joined(150, () => true))
        {
            await Finish(0);
            return;
        }
        try
        {
            if (!await LogIn("test")) return;
            if (!await ToTheBase(Me!, _role == "A" ? 24 : 30)) return;
            if (_role == "A")
            {
                await WatchCrew(Me!);
                await AirlockByHand(Me!);
                // a rover once the EVA colonist has taken its own (B says so)
                await Heard("B", "eva", 420);
                await RoverByHand(Me!);
            }
            else
            {
                await WatchEva(Me!);
                Say("eva");
                await SeeAirlock();
                await SeeRover();
            }
            Say("done");
            await Heard(_role == "A" ? "B" : "A", "done", 300);
        }
        catch (Exception e)
        {
            // a throw ends the run as a failure, never as a pass with half the looks
            Fail($"the check threw: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            await Finish(3);
        }
    }

    private ClientWorld? World => GetParent() as ClientWorld;

    private WorldOrigin? Origin => Me?.Origin;

    private Node3D? CameraNode() => GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false) as Node3D;

    private List<FootPlayer> BotCopies() => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>()
        .Where(p => IsInstanceValid(p) && FootPlayer.NetId(p.Name) is long id && PlayerReplication.IsBotId(id) && p.NetGlobal.Body == BodyId.Moon).ToList();

    /// <summary>The base as this client built it, or null.</summary>
    private Node3D? Colony() => MoonSurface.Active?.Sites.TryGetValue(Base.Id, out var p) == true ? p.Node : null;

    /// <summary>A body's height over the plateau, metres (the colony's frame).</summary>
    private float Over(Node3D colony, FootPlayer b) => colony.ToLocal(b.GlobalPosition).Y;

    /// <summary>The fly camera to the base (<c>/goto</c>), down on the plateau <paramref name="diag"/> m out between two spines, then T.</summary>
    private async Task<bool> ToTheBase(FootPlayer me, float diag)
    {
        if (World is not { } world) { Fail("no client world"); return false; }
        Chat!.Send(FormattableString.Invariant($"/goto moon {Base.Lat:F6} {Base.Lon:F6} 0.3"));
        if (!await Until(() => Origin?.Body == BodyId.Moon, 15)) { Fail("the server's /goto never moved my camera to the Moon"); return false; }
        if (!await Until(() => BodyGround.Active != null && CameraNode() != null, 10)) { Fail("no Moon ground (--bodies)"); return false; }
        if (!await Until(() => Colony() != null, 30)) { Fail("the base was never built here"); return false; }
        var colony = Colony()!;
        var cam = CameraNode()!;
        // between the first and second spines: the heading's diagonal, on the plateau (y = 0 in the colony's frame)
        double h = (Base.Heading + 45) * Math.PI / 180;
        var local = new Vector3((float)Math.Sin(h) * diag, 3f, (float)-Math.Cos(h) * diag);
        cam.GlobalPosition = colony.ToGlobal(local);
        var ground = BodyGround.Active!;
        if (!await Until(() => ground.Covers(cam.GlobalPosition), 30)) { Fail("no near ground under the camera after 30 s"); return false; }
        world.ToggleMode();
        if (!await Until(() => Me is { } m && m.Origin?.Body == BodyId.Moon && m.IsOnFloor(), 30)) { Fail("never stood on the Moon"); return false; }
        GD.Print(FormattableString.Invariant($"{Log} on the plateau {Over(colony, me):F2} m over it, {colony.ToLocal(me.GlobalPosition).Length():F0} m from the hub"));
        return true;
    }

    // ---- A: the crew in the rooms ----------------------------------------------------------------------------------

    private async Task WatchCrew(FootPlayer me)
    {
        bool crew = await Until(() => BotCopies().Count >= 4, 60);
        Expect(crew, $"the server's crew here ({BotCopies().Count} colonists' copies)");
        if (!crew || Colony() is not { } colony) return;
        Expect(ColonyColonists.In(colony).All(c => !c.BotsOn), "this client runs no crew of its own");
        var inAir = new Dictionary<FootPlayer, bool>();
        var start = new Dictionary<FootPlayer, Vector3>();
        int samples = 0, low = 0, throughWalls = 0;
        float walked = 0;
        double until = Time.GetTicksMsec() / 1000.0 + 60;
        while (Time.GetTicksMsec() / 1000.0 < until)
        {
            foreach (var b in BotCopies())
            {
                var head = b.GlobalPosition + colony.GlobalBasis.Y * 1.6f;
                bool air = ColonyAirlocks.Pressurised(head);
                if (inAir.TryGetValue(b, out bool was) && was != air && !ColonyAirlocks.NearChamber(b.GlobalPosition, 3f))
                {
                    throughWalls++;
                    GD.Print(FormattableString.Invariant($"{Log} {b.Name} {(air ? "into" : "out of")} the air away from any airlock at {colony.ToLocal(b.GlobalPosition)}"));
                }
                inAir[b] = air;
                if (!air) continue;
                samples++;
                // inside, its feet on a floor: a module's at the sill (1.57 m), the hangar's slab, a biodome's garden
                if (Over(colony, b) < 0.5f)
                {
                    low++;
                    GD.Print(FormattableString.Invariant($"{Log} {b.Name} inside {Over(colony, b):F2} m over the plateau: under a floor"));
                }
                if (!start.TryGetValue(b, out var s)) start[b] = s = b.GlobalPosition;
                walked = Mathf.Max(walked, ((b.GlobalPosition - s) with { Y = 0 }).Length());
            }
            await Seconds(0.2);
        }
        GD.Print(FormattableString.Invariant($"{Log} crew: {inAir.Count} colonists seen, {samples} looks inside, {low} under a floor, {throughWalls} through a wall, walked {walked:F1} m"));
        Expect(samples > 50, "colonists inside the base's air");
        Expect(low == 0, "every colonist inside stands on a floor");
        Expect(throughWalls == 0, "no colonist passes a wall (in or out of the air only at an airlock)");
        Expect(walked >= 3, FormattableString.Invariant($"a colonist walks through the rooms ({walked:F1} m)"));
    }

    // ---- B: the EVA through an airlock -----------------------------------------------------------------------------

    private async Task WatchEva(FootPlayer me)
    {
        // the crew's copies come as the server's interest reaches this client: up to two minutes
        bool crew = await Until(() => BotCopies().Count >= 3, 120);
        Expect(crew, $"the server's crew here ({BotCopies().Count} colonists' copies)");
        // the EVA is watched whatever the count (A takes a rover only once this says the EVA is done)
        if (Colony() is not { } colony) return;
        // by name: a copy is freed when the server takes its bot away (asleep, out of reach)
        var wasIn = new HashSet<string>();
        string? eva = null;
        float evaOver = 0;
        bool doorSeen = false, rode = false;
        bool outside = await Until(() =>
        {
            doorSeen |= ColonyAirlocks.Of(Base.Id)?.AnyOuterOpen == true;
            foreach (var b in BotCopies())
            {
                // the EVA colonist drives the depot's open rover out (the server parks them, #2000)
                if (b.Ride == MoonRovers.Open.Kind) rode = true;
                bool air = ColonyAirlocks.Pressurised(b.GlobalPosition + colony.GlobalBasis.Y * 1.6f);
                string name = b.Name;
                if (air) wasIn.Add(name);
                // out of the air, off the stairs, on the plateau: through an airlock
                // (a remote copy is not simulated: its height over the plateau, not IsOnFloor)
                else if (eva == null && wasIn.Contains(name) && Mathf.Abs(Over(colony, b)) < 0.5f && !ColonyAirlocks.NearChamber(b.GlobalPosition, 4f))
                {
                    eva = name;
                    evaOver = Over(colony, b);
                }
            }
            return eva != null && rode;
        }, 420);
        GD.Print(FormattableString.Invariant($"{Log} EVA: {(eva != null ? eva + " outside, " + evaOver.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " m over the plateau" : "nobody out")}, an outer door seen open {doorSeen}, cycles heard {ColonyDoors.CyclesHeard}"));
        Expect(eva != null, "a colonist goes out through an airlock and stands on the plateau");
        Expect(rode, "the EVA colonist drives a depot rover (parked by the server)");
        Expect(doorSeen, "this client saw the airlock's outer door open (the server's cycle)");
        // the inner door shut and the outer one open: at least the cycle out (the one in may come before this client is near)
        Expect(ColonyDoors.CyclesHeard >= 1, "the server's cycles reached this client");
    }

    // ---- A: my own E, at an airlock and at a rover -----------------------------------------------------------------

    /// <summary>A, outside an airlock whose outer door is shut, presses E (<see cref="FootPlayer.TryInteract"/>): the door opens.</summary>
    private async Task AirlockByHand(FootPlayer me)
    {
        if (ColonyAirlocks.Of(Base.Id) is not { } set) { Fail("no airlocks here"); return; }
        // a spine's airlock (the hangar's is the last) with its outer door shut and no colonist on its stairs
        int index = -1;
        for (int i = 0; i < set.Count - 1 && index < 0; i++)
            if (!set.OuterOpen(i) && !BotCopies().Any(b => b.GlobalPosition.DistanceTo(set.OutsideOf(i).At) < 4)) index = i;
        if (index < 0) { Fail("no shut airlock free"); return; }
        var (at, into) = set.OutsideOf(index);
        me.GlobalPosition = at + Vector3.Up * 0.5f;
        me.Velocity = Vector3.Zero;
        me.LookYaw = Mathf.Atan2(-into.X, -into.Z);
        await Seconds(1.5);
        bool used = me.TryInteract();
        Expect(used, $"my E at airlock {index}'s outer door is taken");
        bool opened = await Until(() => set.OuterOpen(index), 15);
        Expect(opened, $"airlock {index}'s outer door opens for my E (the server's cycle)");
        Say($"airlock {index}");
        await Seconds(1);
    }

    /// <summary>A, beside a free depot rover, presses E: it rides it, and drives it 4 s.</summary>
    private async Task RoverByHand(FootPlayer me)
    {
        VehicleBody? Free() => Enumerable.Range(0, 2)
            .Select(i => VehicleManager.Instance?.GetNodeOrNull<VehicleBody>($"{MoonRoverDepot.Prefix}{Base.Id}_{i}"))
            .FirstOrDefault(v => v != null && IsInstanceValid(v) && VehicleManager.Instance!.Enterable(v));
        bool any = await Until(() => Free() != null, 120);
        Expect(any, "a free open rover of the depot here (parked by the server)");
        if (!any) return;
        var rover = Free()!;
        var side = rover.GlobalBasis.X.Normalized();
        me.GlobalPosition = rover.GlobalPosition + side * 2.0f + Vector3.Up * 0.5f;
        me.Velocity = Vector3.Zero;
        var to = rover.GlobalPosition - me.GlobalPosition;
        me.LookYaw = Mathf.Atan2(-to.X, -to.Z);
        await Seconds(1.5);
        me.TryInteract();
        bool riding = await Until(() => me.Ride == MoonRovers.Open.Kind, 10);
        Expect(riding, $"my E at the rover: riding it (ride {me.Ride})");
        if (!riding) return;
        Say("rover");
        var start = me.GlobalPosition;
        me.RideControls = () => new RideInput(1f, 0f, 0f, false);
        await Seconds(4);
        me.RideControls = () => new RideInput(0f, 1f, 0f, false);
        GD.Print(FormattableString.Invariant($"{Log} drove the rover {me.GlobalPosition.DistanceTo(start):F1} m"));
        await Seconds(2);
    }

    // ---- B: A's E seen here ----------------------------------------------------------------------------------------

    private FootPlayer? OtherCopy() => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>()
        .FirstOrDefault(p => IsInstanceValid(p) && !p.Npc && !p.IsMultiplayerAuthority() && FootPlayer.NetId(p.Name) is > 0);

    private async Task SeeAirlock()
    {
        var line = await HeardStarting("A", "airlock ", 240);
        if (line == null) { Fail("A never worked an airlock"); return; }
        int index = int.Parse(line[(line.LastIndexOf(' ') + 1)..]);
        bool open = await Until(() => ColonyAirlocks.Of(Base.Id)?.OuterOpen(index) == true, 15);
        Expect(open, $"A's E at airlock {index}: its outer door open here too");
    }

    private async Task SeeRover()
    {
        if (!await Heard("A", "rover", 240)) { Fail("A never took a rover"); return; }
        bool rides = await Until(() => OtherCopy() is { } a && a.Ride == MoonRovers.Open.Kind, 15);
        Expect(rides, $"A's copy rides the open rover (sees {OtherCopy()?.Ride})");
        if (!rides) return;
        var a = OtherCopy()!;
        var start = a.GlobalPosition;
        bool moved = await Until(() => IsInstanceValid(a) && a.GlobalPosition.DistanceTo(start) > 3f, 15);
        GD.Print(FormattableString.Invariant($"{Log} A's rover moved {(IsInstanceValid(a) ? a.GlobalPosition.DistanceTo(start) : 0):F1} m here"));
        Expect(moved, "A's rover drives here");
    }
}
