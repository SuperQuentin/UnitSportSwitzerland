using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Net;

/// <summary>
/// <c>--idscheck ANNA|ZED|IMPOSTOR</c> (#1359, <c>tools/idsnetcheck.sh</c>): saved data follows a player's id, never the
/// name. The server starts with files from before ids, keyed by the name "Anna": an admin and a bank account of 500 CHF.
/// <list type="bullet">
/// <item>ANNA (her own install, named Anna) claims both: an operator, 500 CHF.</item>
/// <item>ZED (the same install, now named Zed) keeps both: they follow the id, not the name.</item>
/// <item>IMPOSTOR (another install, named Anna) gets neither, over the time the claim would take.</item>
/// </list>
/// </summary>
public partial class IdsNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--idscheck");

    private const long Claimed = 500;

    public IdsNetProbe(ItemController items) : base(items, "ids") { }
    public IdsNetProbe() : this(null!) { }

    public override async void _Ready()
    {
        _role = Role ?? "ANNA";
        if (!await Joined(120) || Bank.Instance is not { } bank) { if (_failures == 0) Fail("no bank"); await Finish(0); return; }

        if (_role == "IMPOSTOR")
        {
            // nothing should come: the bounded wait is the time the real owner gets both in (well under it)
            if (await Until(() => Permissions.IsAdmin || bank.Balance > 0, 20))
                Fail($"the impostor got the name's data: admin {Permissions.IsAdmin}, balance {bank.Balance}");
            else GD.Print($"{Log} not an operator, balance {bank.Balance}: the name alone reaches nothing");
        }
        else
        {
            if (!await Until(() => Permissions.IsAdmin && bank.Balance == Claimed, 30))
                Fail($"expected an operator with {Claimed} CHF: admin {Permissions.IsAdmin}, balance {bank.Balance}");
            else GD.Print($"{Log} an operator with {bank.Balance} CHF");
        }
        await Finish(0);
    }
}
