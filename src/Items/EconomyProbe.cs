using Godot;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Items;

/// <summary>
/// <c>--econcheck &lt;password&gt;</c> with <c>--connect</c>: the multiplayer half of issue #32, from
/// a real client against a real dedicated server started with <c>--admin-password</c>.
/// <list type="number">
/// <item>joined as a plain player, the server says not admin, and a vehicle this client conjures
/// is refused by the server (<c>VehicleManager.RequestPark</c>);</item>
/// <item><c>/login</c> flips the admin flag through <c>ChatManager.AdminStatus</c>, and the same
/// park is then spawned by the server;</item>
/// <item>cash deposited away from a bank is refused by the server and stays in the pocket (#213:
/// money moves only at a bank's counter; <c>tools/bankcheck.sh</c> checks the counter itself).</item>
/// </list>
/// Runs on a scratch inventory.
/// </summary>
public partial class EconomyProbe : ChatProbe
{
    public static string? Password => CmdArgs.Has("--econcheck") ? CmdArgs.Value("--econcheck") ?? "" : null;

    private readonly ChatManager _chat;
    private readonly Inventory _inventory;
    private string? _refused;

    public EconomyProbe(ChatManager chat, Inventory inventory) : base(null!, "econ")
    {
        _chat = chat;
        _inventory = inventory;
    }

    public EconomyProbe() : this(null!, null!) { }

    public override async void _Ready()
    {
        VehicleManager.Refused += m => _refused = m;

        // joined, named, and the balance for that name has arrived
        if (!await Until(() => Permissions.Online, 30)) { Fail("never connected"); return; }
        await Seconds(3);
        long before = Bank.Instance?.Balance ?? -1;
        Expect(!Permissions.IsAdmin && !Permissions.CanSpawnVehicles, "a plain player may not spawn vehicles");

        var vehicles = VehicleManager.Instance!;
        var at = GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
        var bike = new VehicleState(RideKind.RoadBike, vehicles.Origin.ToGlobal(at + new Vector3(3, 0, 0)), 0, Vector3.Zero, 100, true, false, 0, 0);

        int count = Count(vehicles);
        vehicles.Park(bike);
        Expect(await Until(() => _refused != null, 5), $"server refused the spawned bike: \"{_refused}\"");
        await Seconds(1);
        Expect(Count(vehicles) == count, "and no vehicle appeared");

        // #262: the admin item and money commands are refused to a plain player
        int cash0 = _inventory.Cash;
        _chat.Send("/money 100");
        _chat.Send("/give me bread 2");
        await Seconds(1.5);
        Expect(_inventory.Cash == cash0 && !_inventory.Contains(ItemId.Bread), "a plain player's /money and /give are refused");
        Expect(!_chat.CanUseCatalogue && !CatalogueUi.Allowed, "no catalogue for a plain player");

        _chat.Send($"/login {Password}");
        Expect(await Until(() => Permissions.IsAdmin, 5), "/login: the server says admin");
        Expect(Permissions.CanSpawnVehicles, "an admin may spawn vehicles");

        _refused = null;
        vehicles.Park(bike);
        Expect(await Until(() => Count(vehicles) > count, 5) && _refused == null, "the admin's bike is spawned by the server");

        _inventory.Add(ItemId.Francs, 25);
        Bank.Instance!.ClaimAll();
        Expect(Bank.Instance.Pending && _inventory.Cash == 25, "cash stays in the pocket until the server answers");
        Expect(await Until(() => !Bank.Instance.Pending, 5), "the server answered the deposit");
        Expect(_inventory.Cash == 25 && Bank.Instance.Balance == before,
            $"refused outside a bank: account {before} -> {Bank.Instance.Balance}, pocket {_inventory.Cash}");

        // #262, as an admin: the server's commands reach this client
        Expect(_chat.CanUseCatalogue && CatalogueUi.Allowed, "an admin gets the catalogue");
        int cash = _inventory.Cash;
        _chat.Send("/money 500");
        Expect(await Until(() => _inventory.Cash == cash + 500, 5), $"/money: pocket {cash} -> {_inventory.Cash}");
        _chat.Send("/give me bread 2");
        Expect(await Until(() => _inventory.Contains(ItemId.Bread), 5), "/give me: bread arrives");
        _chat.Send("/bank set 777");
        Expect(await Until(() => Bank.Instance.Balance == 777, 5), $"/bank set: account {Bank.Instance.Balance}");
        // the server's accounts file is shared user data: put it back
        _chat.Send($"/bank set {Math.Max(before, 0)}");
        await Until(() => Bank.Instance.Balance == Math.Max(before, 0), 5);
        _chat.Send("/clear");
        Expect(await Until(() => !_inventory.Contains(ItemId.Bread), 5), "/clear: the pack is empty");

        await Finish(0);
    }

    private static int Count(Node vehicles) => vehicles.GetChildren().OfType<VehicleBody>().Count();
}
