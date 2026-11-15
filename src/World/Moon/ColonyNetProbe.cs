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
            if (_role == "A") await WatchCrew(Me!);
            else await WatchEva(Me!);
            Say("done");
            await Heard(_role == "A" ? "B" : "A", "done", 300);
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
        bool crew = await Until(() => BotCopies().Count >= 4, 60);
        Expect(crew, "the server's crew here");
        if (!crew || Colony() is not { } colony) return;
        var wasIn = new HashSet<FootPlayer>();
        FootPlayer? eva = null;
        bool doorSeen = false;
        bool outside = await Until(() =>
        {
            doorSeen |= ColonyAirlocks.Of(Base.Id)?.AnyOuterOpen == true;
            foreach (var b in BotCopies())
            {
                bool air = ColonyAirlocks.Pressurised(b.GlobalPosition + colony.GlobalBasis.Y * 1.6f);
                if (air) wasIn.Add(b);
                // out of the air, off the stairs, on the plateau: through an airlock
                // (a remote copy is not simulated: its height over the plateau, not IsOnFloor)
                else if (wasIn.Contains(b) && Mathf.Abs(Over(colony, b)) < 0.5f && !ColonyAirlocks.NearChamber(b.GlobalPosition, 4f))
                {
                    eva = b;
                    return true;
                }
            }
            return false;
        }, 300);
        GD.Print(FormattableString.Invariant($"{Log} EVA: {(eva != null ? eva.Name + " outside, " + Over(colony, eva).ToString("F2") + " m over the plateau" : "nobody out")}, an outer door seen open {doorSeen}, cycles heard {ColonyDoors.CyclesHeard}"));
        Expect(outside, "a colonist goes out through an airlock and stands on the plateau");
        Expect(doorSeen, "this client saw the airlock's outer door open (the server's cycle)");
        // the inner door shut and the outer one open: at least the cycle out (the one in may come before this client is near)
        Expect(ColonyDoors.CyclesHeard >= 1, "the server's cycles reached this client");
    }
}
