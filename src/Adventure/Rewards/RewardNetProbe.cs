using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// <c>--rewardnet open|watch</c> with <c>--connect</c>, <c>--pois fixture</c> and <c>--rewardspots fixture</c> (driven
/// by <c>tools/rewardnetcheck.sh</c>, #1227). The server places, checks and rolls; each role checks what reached this
/// client:
/// <list type="bullet">
/// <item><c>open</c> (A): opens the Common chest (the server's roll arrives; it is gone for A), cannot open it again, a
/// forged chest id is refused; picks an edelweiss; runs the chapel's trial (the server's board has it); in a Battle
/// Royale lobby a chest is refused.</item>
/// <item><c>watch</c> (B): after A opened it, the chest still stands for B, and B opens it too (its own roll); after A
/// picked the edelweiss, it is still there for B, who picks it; the trial's board B is sent holds A's run.</item>
/// </list>
/// </summary>
public partial class RewardNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--rewardnet");
    private const string Password = "rewardpw";

    public RewardNetProbe(ItemController items) : base(items, "rewardnet", "RW", "rewardnet_") { }
    public RewardNetProbe() : this(null!) { }

    private static RewardService Svc => RewardService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static RewardWorld World => RewardWorld.Instance!;
    private static ChestsProgress Chests => Adv.Mine.Get(ChestsProgress.Section);
    private static CollectProgress Collect => Adv.Mine.Get(CollectProgress.Section);

    private readonly List<string> _opened = new(), _refused = new();

    public override async void _Ready()
    {
        _role = Role ?? "OPEN";
        if (!await Joined(150, () => AdventureService.Instance != null && RewardService.Instance is { Map.Fixed.Count: > 0 } && RewardWorld.Instance != null)) return;
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it");
        Expect(!Adv.Authority, "the server keeps the progress");
        Svc.ChestOpened += (id, _, _, _, _) => _opened.Add(id);
        Svc.Refused += (_, why) => _refused.Add(why);
        switch (_role)
        {
            case "OPEN": await Open(); break;
            default: await Watch(); break;
        }
        await Finish(3.0);
    }

    private static RewardSpot Common => Svc.Map.Fixed!.First(s => s.Kind == SpotKind.Chest && s.Tier == ChestTier.Common && s.Seal == SealKind.None);
    private static RewardSpot Other => Svc.Map.Fixed!.Where(s => s.Kind == SpotKind.Chest && s.Tier == ChestTier.Common && s.Seal == SealKind.None).Skip(1).First();
    private static RewardSpot Flower => Svc.Map.Fixed!.First(s => s.Kind == SpotKind.Edelweiss);

    private async Task Open()
    {
        var chest = Common;
        await WalkTo(chest.E, chest.N - 1.5);
        Expect(await Until(() => World.Chests.ContainsKey(chest.Id), 10), "the chest stands here");
        Expect(Me!.TryInteract(), "E at the chest");
        Expect(await Until(() => _opened.Contains(chest.Id), 10), "the server rolled it for A");
        Expect(await Until(() => Chests.Opened.Contains(chest.Id), 5), "A's chests section has it");
        Expect(await Until(() => !World.Chests.ContainsKey(chest.Id), 6), "and it is gone for A");
        Say("opened");
        Expect(await Heard("WATCH", "opened", 90), "B opened the same chest");
        await Seconds(1.0);
        Expect(!World.Chests.ContainsKey(chest.Id), "A's view is A's: still gone");
        _refused.Clear();
        Svc.AskOpen(chest.Id);
        Expect(await Until(() => _refused.Contains("You opened it already."), 5), $"A's second open is refused ({string.Join("; ", _refused)})");
        // forged: a cell no chest stands in, an id of no shape
        _refused.Clear();
        Svc.AskOpen(RewardRules.CellId(RewardRules.ChestPrefix, chest.E + 300, chest.N + 300));
        Svc.AskOpen("ch:forged");
        Expect(await Until(() => _refused.Count(r => r == "No such chest.") == 2, 5), $"forged chest ids are refused ({string.Join("; ", _refused)})");

        var f = Flower;
        await WalkTo(f.E, f.N - 0.6);
        Svc.AskPick(f.Id);
        Expect(await Until(() => Collect.Held == 1 && Collect.Taken.Contains(f.Id), 5), "A picked the edelweiss");
        Say("picked");

        // the chapel's trial: its board lives on the server
        var cat = PoiCatalogueHost.Current;
        var def = Trials.At(cat.OfKind(PoiKind.Chapel)[0], cat).First(t => t.Kind == TrialKind.Run);
        await WalkTo(def.StartE, def.StartN - 1.2);
        Svc.AskTrial(def.Id);
        Expect(await Until(() => Svc.Trial?.Id == def.Id, 10), "the trial started");
        if (Svc.Trial is { } t)
            foreach (var g in t.Gates) await WalkTo(g.E, g.N, 5);
        Expect(await Until(() => Adv.Mine.Get(TrialsProgress.Section).Best.ContainsKey(def.Id), 10), "A finished: the server kept A's best");
        Expect(await Until(() => Svc.Boards.GetValueOrDefault(def.Id) is { Count: > 0 } rows && rows[0].Name == "Lea", 10), "the board names A");
        Say("trial " + def.Id);

        // a Battle Royale round: nothing to claim
        if (!await LogIn(Password)) return;
        Chat!.Send("/br open here");
        Expect(await Until(() => BattleRoyale.BrManager.Instance?.State.Phase is BattleRoyale.BrPhase.Lobby, 15), "a Battle Royale lobby is open");
        Chat.Send("/br join");
        Expect(await Until(() => BattleRoyale.BrManager.Instance?.MyEntry != null, 15), "A joined it");
        var other = Other;
        await WalkTo(other.E, other.N - 1.5);
        _refused.Clear();
        Svc.AskOpen(other.Id);
        Expect(await Until(() => _refused.Contains("No adventuring now."), 10) && !Chests.Opened.Contains(other.Id), $"in the round a chest is refused ({string.Join("; ", _refused)})");
        Chat.Send("/br leave");
        await Until(() => BattleRoyale.BrManager.Instance?.MyEntry == null, 15);
        Chat.Send("/br cancel");
        Say("br");
    }

    private async Task Watch()
    {
        var chest = Common;
        await WalkTo(chest.E + 2.5, chest.N - 1.5);
        Expect(await Heard("OPEN", "opened", 120), "A opened the chest");
        await Seconds(1.0);
        Expect(World.Chests.ContainsKey(chest.Id) && !Chests.Opened.Contains(chest.Id), "B still sees it closed: nothing of A's reached B");
        await WalkTo(chest.E, chest.N - 1.5);
        Expect(Me!.TryInteract(), "E at the chest");
        Expect(await Until(() => _opened.Contains(chest.Id), 10), "B opens it too, its own roll");
        Expect(await Until(() => Chests.Opened.Contains(chest.Id) && !World.Chests.ContainsKey(chest.Id), 6), "and now it is gone for B");
        Say("opened");

        var f = Flower;
        Expect(await Heard("OPEN", "picked", 120), "A picked the edelweiss");
        await WalkTo(f.E, f.N - 0.6);
        Expect(await Until(() => World.Shows(f.Id), 5), "B still sees it");
        Svc.AskPick(f.Id);
        Expect(await Until(() => Collect.Held == 1 && Collect.Taken.Contains(f.Id), 5), "and picks it");

        var trial = (await HeardStarting("OPEN", "trial ", 180))?["trial ".Length..].Trim();
        Expect(trial != null, "A finished the trial");
        if (trial != null)
        {
            Svc.AskBoard(trial);
            Expect(await Until(() => Svc.Boards.GetValueOrDefault(trial) is { Count: > 0 } rows && rows.Any(r => r.Name == "Lea"), 10),
                "the server's board, sent to B, holds A's run");
            Expect(!Adv.Mine.Get(TrialsProgress.Section).Best.ContainsKey(trial), "but not in B's progress");
        }
        Expect(await Heard("OPEN", "br", 120), "A's Battle Royale part is done");
        Expect(Collect.Held == 1 && Chests.Opened.Count == 1, "B's progress is B's own");
    }

    /// <summary>Walks the body in <paramref name="step"/> m steps (10 m/s at 5 m: under every pace check).</summary>
    private async Task WalkTo(double e, double n, double step = 8)
    {
        var me = Me!;
        while (true)
        {
            var g = me.Global;
            double de = e - g.E, dn = n - g.N, d = Math.Sqrt(de * de + dn * dn);
            if (d < 0.3) break;
            double s = Math.Min(d, step);
            me.GlobalPosition += new Vector3((float)(de / d * s), 0.2f, (float)(-dn / d * s));
            me.Velocity = Vector3.Zero;
            await Seconds(0.5);
        }
        // the published position catches up before a claim
        await Seconds(0.6);
    }
}
