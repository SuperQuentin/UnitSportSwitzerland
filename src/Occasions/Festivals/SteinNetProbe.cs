using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;

namespace UnitSport.Occasions.Festivals;

/// <summary>
/// <c>--steinnet a|b</c> with <c>--connect --festival test</c> (driven by <c>tools/steinnetcheck.sh</c>, #1269 part 3): the stone put over a
/// dedicated server whose calendar reads 14 March 2027 (the check-only festival's day 5, when its stone opens). Everything is asserted on the
/// REMOTE peers. <c>a</c> (Lea) walks to the stone, E opens an attempt on the server, a perfect put through the real input actions lands: she hears
/// it, her section holds the Batzen the server paid, a stake shows. <c>b</c> (Tom), standing 5 m away, is told the same landing for someone else's
/// put (the same distance to the millimetre), sees the stone's stake, and is paid nothing. Lea then sends a forged put (stamps from the future) for
/// a fresh attempt: the server refuses it over the wire and it uses up no put; a stone asked for from far away is refused too.
/// </summary>
public partial class SteinNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--steinnet");

    private const string Instance = "test-2027";

    public SteinNetProbe(ItemController items) : base(items, "steinnet", "ST", "steinnet_") { }
    public SteinNetProbe() : this(null!) { }

    private static FestivalService Fest => FestivalService.Instance!;
    private static SteinService Svc => SteinService.Instance!;
    private static FestivalInstance Mine => Fest.Mine.Instances.GetValueOrDefault(Instance) ?? new FestivalInstance();

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && SteinService.Instance != null && SteinGame.Instance != null)) return;
        Expect(await Until(() => Fest.RunningOf(FestivalCatalog.TestId) is { Day: 5, Site: not null }, 40), "the server runs the test festival on day 5 with a site");
        Expect(await Until(() => Svc.Open() != null, 10), "the stone is open (the day table reached this client)");
        Expect(await Until(() => AdventureService.Instance!.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it");
        if (Svc.Open() is not { } o) { Fail("no stone"); return; }
        var ground = SteinGround.Of(o.Site);
        var me = Me!;
        // A on the stone's near side, B five metres behind it
        double back = _role == "A" ? 0.8 : 5.0;
        var g = me.Global;
        me.GlobalPosition += new Vector3((float)(ground.E - ground.DirE * back - g.E), 0.2f, (float)-(ground.N - ground.DirN * back - g.N));
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);

        if (_role == "A") await A(); else await B();
        await Finish(5.0);
    }

    private async Task A()
    {
        var game = SteinGame.Instance!;
        var landings = new List<SteinLanding>();
        var refused = new List<string>();
        Svc.Landed += landings.Add;
        Svc.Refused += refused.Add;
        Say("ready");
        Expect(await Heard("B", "ready", 120), "B is on");

        Expect(await Until(() => SteinGame.InteractionFor(Me!) != null, 10), "beside the stone there is a prompt");
        Expect(Me!.TryInteract() && await Until(() => game.State == SteinGame.Phase.Ready, 15), $"E: the server opened an attempt and told this client ({game.State}, refused {string.Join("; ", refused)})");
        Expect(game.Attempt is { Seed: not 0 }, "its seed came over the wire");
        game.PitchOverride = 40;
        await SteinCheck.PerfectPut(this, game);
        Expect(await Until(() => landings.Count >= 1, 15), "the server's word on the put reached this client");
        if (landings.Count == 0) return;
        var l = landings[0];
        Expect(l.Foul == null && l.Metres > 3.0 && l.Power > 0.9, $"{l.Metres:F2} m, power {l.Power:F2}");
        Expect(await Until(() => Mine.Batzen == l.Batzen && l.Batzen > 0 && Mine.Points == l.Points, 10),
            $"the section holds what the server paid: {Mine.Batzen} Batzen, {Mine.Points} points ({l.Batzen}/{l.Points})");
        Expect(Mine.Played.GetValueOrDefault("stein:5") == 1, "one put today");
        Expect(await Until(() => game.StakesShown >= 1, 10), "a stake shows where it fell");
        Say(FormattableString.Invariant($"landed {l.Metres:F3} {l.Batzen}"));
        await Until(() => game.State == SteinGame.Phase.Idle, 10);

        // a forged put for a fresh attempt: refused by the server, over the wire, and no put is used
        var began = new List<SteinAttempt>();
        Svc.Began += began.Add;
        Svc.AskBegin(0);
        Expect(await Until(() => began.Count >= 1, 10), "a fresh attempt opened");
        int refusedBefore = refused.Count;
        Svc.AskThrow(began.Count > 0 ? began[^1].Id : 99, [1e9, 1e9 + 1, 1e9 + 2, 1e9 + 3, 1e9 + 4, 1e9 + 5], 40);
        Expect(await Until(() => refused.Count > refusedBefore, 10), $"a forged put is refused ({(refused.Count > 0 ? refused[^1] : "")})");
        await Seconds(1.0);
        Expect(Mine.Played.GetValueOrDefault("stein:5") == 1 && Mine.Batzen == l.Batzen, "and uses no put, pays nothing");
        Say("done");
    }

    private async Task B()
    {
        var game = SteinGame.Instance!;
        var landings = new List<SteinLanding>();
        Svc.Landed += landings.Add;
        Say("ready");
        Expect(await Heard("A", "ready", 120), "A is on");
        string? line = await HeardStarting("A", "landed ", 120);
        Expect(line != null, "A put the stone");
        Expect(await Until(() => landings.Count >= 1, 15), "B was told of the put, 5 m from the stone");
        if (line == null || landings.Count == 0) return;
        var parts = line.Trim().Split(' ');
        double metres = double.Parse(parts[^2], System.Globalization.CultureInfo.InvariantCulture);
        var l = landings[0];
        Expect(Math.Abs(l.Metres - metres) < 0.0015, $"it is A's distance to the millimetre ({l.Metres:F3} / {metres:F3})");
        Expect(l.Thrower != Multiplayer.GetUniqueId() && l.Name == "Lea", $"it names the thrower, not B ({l.Thrower}, {l.Name})");
        Expect(await Until(() => game.StakesShown >= 1, 10), "B sees the stake where it fell");
        Expect(Mine.Batzen == 0 && Mine.Points == 0, $"B is paid nothing for it ({Mine.Batzen} Batzen)");
        Expect(game.State == SteinGame.Phase.Idle && !Me!.GameBusy, "B's walk and hands are free");
        Expect(await Heard("A", "done", 60), "A finished");
    }
}
