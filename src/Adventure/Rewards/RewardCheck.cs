using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Rewards;

/// <summary>
/// <c>--rewardcheck --world fixture --systems ui,physics,loot,adventure --pois fixture --rewardspots fixture</c> (quick tier,
/// #1227): offline, this client plays the server's part on the flat fixture with the hand-placed spots. Opens a Common
/// chest (the lid turns, the reveal, the items, reopening and a forged id refused); breaks every seal (Höhenfeuer,
/// cowbells with a wrong bell, a ring run, the plates held by a dropped stone, the marmot's guide) and opens what they
/// held, the Luxurious chest by its edition; picks the edelweiss with the gather hold's target and offers five at the
/// chapel; adds a stone to the pass's cairn; runs the chapel's trial (gold, the board) and is put out of a second run
/// for going too fast; frames the fixture peak's photo spot (and is refused 90° off); hears the detector quicken; lets
/// out a gust; opens the Collection tab; reads the save file back. Progress in <c>test_output/rewardcheck</c>.
/// Windowed, it saves <c>test_output/reward_chest.png</c>, <c>reward_guide.png</c>, <c>reward_collection.png</c>.
/// </summary>
public partial class RewardCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--rewardcheck");

    private readonly Func<FootPlayer?> _me;
    private readonly List<AdventureEvent> _events = new();
    private readonly List<string> _opened = new(), _refused = new();
    private readonly List<(string Spot, bool Taken)> _photos = new();

    public RewardCheck(ItemController items, Func<FootPlayer?> me) : base(items, "rewardcheck", "", "reward_") => _me = me;
    public RewardCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static bool Windowed => DisplayServer.GetName() != "headless";
    private static RewardService Svc => RewardService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static RewardWorld World => RewardWorld.Instance!;

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => AdventureService.Instance != null && RewardService.Instance is { Map.Fixed: { Count: > 0 } } && RewardWorld.Instance != null
                && Me is { } me && me.IsOnFloor(), 90))
        {
            Fail($"no adventure ({AdventureService.Instance != null}), rewards ({RewardService.Instance?.Map.Fixed?.Count}) or player on the ground");
            return;
        }
        await Until(() => Me!.Down != 0, 3);
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        await Seconds(1);
        Adv.Events.Raised += e => _events.Add(e);
        Svc.ChestOpened += (id, _, _, _, _) => _opened.Add(id);
        Svc.Refused += (_, why) => _refused.Add(why);
        Svc.PhotoJudged += (spot, taken, _) => _photos.Add((spot, taken));
        Expect(Adv.Quests.Plans.Contains("rewards"), "the rewards run: their quests may be offered");
        var spots = Svc.Map.Fixed!;
        RewardSpot Chest(SealKind seal, ChestTier? tier = null) => spots.First(s => s.Kind == SpotKind.Chest && s.Seal == seal && (tier == null || s.Tier == tier));

        await CommonChest(Chest(SealKind.None, ChestTier.Common));
        await Bonfire(Chest(SealKind.Bonfire));
        await Bells(Chest(SealKind.Bells));
        await Rings(Chest(SealKind.Rings));
        await Plates(Chest(SealKind.Plates, ChestTier.Fine));
        await Guide(Chest(SealKind.Guide));
        await Luxurious(spots.First(s => s.Tier == ChestTier.Luxurious));
        await Edelweiss(spots.Where(s => s.Kind == SpotKind.Edelweiss).ToList(), spots.First(s => s.Kind == SpotKind.Cairn));
        await Trial();
        await Photo();
        await Gadgets(spots.First(s => s.Kind == SpotKind.Chest && s.Tier == ChestTier.Common && !Mine.Opened.Contains(s.Id)));
        await Collection();
        ReadBack();
        await Finish(0);
    }

    private static ChestsProgress Mine => Adv.Mine.Get(ChestsProgress.Section);
    private static CollectProgress Collect => Adv.Mine.Get(CollectProgress.Section);

    // ---- chests ---------------------------------------------------------------------------------

    private async Task CommonChest(RewardSpot chest)
    {
        await WalkTo(chest.E, chest.N - 1.5);
        Expect(await Until(() => World.Chests.ContainsKey(chest.Id), 5), $"the Common chest is drawn ({World.Chests.Count} chests near)");
        Expect(RewardWorld.PromptFor(Me!)?.Contains("Open the common chest") == true, $"the prompt says so ({RewardWorld.PromptFor(Me!)})");
        int crystals = CountOf(ItemId.Bergkristall), before = PackTotal();
        Expect(Me!.TryInteract(), "E at the chest");
        Expect(await Until(() => _opened.Contains(chest.Id), 5), "the server opened it for us");
        await Seconds(0.75);
        var node = World.Chests.GetValueOrDefault(chest.Id);
        Expect(node is { LidAngle: > 1.5f }, $"the lid swung open ({node?.LidAngle:F2} rad)");
        Expect(ChestRevealUi.Instance?.Showing == true && ChestRevealUi.Instance.Last.StartsWith("Common chest"), $"the reveal shows it ({ChestRevealUi.Instance?.Last})");
        if (Windowed) GD.Print($"{Log} shot {Shot("chest")}");
        Expect(await Until(() => PackTotal() > before, 3), $"its items are in the pack ({before} -> {PackTotal()}, crystal {crystals} -> {CountOf(ItemId.Bergkristall)})");
        Expect(Mine.Opened.Contains(chest.Id) && Mine.ByTier[0] == 1, "the chests section has it");
        Expect(_events.OfType<ChestOpened>().Any(e => e.ChestId == chest.Id && e.Tier == 0), "ChestOpened raised");
        Expect(_events.OfType<ItemObtained>().Any(e => e.Source == "chest:" + chest.Id), "its items raised ItemObtained");
        Expect(await Until(() => !World.Chests.ContainsKey(chest.Id), 5), "then it is gone for us");
        Expect(await Svc.ServeOpen(AdventureService.LocalPeer, chest.Id) == "You opened it already.", "opening it again is refused");
        // a forged id: a cell no chest stands in, and a shape no chest has
        var forged = RewardRules.CellId(RewardRules.ChestPrefix, chest.E + 300, chest.N + 300);
        Expect(await Svc.ServeOpen(AdventureService.LocalPeer, forged) == "No such chest.", $"a forged id is refused ({forged})");
        Expect(await Svc.ServeOpen(AdventureService.LocalPeer, "ch:nothing") == "No such chest.", "a forged shape too");
        // far from a chest: refused
        var other = Svc.Map.Fixed!.First(s => s.Kind == SpotKind.Chest && s.Seal == SealKind.None && s.Id != chest.Id);
        Expect((await Svc.ServeOpen(AdventureService.LocalPeer, other.Id))?.StartsWith("Too far") == true, "a chest 80 m away is refused");
        await Until(() => ChestRevealUi.Instance?.Showing != true, 8);
    }

    private async Task OpenSealed(RewardSpot chest, string what)
    {
        await WalkTo(chest.E, chest.N - 1.5);
        Expect(await Until(() => World.Chests.ContainsKey(chest.Id) && RewardWorld.Unsealed(chest), 5), $"{what}: the seal is broken");
        Expect(Me!.TryInteract(), $"{what}: E at the chest");
        Expect(await Until(() => _opened.Contains(RewardRules.OpenId(chest, RewardRules.Edition(DateTime.UtcNow))), 5), $"{what}: opened");
        await Until(() => ChestRevealUi.Instance?.Showing != true, 8);
    }

    private async Task Bonfire(RewardSpot chest)
    {
        var layout = Svc.Map.LayoutOf(chest);
        await WalkTo(chest.E, chest.N - 1.5);
        await Until(() => World.Chests.ContainsKey(chest.Id), 5);
        Expect(RewardWorld.PromptFor(Me!)?.Contains("Sealed: Höhenfeuer") == true, $"the sealed chest says what it waits for ({RewardWorld.PromptFor(Me!)})");
        Expect(await Svc.ServeOpen(AdventureService.LocalPeer, chest.Id) is { } sealedWhy && sealedWhy.StartsWith("Sealed"), "a sealed chest does not open");
        for (int i = 0; i < 3; i++)
        {
            var p = layout.Props[i];
            await WalkTo(p.E, p.N - 1.2);
            Expect(RewardWorld.PromptFor(Me!)?.Contains("Light the wood stack") == true, $"stack {i + 1}: the prompt ({RewardWorld.PromptFor(Me!)})");
            Me!.TryInteract();
            int bit = 1 << i;
            Expect(await Until(() => (Svc.SealMasks.GetValueOrDefault(chest.Id) & bit) != 0, 3), $"stack {i + 1} lit");
        }
        Expect((Svc.SealMasks[chest.Id] & Seals.DoneBit) != 0, "three fires in 90 s: the seal is done");
        await OpenSealed(chest, "Höhenfeuer");
    }

    private async Task Bells(RewardSpot chest)
    {
        var layout = Svc.Map.LayoutOf(chest);
        await WalkTo(layout.Props[4].E, layout.Props[4].N - 0.5);
        Expect(RewardWorld.PromptFor(Me!)?.Contains("Listen to the tune") == true, $"the fifth bell plays the tune ({RewardWorld.PromptFor(Me!)})");
        Me!.TryInteract();
        await Seconds(0.5);
        int wrong = (layout.Tune[0] + 1) % 4;
        await Ring(layout, wrong);
        Expect(await Until(() => _refused.Any(r => r.StartsWith("Wrong bell")), 3), "a wrong bell starts the tune again");
        foreach (int b in layout.Tune) await Ring(layout, b);
        Expect(await Until(() => (Svc.SealMasks.GetValueOrDefault(chest.Id) & Seals.DoneBit) != 0, 3), "the tune rung right: the seal is done");
        await OpenSealed(chest, "Cowbells");
    }

    private async Task Ring(SealLayout layout, int bell)
    {
        await WalkTo(layout.Props[bell].E, layout.Props[bell].N - 0.4);
        Me!.TryInteract();
        await Seconds(0.4);
    }

    private async Task Rings(RewardSpot chest)
    {
        var layout = Svc.Map.LayoutOf(chest);
        // run through them at a brisk run's pace: the body's middle through each ring's centre
        for (int i = 0; i < layout.Props.Length; i++)
        {
            await WalkTo(layout.Props[i].E, layout.Props[i].N, 6);
            await Seconds(0.4);
        }
        Expect(await Until(() => (Svc.SealMasks.GetValueOrDefault(chest.Id) & Seals.DoneBit) != 0, 3),
            $"the rings run in order and in time ({layout.Props.Length} rings, mask {Svc.SealMasks.GetValueOrDefault(chest.Id)})");
        await OpenSealed(chest, "Ring run");
    }

    private async Task Plates(RewardSpot chest)
    {
        var layout = Svc.Map.LayoutOf(chest);
        var a = layout.Props[0];
        var b = layout.Props[1];
        await WalkTo(a.E, a.N);
        await Seconds(0.5);
        Expect(await Until(() => _refused.Any(r => r.Contains("other plate")), 3), "one plate alone does not open it");
        // a stone dropped on the other plate holds it down
        await WalkTo(b.E + 2, b.N);
        Items.DroppedItems.Instance!.Drop(new ItemStack(ItemId.Stone, 1), WorldAt(b.E, b.N, a.Alt + 0.3f), Vector3.Zero, Vector3.Zero, Vector3.Zero);
        await Seconds(1.0);
        await WalkTo(a.E + 5, a.N);
        await WalkTo(a.E, a.N);
        Expect(await Until(() => (Svc.SealMasks.GetValueOrDefault(chest.Id) & Seals.DoneBit) != 0, 3), "a body on one plate and a stone on the other: done");
        await OpenSealed(chest, "Pressure plates");
    }

    private async Task Guide(RewardSpot chest)
    {
        var layout = Svc.Map.LayoutOf(chest);
        Expect(!World.Chests.ContainsKey(chest.Id), "a guide's chest is hidden until the guide leads to it");
        var burrow = layout.Props[0];
        await WalkTo(burrow.E, burrow.N - 1.5);
        Expect(RewardWorld.PromptFor(Me!)?.Contains("Wake the marmot") == true, $"the burrow's prompt ({RewardWorld.PromptFor(Me!)})");
        Me!.TryInteract();
        Expect(await Until(() => Svc.Guide is { Leg: 1 } && GuideNode.Instance?.Visible == true, 3), "the marmot pops out and sets off");
        await Seconds(0.6);
        if (Windowed) GD.Print($"{Log} shot {Shot("guide")}");
        for (int leg = 1; leg < layout.Props.Length; leg++)
        {
            await Seconds(GuideBook.LegSeconds(layout, leg) + 0.3);
            await WalkTo(layout.Props[leg].E, layout.Props[leg].N, 6);
            int want = leg + 1;
            if (leg < layout.Props.Length - 1)
                Expect(await Until(() => Svc.Guide?.Leg == want, 3), $"reached its waypoint {leg}: it runs on ({Svc.Guide?.Leg})");
        }
        Expect(await Until(() => (Svc.SealMasks.GetValueOrDefault(chest.Id) & Seals.DoneBit) != 0, 3), "the guide arrived: the seal is done");
        Expect(await Until(() => World.Chests.ContainsKey(chest.Id), 3), "and the chest shows itself");
        await OpenSealed(chest, "Guide");
    }

    private async Task Luxurious(RewardSpot chest)
    {
        var layout = Svc.Map.LayoutOf(chest);
        var a = layout.Props[0];
        var b = layout.Props[1];
        await WalkTo(b.E + 2, b.N);
        Items.DroppedItems.Instance!.Drop(new ItemStack(ItemId.Stone, 1), WorldAt(b.E, b.N, a.Alt + 0.3f), Vector3.Zero, Vector3.Zero, Vector3.Zero);
        await Seconds(1.0);
        await WalkTo(a.E, a.N);
        await Until(() => (Svc.SealMasks.GetValueOrDefault(chest.Id) & Seals.DoneBit) != 0, 3);
        await OpenSealed(chest, "Luxurious");
        string edition = RewardRules.OpenId(chest, RewardRules.Edition(DateTime.UtcNow));
        Expect(edition.Contains('#') && Mine.Opened.Contains(edition), $"the Luxurious chest is opened by this year's edition ({edition})");
        Expect(Mine.Owned.Count >= 1, $"a Luxurious chest always holds a cosmetic ({string.Join(",", Mine.Owned)})");
    }

    // ---- edelweiss, offering, cairn -------------------------------------------------------------------

    private async Task Edelweiss(List<RewardSpot> flowers, RewardSpot cairn)
    {
        var gathering = Loot.Gathering.Instance;
        foreach (var f in flowers)
        {
            await WalkTo(f.E, f.N - 0.6);
            Expect(await Until(() => World.Shows(f.Id) && gathering?.Target == Loot.Gathering.Resource.Edelweiss, 3),
                $"the gather hold offers {f.Id} ({gathering?.Target}, {gathering?.Prompt})");
            int held = Collect.Held;
            Svc.AskPick(f.Id);
            Expect(await Until(() => Collect.Held == held + 1 && !World.Shows(f.Id), 3), $"picked: {Collect.Held} held, no longer drawn");
        }
        Expect(await Svc.ServePick(AdventureService.LocalPeer, flowers[^1].Id) == "You picked it already.", "an edelweiss picks once");
        Expect(_events.OfType<CollectibleTaken>().Count(e => e.Kind == "edelweiss" && e.Canton == "VS") == flowers.Count, "CollectibleTaken raised for each");
        var chapel = PoiCatalogueHost.Current.OfKind(PoiKind.Chapel)[0];
        await WalkTo(chapel.E, chapel.N - 3);
        await Until(() => World.BoxAt(chapel.Id) != null, 3);
        var box = World.BoxAt(chapel.Id);
        if (box is { } at)
        {
            var (be, bn) = Lv95Of(at);
            await WalkTo(be, bn - 1.0);
        }
        Expect(RewardWorld.PromptFor(Me!)?.Contains("Offer edelweiss") == true, $"the chapel's offering box ({RewardWorld.PromptFor(Me!)})");
        int before = Collect.Held;
        int stepsBefore = Adv.Mine.Get(StaminaUpgrades.Section).Steps;
        float maxBefore = Me!.StaminaMeter.Max;
        Svc.AskOffer(chapel.Id);
        Expect(await Until(() => Collect.Offered == 5 && Collect.Held == before - 5, 3), $"five offered ({Collect.Held} held, {Collect.Offered} offered)");
        Expect(_events.OfType<EdelweissOffered>().Any(e => e.ShrineId == chapel.Id && e.Offered == 5), "EdelweissOffered raised");
        // five offered = one stamina step, through the stamina plan's "stamina" reward (#1225)
        Expect(await Until(() => Adv.Mine.Get(StaminaUpgrades.Section).Steps == stepsBefore + 1, 3),
            $"a stamina step bought ({stepsBefore} -> {Adv.Mine.Get(StaminaUpgrades.Section).Steps})");
        Expect(_events.OfType<StaminaRaised>().Any(e => e.Steps == stepsBefore + 1), "StaminaRaised raised");
        Expect(await Until(() => Me!.StaminaMeter.Max > maxBefore, 3), $"max stamina rises ({maxBefore:F0} -> {Me!.StaminaMeter.Max:F0})");
        await WalkTo(cairn.E, cairn.N - 0.6);
        Expect(await Until(() => gathering?.Target == Loot.Gathering.Resource.Cairn, 3), $"the gather hold offers the cairn ({gathering?.Target})");
        Svc.AskPick(cairn.Id);
        Expect(await Until(() => Collect.Taken.Contains(cairn.Id) && Collect.Cairns.GetValueOrDefault("VS") == 1, 3), "a stone added to the pass's cairn");
    }

    // ---- trials -------------------------------------------------------------------------------

    private async Task Trial()
    {
        var cat = PoiCatalogueHost.Current;
        var chapel = cat.OfKind(PoiKind.Chapel)[0];
        var def = Trials.At(chapel, cat).First(t => t.Kind == TrialKind.Run);
        await WalkTo(def.StartE, def.StartN - 1.2);
        Expect(await Until(() => World.Posts.Any(p => p.Def.Id == def.Id), 3), "the chapel's trial post stands there");
        Expect(RewardWorld.PromptFor(Me!)?.Contains("Start the trial") == true, $"its prompt ({RewardWorld.PromptFor(Me!)})");
        Me!.TryInteract();
        Expect(await Until(() => Svc.Trial?.Id == def.Id, 3), "the trial started: its gates arrived");
        var gates = Svc.Trial!.Gates;
        foreach (var g in gates) await WalkTo(g.E, g.N, 5);
        Expect(await Until(() => Adv.Mine.Get(TrialsProgress.Section).Best.ContainsKey(def.Id), 5), $"finished: a personal best kept ({TrialRunner.Instance?.Reported} gates reported)");
        var best = Adv.Mine.Get(TrialsProgress.Section).Best.GetValueOrDefault(def.Id);
        Expect(best is { Medal: 3 }, $"under par: gold ({best?.Seconds:F1} s, medal {best?.Medal})");
        Expect(_events.OfType<TrialFinished>().Any(e => e.TrialId == def.Id && e.Medal == 3), "TrialFinished raised");
        Expect(Svc.BoardOf(def.Id).Count == 1, "the server's board holds the run");
        Expect(await Until(() => Svc.Boards.GetValueOrDefault(def.Id)?.Count == 1, 3), "and sends it");
        // a second run, jumping to the far gate: out for the pace
        await WalkTo(def.StartE, def.StartN - 1.2);
        Me!.TryInteract();
        await Until(() => Svc.Trial?.Id == def.Id, 3);
        await Jump(gates[0].E, gates[0].N);
        await Seconds(0.3);
        await Jump(gates[1].E, gates[1].N);
        Expect(await Until(() => Svc.Trial == null, 3), "a 200 m jump between gates puts the run out");
    }

    // ---- photo, gadgets ---------------------------------------------------------------------------

    private async Task Photo()
    {
        var spot = PhotoSpots.Extra.FirstOrDefault();
        if (spot == null) { Expect(false, "the fixture photo spot exists"); return; }
        await WalkTo(spot.StandE + 20, spot.StandN - 30);
        var g = Me!.Global;
        double de = spot.TargetE - g.E, dn = spot.TargetN - g.N;
        float yaw = (float)(Math.Atan2(de, dn) * 180 / Math.PI);
        float pitch = (float)(Math.Atan2(spot.TargetAlt - (g.Alt + 1.6), Math.Sqrt(de * de + dn * dn)) * 180 / Math.PI);
        Svc.ReportPhoto(spot.Id, yaw + 90, pitch, 40);
        Expect(await Until(() => _photos.Any(p => p.Spot == spot.Id && !p.Taken), 3) && !Adv.Mine.Get(PhotosProgress.Section).Taken.Contains(spot.Id),
            "a photo aimed 90° off is not counted");
        Svc.ReportPhoto(spot.Id, yaw, pitch, 40);
        Expect(await Until(() => Adv.Mine.Get(PhotosProgress.Section).Taken.Contains(spot.Id), 3), "framed: the photo spot is taken");
        Expect(_events.OfType<PhotoTaken>().Any(e => e.TargetId == spot.Id), "PhotoTaken raised");
        Svc.ReportFauna(Fauna.Marmot);
        Expect(await Until(() => Collect.Fauna.GetValueOrDefault(Fauna.Marmot) == 1, 3), "the marmot is on the fauna page");
        Svc.ReportFauna("Unicorn");
        await Seconds(0.3);
        Expect(!Collect.Fauna.ContainsKey("Unicorn"), "an unknown animal is not");
    }

    private async Task Gadgets(RewardSpot chest)
    {
        int slot = Inventory.HotbarSize - 1;
        _items.Inventory.Put(slot, new ItemStack(ItemId.TreasureDetector, 1));
        _items.Inventory.Select(slot);
        await WalkTo(chest.E + 150, chest.N);
        Expect(await Until(() => RewardHud.Instance?.Interval != null, 3), $"the detector beeps 150 m from a chest ({RewardHud.Instance?.Interval})");
        float far = RewardHud.Instance?.Interval ?? 0;
        await WalkTo(chest.E + 5, chest.N);
        await Seconds(0.6);
        float near = RewardHud.Instance?.Interval ?? 9;
        Expect(near < far, $"and faster near it ({far:F2} s -> {near:F2} s)");
        RewardHud.Instance?.Ping();
        await Seconds(0.4);
        int gusts = GustFx.Count, volumes = UnitSport.World.AirLift.Volumes.Count;
        var gustAt = Me!.GlobalPosition + new Vector3(0, 0, -5);
        ItemEvents.Instance?.Send(ItemEventKind.Gust, gustAt, Vector3.Forward);
        Expect(await Until(() => GustFx.Count == gusts + 1, 2), "a bottled gust lets out its updraft");
        // the lift is the stamina plan's air (#1225): a volume that rises 20 m over where it landed
        var air = ItemEvents.Instance!.Origin!.ToGlobal(gustAt + new Vector3(0, 20, 0));
        float lift = UnitSport.World.AirLift.Volumes.LiftAt(air.E, air.N, (float)air.Alt, Net.ClockSync.SimNow);
        Expect(UnitSport.World.AirLift.Volumes.Count == volumes + 1 && lift >= GustFx.Speed * 0.9f, $"and the air there rises ({lift:F1} m/s)");
        float later = UnitSport.World.AirLift.Volumes.LiftAt(air.E, air.N, (float)air.Alt, Net.ClockSync.SimNow + GustFx.Seconds + 1);
        Expect(later == 0, "for 20 seconds");
        Expect(Crafting.Recipes.All.Any(r => r.Out == ItemId.TreasureDetector && r.In.Any(i => i.Id == ItemId.Bergkristall && i.Count == 4)), "the detector's recipe: 4 Bergkristall, electronics, copper wire");
        _items.Inventory.Put(slot, ItemStack.Empty);
    }

    private async Task Collection()
    {
        Ui.AdventureLog.Open(CollectionPage.TabId);
        await Seconds(0.8);
        Expect(GameShell.Instance?.Top is Ui.AdventureLog { Current: CollectionPage.TabId }, "the Journal opens on the Collection");
        if (Windowed) GD.Print($"{Log} shot {Shot("collection")}");
        GameShell.Instance?.Back();
        await Seconds(0.3);
    }

    private void ReadBack()
    {
        Adv.SaveAll();
        SaveQueue.Flush();
        string file = Adv.FileOf(AdventureService.LocalKey);
        try
        {
            var back = AdventureBook.FromJson(System.IO.File.ReadAllText(file));
            Expect(back.Get(ChestsProgress.Section).Opened.Count == Mine.Opened.Count && Mine.Opened.Count >= 7, $"the save file holds the chests ({Mine.Opened.Count})");
            Expect(back.Get(CollectProgress.Section).Offered == 5, "and the edelweiss offered");
            Expect(back.Get(TrialsProgress.Section).Best.Count == 1, "and the trial's best");
            Expect(back.Get(PhotosProgress.Section).Taken.Count == 1, "and the photo");
        }
        catch (Exception e) { Expect(false, $"the save file reads back ({file}: {e.Message})"); }
    }

    // ---- moving --------------------------------------------------------------------------------

    /// <summary>Walks the body in <paramref name="step"/> m steps ten times a second (a run's pace at 6 m: under every pace check).</summary>
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
            await Seconds(s > step * 0.5 ? 0.5 : 0.3);
        }
        await Seconds(0.2);
    }

    /// <summary>A world point at an LV95 position and height, from where the body is (the probe's own frame).</summary>
    private Vector3 WorldAt(double e, double n, double alt)
    {
        var g = Me!.Global;
        return Me.GlobalPosition + new Vector3((float)(e - g.E), (float)(alt - g.Alt), (float)-(n - g.N));
    }

    private (double E, double N) Lv95Of(Vector3 w)
    {
        var g = Me!.Global;
        var d = w - Me.GlobalPosition;
        return (g.E + d.X, g.N - d.Z);
    }

    private async Task Jump(double e, double n)
    {
        var me = Me!;
        var g = me.Global;
        me.GlobalPosition += new Vector3((float)(e - g.E), 0.2f, (float)-(n - g.N));
        me.Velocity = Vector3.Zero;
        await Seconds(0.3);
    }
}
