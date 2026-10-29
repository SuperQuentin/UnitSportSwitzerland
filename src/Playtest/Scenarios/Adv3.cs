using Godot;
using UnitSport.Adventure;
using UnitSport.Adventure.Together;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Occasions;
using UnitSport.Occasions.Festivals;
using UnitSport.Player;

namespace UnitSport.Playtest.Scenarios;

/// <summary>
/// What the Adventure phase 3 scenarios (<c>adv3-*</c>, #1390) share: the launch flags that boot the adventure and the
/// check-only festival (the festivals are held at the fixture's start under <c>--festival test</c>), the festival's date set
/// from the scenario (<see cref="OccasionManager.OverrideDate"/>, as the festival checks do), standing the player at a spot
/// of LV95, the stand-in party member and what a scenario gives and takes back. Every progress file goes to
/// <c>test_output/playtest/adv3</c>, never the player's saves.
/// </summary>
internal static class Adv3
{
    public const string Category = "Adventure phase 3";

    /// <summary>The systems of a fixture playtest, plus the terrain (the crevasses read its cover) and the adventure.</summary>
    private static readonly string AllSystems = string.Join(',',
        Systems.All.Where(s => s is not (Systems.Generated or Systems.Traffic or Systems.Trains or Systems.Dormant)));

    /// <summary>The launch flags every adv3 scenario needs (one pair each): the director relaunches the game with them when they are missing.</summary>
    public static readonly string[] Flags =
    [
        "--festival", "test",
        "--systems", AllSystems,
        "--pois", "fixture",
        "--givers", "fixture",
        "--crevasses", "on",
        "--adventure-dir", "res://test_output/playtest/adv3",
    ];

    /// <summary>The whole feature's code, minus its checks and probes: what a verdict on it is about.</summary>
    public static string[] Dir(params string[] dirs) =>
        dirs.SelectMany(d => d.EndsWith(".cs", StringComparison.Ordinal) ? new[] { d } : [d + "/**"])
            .Concat(["!**/*Check.cs", "!**/*NetProbe.cs", "!**/*Probe.cs"]).ToArray();

    public static void OnClear(PlaytestContext c, Action undo) => c.Own(new PlaytestUndo(undo));

    /// <summary>Scratch progress, the clock, and a calendar read at <paramref name="date"/> (null: today's).</summary>
    public static async Task Begin(PlaytestContext c, string time, DateOnly? date = null)
    {
        Bank.Scratch = true;
        await c.OnFoot();
        if (OccasionManager.Instance is not { } occasions || FestivalService.Instance == null || AdventureService.Instance == null)
            throw new InvalidOperationException("the adventure or the festival service is missing: this scenario needs the adv3 launch flags (the director relaunches with them)");
        occasions.OverrideDate(date);
        c.Command($"/time set {time}");
    }

    /// <summary>The festival <paramref name="id"/> on <paramref name="date"/>: waits for the server to resolve its site (the fixture's start).</summary>
    public static async Task<ResolvedSite> Festival(PlaytestContext c, string id, DateOnly date, string time)
    {
        await Begin(c, time, date);
        var svc = FestivalService.Instance!;
        for (int i = 0; i < 400; i++)
        {
            if (svc.RunningOf(id)?.Site is { } site) return site;
            await c.Wait(0.1);
        }
        throw new InvalidOperationException($"the festival '{id}' did not start on {date:yyyy-MM-dd}");
    }

    /// <summary>The Batzen a scenario hands the player to spend, as the festival's own award path would.</summary>
    public static int Batzen(string festival, int n) =>
        FestivalService.Instance!.Award(AdventureService.LocalPeer, festival, n, "playtest");

    /// <summary>Puts the player on foot at an LV95 point, facing another.</summary>
    public static async Task<FootPlayer> Stand(PlaytestContext c, (double E, double N) at, (double E, double N) look)
    {
        var me = await c.OnFoot();
        var origin = me.Origin ?? throw new InvalidOperationException("no world origin yet");
        var p = origin.ToWorld(at.E, at.N, 0);
        p = p with { Y = c.Ground(p) };
        var l = origin.ToWorld(look.E, look.N, 0);
        return await c.PutPlayer(p, PlaytestContext.Facing(p, l));
    }

    /// <summary>Puts items in the pack for this scenario only: the next scenario takes back what is left.</summary>
    public static void Give(PlaytestContext c, ItemId id, int count)
    {
        if (ItemController.Instance?.Inventory is not { } inv) throw new InvalidOperationException("no inventory");
        inv.Add(id, count);
        OnClear(c, () => inv.TakePlain(id, count));
    }

    /// <summary>A stand-in party member (peer 2, a body that stands still) as the offline checks use: paired with the local player.</summary>
    public static FootPlayer Partner(PlaytestContext c, FootPlayer me, Vector3 offset)
    {
        var partner = new FootPlayer { Name = "Partner", Npc = true, Terrain = me.Terrain, Origin = me.Origin };
        partner.WalkControls = () => (Vector3.Zero, false);
        c.Own(partner);
        partner.DebugLaunch(me.GlobalPosition + offset, Vector3.Zero);
        TogetherService.Instance!.Pair(partner, Pal);
        OnClear(c, () => TogetherService.Instance?.ServeUnlink(AdventureService.LocalPeer, "playtest"));
        return partner;
    }

    public const long Pal = 2;
}

/// <summary>A node that runs an action when it is freed: a scenario's way to take back what it changed (<see cref="Adv3.OnClear"/>).</summary>
internal sealed partial class PlaytestUndo(Action undo) : Node
{
    public override void _ExitTree()
    {
        try { undo(); }
        catch (Exception e) { GD.PushWarning($"[playtest] adv3 undo: {e.Message}"); }
    }
}
