using System.Threading.Tasks;
using Godot;
using UnitSport.Adventure.Places;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Ui;

namespace UnitSport.Adventure.Navigation;

/// <summary>
/// <c>--navcheck --world fixture --systems ui,physics,adventure --pois fixture</c> (quick tier, #1226): offline, on the
/// flat fixture with the fixture POIs. The free-roam compass is up with the tracked target (its label, its side of the
/// strip, pinned when behind), standing still costs no redraw; the target marker sits on screen for a target ahead and
/// on the screen's edge ellipse, low, for one behind; a pin is placed (the section, a compass icon) and shown on the map;
/// the guiding gust blows toward the target (its bearing within 3°, warm leaves down here) and the high-contrast arrow
/// shows for a target ahead; a place tracked from the map is let go on arrival; the Journal's Travel tab opens; the save
/// file holds the pin. Windowed it saves <c>test_output/nav_*.png</c> (compass, marker on and off screen, wind, arrow,
/// map, board), in the style the run was given (<c>--style ps1|cartoon|realistic</c>).
/// </summary>
public partial class NavCheck : ChatProbe
{
    public static bool Requested => CmdArgs.Has("--navcheck");

    private readonly Func<FootPlayer?> _me;

    public NavCheck(ItemController items, Func<FootPlayer?> me) : base(items, "navcheck", "", "nav_") => _me = me;
    public NavCheck() : this(null!, () => null) { }

    protected override FootPlayer? Me => _me();

    private static bool Windowed => DisplayServer.GetName() != "headless";

