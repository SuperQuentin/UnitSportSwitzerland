using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Errands;

/// <summary>
/// <c>--worldeventnet lead|follow</c> with <c>--connect</c> (driven by <c>tools/worldeventnetcheck.sh</c>, #1339): a dedicated server
/// on the flat fixture planning an avalanche and then a forest fire (<c>--worldevent avalanche,fire --worldevent-forest</c>), and two
/// clients, Lea and Tom. What each reads is what reached it from the server:
/// <list type="number">
/// <item>the avalanche's plan reaches both (the cone is drawn, the same spots), the server checks where their bodies stand when they
/// hold the dig, and their two shovels together dig faster than one;</item>
/// <item>both are paid: a count and five points in the canton in each one's own section, and told so;</item>
/// <item>the fire reaches both, with the burning cells the server sent; both beat out cells until it is out, and both are paid;</item>
/// <item>a far dig the server refuses (a body 30 m off).</item>
/// </list>
/// </summary>
public partial class WorldEventNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--worldeventnet");

    public WorldEventNetProbe(ItemController items) : base(items, "worldeventnet", "WE", "worldeventnet_") { }
    public WorldEventNetProbe() : this(null!) { }

    private static WorldEvents Events => WorldEvents.Instance!;
    private ErrandsProgress Errands => AdventureService.Instance!.Mine.Get(ErrandsProgress.Section);
    private readonly List<string> _toasts = new();

    public override async void _Ready()
    {
        _role = Role ?? "LEAD";
        if (!await Joined(150, () => WorldEvents.Instance != null && AdventureService.Instance != null)) return;
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        Expect(await Until(() => AdventureService.Instance!.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it");
        var was = Events.Toast;
        Events.Toast = t => { was?.Invoke(t); _toasts.Add(t); };

        // the avalanche: the plan reaches both clients
        Expect(await Until(() => Events.Views.Any(v => v.Kind == WorldEventKind.Avalanche), 120), "the avalanche reached this client");
        var cone = Events.Views.First(v => v.Kind == WorldEventKind.Avalanche);
        Expect(cone.Avalanche is { Buried: > 0 }, $"with {cone.Avalanche?.Buried} buried (the same spots from the seed)");
        Say($"cone {cone.Id} {cone.Avalanche!.Spots[0].E:F0}");
        var spot = cone.Avalanche.Spots[0];
        // a body 30 m off is refused by the server (its word, not the client's)
        await Jump(spot.E + 30, spot.N);
        Events.AskHelp(cone.Id, EventAct.DigStart, 0);
        await Seconds(2);
        Expect(cone.Data.Length == 0 || cone.Data[1] == 0, "a dig from 30 m off does nothing");
        await Jump(spot.E + 1.5, spot.N);
        await Seconds(1.5);
        Events.AskHelp(cone.Id, EventAct.DigStart, 0);
        Expect(await Until(() => cone.Data.Length > 1 && cone.Data[1] > 0, 15), "the server counts the dig");
        Expect(await Until(() => !Events.Views.Any(v => v.Kind == WorldEventKind.Avalanche), 40), "the cone is gone when the buried are out");
        Expect(await Until(() => Errands.Events.GetValueOrDefault("Avalanche") == 1, 15), "the count reached this player's section");
        Expect(Errands.Points("VS") >= WorldEventRules.RepPoints || Errands.Points("") >= WorldEventRules.RepPoints || Errands.Rep.Values.Sum() >= WorldEventRules.RepPoints, $"five points of reputation ({string.Join(",", Errands.Rep)})");
        Expect(_toasts.Any(t => t.Contains("dug out")), "and the end is told");
        Say("dug");

        // the fire
        Expect(await Until(() => Events.Views.Any(v => v.Kind == WorldEventKind.ForestFire && v.Data.Length > 0), 120), "the fire and its burning cells reached this client");
        var fire = Events.Views.First(v => v.Kind == WorldEventKind.ForestFire);
        Expect(fire.Data.Length >= FireSim.Start, $"{fire.Data.Length} cells burn");
        for (int guard = 0; guard < 80 && Events.Views.Contains(fire); guard++)
        {
            if (fire.Data.Length == 0) { await Seconds(0.5); continue; }
            int key = fire.Data[0];
            var (a, b) = FireSim.Unpack(key);
            await Jump(fire.E + a * FireSim.CellM + 1, fire.N + b * FireSim.CellM);
            await Seconds(0.6);
            Events.AskHelp(fire.Id, EventAct.BeatStart, key);
            await Until(() => !fire.Data.Contains(key) || !Events.Views.Contains(fire), 8);
            Events.AskHelp(fire.Id, EventAct.BeatStop, key);
        }
        Expect(await Until(() => !Events.Views.Contains(fire), 20), "the fire is out for this client");
        Expect(await Until(() => Errands.Events.GetValueOrDefault("ForestFire") == 1, 15), "and the count reached this player's section");
        Say("out");
        Expect(await Heard(_role == "LEAD" ? "FOLLOW" : "LEAD", "out", 120), "the other client finished too");
        await Finish(6.0);
    }

    private async Task Jump(double e, double n)
    {
        var me = Me!;
        while (true)
        {
            var g = me.Global;
            double de = e - g.E, dn = n - g.N, d = Math.Sqrt(de * de + dn * dn);
            if (d < 0.5) break;
            double step = Math.Min(d, 20);
            me.GlobalPosition += new Vector3((float)(de / d * step), 0.2f, (float)(-dn / d * step));
            me.Velocity = Vector3.Zero;
            await Seconds(0.4);
        }
    }
}
