using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.World.Locate;

/// <summary>
/// <c>--locatenet</c> with <c>--connect</c> on the garage course with the fixture POIs (driven by
/// <c>tools/locatenetcheck.sh</c>, #1277): <c>/locate</c> answers come from the server's own index and lists
/// (this client never builds one online), a parking entrance is moved onto the block's garage door, and
/// <c>/go</c> on the coordinates of an answer moves this player there under the server's teleport rule.
/// </summary>
public partial class LocateNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--locatenet");

    public LocateNetProbe(ItemController items) : base(items, "locatenet") { }
    public LocateNetProbe() : this(null!) { }

    public override async void _Ready()
    {
        _role = "A";
        if (!await Joined(150)) return;

        // the server may still be indexing its buildings: ask again until it answers
        string? garage = null;
        for (int i = 0; i < 20 && garage == null; i++)
        {
            int from = _heard.Count;
            Chat!.Send("/locate apartment building + parking entrance");
            await Until(() => _heard.Skip(from).Any(l => l.Contains("garage door") || l.StartsWith("Still indexing", StringComparison.Ordinal)), 10);
            garage = _heard.Skip(from).FirstOrDefault(l => l.Contains("garage door"));
            if (garage == null) await Seconds(1);
        }
        Expect(garage != null, "the server answered /locate with a garage door");
        Expect(LocateService.Instance?.Buildings == null, "this client built no index of its own online");

        int heard = _heard.Count;
        Chat!.Send("/locate castle");
        Expect(await Until(() => _heard.Skip(heard).Any(l => l.Contains("Fixture Castle")), 10), "the server's POIs answer /locate castle");

        if (garage != null && LocateCoords.FindAll(garage) is [var at, ..])
        {
            Chat!.Send($"/go {LocateCoords.Format(at.E, at.N)}");
            Expect(await Until(() => Me is { } me && me.Global.HorizontalDistanceTo(new GlobalPos(at.E, at.N, 0)) < 30, 20),
                $"/go moved this player to the garage door ({Me?.Global})");
        }
        await Finish(1.0);
    }
}
