using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Birthdays;

/// <summary>
/// <c>--birthdaycheck --date 2026-11-01 --birthday 01-11-1996 --birthday-public --world fixture --chunks fixture:flat --systems ui,physics,occasions</c>
/// (quick tier, #1423): offline, this client plays the server's part, and the pinned birthday saves nothing. It is the birthday, the
/// age 30; the player celebrates (shown); the gift arrives in the pack once, and opening it gives 2026's collectible, the party hat, the
/// cake, three rockets and CHF 30, with the paper; no second gift this year; the party hat is on the head; the cake set down loses a
/// slice per E (its mesh follows) and is gone after the eighth; a wish to oneself is refused; a rocket lights the birthday show
/// (its seed from the name and the day), a second one at once is refused and kept; a print by the cake has the birthday frame.
/// </summary>
public partial class BirthdayCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--birthdaycheck");

    private readonly Func<FootPlayer?> _me;

    public BirthdayCheck(ItemController items, Func<FootPlayer?> me) : base(items, "birthdaycheck") => _me = me;
    public BirthdayCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private Inventory Pack => _items.Inventory;

    private int Slot(ItemId id)
    {
        for (int i = 0; i < Inventory.Size; i++) if (Pack[i].Id == id) return i;
        return -1;
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (Pack[i].Id == id) n += Pack[i].Count;
        return n;
    }

    public override async void _Ready()
    {
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => BirthdayService.Instance != null && Me is { } m && m.IsOnFloor() && m.Origin != null, 90))
        {
            Fail("no birthday service or no player on the ground");
            return;
        }
        var me = Me!;
        var svc = BirthdayService.Instance!;

        Expect(MyBirthday.IsToday, $"today ({Calendar.Today:yyyy-MM-dd}) is the birthday ({MyBirthday.Date})");
        Expect(MyBirthday.Age == 30, $"the age turned is 30 (got {MyBirthday.Age})");
        Expect(await Until(() => svc.Celebrants.ContainsKey(svc.Self), 5), "the player celebrates, shown");

        // the gift
        Expect(await Until(() => Slot(ItemId.BirthdayGift) >= 0, 10), "the gift arrives in the pack");
        int gift = Slot(ItemId.BirthdayGift);
        Expect(gift >= 0 && Pack[gift].Data == "2026", $"the gift is 2026's (data '{(gift >= 0 ? Pack[gift].Data : "")}')");
        Expect(!MyBirthday.TakeGift(), "no second gift this year");
        int cash = Pack.Cash, paper = BirthdayFx.Count;
        var collectible = ItemDefs.BirthdayCollectibles[BirthdayRules.CollectibleIndex(2026, ItemDefs.BirthdayCollectibles.Length)];
        if (gift >= 0) _items.UseSlot(me, gift);
        Expect(Count(ItemId.BirthdayGift) == 0, "the gift is opened");
        Expect(Count(collectible) == 1, $"it held 2026's collectible, the {collectible}");
        Expect(Count(ItemId.PartyHat) == 1 && Count(ItemId.BirthdayCake) == 1, "and a party hat and a cake");
        Expect(Count(ItemId.BirthdayRocket) == BirthdayRules.ShowsPerDay, $"and {BirthdayRules.ShowsPerDay} rockets (got {Count(ItemId.BirthdayRocket)})");
        Expect(Pack.Cash == cash + BirthdayRules.GiftCash, $"and CHF {BirthdayRules.GiftCash} (cash {cash} -> {Pack.Cash})");
        Expect(BirthdayFx.Count > paper, "the paper flew");

        // the party hat, worn while the head slot is empty
        Expect(await Until(() => me.HeadwearId == (int)Headwear.PartyHat, 3), $"the party hat is on (headwear {me.HeadwearId})");

        await Cake(me, svc);

        Expect(svc.ServeWish(svc.Self, svc.Self) != null, "a wish to oneself is refused");

        // the fireworks
        int rockets = Count(ItemId.BirthdayRocket);
        _items.UseSlot(me, Slot(ItemId.BirthdayRocket));
        Expect(Count(ItemId.BirthdayRocket) == rockets - 1, "a rocket is lit and used up");
        var fw = Festivals.Fireworks.Instance;
        Expect(fw != null && await Until(() => fw.PlayingOwn, 8), "the birthday show plays");
        string name = GameSettings.Current.PlayerName is { Length: > 0 } n ? n : "#1";
        Expect(fw?.ShowSeed == BirthdayRules.ShowSeed(name, Calendar.Today, 1), $"its seed is the name's and the day's ({fw?.ShowSeed})");
        _items.UseSlot(me, Slot(ItemId.BirthdayRocket));
        Expect(Count(ItemId.BirthdayRocket) == rockets - 1, "a second rocket while the first burns is refused and kept");

        // the Polaroid's birthday frame
        var frame = Image.CreateEmpty(64, 64, false, Image.Format.Rgb8);
        frame.Fill(new Color(0.3f, 0.4f, 0.5f));
        var (_, plain) = PhotoStore.Print(frame);
        var (_, party) = PhotoStore.Print(frame, birthday: true);
        int marked = 0;
        for (int y = PhotoStore.Border + PhotoStore.ImageSize; y < PhotoStore.CardH; y++)
            for (int x = 0; x < PhotoStore.CardW; x++)
                if (!party.GetPixel(x, y).IsEqualApprox(plain.GetPixel(x, y))) marked++;
        Expect(marked > 400, $"a print by a cake has the birthday frame ({marked} px in the margin)");
        Expect(svc.CakeNear(me.GlobalPosition) == false, "no cake near once it is eaten: the next print is plain");

        await Finish(0.5);
    }

    private async Task Cake(FootPlayer me, BirthdayService svc)
    {
        var placed = PlacedObjects.Instance!;
        _items.Inventory.TakeOne(Slot(ItemId.BirthdayCake));
        long id = 0;
        placed.RequestPlace(PlacedKind.BirthdayCake, new Transform3D(Basis.Identity, me.GlobalPosition + new Vector3(1.0f, 0, 0)), "", r => id = r.Object?.Id ?? -1);
        if (!await Until(() => id != 0, 5) || id < 0)
        {
            Expect(false, "the cake is set down");
            return;
        }
        Expect(svc.CakeLeft(id) == BirthdayRules.CakeSlices, $"the cake has {BirthdayRules.CakeSlices} slices");
        Expect(BirthdayService.InteractionFor(me) is { } prompt && prompt.Text.Contains("slice"), "E at the cake offers a slice");
        Expect(BirthdayService.TryInteract(me), "E takes a slice");
        Expect(svc.CakeLeft(id) == BirthdayRules.CakeSlices - 1, $"one slice fewer ({svc.CakeLeft(id)})");
        var mesh = placed.GetNodeOrNull<MeshInstance3D>($"P{id}/Cake");
        Expect(mesh?.Mesh == BirthdayMeshes.Cake(BirthdayRules.CakeSlices - 1), "the cake's mesh shows the slice gone");
        for (int i = 1; i < BirthdayRules.CakeSlices; i++) BirthdayService.TryInteract(me);
        Expect(await Until(() => !placed.All.ContainsKey(id), 3), "the cake is gone after the last slice");
        Expect(!BirthdayService.TryInteract(me), "no cake, no slice");
    }
}
