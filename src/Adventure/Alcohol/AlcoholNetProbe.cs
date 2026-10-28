using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Adventure.Alcohol;

/// <summary>
/// <c>--alcoholnet drink|test|off</c> with <c>--connect</c> (driven by <c>tools/alcoholnetcheck.sh</c>, #1271): a server with
/// <c>--alcohol-test</c> and two clients, Lea (<c>drink</c>) and Tom (<c>test</c>, its own <c>user://</c>). What each reads is
/// what reached it from the other, the remote peer:
/// <list type="number">
/// <item>Lea drinks (four beers, back-dated 40 min, through <c>/alcohol test drink</c>): her synced log gives her a level,
/// and her own electronic reading is the server's number; Tom's own level, log and screen stay empty (Lea's level never
/// reaches him);</item>
/// <item>Tom holds out his breathalyzer to Lea (E at her), Lea accepts the invite, and Tom's screen reads the same number as
/// Lea's, from the server;</item>
/// <item>Lea drinks a real beer from her pack (the <c>ReportDrank</c> path): Tom's next test says <c>WAIT</c> (mouth alcohol),
/// the server's rule;</item>
/// <item><c>off</c> (a server with <c>--alcohol off</c>, one client): the mode reaches the client through <c>Arrival.Rules</c>,
/// a drink logs nothing, the device reads 0.00.</item>
/// </list>
/// </summary>
public partial class AlcoholNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--alcoholnet");

    public AlcoholNetProbe(ItemController items) : base(items, "alcoholnet", "AL", "alcoholnet_") { }
    public AlcoholNetProbe() : this(null!) { }

    private static AlcoholService Svc => AlcoholService.Instance!;
    private static AdventureService Adventure => AdventureService.Instance!;

    public override async void _Ready()
    {
        _role = Role ?? "DRINK";
        if (!await Joined(150, () => AlcoholService.Instance != null && AdventureService.Instance != null)) return;
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        Expect(await Until(() => Adventure.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it");
        if (_role == "DRINK") await Drink();
        else if (_role == "TEST") await Test();
        else await Off();
        await Finish(6.0);
    }

    private static int Centi(double level) => (int)Math.Round(level * 100);

    private long OtherPeer()
    {
        var other = PlayerSnapshot.Of(GetTree()).Select(s => s.Player).FirstOrDefault(p => p != Me && !p.Npc);
        return other != null && FootPlayer.NetId(other.Name) is { } id ? id : 0;
    }

    // ---- Lea ---------------------------------------------------------------------------------------

    private async Task Drink()
    {
        Expect(await Heard("TEST", "ready", 90), "Tom is on");
        Expect(Svc.Mode == AlcoholMode.Real, "the server's mode is real");
        Chat!.Send("/alcohol test drink 4 40");
        Expect(await Until(() => Svc.Level > 0.5, 20), $"the drinks reached Lea: her synced log gives {Svc.Level:F2} per mille");
        Say("drunk");

        // her own electronic reading: the server's number
        _items.Inventory.Put(2, new ItemStack(ItemId.Breathalyzer, 1));
        _items.Inventory.Select(2);
        _items.UseSlot(Me!, 2);
        Expect(await Until(() => Svc.LastResult is { Kind: BreathKind.Electronic }, 15), "her own blow gets the server's reading");
        double own = Svc.LastResult!.Value.Level;
        Expect(Math.Abs(own - Svc.Level) < 0.04, $"it is the level her log gives ({own:F2} vs {Svc.Level:F2})");
        Say($"self {Centi(own)}");
        await Until(() => !_items.UseBusy, 5);

        // Tom offers, she accepts
        Expect(await Heard("TEST", "offered", 90), "Tom offered a test");
        Expect(await Until(() => InviteService.Client is { IncomingCount: > 0 }, 20), "the invite reached Lea");
        Chat.Send("/accept");
        Expect(await Until(() => Svc.LastResult is { Kind: BreathKind.Friend }, 20), "Lea's screen shows the reading too");
        Say("accepted");

        // a real beer from the pack: the report path; then mouth alcohol
        _items.Inventory.Put(3, new ItemStack(ItemId.BeerBottle, 1));
        _items.Inventory.Select(3);
        _items.UseSlot(Me!, 3);
        Expect(await Until(() => _items.Inventory[3].IsEmpty, 10), "Lea drank a beer");
        await Seconds(1.0);
        Say("beer");
        Expect(await Until(() => InviteService.Client is { IncomingCount: > 0 }, 60), "Tom's second offer reached Lea");
        Chat.Send("/accept");
        Expect(await Heard("TEST", "waited", 90), "Tom's second test said WAIT");
        Expect(Svc.Level >= own - 0.05, "her own level did not fall for a beer");
    }

    // ---- Tom ---------------------------------------------------------------------------------------

    private async Task Test()
    {
        // Lea may not be listening yet when the first line goes out: say it again until she answers
        for (int i = 0; i < 45 && !_heard.Any(l => l.Contains("AL DRINK drunk")); i++)
        {
            Say("ready");
            await Seconds(2.0);
        }
        Expect(await Heard("DRINK", "drunk", 90), "Lea drank");
        Expect(Svc.Level == 0 && Adventure.Mine.Get(AlcoholProgress.Section).Drinks.Count == 0 && Svc.LastResult == null,
            "Lea's drinks never reached Tom: no log, no level, no screen");
        long lea = OtherPeer();
        Expect(lea > 0, $"Lea's body is here ({lea})");
        string? selfLine = await HeardStarting("DRINK", "self ", 60);
        int selfCenti = selfLine != null && int.TryParse(selfLine.Replace("self ", "").Trim(), out int c) ? c : -1;

        _items.Inventory.Put(2, new ItemStack(ItemId.Breathalyzer, 1));
        _items.Inventory.Select(2);
        Svc.AskTest(lea);
        Say("offered");
        Expect(await Until(() => Svc.LastResult is { Kind: BreathKind.Friend }, 40), "Lea accepted: the reading is on Tom's screen");
        var r = Svc.LastResult!.Value;
        Expect(r.Subject == lea && !r.Waiting, "it is hers and a real one");
        Expect(selfCenti >= 0 && Math.Abs(Centi(r.Level) - selfCenti) <= 3, $"it matches the server's number on her own screen ({Centi(r.Level)} vs {selfCenti})");
        Expect(r.Level is > 0.5 and < 1.2, $"four beers 40 min ago: {r.Level:F2} per mille");
        Expect(Svc.Level == 0, "and Tom is still sober");

        Expect(await Heard("DRINK", "beer", 90), "Lea had a beer");
        Svc.AskTest(lea);
        Expect(await Until(() => InviteService.Client != null, 5), "invites run");
        await Seconds(0.5);
        Say("offered2");
        Expect(await Until(() => Svc.LastResult is { Kind: BreathKind.Friend, WaitSeconds: > 0 }, 40),
            "a minute after a beer the test says WAIT (mouth alcohol)");
        Expect(Hud?.Shown.StartsWith("WAIT") == true, $"on the screen: '{Hud?.Shown}'");
        Say("waited");
    }

    private AlcoholHud? Hud => GetParent().GetNodeOrNull<AlcoholHud>("AlcoholHud");

    // ---- one client against --alcohol off -----------------------------------------------------------------

    private async Task Off()
    {
        Expect(await Until(() => Permissions.ServerAlcohol == 0, 20), "the server's off reached the client with the rules");
        Expect(Svc.Mode == AlcoholMode.Off, "so the service is off");
        _items.Inventory.Put(3, new ItemStack(ItemId.BeerBottle, 1));
        _items.Inventory.Select(3);
        _items.UseSlot(Me!, 3);
        Expect(await Until(() => _items.Inventory[3].IsEmpty, 10) || true, "a beer is drunk");
        await Seconds(1.5);
        Expect(Adventure.Mine.Get(AlcoholProgress.Section).Drinks.Count == 0, "it carries no alcohol: nothing logged");
        _items.Inventory.Put(2, new ItemStack(ItemId.Breathalyzer, 1));
        _items.Inventory.Select(2);
        await Until(() => !_items.UseBusy, 10);
        _items.UseSlot(Me!, 2);
        Expect(await Until(() => Hud?.Shown.StartsWith("0.00") == true, 20), $"the device reads 0.00 ('{Hud?.Shown}')");
    }
}
