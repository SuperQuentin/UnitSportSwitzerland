using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Adventure.Places;

/// <summary>
/// <c>--placescheck --systems ui,physics,adventure --pois fixture</c> (quick tier, #1224): offline, this client
/// plays the server's part on the flat fixture with the fixture POIs. Walks to the crossed peak (two ticks
/// stamp it: the section, <see cref="PoiDiscovered"/>, the toast, the fog round it); a <c>"reveal"</c> reward
/// clears the castle's disc; a jump onto the pass stamps nothing until the body has walked 300 m away and
/// back; mounted in a car at the tower nothing counts, on foot it does; E at the summit cross syncs it (its
/// 3 km disc revealed, <see cref="ViewpointSynced"/>, <see cref="Interacted"/>); the starter quests that need
/// places are offered; the Atlas tab and the adventure map open; the save file holds the stamps and the fog.
/// Progress in <c>test_output/placescheck</c>. Windowed, it saves <c>test_output/places_*.png</c>.
/// </summary>
public partial class PlacesCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--placescheck");

    private readonly Func<FootPlayer?> _me;

    public PlacesCheck(ItemController items, Func<FootPlayer?> me) : base(items, "placescheck", "", "places_") => _me = me;
    public PlacesCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private readonly List<AdventureEvent> _events = new();

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => AdventureService.Instance != null && PlacesService.Instance is { Catalogue.Count: > 0 } && Me is { } me && me.IsOnFloor(), 90))
        {
            Fail($"no adventure ({AdventureService.Instance != null}), places ({PlacesService.Instance?.Catalogue.Count}) or player on the ground");
            return;
        }
        // a command-line run drops the body from the free camera's height: a fall that knocks it out
        await Until(() => Me!.Down != 0, 3);
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        await Seconds(1);
        Expect(Me!.Down == 0, "the body is up (a knocked-out body finds nothing)");
        var svc = AdventureService.Instance!;
        var places = PlacesService.Instance!;
        var cat = places.Catalogue;
        svc.Events.Raised += e => _events.Add(e);
        var mine = () => svc.Mine.Get(PlacesProgress.Section);
        var peak = cat.OfKind(PoiKind.Peak)[0];
        var pass = cat.OfKind(PoiKind.Pass)[0];
        var tower = cat.OfKind(PoiKind.LookoutTower)[0];
        var castle = cat.OfKind(PoiKind.Castle)[0];
        var toast = GetParent().GetNodeOrNull<StampToast>("StampToast");

        Expect(svc.Quests.Plans.Contains("places"), "places run: the quests that need them may be offered");
        Expect(svc.Quests.Available(svc.Mine.Get(QuestsProgress.Section)).Any(q => q.Id == "uri-passes"), "the Uri passes quest is offered now");
        Expect(cat.Resolve("peak:fixture-peak") == peak, "the fixture alias resolves");

        // the peak, walked to
        await WalkTo(peak.E, peak.N - 5);
        Expect(await Until(() => mine().Found.ContainsKey(peak.Id), 5), $"walking up the peak stamps it ({string.Join(",", mine().Found.Keys)})");
        Expect(_events.OfType<PoiDiscovered>().Any(d => d.PoiId == peak.Id && d.Kind == "peak" && d.Canton == "VS"), "PoiDiscovered raised for it");
        Expect(mine().Found.TryGetValue(peak.Id, out var stamp) && stamp.Mode == (byte)StampMode.OnFoot, "stamped on foot");
        Expect(await Until(() => toast?.Last.Contains("Fixture Peak") == true, 5), $"the stamp toast says so ({toast?.Last})");
        Expect(places.MyFog.Revealed(peak.E, peak.N) && places.MyFog.Revealed(peak.E + 100, peak.N), "the fog round it is revealed");
        Expect(!places.MyFog.Revealed(peak.E + 2500, peak.N), "but not 2.5 km away");

        // a reward that reveals the map round a place
        svc.Grant(AdventureService.LocalPeer, new Unlock("reveal", castle.Id + "@0.5"), "check");
        Expect(await Until(() => places.MyFog.Revealed(castle.E, castle.N), 3), "the \"reveal\" reward clears the castle's disc");

        // a jump onto the pass: nothing until walked 300 m away and back
        await Jump(pass.E, pass.N);
        await Seconds(2.5);
        Expect(!mine().Found.ContainsKey(pass.Id), "jumped onto the pass: not stamped");
        await WalkTo(pass.E, pass.N + 340);
        await WalkTo(pass.E, pass.N);
        Expect(await Until(() => mine().Found.ContainsKey(pass.Id), 5), "walked 300 m away and back: stamped");

        // a car at the tower: nothing; on foot: found
        await WalkTo(tower.E - 120, tower.N);
        var me = Me!;
        bool rode = me.SetRide((RideKind)CarCatalog.First);
        await WalkTo(tower.E, tower.N - 5);
        await Seconds(2.5);
        Expect(rode && !mine().Found.ContainsKey(tower.Id), $"in a car at the tower nothing is found (ride {me.RideKindId})");
        me.TryInteract();   // E: out of the car
        await Until(() => me.RideKindId == 0, 3);
        await WalkTo(tower.E, tower.N - 3);
        Expect(await Until(() => mine().Found.ContainsKey(tower.Id), 5), "out of the car, on foot: the tower is stamped");

        // the summit cross: E syncs the view
        await WalkTo(peak.E, peak.N);
        var markers = PoiMarkers.Instance;
        Expect(await Until(() => markers?.InReach(Me!) == peak, 5), $"the cross's marker is in reach ({markers?.Placed.Count} placed)");
        Expect(PoiMarkers.PromptFor(Me!)?.Contains("Look out") == true, $"the prompt says so ({PoiMarkers.PromptFor(Me!)})");
        Expect(Me!.TryInteract(), "E at the cross");
        Expect(await Until(() => mine().Synced.Contains(peak.Id), 3), "the viewpoint is synced");
        Expect(await Until(() => places.MyFog.Revealed(peak.E + 2500, peak.N), 3), "its 3 km disc is revealed");
        Expect(_events.OfType<ViewpointSynced>().Any(v => v.PoiId == peak.Id), "ViewpointSynced raised");
        Expect(_events.OfType<Interacted>().Any(i => i.TargetId == peak.Id), "Interacted raised");
        Expect(_events.OfType<CantonMilestone>().Any(m => m.Canton == "VS"), $"a VS milestone was raised ({string.Join(",", _events.OfType<CantonMilestone>().Select(m => m.Percent))})");

        // the Atlas and the map
        Ui.AdventureLog.Open(AtlasPage.TabId);
        await Seconds(0.8);
        Expect(GameShell.Instance?.Top is Ui.AdventureLog { Current: AtlasPage.TabId }, "the Journal opens on the Atlas");
        if (DisplayServer.GetName() != "headless") GD.Print($"{Log} shot {Shot("atlas")}");
        AtlasPage.Open(peak);
        await Seconds(0.5);
        if (DisplayServer.GetName() != "headless") GD.Print($"{Log} shot {Shot("atlas_card")}");
        GameShell.Instance?.Back();
        await Seconds(0.3);
        AdventureMap.Toggle();
        await Seconds(0.8);
        Expect(GameShell.Instance?.Top is AdventureMap, "the adventure map opens");
        if (GameShell.Instance?.Top is AdventureMap map)
        {
            map.Select(peak, centre: true);
            await Seconds(0.6);
            Expect(map.Selected == peak, "a place is selected on it");
            if (DisplayServer.GetName() != "headless") GD.Print($"{Log} shot {Shot("map")}");
            GameShell.Instance?.Back();
        }

        svc.SaveAll();
        SaveQueue.Flush();
        string file = svc.FileOf(AdventureService.LocalKey);
        try
        {
            var back = AdventureBook.FromJson(System.IO.File.ReadAllText(file));
            var saved = back.Get(PlacesProgress.Section);
            var fog = FogMask.Decode(back.Get(FogProgress.Section).Mask);
            Expect(saved.Found.ContainsKey(peak.Id) && saved.Synced.Contains(peak.Id), $"the save file holds the stamps ({file})");
            Expect(fog.Revealed(peak.E + 2500, peak.N) && fog.Count == places.MyFog.Count, $"and the fog ({fog.Count} cells)");
        }
        catch (Exception e) { Expect(false, $"the save file reads back ({file}: {e.Message})"); }
        await Finish(0);
    }

    /// <summary>Walks the body in 20 m steps twice a second (under the jump rule's 40 m/s), on the flat ground.</summary>
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
        GD.Print(FormattableString.Invariant($"{Log} at {me.Global.E:F1}/{me.Global.N:F1}/{me.Global.Alt:F1}"));
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
