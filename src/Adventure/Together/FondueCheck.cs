using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// <c>--fonduecheck --world fixture --systems ui,terrain,physics,adventure --fondueseed 2</c> (quick tier, #1266): offline,
/// with a local partner body (<see cref="TogetherService.Pair"/>). A campfire laid through the item's path, a fondue pot
/// used at it starts the table; the partner sits; cheese, bread and wine in (and a refusal giving an item back); a
/// scripted figure-eight through the panel's own sample path; dips; the forced bread loss (the seed's second dip), the
/// forfeit popup and its line; the pot to its end: <c>Fed</c> and <c>Warm</c> on the stamina. Then a second pot left
/// unstirred until it splits. Windowed it saves <c>test_output/fondue_panel.png</c>, <c>fondue_table.png</c> and
/// <c>fondue_split.png</c>. The pack is put back as it was; progress in <c>test_output/fonduecheck</c>.
/// </summary>
public partial class FondueCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--fonduecheck");
    private static bool Windowed => DisplayServer.GetName() != "headless";
    private const long Pal = 2;

    private readonly Func<FootPlayer?> _me;
    private FootPlayer? _partner;
    private Camera3D? _cam;

    public FondueCheck(ItemController items, Func<FootPlayer?> me) : base(items, "fonduecheck", "", "fondue_") => _me = me;
    public FondueCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();
    private static TogetherService Svc => TogetherService.Instance!;

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => TogetherService.Instance != null && Me is { } m && m.IsOnFloor(), 90))
        {
            Fail("no together service or no player on the ground");
            return;
        }
        await Until(() => Me!.Down != 0, 3);
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        var me = Me!;
        var inv = _items.Inventory;
        var kept = new ItemStack[Inventory.Size];
        for (int i = 0; i < Inventory.Size; i++) { kept[i] = inv[i]; inv.Put(i, ItemStack.Empty); }
        try { await Run(me, inv); }
        finally
        {
            for (int i = 0; i < Inventory.Size; i++) inv.Put(i, kept[i]);
        }
        await Finish(0.5);
    }

    private async Task Run(FootPlayer me, Inventory inv)
    {
        var panel = GetParent().GetNodeOrNull<FonduePanel>("FonduePanel");
        Expect(panel != null, "the pot's panel is there");
        _partner = new FootPlayer { Name = "Partner", Npc = true, Terrain = me.Terrain, Origin = me.Origin };
        GetParent().AddChild(_partner);
        _partner.DebugLaunch(me.GlobalPosition + new Vector3(1.6f, 0.3f, 0.6f), Vector3.Zero);
        Svc.Pair(_partner, Pal);

        // the fire, laid through the item's own path
        var placed = PlacedObjects.Instance!;
        me.LookYaw = 0f;
        me.LookPitch = -0.6f;
        int fire = Hold(ItemId.Campfire);
        await Seconds(0.3);   // the camera follows the new pitch
        var aim = FlagGhost.Aim(me, Placeables.ForItem(ItemId.Campfire));
        GD.Print($"{Log} the fire's ghost: {aim.Kind} {aim.Valid} {aim.Reason}");
        // a fire already burning here (a run before on the same user://) serves as well
        bool laid = aim.Kind == FlagAimKind.Plant;
        if (laid) _items.UseSlot(me, fire);
        Expect(await Until(() => Fire(placed, me) != null, 4), laid ? "a campfire laid" : "a campfire burns here already");
        if (Fire(placed, me) is not { } fireAt) return;
        // sit down by it: 1.5 m off, the partner across
        me.DebugLaunch(fireAt + new Vector3(0, 0.3f, 1.5f), Vector3.Zero);
        _partner.DebugLaunch(fireAt + new Vector3(1.4f, 0.3f, 0.4f), Vector3.Zero);
        await Seconds(0.8);

        // a fondue pot used at it: the shared pot, not the old share
        Expect(Svc.ServeStart(AdventureService.LocalPeer, atFire: false) != null, "no pot started at a fire without bringing one");
        _items.UseSlot(me, Hold(ItemId.FonduePot));
        if (!await Until(() => Svc.MyPot != null, 3)) { Expect(false, "the fondue pot used at the fire starts a table"); return; }
        Expect(inv.CountPlain(ItemId.FonduePot) == 0, "the pot left the pack");
        Expect(await Until(() => panel?.Open == true, 2), "the panel opens");
        int pot = Svc.MyPot!.Id;
        Expect(Svc.ServeSit(Pal, pot) == null && Svc.MyPot.Seats.Length == 2, "the partner sits down");
        Expect(Svc.Table != null, "the caquelon stands by the fire");

        // the ingredients
        inv.Add(ItemId.Cheese, 2);
        inv.Add(ItemId.Bread, 2);
        inv.Add(ItemId.WhiteWine, 1);
        inv.Add(ItemId.Apple, 1);
        Svc.AskAdd(ItemId.Apple);
        Expect(inv.CountPlain(ItemId.Apple) == 1, "an apple does not go in: given back");
        Svc.AskAdd(ItemId.Cheese);
        Expect(Svc.ServeAdd(Pal, pot, ItemId.Cheese) == null, "the partner adds cheese");
        Expect(await Until(() => Svc.MyPot is { Cooking: true, Dips: 12 }, 2), $"two cheeses: it melts, 12 dips ({Svc.MyPot?.Dips})");
        Svc.AskAdd(ItemId.Bread);
        Svc.AskAdd(ItemId.WhiteWine);
        Svc.ServeAdd(Pal, pot, ItemId.Bread);
        Expect(await Until(() => Svc.MyPot is { Wine: true } p && p.HasBread(AdventureService.LocalPeer), 2), "bread on the fork, the wine in");
        Expect(panel!.Action.StartsWith("Dip"), $"the big action is to dip ('{panel.Action}')");

        // la huit: a scripted lemniscate through the panel's own sample path, two and a bit eights
        int eights = panel.Eights;
        for (int i = 0; i <= 160; i++)
        {
            double u = i / 72.0, a = 2 * Math.PI * u, s = Math.Sin(a), c = Math.Cos(a), d = 1 + s * s;
            panel.FeedSample(new Vector2(0.5f + (float)(c / d) * 0.4f, 0.5f + (float)(s * c / d) * 0.4f));
            await Seconds(1.0 / 30);
        }
        Expect(panel.Eights > eights, $"eights stirred ({panel.Eights - eights})");
        if (Windowed) GD.Print($"{Log} shot {Shot("panel")}");
        if (Windowed)
        {
            panel.Visible = false;
            GD.Print($"{Log} shot {await ShotAt("table", Svc.Table!.GlobalPosition, me.GlobalPosition)}");
            panel.Visible = true;
        }

        // dips: the partner first, then ours, the seed's second: our bread is lost
        string? lost = null;
        Svc.BreadLostAt += who => lost ??= who.ToString();
        string? line = null;
        Svc.ForfeitShown += (_, _, l) => line = l;
        Expect(Svc.ServeDip(Pal, pot) == DipResult.Ok, "the partner dips");
        Svc.AskDip();
        Expect(await Until(() => lost == AdventureService.LocalPeer.ToString(), 2), "our bread sinks in the cheese");
        Expect(await Until(() => Svc.ForfeitUntil > GameClock.Now, 1), "a forfeit is owed: the popup");
        Svc.AskForfeit(Forfeit.Line);
        Expect(await Until(() => line != null, 2) && line!.Contains(Svc.NameOfPeer(AdventureService.LocalPeer)), $"the forfeit's line to the table ('{line}')");

        // eat it up: a fresh bread, dips in turn until the pot is empty
        Svc.AskAdd(ItemId.Bread);
        for (int i = 0; i < 12 && Svc.MyPot != null; i++)
        {
            await Seconds(2.05);
            Svc.ServeDip(Pal, pot);
            Svc.AskDip();
            if (Svc.MyPot is { } v && !v.HasBread(AdventureService.LocalPeer) && inv.CountPlain(ItemId.Bread) == 0) inv.Add(ItemId.Bread, 1);
            if (Svc.MyPot is { } w && !w.HasBread(AdventureService.LocalPeer)) Svc.AskAdd(ItemId.Bread);
            if (Svc.Pots.Get(pot) is { } bp && !bp.Bread.Contains(Pal)) Svc.ServeAdd(Pal, pot, ItemId.Bread);
        }
        Expect(await Until(() => Svc.MyPot == null, 6), "the pot is empty: the table ends");
        Expect(Svc.LastFeast.StartsWith("[stamina] buff fed"), $"the feast ({Svc.LastFeast})");
        var m = me.StaminaMeter;
        Expect(m.BuffLeft(StaminaBuff.Fed) > 0 && m.BuffLeft(StaminaBuff.Warm) > 0, $"Fed and Warm on the stamina ({m.BuffLeft(StaminaBuff.Fed):F0} s, {m.BuffLeft(StaminaBuff.Warm):F0} s)");
        Expect(_partner.StaminaMeter.BuffLeft(StaminaBuff.Fed) > 0, "the partner is fed too");
        Expect(panel.Open == false, "the panel closes");

        // a second pot, nobody stirring and no wine: it splits
        inv.Add(ItemId.FonduePot, 1);
        inv.Add(ItemId.Cheese, 2);
        _items.UseSlot(me, Hold(ItemId.FonduePot));
        Expect(await Until(() => Svc.MyPot != null, 3), "a second pot");
        int second = Svc.MyPot!.Id;
        Svc.ServeSit(Pal, second);
        Svc.AskAdd(ItemId.Cheese);
        Svc.AskAdd(ItemId.Cheese);
        Expect(await Until(() => Svc.MyPot is { Splitted: true }, 30), $"left unstirred on a high heat, the cheese splits ({Svc.MyPot?.Split:F2})");
        await Seconds(0.5);
        Expect(Svc.Table?.ShownSplit >= 0.99f, "the cheese shows it");
        if (Windowed)
        {
            panel!.Visible = false;
            GD.Print($"{Log} shot {await ShotAt("split", Svc.Table!.GlobalPosition, me.GlobalPosition)}");
            panel.Visible = true;
        }
        Svc.AskLeave();
        Expect(await Until(() => Svc.MyPot == null, 2), "leaving the table: no feast, the panel goes");
    }

    private static Vector3? Fire(PlacedObjects placed, FootPlayer me)
    {
        double now = World.WorldClock.EnvNow;
        foreach (var o in placed.All.Values)
            if (o.Kind == PlacedKind.Campfire && Crafting.CampfireClock.Burning(o.Payload, now)
                && o.WorldTransform(placed.Origin).Origin is var at && at.DistanceTo(me.GlobalPosition) < 8f) return at;
        return null;
    }

    /// <summary>The item in the last hotbar slot, selected (as CampfireProbe holds one).</summary>
    private int Hold(ItemId id)
    {
        var inv = _items.Inventory;
        int slot = Inventory.HotbarSize - 1;
        if (inv.CountPlain(id) == 0) inv.Add(id, 1);
        for (int i = 0; i < Inventory.Size; i++)
        {
            if (inv[i].IsEmpty || inv[i].Id != id || i == slot) continue;
            var other = inv[slot];
            inv.Put(slot, inv[i]);
            inv.Put(i, other);
            break;
        }
        inv.Select(slot);
        return slot;
    }

    /// <summary>A picture of the pot from above the table.</summary>
    private async Task<string> ShotAt(string name, Vector3 pot, Vector3 me)
    {
        _cam ??= new Camera3D { Name = "FondueCam", Fov = 45f };
        if (_cam.GetParent() == null) GetParent().AddChild(_cam);
        var away = (pot - me) with { Y = 0 };
        away = away.LengthSquared() > 0.01f ? away.Normalized() : Vector3.Back;
        _cam.GlobalPosition = pot + away * 1.1f + Vector3.Up * 1.2f;
        _cam.LookAt(pot + Vector3.Up * 0.4f, Vector3.Up);
        var was = GetViewport().GetCamera3D();
        _cam.MakeCurrent();
        for (int i = 0; i < 3; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        string path = Shot(name);
        was?.MakeCurrent();
        return path;
    }
}
