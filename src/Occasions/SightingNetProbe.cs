using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions;

/// <summary>
/// <c>--sightingnet A|B</c> with <c>--connect</c> (driven by <c>tools/sightingnetcheck.sh</c>, #863):
/// a server running the <c>ufo</c> occasion plans an abduction, then a landing
/// (<c>--sighting abduction,landing</c>) on the flat fixture's pasture.
/// <list type="bullet">
/// <item>Both clients get the same plans (the script compares their <c>plan</c> lines), see a cow of
/// the herd lifted while the beam is on (the same cow: the script compares their <c>cow</c> lines)
/// and set down again after.</item>
/// <item>A boards the landed saucer (a parked UFO, E at its ramp) and flies it: B sees A riding a UFO
/// and the sighting's saucer marked taken.</item>
/// </list>
/// Windowed (<c>SHOTS=1</c>), B saves its view of the beam and the landing to <c>test_output/sightingnet_B_*.png</c>.
/// </summary>
public partial class SightingNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--sightingnet");

    public SightingNetProbe(ItemController items) : base(items, "sightingnet", "SN", "sightingnet_") { }
    public SightingNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150)) { await Finish(0); return; }
        // --checkspeed n (A only, tools/sightingnetcheck.sh): the scripted flights n times faster, x1 from just before
        // a beam or a touchdown until what the physics did is checked (#1007)
        if (!await SpeedUp("pw863")) { await Finish(0); return; }
        await Abduction();
        await Landing(Me!);
        await Finish(2.0);
    }

    private static UfoSightings? Sightings => UfoSightings.Instance;

    private async Task<Sighting?> PlanOf(SightingKind kind, double seconds)
    {
        Sighting? found = null;
        await Until(() =>
        {
            if (Sightings is not { } s) return false;
            foreach (var p in s.Plans) if (p.Kind == kind) { found = p; return true; }
            return false;
        }, seconds);
        if (found is { } f)
            GD.Print(FormattableString.Invariant($"{Log} plan {f.Kind} {f.Id} at {f.E:F1}/{f.N:F1} ground {f.Ground:F1} start {f.Start:F1} craft {f.Count}"));
        return found;
    }

    /// <summary>Waits for the sightings' clock to reach <paramref name="tau"/> s into <paramref name="s"/>.</summary>
    private Task<bool> At(Sighting s, float tau) => Until(() => UfoSightings.Now >= s.Start + tau, tau + 60);

    private async Task Abduction()
    {
        var plan = await PlanOf(SightingKind.Abduction, 150);
        Expect(plan != null, "an abduction was planned and sent");
        if (plan is not { } s) return;
        Expect(OccasionManager.Instance?.IsActive(OccasionIds.Ufo, OccasionFacets.Decorations) == true, "the server's ufo occasion runs here");

        // the cow's lift and fall at x1
        await At(s, SightingPath.AbductBeamOn - 2f);
        if (!await SpeedBack()) return;

        await At(s, SightingPath.AbductBeamOn + 1f);
        Expect(TractorBeams.Active(GetTree()).Count > 0, "the sighting's beam is in this peer's tractor beams");
        (long Key, float Lift)? best = null;
        await Until(() =>
        {
            if (Farming.Herds.Instance?.Lifted() is { } l && (best == null || l.Lift > best.Value.Lift)) best = l;
            return best is { Lift: > 6f };
        }, SightingPath.AbductBeamOff - SightingPath.AbductBeamOn - 2f);
        Expect(best is { Lift: > 6f }, FormattableString.Invariant($"a cow rose in the beam (highest {best?.Lift ?? 0f:F1} m)"));
        if (best is { } b) GD.Print($"{Log} cow {b.Key:X}");
        if (_role == "B" && CmdArgs.Has("--windowed-shots")) Shot("beam");

        await At(s, SightingPath.AbductLeave + 6f);
        Expect(Farming.Herds.Instance?.InAir == 0, "the cow was set down again after the beam");
        // the rest of its flight and the landing's approach fast again
        await SpeedUp("pw863");
    }

    private async Task Landing(FootPlayer me)
    {
        var plan = await PlanOf(SightingKind.Landing, 150);
        Expect(plan != null, "a landing was planned and sent");
        if (plan is not { } s || Sightings is not { } sightings) return;
        // touchdown, the parked saucer and the boarding at x1
        await At(s, SightingPath.LandTouch - 2f);
        if (!await SpeedBack()) return;
        await At(s, SightingPath.BoardFrom + 3f);
        string name = UfoSightings.ParkedName(s.Id);
        bool parked = await Until(() => Vehicles.VehicleManager.Instance?.GetNodeOrNull<Vehicles.VehicleBody>(name) != null, 15);
        Expect(parked, $"the landed saucer is a parked UFO here ({name})");
        if (_role == "B" && CmdArgs.Has("--windowed-shots")) Shot("landed");

        if (_role == "A")
        {
            if (Vehicles.VehicleManager.Instance?.GetNodeOrNull<Vehicles.VehicleBody>(name) is not { } ufo) return;
            var foot = ufo.GlobalTransform * ufo.Ride.EntryPoint;
            me.PlaceAt(foot + Vector3.Up * 0.2f, ufo.GlobalRotation.Y);
            await Seconds(1.0);
            bool boarded = false;
            for (int i = 0; i < 10 && !boarded; i++)
            {
                me.TryInteract();
                boarded = await Until(() => me.Ride == RideKind.Ufo, 2);
            }
            Expect(boarded, "A boarded the landed saucer with the interact action");
            Expect(await Until(() => sightings.IsTaken(s.Id), 10), "the authority marked the sighting's saucer taken");
            Say("boarded");
            Expect(await Heard("B", "seen", 30), "B saw A fly the saucer");
        }
        else
        {
            if (!await Heard("A", "boarded", 90)) { Fail("A never boarded"); return; }
            bool seen = await Until(() => Copy() is { Ride: RideKind.Ufo } && sightings.IsTaken(s.Id), 20);
            Expect(seen, "B sees A riding a UFO and the sighting's saucer taken");
            if (seen) Say("seen");
        }
    }

    private FootPlayer? Copy()
    {
        foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
            if (node is FootPlayer p && p != Me && !p.IsMultiplayerAuthority() && !p.Npc) return p;
        return null;
    }
}
