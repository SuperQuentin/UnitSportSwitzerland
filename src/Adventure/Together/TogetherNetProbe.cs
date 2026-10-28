using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Together;

/// <summary>
/// <c>--togethernet lead|follow</c> with <c>--connect</c> (driven by <c>tools/togethernetcheck.sh</c>, #1266): a server
/// with <c>--together-test --fondueseed 2</c> and two clients, Lea (<c>lead</c>) and Tom (<c>follow</c>, its own
/// <c>user://</c>). What each reads is what reached it from the other, the remote peer:
/// <list type="number">
/// <item>before a party nobody counts as together (no yield bonus for a stranger); after it both count 1;</item>
/// <item>Lea holds hands with Tom (<c>/rope hands Tom</c>, Tom accepts): both books hold the link, Tom sees Lea's body pose
/// the hold; Lea walks 20 m and Tom, with no input, follows;</item>
/// <item>a campfire and a fondue pot: Tom sits, both add and dip; the pot's state arrives on both; Lea's bread is lost (the
/// seed) and Tom sees it; the forfeit's line reaches Tom; at the end both are <c>Fed</c>;</item>
/// <item>a veteran Lea (seeded) and a newcomer Tom: the summit milestone gives Tom the Erstbesteigung cap and Lea the
/// Bergführer cap;</item>
/// <item>Lea in a Battle Royale lobby: nothing together any more, the link is gone on both.</item>
/// </list>
/// </summary>
public partial class TogetherNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--togethernet");
    private const string Password = "togetherpw";

    public TogetherNetProbe(ItemController items) : base(items, "togethernet", "TG", "togethernet_") { }
    public TogetherNetProbe() : this(null!) { }

    private static TogetherService Svc => TogetherService.Instance!;
    private static AdventureService Adventure => AdventureService.Instance!;

    public override async void _Ready()
    {
        _role = Role ?? "LEAD";
        if (!await Joined(150, () => TogetherService.Instance != null && AdventureService.Instance != null)) return;
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        Expect(await Until(() => Adventure.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it");
        if (_role == "LEAD") await Lead(); else await Follow();
        await Finish(6.0);
    }

    private long OtherPeer() => Parties.Client!.Members.First(m => m.Peer != Multiplayer.GetUniqueId()).Peer;

    // ---- Lea ---------------------------------------------------------------------------------------

    private async Task Lead()
    {
        Expect(await Heard("FOLLOW", "ready", 90), "Tom is on");
        await Seconds(1.0);
        Expect(Svc.NearHere == 0, $"no party: nobody counts as together, no yield bonus ({Svc.NearHere})");
        Chat!.Send("/party invite Tom");
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 30), "Lea's party holds Lea and Tom");
        Expect(await Until(() => Svc.NearHere == 1, 10), $"a party member near: together x1 on Lea ({Svc.NearHere})");
        Say("party");

        // the fire first, where the ground is level (the walk below goes uphill on some fixtures)
        Vector3? fireAt = await LayFire();
        Say("fire");

        // hands
        Chat.Send("/rope hands Tom");
        Expect(await Until(() => Svc.Links.Count == 1, 30), "Tom took her hand: the link is in Lea's book");
        Expect(await Until(() => Me!.HandHold != null, 10), "Lea's own hand is posed to the hold");
        Say("hands");
        Expect(await Heard("FOLLOW", "saw-hands", 40), "Tom saw it");

        // walk away 20 m: Tom follows by himself
        async Task Walk(float dx)
        {
            for (int i = 0; i < 100; i++)
            {
                Me!.Velocity = Vector3.Zero;
                Me.GlobalPosition += new Vector3(dx, 0, 0);
                await Seconds(0.15);
            }
        }
        await Walk(0.2f);
        Say("walked");
        Expect(await Heard("FOLLOW", "followed", 60), "Tom followed with no input");
        Expect(await Until(() => Svc.Links.Count == 1, 5), "still linked after the walk");
        await Walk(-0.2f);
        await Seconds(2.0);

        await Pot(fireAt);

        // the mentor: Lea a seeded veteran, Tom a newcomer
        Chat.Send("/rope test veteran");
        await Seconds(1.0);
        Say("veteran");
        Expect(await Heard("FOLLOW", "mentored", 60), "Tom got his cosmetic");
        Expect(await Until(() => CountOf(ItemId.BergfuehrerCap) > 0, 20), "Lea, the guide, got the Bergführer cap");

        // a Battle Royale round: nothing together
        if (!await LogIn(Password)) return;
        Chat.Send("/rope hands Tom");
        Expect(await Until(() => Svc.Links.Count == 1, 15), "linked again before the round");
        Chat.Send("/br open here");
        Expect(await Until(() => BattleRoyale.BrManager.Instance?.State.Phase is BattleRoyale.BrPhase.Lobby, 15), "a Battle Royale lobby is open");
        Chat.Send("/br join");
        Expect(await Until(() => BattleRoyale.BrManager.Instance?.MyEntry != null, 15), "Lea joined it");
        Expect(await Until(() => Svc.Links.Count == 0, 15), "in the round the link is gone");
        Say("round");
        Chat.Send("/br leave");
        await Until(() => BattleRoyale.BrManager.Instance?.MyEntry == null, 15);
        Chat.Send("/br cancel");
        Say("done");
    }

    private async Task<Vector3?> LayFire()
    {
        var me = Me!;
        var inv = _items.Inventory;
        int fire = Hold(ItemId.Campfire);
        me.LookYaw = 0f;
        me.LookPitch = -0.6f;
        await Seconds(0.4);
        var placed = PlacedObjects.Instance!;
        var aim = FlagGhost.Aim(me, Placeables.ForItem(ItemId.Campfire));
        GD.Print($"{Log} the fire's ghost: {aim.Kind} {aim.Valid} {aim.Reason}");
        _items.UseSlot(me, fire);
        Expect(await Until(() => Fire(placed, me) != null, 10), "a campfire burns by Lea");
        return Fire(placed, me);
    }

    private async Task Pot(Vector3? fireAt)
    {
        var me = Me!;
        var inv = _items.Inventory;
        inv.Add(ItemId.Cheese, 2);
        inv.Add(ItemId.Bread, 8);
        if (fireAt is not { } at) return;
        me.DebugLaunch(at + new Vector3(0, 0.3f, 1.5f), Vector3.Zero);
        var placed = PlacedObjects.Instance!;
        await Seconds(1.0);
        Say("atfire-lea");
        Expect(await Heard("FOLLOW", "atfire", 30), "Tom is at the fire");
        await Seconds(1.0);
        GD.Print($"{Log} stations {Crafting.CraftStations.Where(me).Here}, fire {Fire(placed, me)}");
        _items.UseSlot(me, Hold(ItemId.FonduePot));
        Expect(await Until(() => Svc.MyPot != null, 15), "the pot used at the fire starts a table for Lea");
        Expect(await Until(() => Svc.MyPot is { Seats.Length: 2 }, 20), "Tom sat down: two seats on Lea's panel");
        Svc.AskAdd(ItemId.Cheese);
        Svc.AskAdd(ItemId.Cheese);
        Expect(await Until(() => Svc.MyPot is { Cooking: true, Dips: 12 }, 15), $"cheese in: 12 dips ({Svc.MyPot?.Dips})");
        Svc.AskAdd(ItemId.Bread);
        Expect(await Until(() => Svc.MyPot?.HasBread(Multiplayer.GetUniqueId()) == true, 10), "Lea's bread is on the fork");
        Say("cooking");
        Expect(await Heard("FOLLOW", "breadin", 30), "Tom's bread is in too");
        Svc.AskStir(0.9f);
        string? lost = null;
        Svc.BreadLostAt += who => lost ??= who.ToString();
        // Tom dips first, then Lea: the seed's second dip loses a bread
        Expect(await Heard("FOLLOW", "dipped", 30), "Tom dipped");
        Svc.AskDip();
        Expect(await Until(() => lost == Multiplayer.GetUniqueId().ToString(), 10), "Lea's bread sinks in the cheese");
        Expect(await Until(() => Svc.ForfeitUntil > GameClock.Now, 5), "a forfeit is owed to Lea");
        Svc.AskForfeit(Forfeit.Line);
        Say("forfeit");
        for (int i = 0; i < 16 && Svc.MyPot != null; i++)
        {
            if (Svc.MyPot is { } p && !p.HasBread(Multiplayer.GetUniqueId())) Svc.AskAdd(ItemId.Bread);
            await Seconds(2.1);
            Svc.AskDip();
        }
        Expect(await Until(() => Svc.MyPot == null, 30), "the pot is empty: the table ends");
        Expect(await Until(() => me.StaminaMeter.BuffLeft(StaminaBuff.Fed) > 0, 10), $"Lea is Fed ({me.StaminaMeter.BuffLeft(StaminaBuff.Fed):F0} s)");
        Say("fed");
        Expect(await Heard("FOLLOW", "fed", 30), "Tom is fed too");
    }

    // ---- Tom ---------------------------------------------------------------------------------------

    private async Task Follow()
    {
        Say("ready");
        await Seconds(1.0);
        Expect(Svc.NearHere == 0, $"no party yet: no together on Tom ({Svc.NearHere})");
        Expect(await Until(() => InviteService.Client?.Incoming.Any(v => v.Kind == Parties.Kind) == true, 90), "Tom is invited to Lea's party");
        if (InviteService.Client?.Incoming.FirstOrDefault(v => v.Kind == Parties.Kind) is { } party) InviteService.Client.Accept(party.Id);
        Expect(await Until(() => Parties.Client is { Members.Count: 2 }, 20), "Tom's party holds both");
        Expect(await Until(() => Svc.NearHere == 1, 10), $"the party yield applies to Tom too: together x1 ({Svc.NearHere})");
        Expect(await Heard("LEAD", "party", 30), "Lea has her side");

        // the hold
        Expect(await Until(() => InviteService.Client?.Incoming.Any(v => v.Kind == TogetherService.RopeKind) == true, 60), "Lea asks to hold hands");
        if (InviteService.Client?.Incoming.FirstOrDefault(v => v.Kind == TogetherService.RopeKind) is { } hold) InviteService.Client.Accept(hold.Id);
        Expect(await Until(() => Svc.Links.Count == 1, 20), "the link is in Tom's book");
        long lea = OtherPeer();
        Expect(await Until(() => Svc.Body(lea) is { HandHold: not null }, 10), "Tom sees Lea's remote body posed to hold his hand");
        Say("saw-hands");
        Expect(await Heard("LEAD", "walked", 90), "Lea walked 20 m");
        Expect(await Until(() => Svc.Body(lea) is { } b && Me!.GlobalPosition.DistanceTo(b.GlobalPosition) < 3.5f, 20),
            $"with no input Tom followed: {(Svc.Body(lea) is { } l ? Me!.GlobalPosition.DistanceTo(l.GlobalPosition) : -1):F1} m from Lea");
        Say("followed");

        // the pot
        var inv = _items.Inventory;
        inv.Add(ItemId.Bread, 8);
        Expect(await Heard("LEAD", "atfire-lea", 120), "Lea is back at the fire");
        var placed = PlacedObjects.Instance!;
        if (placed.All.Values.FirstOrDefault(o => o.Kind == PlacedKind.Campfire) is { } fire)
            Me!.DebugLaunch(fire.WorldTransform(placed.Origin).Origin + new Vector3(1.4f, 0.3f, 0.4f), Vector3.Zero);
        await Seconds(1.0);
        Say("atfire");
        Expect(await Until(() => Svc.OfferedHere, 40), "the pot is offered to Tom");
        Svc.TryPotInteract(Me!);
        Expect(await Until(() => Svc.MyPot != null, 10), "Tom sat down");
        Expect(await Heard("LEAD", "cooking", 60), "Lea's pot cooks");
        Expect(await Until(() => Svc.MyPot is { Cooking: true, Dips: 12 }, 10), $"the pot state reached Tom: cooking, 12 dips ({Svc.MyPot?.Dips})");
        Svc.AskAdd(ItemId.Bread);
        Expect(await Until(() => Svc.MyPot?.HasBread(Multiplayer.GetUniqueId()) == true, 10), "Tom's bread is on the fork");
        Say("breadin");
        string? lost = null;
        Svc.BreadLostAt += who => lost ??= who.ToString();
        string? line = null;
        Svc.ForfeitShown += (_, _, l) => line = l;
        await Seconds(1.5);
        Svc.AskDip();
        Say("dipped");
        Expect(await Until(() => lost == lea.ToString(), 30), "Tom sees Lea's bread sink");
        Expect(await Until(() => line != null, 30), $"the forfeit's line reaches Tom ('{line}')");
        for (int i = 0; i < 16 && Svc.MyPot != null; i++)
        {
            if (Svc.MyPot is { } p && !p.HasBread(Multiplayer.GetUniqueId())) Svc.AskAdd(ItemId.Bread);
            await Seconds(2.1);
            Svc.AskDip();
        }
        Expect(await Until(() => Svc.MyPot == null, 30), "the table ended on Tom's side");
        Expect(await Until(() => Me!.StaminaMeter.BuffLeft(StaminaBuff.Fed) > 0, 10), $"Tom is Fed ({Me!.StaminaMeter.BuffLeft(StaminaBuff.Fed):F0} s)");
        Say("fed");

        // the mentor
        Expect(await Heard("LEAD", "veteran", 90), "Lea is a veteran");
        Chat!.Send("/rope test summit");
        Expect(await Until(() => CountOf(ItemId.ErstbesteigungCap) > 0, 20), "Tom, the newcomer, got the Erstbesteigung cap");
        Say("mentored");

        // the round
        Expect(await Heard("LEAD", "round", 90), "Lea is in a round");
        Expect(await Until(() => Svc.Links.Count == 0, 15), "the link is gone on Tom's side");
        Expect(await Heard("LEAD", "done", 60), "Lea is done");
    }

    private static Vector3? Fire(PlacedObjects placed, FootPlayer me)
    {
        double now = World.WorldClock.EnvNow;
        foreach (var o in placed.All.Values)
            if (o.Kind == PlacedKind.Campfire && Crafting.CampfireClock.Burning(o.Payload, now)
                && o.WorldTransform(placed.Origin).Origin is var at && at.DistanceTo(me.GlobalPosition) < 8f) return at;
        return null;
    }

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
}