    public override async void _Ready()
    {
        Bank.Scratch = true;
        await Until(() => GetViewport().GetCamera3D() != null, 60);
        if (Me == null) GetParent<ClientWorld>().ToggleMode();
        if (!await Until(() => AdventureService.Instance != null && PlacesService.Instance is { Catalogue.Count: > 0 } && AdventureTravel.Instance != null
                && AdventureCompass.Instance != null && Me is { } me && me.IsOnFloor(), 90))
        {
            Fail($"no adventure ({AdventureService.Instance != null}), places, travel ({AdventureTravel.Instance != null}) or player on the ground");
            return;
        }
        await Until(() => Me!.Down != 0, 3);
        await Until(() => Me!.Down == 0 && !Me.KnockedOut, 30);
        await Seconds(1);
        var svc = AdventureService.Instance!;
        var travel = AdventureTravel.Instance!;
        var compass = AdventureCompass.Instance!;
        var cat = PlacesService.Instance!.Catalogue;
        var peak = cat.OfKind(PoiKind.Peak)[0];
        var me = Me!;

        Expect(svc.Quests.Plans.Contains("navigation"), "navigation runs: its quests may be offered");
        Expect(compass.Shown, "the free-roam compass is up while adventuring");

        // the tracked target on the strip
        svc.TrackPlace(peak.Name, peak.E, peak.N, 0);
        await Seconds(0.4);
        var strip = compass.Strip;
        var target = Find(strip, 1);
        Expect(target is { Shape: MarkerShape.Diamond } && target.Value.Label.EndsWith(" m"), $"the target is the strip's diamond with its distance ('{target?.Label}')");
        if (target is { } tm)
        {
            var (_, pinned) = CompassStrip.XOf(tm.Toward, strip.DrawnHeading);
            float rel = CompassMath.Relative(CompassStrip.Bearing(tm.Toward), strip.DrawnHeading);
            Expect(pinned == Math.Abs(rel) > 80, $"pinned to the edge exactly when behind (rel {rel:F0}°)");
        }
        // standing still costs nothing
        int before = strip.Redraws;
        await Seconds(1.0);
        Expect(strip.Redraws - before <= 1, $"standing still: {strip.Redraws - before} redraw(s) in a second");
        if (Windowed) GD.Print($"{Log} shot {Shot("compass")}");

        // a target straight ahead of the view, then straight behind
        var cam = GetViewport().GetCamera3D()!;
        var f = -cam.GlobalBasis.Z;
        var fwd = new Vector2(f.X, -f.Z).Normalized();
        var at = me.Global;
        svc.TrackPlace("Ahead", at.E + fwd.X * 200, at.N + fwd.Y * 200, 0);
        var marker = TargetMarker.Instance!;
        Expect(await Until(() => marker.Showing && marker.OnScreen, 3), $"a target ahead: the marker on screen at {marker.At}");
        if (Windowed) GD.Print($"{Log} shot {Shot("marker_on")}");
        svc.TrackPlace("Behind", at.E - fwd.X * 200, at.N - fwd.Y * 200, 0);
        var screen = GetViewport().GetVisibleRect().Size;
        Expect(await Until(() => marker.Showing && !marker.OnScreen, 3), "a target behind: the marker leaves the screen");
        var e = (marker.At - screen / 2) / new Vector2(screen.X / 2 - 48, screen.Y / 2 - 48);
        Expect(Math.Abs(e.Length() - 1) < 0.05f && marker.At.Y > screen.Y / 2, $"pinned on the edge ellipse, at the bottom (turn round): {marker.At}");
        if (Windowed) GD.Print($"{Log} shot {Shot("marker_off")}");

        // the guiding gust toward a target ahead
        svc.TrackPlace("Ahead", at.E + fwd.X * 300, at.N + fwd.Y * 300, 0);
        var wind = GuidingWind.Instance!;
        Expect(GuidingWind.Can && wind.Summon() && wind.Blowing, "N: the gust blows");
        await Seconds(0.6);
        float want = CompassStrip.Bearing(fwd);
        Expect(Math.Abs(Mathf.Wrap(wind.Bearing - want, -180, 180)) < 3, $"toward the target ({wind.Bearing:F0}° for {want:F0}°)");
        Expect(wind.Palette == 0, "warm leaves below the snow line");
        if (Windowed) { await Seconds(0.8); GD.Print($"{Log} shot {Shot("wind")}"); }
        GameSettings.Current.GuideHighContrast = true;
        wind.Summon();
        Expect(await Until(() => wind.ArrowShown, 2), "high contrast: the ground arrow instead of leaves");
        if (Windowed) GD.Print($"{Log} shot {Shot("arrow")}");
        GameSettings.Current.GuideHighContrast = false;

        // a pin, on the compass and on the map
        travel.AskPinAdd(at.E + 40, at.N + 20, (byte)PinIcon.Star, "  Picnic\u0007 spot  ", shared: false);
        Expect(await Until(() => travel.MyPins.Pins.Count == 1, 3), "the pin is in the pins section");
        Expect(travel.MyPins.Pins.Count == 1 && travel.MyPins.Pins[0].Note == "Picnic spot", $"its note sanitised ('{(travel.MyPins.Pins.Count > 0 ? travel.MyPins.Pins[0].Note : "")}')");
        await Seconds(0.3);
        bool icon = false;
        foreach (var m in strip.Markers) icon |= m.Shape == MarkerShape.Icon;
        Expect(icon, "the pin is on the compass");
        AdventureMap.Toggle();
        Expect(await Until(() => GameShell.Instance?.Top is AdventureMap, 2), "the adventure map opens");
        if (GameShell.Instance?.Top is AdventureMap map)
        {
            var pin = travel.MyPins.Pins[0];
            map.View.FocusOn(pin.E, pin.N, 128);
            map.Redraw();
            await Seconds(0.5);
            var rel = new Vector2((float)(pin.E - AdventureMap.LatticeE), (float)(pin.N - AdventureMap.LatticeN));
            Expect(NavMapOverlay.Instance.Click(rel + new Vector2(0, 12f * 1000 / 128), MouseButton.Left), "a click on the pin's head opens its card");
            await Seconds(0.3);
            if (Windowed) GD.Print($"{Log} shot {Shot("map")}");
            GameShell.Instance?.Back();
            await Seconds(0.3);
        }

        // the Journal's Travel tab
        AdventureLog.Open(DepartureBoard.TabId);
        await Seconds(0.6);
        Expect(GameShell.Instance?.Top is AdventureLog { Current: DepartureBoard.TabId }, "the Journal opens on the Travel tab");
        if (Windowed) GD.Print($"{Log} shot {Shot("board")}");
        GameShell.Instance?.Back();
        await Seconds(0.3);

        // a place tracked from the map is let go on arrival
        at = me.Global;
        svc.TrackPlace("Here", at.E + 3, at.N, 0);
        Expect(await Until(() => svc.TrackedPlace == null, 3), "arrived at a tracked place: it is let go");

        svc.SaveAll();
        SaveQueue.Flush();
        try
        {
            var back = AdventureBook.FromJson(System.IO.File.ReadAllText(svc.FileOf(AdventureService.LocalKey)));
            Expect(back.Get(PinBook.Section).Pins.Count == 1, "the save file holds the pin");
        }
        catch (Exception ex) { Expect(false, $"the save file reads back: {ex.Message}"); }
        await Finish(0);
    }

    private static CompassMarker? Find(CompassStrip strip, int key)
    {
        foreach (var m in strip.Markers) if (m.Key == key) return m;
        return null;
    }
}
