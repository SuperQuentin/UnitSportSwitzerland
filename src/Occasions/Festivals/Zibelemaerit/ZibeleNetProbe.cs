using Godot;
using UnitSport.Adventure;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;

namespace UnitSport.Occasions.Festivals.Zibelemaerit;

/// <summary>
/// <c>--zibelenet a|b</c> with <c>--connect --festival test</c> (driven by <c>tools/zibelenetcheck.sh</c>, #1373): the Zibelemärit over a dedicated server whose
/// calendar reads 22 November 2027 (its one day, held at the fixture start). Everything is asserted on the REMOTE peers. Lea (A) tastes at the Konfetti stall
/// (the stamp reaches her own section and the stall's state comes back over the wire), buys six confetti eggs (they arrive in her pack), throws five with the
/// item's Use (the server counts them from her position and the fifth pays, which reaches her own section) and says so. Tom (B) then tastes at the braid stall: his
/// stamp is his own (not Lea's), he is paid in his own section, holds none of her eggs; a use from 150 m away and a throw from there are refused or uncounted by
/// the server; he buys his own braid.
/// </summary>
public partial class ZibeleNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--zibelenet");

    public ZibeleNetProbe(ItemController items) : base(items, "zibelenet", "ZN", "zibelenet_") { }
    public ZibeleNetProbe() : this(null!) { }

    private static FestivalService Svc => FestivalService.Instance!;
    private static AdventureService Adv => AdventureService.Instance!;
    private static FestivalInstance Mine => Svc.Mine.Instances.GetValueOrDefault("zibelemaerit-2027") ?? new FestivalInstance();

    private void Teleport(double e, double n)
    {
        var p = Me!;
        p.GlobalPosition = p.Origin!.ToWorld(e, n, p.Global.Alt + 0.3);
        p.Velocity = Vector3.Zero;
    }

    private int Count(ItemId id)
    {
        int n = 0;
        for (int i = 0; i < Inventory.Size; i++) if (_items.Inventory[i].Id == id) n += _items.Inventory[i].Count;
        return n;
    }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => FestivalService.Instance != null && AdventureService.Instance != null && ZibeleGame.Instance != null)) return;
        Expect(await Until(() => Adv.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached the server");
        const string Id = ZibeleRules.FestivalId;
        var game = ZibeleGame.Instance!;
        Expect(await Until(() => Svc.RunningOf(Id) is { Day: 1, Site: not null }, 40), "the server runs the market on its day and this client knows it");
        Expect(await Until(() => Me is { } me && me.IsOnFloor(), 30), "the player stands on the ground");
        var site = Svc.RunningOf(Id)!.Site!;
        string? refused = null;
        Svc.Refused += why => refused = why;
        var told = new List<FestivalService.ZibeleEvent>();
        Svc.ZibeleTold += (_, _, what, _, _) => told.Add(what);
        void AtStall(int i)
        {
            var st = ZibeleRules.Layout(site)[i];
            Teleport(st.E, st.N - 2.0);
        }

        if (_role == "A")
        {
            AtStall(3);
            Expect(await Until(() => game.NearStall == 3, 10), "A stands at the Konfetti stall");
            await Seconds(1.0);   // the server learns where the body is from the movement packets: let it
            Expect(ZibeleGame.InteractionFor(Me!) is { } p && p.Text.Contains("Taste"), "the taste is offered");
            ZibeleGame.TryInteract(Me!);
            Expect(await Until(() => told.Contains(FestivalService.ZibeleEvent.Tasted), 10), $"the server told A it tasted ({string.Join(",", told)}; refused: {refused})");
            Expect(await Until(() => Mine.Batzen == ZibeleRules.StampBatzen && Mine.Points == ZibeleRules.StampPoints, 10), $"the taste paid in A's own section ({Mine.Batzen}/{Mine.Points})");
            Expect(ZibeleRules.Stamped(Svc.ZibeleStampsToday(1), 3), "and the stall's stamp came back over the wire");
            Expect(await Until(() => ZibeleGame.InteractionFor(Me!) is { } q && q.Text.Contains("Buy 6 confetti eggs"), 10), "now the eggs are offered");
            ZibeleGame.TryInteract(Me!);
            Expect(await Until(() => Count(ItemId.Konfettiei) == 6, 10), $"six eggs arrived in A's pack (refused: {refused})");
            Expect(await Until(() => Mine.Batzen == ZibeleRules.StampBatzen - 3, 10), $"for 3 Batzen ({Mine.Batzen})");
            int slot = Enumerable.Range(0, Inventory.Size).First(i => _items.Inventory[i].Id == ItemId.Konfettiei);
            _items.Inventory.Select(Math.Min(slot, Inventory.HotbarSize - 1));
            int batzen = Mine.Batzen;
            for (int k = 0; k < 5; k++)
            {
                _items.UseSlot(Me!, slot);
                await Seconds(0.5);
            }
            Expect(Count(ItemId.Konfettiei) == 1, $"A threw five eggs ({Count(ItemId.Konfettiei)} left)");
            Expect(await Until(() => told.Contains(FestivalService.ZibeleEvent.Thrown), 10), $"the server counted the throws from A's position and told A ({string.Join(",", told)})");
            Expect(await Until(() => Mine.Batzen == batzen + ZibeleRules.ThrowBatzen && Mine.Paid.GetValueOrDefault("konfetti:1") == ZibeleRules.ThrowBatzen, 10), $"the fifth throw paid in A's own section ({Mine.Batzen})");
            Say("threw");
            Expect(await Heard("B", "done", 150), "B finished too");
        }
        else
        {
            Expect(await Heard("A", "threw", 150), "A threw");
            AtStall(0);
            Expect(await Until(() => game.NearStall == 0, 10), "B stands at the braid stall");
            await Seconds(1.0);
            Expect(!ZibeleRules.Stamped(Svc.ZibeleStampsToday(1), 3) && !ZibeleRules.Stamped(Svc.ZibeleStampsToday(1), 0), "B's stamps are B's own: nothing is stamped for B");
            Expect(Count(ItemId.Konfettiei) == 0 && Mine.Batzen == 0, "B holds none of A's eggs and no Batzen");
            ZibeleGame.TryInteract(Me!);
            Expect(await Until(() => Mine.Batzen == ZibeleRules.StampBatzen, 10), $"B's own taste paid B ({Mine.Batzen}; refused: {refused})");
            // forged requests from where B does not stand
            Teleport(site.E + 150, site.N + 150);
            await Seconds(1.0);
            refused = null;
            Svc.AskZibele(2);
            Expect(await Until(() => refused?.Contains("Go to the stall") == true, 10), $"a stall from 150 m away is refused ({refused})");
            Expect(Mine.Batzen == ZibeleRules.StampBatzen, "nothing was paid for it");
            // B buys its own braid
            AtStall(0);
            Expect(await Until(() => game.NearStall == 0, 10), "back at the stall");
            await Seconds(1.0);
            ZibeleGame.TryInteract(Me!);
            Expect(await Until(() => Count(ItemId.Zwiebelzopf) == 1 && Mine.Batzen == 0, 10), $"B bought a braid of its own ({Count(ItemId.Zwiebelzopf)}, {Mine.Batzen} Batzen)");
            Say("done");
        }
        await Finish(2.0);
    }
}
