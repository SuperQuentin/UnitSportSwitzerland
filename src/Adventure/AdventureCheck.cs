using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Adventure;

/// <summary>
/// <c>--adventurecheck --systems ui,physics,adventure</c> (quick tier, #1223): offline, this client plays the
/// server's part. Starts the probe errand, walks the body to its first area (the 2 Hz reach check moves
/// it on), is refused a hand-over away from the second, hands an apple over there, and checks the
/// rewards (the account, the pack, the title), the toasts, the tracked target, a done quest refused again,
/// abandoning, and the save file read back after <c>SaveQueue.Flush</c>. Its progress lives in
/// <c>test_output/adventurecheck</c> and the bank writes nothing (<see cref="Bank.Scratch"/>): the player's
/// own saves are untouched. Windowed, it also saves the Journal and the tracker to
/// <c>test_output/adventure_*.png</c>.
/// </summary>
public partial class AdventureCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--adventurecheck");

    private readonly Func<FootPlayer?> _me;

    public AdventureCheck(ItemController items, Func<FootPlayer?> me) : base(items, "adventurecheck", "", "adventure_") => _me = me;
    public AdventureCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    /// <summary>Called by the service before it loads: every run starts from no progress.</summary>
    public static void ClearScratch()
    {
        if (AdventureService.ScratchDir is { } dir && System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
    }

    private static AdventureService? Service => AdventureService.Instance;
    private static QuestsProgress Quests => Service!.Mine.Get(QuestsProgress.Section);

    public override async void _Ready()
    {
        Bank.Scratch = true;
        // a command-line run starts with the free camera: on foot, as the campfire check does
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => Service != null && Me is { } me && me.IsOnFloor(), 90))
        {
            Fail($"no adventure service ({Service != null}) or no player on the ground ({Me?.Global})");
            return;
        }
        var svc = Service!;
        var at = Me!.Global;
        var a = ProbeQuests.PointA(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);
        var b = ProbeQuests.PointB(SpawnPoint.DefaultLv95E, SpawnPoint.DefaultLv95N);
        GD.Print(FormattableString.Invariant($"{Log} at {at.E:F1}/{at.N:F1}; A at {a.E:F0}/{a.N:F0}"));
        Expect(svc.Authority, "offline, this client keeps the progress");
        Expect(svc.Active(AdventureService.LocalPeer), "the local player adventures");
        Expect(svc.Mine.Ids.Contains(QuestsProgress.Section.Id), "the quests section is there from the start");
        Expect(svc.Quests.Available(Quests).Any(q => q.Id == ProbeQuests.Errand), "the probe errand is offered");
        Expect(!svc.Quests.Available(Quests).Any(q => q.Source == "catalog"), "no starter quest without the other plans' systems");
        long balance = Bank.Instance?.Balance ?? -1;
        var toasts = GetParent().GetNodeOrNull<Ui.QuestToasts>("QuestToasts");
        var tracker = GetParent().GetNodeOrNull<Ui.QuestTracker>("QuestTracker");

        svc.AskStart(ProbeQuests.Errand);
        Expect(await Until(() => Quests.Active.ContainsKey(ProbeQuests.Errand), 5), "the errand starts");
        Expect(Quests.Tracked == ProbeQuests.Errand, "and is tracked");
        Expect(await Until(() => toasts?.Last.StartsWith("Quest started") == true, 5), $"a toast says so ({toasts?.Last})");
        Expect(await Until(() => svc.TrackedTarget is { } t && Math.Abs(t.E - a.E) < 0.01 && Math.Abs(t.N - a.N) < 0.01, 5), $"the tracked target is area A ({svc.TrackedTarget})");
        Expect(await Until(() => tracker?.Shown.Contains("Probe errand") == true, 5), $"the tracker shows it ({tracker?.Shown})");

        await GoTo(a.E, a.N);
        Expect(await Until(() => Quests.Active.TryGetValue(ProbeQuests.Errand, out var s) && s.Step == 1, 5), "reaching area A moves the errand on");
        Expect(await Until(() => svc.TrackedTarget is { } t2 && Math.Abs(t2.N - b.N) < 0.01, 5), "the tracked target is area B now");

        string? refused = null;
        svc.Refused += why => refused = why;
        svc.AskDeliver(ProbeQuests.Errand, 1);
        Expect(refused?.Contains("no Apple") == true, $"no apple, no hand-over ({refused})");
        _items.Inventory.Add(ItemId.Apple, 1);
        refused = null;
        svc.AskDeliver(ProbeQuests.Errand, 1);
        Expect(await Until(() => refused != null, 3) && refused!.Contains("probe B"), $"too far from B, refused ({refused})");
        Expect(CountOf(ItemId.Apple) == 1, "the apple stays in the pack");

        await GoTo(b.E, b.N);
        svc.AskDeliver(ProbeQuests.Errand, 1);
        Expect(await Until(() => Quests.Done.ContainsKey(ProbeQuests.Errand), 5), "handing the apple over at B completes the errand");
        Expect(CountOf(ItemId.Apple) == 0, "the apple left the pack");
        Expect(CountOf(ItemId.Chocolate) >= 1, "the chocolate reward is in the pack");
        Expect(Bank.Instance?.Balance == balance + 25, $"CHF 25 in the account ({balance} -> {Bank.Instance?.Balance})");
        Expect(Quests.Titles.Contains("Errand runner") && Quests.Title == "Errand runner", "the title is earned and worn");
        Expect(await Until(() => toasts?.Last.StartsWith("Quest complete") == true, 5), $"the completion toast ({toasts?.Last})");
        Expect(await Until(() => Quests.Tracked == null && svc.TrackedTarget == null, 5), "nothing tracked any more");
        Expect(svc.Start(AdventureService.LocalPeer, ProbeQuests.Errand) != null, "a done quest does not start again");

        svc.AskStart(ProbeQuests.Walk);
        Expect(Quests.Active.ContainsKey(ProbeQuests.Walk), "the walk starts");
        svc.AskAbandon(ProbeQuests.Walk);
        Expect(!Quests.Active.ContainsKey(ProbeQuests.Walk), "and is abandoned");

        if (DisplayServer.GetName() != "headless")
        {
            svc.AskStart(ProbeQuests.Walk);
            await Seconds(1);
            GD.Print($"{Log} shot {Shot("tracker")}");
            Ui.AdventureLog.Open(Ui.AdventureLog.QuestsTab);
            await Seconds(1.5);
            GD.Print($"{Log} shot {Shot("journal")}");
            Ui.AdventureLog.Open(Ui.AdventureLog.BirdsTab);
            await Seconds(1);
            GD.Print($"{Log} shot {Shot("journal_birds")}");
            GameShell.Instance?.Back();
        }

        svc.SaveAll();
        SaveQueue.Flush();
        string file = svc.FileOf(AdventureService.LocalKey);
        try
        {
            var back = AdventureBook.FromJson(System.IO.File.ReadAllText(file)).Get(QuestsProgress.Section);
            Expect(back.Done.ContainsKey(ProbeQuests.Errand) && back.Titles.Contains("Errand runner"), $"the save file holds it ({file})");
        }
        catch (Exception e) { Expect(false, $"the save file reads back ({file}: {e.Message})"); }
        await Finish(0);
    }

    /// <summary>Puts the body at an LV95 point, on the flat ground, and lets the physics settle a moment.</summary>
    private async Task GoTo(double e, double n)
    {
        var me = Me!;
        var g = me.Global;
        me.GlobalPosition += new Vector3((float)(e - g.E), 0.3f, (float)-(n - g.N));
        me.Velocity = Vector3.Zero;
        await Until(() => false, 0.3);
        GD.Print(FormattableString.Invariant($"{Log} at {me.Global.E:F1}/{me.Global.N:F1}"));
    }
}
