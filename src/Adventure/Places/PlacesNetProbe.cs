using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

/// <summary>
/// <c>--placesnet walk|watch|back</c> with <c>--connect</c> and <c>--pois fixture</c> (driven by
/// <c>tools/placesnetcheck.sh</c>, #1224). The server finds places; each role checks what reached this client:
/// <list type="bullet">
/// <item><c>walk</c> (A): walks up the peak: the server stamps it, A's mirror and fog show it; in a car at the
/// tower nothing is found, on foot it is; a jump onto the station stamps nothing; E at the summit cross syncs
/// it (its 3 km disc reaches A's fog by <c>FogDelta</c>).</item>
/// <item><c>watch</c> (B, at the same time): none of A's stamps or fog reach B.</item>
/// <item><c>back</c> (A again, after a server restart, the same <c>user://</c>): the stamps, the synced view and
/// the fog are still there (<c>FogFull</c> on join).</item>
/// </list>
/// </summary>
public partial class PlacesNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--placesnet");

    public PlacesNetProbe(ItemController items) : base(items, "placesnet", "PL", "placesnet_") { }
    public PlacesNetProbe() : this(null!) { }

    private static PlacesProgress Mine => AdventureService.Instance!.Mine.Get(PlacesProgress.Section);

    public override async void _Ready()
    {
        _role = Role ?? "WALK";
        if (!await Joined(150, () => AdventureService.Instance != null && PlacesService.Instance is { Catalogue.Count: > 0 })) return;
        // a knocked-out body finds nothing: up first
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        var svc = AdventureService.Instance!;
        Expect(await Until(() => svc.Mine.Ids.Contains(QuestsProgress.Section.Id), 30), "this player's progress reached it");
        Expect(!svc.Authority, "the server keeps the progress");
        Expect(await Until(() => PlacesService.Instance!.FogMessages > 0, 15), "the server sent this player's fog (FogFull)");
        switch (_role)
        {
            case "WALK": await Walk(); break;
            case "WATCH": await Watch(); break;
            default: await Back(); break;
        }
        await Finish(2.0);
    }

    private static Poi Of(PoiKind k) => PlacesService.Instance!.Catalogue.OfKind(k)[0];

    private async Task Walk()
    {
        var places = PlacesService.Instance!;
        var peak = Of(PoiKind.Peak);
        var tower = Of(PoiKind.LookoutTower);
        var station = Of(PoiKind.RailStation);
        Expect(!Mine.Found.ContainsKey(peak.Id), "a new player has not found the peak");

        await WalkTo(peak.E, peak.N - 5);
        Expect(await Until(() => Mine.Found.ContainsKey(peak.Id), 10), "walking up the peak: the server stamped it and the mirror shows it");
        Expect(await Until(() => places.MyFog.Revealed(peak.E + 100, peak.N), 5), "the fog round it reached this client (FogDelta)");
        Say("stamped");

        await WalkTo(tower.E - 120, tower.N);
        var me = Me!;
        Expect(me.SetRide((RideKind)CarCatalog.First), "in a car");
        await WalkTo(tower.E, tower.N - 5);
        await Seconds(3.5);
        Expect(!Mine.Found.ContainsKey(tower.Id), $"driving to the tower finds nothing (ride {me.RideKindId})");
        me.TryInteract();   // E: out of the car
        await Until(() => me.RideKindId == 0, 3);
        await WalkTo(tower.E, tower.N - 3);
        Expect(await Until(() => Mine.Found.ContainsKey(tower.Id), 10), "on foot at the tower: stamped");

        await Jump(station.E, station.N);
        await Seconds(3.5);
        Expect(!Mine.Found.ContainsKey(station.Id), "a jump onto the station stamps nothing");

        await Jump(peak.E, peak.N);
        Expect(await Until(() => PoiMarkers.Instance?.InReach(Me!) == peak, 8), "the summit cross is in reach");
        Expect(Me!.TryInteract(), "E at the cross");
        Expect(await Until(() => Mine.Synced.Contains(peak.Id), 10), "the server synced the viewpoint");
        Expect(await Until(() => places.MyFog.Revealed(peak.E + 2500, peak.N), 5), "its 3 km disc reached this client's fog");
        Say("synced");
    }

    private async Task Watch()
    {
        var places = PlacesService.Instance!;
        var peak = Of(PoiKind.Peak);
        var tower = Of(PoiKind.LookoutTower);
        Expect(await Heard("WALK", "synced", 150), "A synced the cross");
        await Until(() => false, 1.5);
        Expect(!Mine.Found.ContainsKey(peak.Id) && !Mine.Found.ContainsKey(tower.Id), $"none of A's stamps reached B ({string.Join(",", Mine.Found.Keys)})");
        Expect(!places.MyFog.Revealed(peak.E + 2500, peak.N), "nor A's fog");
    }

    private async Task Back()
    {
        var places = PlacesService.Instance!;
        var peak = Of(PoiKind.Peak);
        var tower = Of(PoiKind.LookoutTower);
        Expect(await Until(() => Mine.Found.ContainsKey(peak.Id) && Mine.Found.ContainsKey(tower.Id), 15),
            $"after the server's restart the stamps are kept ({string.Join(",", Mine.Found.Keys)})");
        Expect(Mine.Synced.Contains(peak.Id), "and the synced view");
        Expect(await Until(() => places.MyFog.Revealed(peak.E + 2500, peak.N), 10), $"and the fog ({places.MyFog.Count} cells)");
    }

    /// <summary>Walks the body in 20 m steps twice a second (under the jump rule's 40 m/s).</summary>
    private async Task WalkTo(double e, double n)
    {
        var me = Me!;
        while (true)
        {
            var g = me.Global;
            double de = e - g.E, dn = n - g.N, d = Math.Sqrt(de * de + dn * dn);
            if (d < 0.5) break;
            double step = Math.Min(d, 20);
            me.GlobalPosition += new Vector3((float)(de / d * step), 0.2f, (float)(-dn / d * step));
            me.Velocity = Vector3.Zero;
            await Seconds(0.5);
        }
        GD.Print(FormattableString.Invariant($"{Log} at {me.Global.E:F1}/{me.Global.N:F1}"));
    }

    private async Task Jump(double e, double n)
    {
        var me = Me!;
        var g = me.Global;
        me.GlobalPosition += new Vector3((float)(e - g.E), 0.2f, (float)-(n - g.N));
        me.Velocity = Vector3.Zero;
        await Seconds(0.5);
    }
}
