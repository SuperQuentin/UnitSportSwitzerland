using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Phone;

/// <summary>The Map app's big map over the view (#1826): up, see-through, pan, zoom, search, pick, travel, fade while moving.</summary>
public partial class PhoneProbe
{
    private Vector2 CanvasToWindow(PhoneMap map, Vector2 at) => GetViewport().GetScreenTransform() * (map.Canvas.GlobalPosition + at);

    private static void MouseAt(Vector2 window, MouseButton button, bool down, MouseButtonMask mask = 0) =>
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = down, Position = window, GlobalPosition = window, ButtonMask = down ? mask | MouseButtonMask.Left : 0 });

    private static void MoveTo(Vector2 window, Vector2 rel = default, MouseButtonMask mask = 0) =>
        Input.ParseInputEvent(new InputEventMouseMotion { Position = window, GlobalPosition = window, Relative = rel, ButtonMask = mask });

    private async Task TypeText(string text)
    {
        foreach (char c in text)
        {
            var key = (Godot.Key)char.ToUpperInvariant(c);
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = c, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Unicode = c, Pressed = false });
            await Wait(0.05);
        }
        await Wait(0.3);
    }

    private async Task MapChecks(FootPlayer me, PhoneController phone)
    {
        Log("-- the Map app, a large see-through map (#1826)");
        if (phone.IsOut) { phone.PutAway(); for (int i = 0; i < 60 && phone.IsOut; i++) await Wait(0.1); }
        await Wait(0.3);
        await Press(PlayerInput.Phone, 0.6);
        await Press(PlayerInput.Phone, 0.8);
        phone.Open(PhoneScreens.Page.Map);
        await Wait(1.2);
        var map = phone.Map;
        if (Platform.IsTouch)
        {
            Check(!phone.MapUp, "touch: the Map stays the list on the glass, no map over the view");
            phone.PutAway();
            for (int i = 0; i < 60 && phone.IsOut; i++) await Wait(0.1);
            return;
        }
        Check(phone.MapUp && map.IsUp, "the Map app puts a large map over the view");
        var view = GetViewport().GetVisibleRect().Size;
        var rect = map.PanelRect;
        Check(rect.Size.X / view.X is > 0.70f and < 0.82f && rect.Size.Y / view.Y is > 0.70f and < 0.82f,
            $"covering about three quarters of the view ({rect.Size.X / view.X:P0} by {rect.Size.Y / view.Y:P0})");
        Check(Mathf.Abs(map.FillAlpha - PhoneMap.RestAlpha) < 0.01f, $"standing still the fills are {PhoneMap.RestAlpha:P0} opaque ({map.FillAlpha:F2})");
        Check(me.PhoneCover == 0f && me.PhoneBackdropShown == 0f, "the world is not hidden: no cover, no black");
        // the phone gets out of the map's rectangle: every corner of its screen is right of it (the map and the phone never overlap)
        await Wait(0.5);
        if (me.PhoneViewmodel is { Visible: true } && me.CameraOrNull is { } lens)
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            foreach (var uv in new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) })
            {
                var corner = lens.UnprojectPosition(me.PhoneScreenToWorld(uv, false, 0f) ?? Vector3.Zero);
                minX = Mathf.Min(minX, corner.X);
                minY = Mathf.Min(minY, corner.Y);
            }
            Check(minX > rect.End.X + 4f || minY > rect.End.Y + 4f,
                $"the phone is wholly outside the map's rectangle (its screen from x {minX:F0}, y {minY:F0}; the map ends at x {rect.End.X:F0}, y {rect.End.Y:F0})");
        }
        else Log("skip the phone's place (no viewmodel drawn)");
        Check(map.Canvas.GetChildCount() > 0 && map.Canvas.SelfModulate.A < 0.7f, $"the relief and background are see-through (self alpha {map.Canvas.SelfModulate.A:F2})");

        // places: the index's when it has some, else a few of the check's own, near the player
        if (!phone.Screens.HasPlaces)
        {
            var (at0, at1, _) = me.Global.Lv95;
            map.TestIndex = new PlaceIndex
            {
                Places =
                [
                    new Place { Name = "Probe Village", Canton = "VS", E = at0 + 1500, N = at1 + 600, Kind = PlaceKind.Town, Buildings = 900 },
                    new Place { Name = "Probe Alp", Canton = "VS", E = at0 - 2500, N = at1 + 1800, Kind = PlaceKind.Summit, Elevation = 2480 },
                    new Place { Name = "Probe Pass", Canton = "VS", E = at0 + 3200, N = at1 - 2100, Kind = PlaceKind.Pass, Elevation = 1980 },
                ],
            };
        }
        for (int i = 0; i < 100 && Ui.MapRelief.TryGet() == null; i++) await Wait(0.1);
        map.Canvas.Redraw();
        await Wait(0.5);
        Shot("phone_1826/map_standing.png");

        // pan: an arrow key, then a drag; the body does not walk on the arrows
        var (lat0, lon0) = map.Canvas.View.Centre;
        var stood = me.GlobalPosition;
        await Hold(Godot.Key.Right, 0.5);
        var (_, lon1) = map.Canvas.View.Centre;
        Check(lon1 > lon0 + 1e-4 && me.GlobalPosition.DistanceTo(stood) < 0.2f, $"the Right arrow pans the map east and the body stands ({(lon1 - lon0) * 1000:F1} mdeg)");
        var mid = map.Canvas.Size / 2;
        var from = CanvasToWindow(map, mid);
        MoveTo(from);
        await Wait(0.1);
        var (latD0, lonD0) = map.Canvas.View.Centre;
        MouseAt(from, MouseButton.Left, true);
        for (int i = 1; i <= 8; i++) { MoveTo(from + new Vector2(0, 20f * i), new Vector2(0, 20), MouseButtonMask.Left); await Wait(0.03); }
        MouseAt(from + new Vector2(0, 160), MouseButton.Left, false);
        await Wait(0.2);
        var (latD1, _) = map.Canvas.View.Centre;
        Check(latD1 > latD0 + 1e-4, $"dragging down moves the map north ({(latD1 - latD0) * 1000:F1} mdeg)");

        // the wheel zooms about the cursor: the point under it stays
        var cursor = from + new Vector2(150, 60);
        MoveTo(cursor);
        await Wait(0.1);
        // the point under the pointer when the wheel event reaches the canvas (its gui_input signal comes before its own handler)
        (double LatDeg, double LonDeg) under = default;
        Vector2 wheelAt = default;
        void Seen(InputEvent e)
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } mb) return;
            wheelAt = mb.Position;
            under = map.Canvas.View.ToLatLon(mb.Position);
        }
        map.Canvas.GuiInput += Seen;
        int z0 = map.Canvas.View.Zoom;
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.WheelUp, Pressed = true, Position = cursor, GlobalPosition = cursor });
        await Wait(0.2);
        map.Canvas.GuiInput -= Seen;
        var held = map.Canvas.View.ToScreenLatLon(under.LatDeg, under.LonDeg);
        if (!Rendered && map.Canvas.View.Zoom == z0)
        {
            // a headless window routes no hover to the canvas, so the wheel event never arrives: the same step by call
            Log("skip the wheel event (headless): zooming by call");
            map.Canvas.ZoomStep(+1);
            Check(map.Canvas.View.Zoom == z0 + 1, "the zoom step works (headless)");
        }
        else Check(map.Canvas.View.Zoom == z0 + 1 && held.DistanceTo(wheelAt) < 3f,
            $"the wheel zooms in about the cursor, the point under it stays (step {z0} to {map.Canvas.View.Zoom}, {held.DistanceTo(wheelAt):F1} px off the pointer at {wheelAt})");
        {
            // the maths alone, no event: the same point stays within a pixel or two (EarthMapView.ZoomAt)
            var v = map.Canvas.View;
            var keep = v.ToLatLon(mid + new Vector2(150, 60));
            int zb = v.Zoom;
            v.ZoomAt(mid + new Vector2(150, 60), -1);
            var shown = v.ToScreenLatLon(keep.LatDeg, keep.LonDeg);
            Log($"zoom maths: the kept point is {shown - (mid + new Vector2(150, 60))} px off after a step out");
            v.ZoomAt(mid + new Vector2(150, 60), +1);
            _ = zb;
        }
        await Key(Godot.Key.Minus, 0.2);
        Check(map.Canvas.View.Zoom == z0, "the minus key zooms out");

        // Home brings the map back to the player
        await Key(Godot.Key.Home, 0.8);
        var g = me.Global.Geodetic;
        var c = map.Canvas.View.Centre;
        Check(Mathf.Abs(c.LatDeg - g.LatDeg) < 3e-4 && Mathf.Abs(c.LonDeg - g.LonDeg) < 3e-4, "Home centres the map on the player");

        // a click on a place selects it; its card offers Travel
        var index = map.NearestPlaces(me.Global);
        if (index.Count > 0)
        {
            var pick = index[0];
            var (pickE, pickN) = pick.Lv95;
            var pt = map.Canvas.View.ToScreen(pickE, pickN);
            if (pt.X > 0 && pt.Y > 0 && pt.X < map.Canvas.Size.X && pt.Y < map.Canvas.Size.Y)
            {
                var w = CanvasToWindow(map, pt);
                MoveTo(w);
                await Wait(0.1);
                MouseAt(w, MouseButton.Left, true);
                MouseAt(w, MouseButton.Left, false);
                await Wait(0.4);
                Check(map.SelectedPlace == pick && map.CardShown, $"a click on a place selects it and shows its card ({map.CardTitle})");
                Shot("phone_1826/map_selected.png");
            }
            else Log($"skip click: {pick.Name} is off the view");
        }
        await Key(Godot.Key.Escape, 0.4);
        Check(!map.CardShown && phone.Screens.Shown == PhoneScreens.Page.Map, "Esc closes the card first, the app stays");

        // the search drawer: type to filter, pick a place to centre on it
        phone.FocusSearch();
        await Wait(0.4);
        Check(map.DrawerOpen && map.Typing, "/ opens the search drawer with the keys in its box");
        var typed = me.GlobalPosition;
        string word = index.Count > 0 ? index[0].Name[..Math.Min(4, index[0].Name.Length)] : "Pro";
        await TypeText(word);
        Check(me.GlobalPosition.DistanceTo(typed) < 0.1f, "typing in the search does not move the body");
        Check(map.SearchText.Length > 0 && map.RowCount > 0, $"the rows follow the query ({map.SearchText}: {map.RowCount} rows)");
        Shot("phone_1826/map_search.png");
        await Key(Godot.Key.Down, 0.2);
        await Key(Godot.Key.Enter, 0.2);
        for (int i = 0; i < 10 && map.DrawerOpen; i++) await Wait(0.1);
        Check(map.SelectedPlace != null && !map.DrawerOpen && map.CardShown, $"a row picks the place, closes the drawer and shows its card ({map.SelectedPlace?.Name})");
        await Wait(0.6);
        if (map.SelectedPlace is { } sel)
        {
            var cc = map.Canvas.View.Centre;
            var sg = PhoneScreens.GlobalOf(sel).Geodetic;
            Check(Mathf.Abs(cc.LatDeg - sg.LatDeg) < 3e-4 && Mathf.Abs(cc.LonDeg - sg.LonDeg) < 3e-4, "and the map centres on it");
        }
        await Key(Godot.Key.Equal, 0.3);
        await Wait(0.3);
        Shot("phone_1826/map_zoomed.png");
        // the drawer and the card step back one at a time
        phone.FocusSearch();
        await Wait(0.3);
        await Key(Godot.Key.Escape, 0.4);
        Check(!map.DrawerOpen && map.CardShown && phone.Screens.Shown == PhoneScreens.Page.Map, "Esc closes the drawer first");

        // the held right button turns the view, the map does not see the pointer meanwhile
        float yaw = me.LookYaw;
        Mouse(MouseButton.Right, true);
        await Wait(0.15);
        Check(!map.PointerEnabled, "holding the right button takes the pointer off the map");
        Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(120, 0) });
        await Wait(0.2);
        Check(Mathf.Abs(Mathf.AngleDifference(yaw, me.LookYaw)) > 0.1f, "and the mouse turns the view");
        Mouse(MouseButton.Right, false);
        await Wait(0.15);
        Check(map.PointerEnabled, "let go, the map has the pointer back");

        // moving: the fills fade to about 20 % smoothly, labels and markers stay; stopping brings them back
        await Key(Godot.Key.Home, 0.6);
        float lowest = 1f, highest = 0f, seenMid = 0f;
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.W, PhysicalKeycode = Godot.Key.W, Pressed = true });
        for (int i = 0; i < 40; i++)
        {
            await Wait(0.03);
            float a = map.FillAlpha;
            lowest = Mathf.Min(lowest, a);
            highest = Mathf.Max(highest, a);
            if (a is > 0.28f and < 0.55f) seenMid++;
            if (i == 30) Shot("phone_1826/map_walking.png");
        }
        float walkingAlpha = map.FillAlpha;
        Check(map.Moving && Mathf.Abs(walkingAlpha - PhoneMap.MovingAlpha) < 0.02f, $"walking, the fills fall to {PhoneMap.MovingAlpha:P0} ({walkingAlpha:F2})");
        Check(seenMid > 0, $"by easing, not a step ({seenMid} frames between)");
        Check(map.Canvas.Modulate.A >= 0.99f, "while the labels, borders and markers stay crisp (the canvas's own modulate is untouched)");
        Input.ParseInputEvent(new InputEventKey { Keycode = Godot.Key.W, PhysicalKeycode = Godot.Key.W, Pressed = false });
        await Wait(1.3);
        Check(!map.Moving && Mathf.Abs(map.FillAlpha - PhoneMap.RestAlpha) < 0.02f, $"standing again, they come back to {PhoneMap.RestAlpha:P0} ({map.FillAlpha:F2})");

        // travel from the card: A / Enter goes behind the curtain, which hides the map and the world
        if (map.SelectedPlace != null && Permissions.CanTeleport)
        {
            await Key(Godot.Key.Enter, 0.4);
            Check(phone.Screens.Shown == PhoneScreens.Page.Travel && !map.IsUp, "Enter on the card travels behind the curtain, the map gives way to it");
            for (int i = 0; i < 400 && phone.IsOut; i++) await Wait(0.1);
            Check(!phone.IsOut, "and the phone goes back in the pocket once there");
        }
        else
        {
            await Key(Godot.Key.Escape, 0.4);
            await Key(Godot.Key.Escape, 0.4);
            Check(phone.Screens.Shown == PhoneScreens.Page.Home, "Esc once more leaves the Map for Home");
            phone.PutAway();
            for (int i = 0; i < 60 && phone.IsOut; i++) await Wait(0.1);
        }
        map.TestIndex = null;
    }
}
