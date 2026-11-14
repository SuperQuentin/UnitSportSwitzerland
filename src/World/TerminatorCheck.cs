using System.Globalization;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World;

/// <summary>
/// <c>--terminatorcheck [shots]</c> (#1845, H6 of #1430): the sky and the day/night seen from space, against an ephemeris
/// the game does not use. Under <c>--frame earth --sun real</c>, at the solstices and equinoxes, at two times each, the
/// camera 400 km and 20,000 km up:
/// <list type="bullet">
/// <item>the subsolar point the globe is lit by (<c>world_true_sun</c> through <c>Globe.Axes</c>) against NOAA's solar
/// position algorithm (<see cref="Noaa"/>, the spreadsheet's formulas);</item>
/// <item>the sky's sun (<c>DayNight.Sky</c>, the sun disc and the space sun) and the ground's light against it;</item>
/// <item>the moon where it is seen from the camera itself (its parallax: up to ~3° at 20,000 km);</item>
/// <item>the stars' celestial sphere as an Earth-fixed frame (<c>Ephemeris.Celestial</c>), whatever the anchor;</item>
/// <item>true north (<c>TrueNorth.WorldNorth</c>) against the Earth's axis laid on the tangent plane;</item>
/// <item><c>/time speed real</c>: the subsolar point moves 360° in 24 h of game time.</item>
/// </list>
/// Windowed (and with <c>shots</c>) it also measures the drawn terminator: from 20,000 km over the evening terminator
/// in the Pacific (even albedo), the globe's twilight along 41 great circles from the subsolar point, its sun term and
/// the air set aside so only the twilight ramp shows: the ramp's middle against 90°. And it saves pictures as drawn,
/// <c>test_output/terminator_*.png</c>.
/// </summary>
public partial class TerminatorCheck : Node
{
    public static bool Requested => CmdArgs.Has("--terminatorcheck");

    /// <summary>The globe's subsolar point may be this far off NOAA's, degrees (0.05° = 5.6 km).</summary>
    public const double SubsolarDeg = 0.05;

    /// <summary>Two of the game's own directions (the sky, the ground's light, the globe's, the stars) may differ this much, degrees.</summary>
    public const double AgreeDeg = 0.02;

    /// <summary>The drawn twilight's middle may be this far from 90° from the subsolar point, degrees.</summary>
    public const double DrawnDeg = 0.75;

    private readonly Camera3D _camera;
    private readonly WorldOrigin _origin;
    private readonly Func<bool> _idle;
    private readonly bool _shots = CmdArgs.Value("--terminatorcheck") == "shots" || !DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase);
    private int _failures;
    private readonly System.Text.StringBuilder _table = new();

    public TerminatorCheck(Camera3D camera, WorldOrigin origin, Func<bool> idle)
    {
        _camera = camera;
        _origin = origin;
        _idle = idle;
    }

    public override async void _Ready()
    {
        bool up = await Until(() => DayNight.Instance is { Sky: not null } && _origin.Frame.IsAnchor, 120);
        if (up)
        {
            // the globe draws from 5 km up
            await Place(46.95, 7.44, 400_000);
            up = await Until(() => Globe.Axes != null, 60);
        }
        if (!up)
        {
            Fail("no real sky or globe (needs --frame earth --sun real)");
            Done();
            return;
        }
        try { await Run(DayNight.Instance!); }
        catch (Exception e) { Expect(false, $"threw: {e}"); }
        Done();
    }

    private static readonly DateOnly[] Dates = { new(2027, 3, 20), new(2027, 6, 21), new(2027, 9, 23), new(2027, 12, 22) };

    private async Task Run(DayNight day)
    {
        var today = SunPath.Today;
        try
        {
            _table.Append("date        UTC    alt km   subsolar(NOAA)        globe-NOAA     sky-globe  light-sky  moon     moon(ground)  stars    north\n");
            foreach (var date in Dates)
                foreach (double utcHour in new[] { 4.0, 15.5 })
                    foreach (double alt in new[] { 400_000.0, 20_000_000.0 })
                        await Scenario(day, date, utcHour, alt);
            GD.Print("[terminatorcheck]\n" + _table);
            await RealSpeed(day);
            if (_shots)
            {
                await Drawn(day, new DateOnly(2027, 3, 20), 16.0);
                await Drawn(day, new DateOnly(2027, 6, 21), 16.0);
                await Drawn(day, new DateOnly(2027, 12, 22), 4.0);
            }
        }
        finally
        {
            SunPath.Today = today;
            day.HourOverride = null;
        }
    }

    /// <summary>The game's clock at a UTC instant: the Swiss date and hour (the clock is Swiss civil time).</summary>
    private static void SetUtc(DayNight day, DateOnly date, double utcHour)
    {
        var utc = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(utcHour);
        var local = utc.AddHours(SwissTime.Offset(utc));
        var swissDate = DateOnly.FromDateTime(local);
        SunPath.Today = () => swissDate;
        day.HourOverride = local.TimeOfDay.TotalHours;
    }

    /// <summary>The camera at <paramref name="alt"/> over a place, looking at the Earth's centre, the frame settled under it.</summary>
    private async Task Place(double latDeg, double lonDeg, double alt, float fovDeg = 70f)
    {
        var at = GlobalPos.FromEcef(Ellipsoid.Wgs84.ToEcef(new Geodetic(latDeg, lonDeg, alt)));
        _camera.Current = true;
        _camera.Fov = fovDeg;
        for (int i = 0; i < 6; i++)
        {
            _camera.GlobalPosition = _origin.ToWorld(at);
            var centre = _origin.Frame.Axes is { } axes ? ToWorld(axes, new EcefPos(0, 0, 0)) : _camera.GlobalPosition + Vector3.Down;
            var look = (centre - _camera.GlobalPosition).Normalized();
            _camera.LookAt(_camera.GlobalPosition + look, Mathf.Abs(look.Y) > 0.99f ? Vector3.Forward : Vector3.Up);
            await Frames(1);
        }
    }

    private async Task Scenario(DayNight day, DateOnly date, double utcHour, double alt)
    {
        SetUtc(day, date, utcHour);
        var utc = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(utcHour);
        // over Bern and over the evening terminator: the moon's parallax and north at two places, the night side too
        var (sLat, sLon) = Noaa.Subsolar(utc);
        foreach (var (lat, lon) in new[] { (46.95, 7.44), (0.0, Wrap(sLon + 90)) })
        {
            await Place(lat, lon, alt);
            Measure(day, utc, alt, sLat, sLon);
        }
    }

    private void Measure(DayNight day, DateTime utc, double alt, double sLat, double sLon)
    {
        if (day.Sky is not { } sky || Globe.Axes is not { } globeAxes || _origin.Frame.Axes is not { } axes)
        {
            Fail("no sky or axes");
            return;
        }
        var noaa = Unit(new EcefPos(Math.Cos(sLat * Deg) * Math.Cos(sLon * Deg), Math.Cos(sLat * Deg) * Math.Sin(sLon * Deg), Math.Sin(sLat * Deg)));
        var globe = ToEcef(globeAxes, Atmosphere.TrueSunWritten);
        var skySun = ToEcef(axes, sky.Sun);
        double offNoaa = Angle(globe, noaa), offSky = Angle(skySun, globe);
        // the ground's light is the sun's while it is up here (at night it is the moon's shading)
        double offLight = double.NaN;
        if (day.SunElevationDeg > 3f && day.Sun is { } light && IsInstanceValid(light))
            offLight = Angle(ToEcef(axes, light.GlobalBasis.Z.Normalized()), skySun);

        // the moon from the camera itself, and how far the ground under it would put it
        var cam = axes.ToEcef(_camera.GlobalPosition.X, _camera.GlobalPosition.Y, _camera.GlobalPosition.Z);
        var moonEcef = Ephemeris.Ecef(SkyBody.Moon, utc);
        var moonTruth = Unit(moonEcef - cam);
        var g = Ellipsoid.Wgs84.ToGeodetic(cam);
        var ground = Unit(moonEcef - Ellipsoid.Wgs84.ToEcef(g with { H = 0 }));
        double offMoon = Angle(ToEcef(axes, sky.Moon), moonTruth), groundMoon = Angle(ground, moonTruth);

        // the stars' sphere as an Earth-fixed frame, whatever the anchor
        var (cx, cy, cz) = Ephemeris.Celestial(utc);
        var stars = sky.Stars;
        double offStars = Math.Max(Angle(ToEcef(axes, stars.Column0), cx), Math.Max(Angle(ToEcef(axes, stars.Column1), cy), Angle(ToEcef(axes, stars.Column2), cz)));

        // true north: the Earth's axis laid on the tangent plane at the camera's place
        var enu = new EnuFrame(g);
        var pole = new EcefPos(0, 0, 1);
        double along = FrameAxes.Dot(pole, enu.Up);
        var northTruth = Unit(new EcefPos(pole.X - enu.Up.X * along, pole.Y - enu.Up.Y * along, pole.Z - enu.Up.Z * along));
        var north = ToEcef(axes, TrueNorth.WorldNorth(_origin.Frame, _camera.GlobalPosition));
        double offNorth = Angle(north, northTruth);

        _table.Append(CultureInfo.InvariantCulture,
            $"{DateOnly.FromDateTime(utc):yyyy-MM-dd}  {utc:HH:mm}  {alt / 1000,6:F0}   {sLat,7:F3} {sLon,8:F3}   {offNoaa,7:F4}° {offNoaa * 111.2,5:F2} km  {offSky,7:F4}°  {(double.IsNaN(offLight) ? "   -   " : $"{offLight,7:F4}°")}  {offMoon,7:F4}°  {groundMoon,7:F4}°     {offStars,7:F4}°  {offNorth,7:F4}°  at {g.LatDeg:F1},{g.LonDeg:F1}\n");
        string where = string.Create(CultureInfo.InvariantCulture, $"{DateOnly.FromDateTime(utc):yyyy-MM-dd} {utc:HH:mm} UTC, {alt / 1000:F0} km over {g.LatDeg:F1},{g.LonDeg:F1}");
        Expect(offNoaa < SubsolarDeg, $"{where}: the globe's subsolar point {offNoaa:F4}° off NOAA's");
        Expect(offSky < AgreeDeg, $"{where}: the sky's sun {offSky:F4}° off the globe's");
        Expect(double.IsNaN(offLight) || offLight < AgreeDeg, $"{where}: the ground's light {offLight:F4}° off the sky's sun");
        Expect(offMoon < AgreeDeg, $"{where}: the moon {offMoon:F4}° off where the camera sees it (the ground under it: {groundMoon:F4}°)");
        Expect(offStars < AgreeDeg, $"{where}: the stars' sphere {offStars:F4}° off the Earth-fixed one");
        Expect(offNorth < AgreeDeg, $"{where}: true north {offNorth:F4}° off the Earth's axis");
    }

    /// <summary><c>/time speed real</c>: the globe's subsolar point goes round once in 24 h of game time.</summary>
    private async Task RealSpeed(DayNight day)
    {
        SetUtc(day, new DateOnly(2027, 6, 21), 12);
        await Place(46.95, 7.44, 400_000);
        var chat = GetParent().GetNodeOrNull<Net.ChatManager>(Net.ChatManager.NodeName);
        day.HourOverride = null;
        if (chat != null) chat.Send("/time speed real");
        else day.DayLengthOverride = TimeCommand.MaxMinutesPerDay;
        await Frames(3);
        Expect(day.MinutesPerDay == TimeCommand.MaxMinutesPerDay, $"/time speed real: {day.MinutesPerDay} min a day");
        double h0 = day.Hour, t0 = GameClock.Now;
        var s0 = ToEcef(Globe.Axes!, Atmosphere.TrueSunWritten);
        await Until(() => GameClock.Now - t0 >= 20, 60);
        double dt = GameClock.Now - t0;
        var s1 = ToEcef(Globe.Axes!, Atmosphere.TrueSunWritten);
        // the subsolar longitude's motion: west at 360° a day (the equation of time's drift is seconds a day)
        double lon0 = Math.Atan2(s0.Y, s0.X), lon1 = Math.Atan2(s1.Y, s1.X);
        double moved = -Math.IEEERemainder(lon1 - lon0, 2 * Math.PI) / Deg;
        double want = 360.0 * dt / 86400;
        double hours = Math.Abs(TimeCommand.ShortWay(h0, day.Hour)) * 3600;
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[terminatorcheck] /time speed real: {dt:F2} s of game time, the clock moved {hours:F2} s, the subsolar point {moved:F5}° (want {want:F5}°)"));
        Expect(Math.Abs(hours - dt) < 0.05 * dt, $"/time speed real: the clock moved {hours:F2} s in {dt:F2} s");
        Expect(Math.Abs(moved - want) < 0.02 * want, $"/time speed real: the subsolar point moved {moved:F5}° in {dt:F2} s, want {want:F5}°");
        if (chat != null) chat.Send("/time speed normal");
        else day.DayLengthOverride = null;
        await Frames(2);
    }

    /// <summary>
    /// The drawn terminator from 20,000 km over the evening one in the Pacific (even albedo): a picture as drawn, then the
    /// globe's twilight alone (its sun term at 0, the air hidden) read along 41 great circles from the subsolar point,
    /// 75° to 105° from it. The ramp runs from the day's plateau to the night's floor; its middle should be at 90°.
    /// </summary>
    private async Task Drawn(DayNight day, DateOnly date, double utcHour)
    {
        SetUtc(day, date, utcHour);
        var utc = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(utcHour);
        var (sLat, sLon) = Noaa.Subsolar(utc);
        var s = Unit(new EcefPos(Math.Cos(sLat * Deg) * Math.Cos(sLon * Deg), Math.Cos(sLat * Deg) * Math.Sin(sLon * Deg), Math.Sin(sLat * Deg)));
        // the evening terminator on the subsolar point's parallel's great circle, 90° east of it... west: the sun sets there
        double camLon = Wrap(sLon + 90);
        string name = string.Create(CultureInfo.InvariantCulture, $"{date:yyyyMMdd}_{utcHour:00}h");
        await Place(0, camLon, 20_000_000, 30f);
        await Until(_idle, 90);
        await Frames(30);
        Save(GetViewport().GetTexture().GetImage(), $"test_output/terminator_{name}.png");

        // the twilight alone
        var atmosphere = FindAtmosphere(GetTree().Root);
        void Isolate()
        {
            RenderingServer.GlobalShaderParameterSet(GlobeSun, Vector3.Zero);
            if (atmosphere != null) atmosphere.Visible = false;
        }
        var globeSun = RenderingServer.GlobalShaderParameterGet(GlobeSun);
        RenderingServer.FramePreDraw += Isolate;
        Image image;
        try
        {
            await Frames(4);
            image = GetViewport().GetTexture().GetImage();
        }
        finally
        {
            RenderingServer.FramePreDraw -= Isolate;
            if (atmosphere != null) atmosphere.Visible = true;
            RenderingServer.GlobalShaderParameterSet(GlobeSun, globeSun);
        }
        Save(image, $"test_output/terminator_{name}_twilight.png");

        // the great circles from the subsolar point through the camera's ground point and either side of it
        var axes = _origin.Frame.Axes!;
        var c = Unit(axes.ToEcef(_camera.GlobalPosition.X, _camera.GlobalPosition.Y, _camera.GlobalPosition.Z));
        double sc = FrameAxes.Dot(s, c);
        var t = Unit(new EcefPos(c.X - s.X * sc, c.Y - s.Y * sc, c.Z - s.Z * sc));
        var b = Cross(s, t);
        const int steps = 121;
        var profile = new double[steps];
        var size = image.GetSize();
        for (int i = 0; i < steps; i++)
        {
            double theta = (75 + 30.0 * i / (steps - 1)) * Deg;
            var values = new List<double>();
            for (int k = -20; k <= 20; k++)
            {
                double phi = k * 0.4 * Deg;
                var dir = Unit(new EcefPos(
                    Math.Cos(theta) * s.X + Math.Sin(theta) * (Math.Cos(phi) * t.X + Math.Sin(phi) * b.X),
                    Math.Cos(theta) * s.Y + Math.Sin(theta) * (Math.Cos(phi) * t.Y + Math.Sin(phi) * b.Y),
                    Math.Cos(theta) * s.Z + Math.Sin(theta) * (Math.Cos(phi) * t.Z + Math.Sin(phi) * b.Z)));
                var p = OnEllipsoid(dir);
                var world = ToWorld(axes, p);
                if (_camera.IsPositionBehind(world)) continue;
                var px = _camera.UnprojectPosition(world);
                int x = (int)px.X, y = (int)px.Y;
                if (x < 0 || y < 0 || x >= size.X || y >= size.Y) continue;
                values.Add(image.GetPixel(x, y).Luminance);
            }
            values.Sort();
            profile[i] = values.Count > 0 ? values[values.Count / 2] : double.NaN;
        }
        // the plateau (75-78°: the sun 12-15° up) and the floor (102-105°: 12-15° down); the ramp's ends where the
        // picture leaves them by 3 % of the range, its middle halfway between (a monotone tone curve keeps both ends)
        double day0 = Median(profile, 0, 12), night0 = Median(profile, steps - 13, steps - 1), range = day0 - night0;
        double ThetaOf(int i) => 75 + 30.0 * i / (steps - 1);
        int hi = Array.FindIndex(profile, v => v < day0 - 0.03 * range);
        int lo = Array.FindLastIndex(profile, v => v > night0 + 0.03 * range);
        double mid = hi >= 0 && lo >= 0 ? (ThetaOf(hi) + ThetaOf(lo)) / 2 : double.NaN;
        var csv = new System.Text.StringBuilder("theta_deg,luminance\n");
        for (int i = 0; i < steps; i++) csv.Append(CultureInfo.InvariantCulture, $"{ThetaOf(i):F2},{profile[i]:F4}\n");
        System.IO.File.WriteAllText(ProjectSettings.GlobalizePath($"res://test_output/terminator_{name}.csv"), csv.ToString());
        GD.Print(string.Create(CultureInfo.InvariantCulture,
            $"[terminatorcheck] drawn {name}: day {day0:F3}, night {night0:F3}; the twilight from {(hi >= 0 ? ThetaOf(hi) : double.NaN):F2}° to {(lo >= 0 ? ThetaOf(lo) : double.NaN):F2}°, its middle {mid:F2}° from the subsolar point (90° the sun's centre on the horizon)"));
        Expect(range > 0.05, $"drawn {name}: the day ({day0:F3}) brighter than the night ({night0:F3})");
        Expect(Math.Abs(mid - 90) < DrawnDeg, $"drawn {name}: the twilight's middle at {mid:F2}° from the subsolar point, want 90°");
    }

    private static readonly StringName GlobeSun = "world_globe_sun";

    private static Node3D? FindAtmosphere(Node root)
    {
        foreach (var node in root.FindChildren("*", nameof(Atmosphere), true, false))
            if (node is Atmosphere a) return a;
        return null;
    }

    private static void Save(Image image, string path)
    {
        var full = ProjectSettings.GlobalizePath("res://" + path);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        image.SavePng(full);
        GD.Print($"[terminatorcheck] saved {path}");
    }

    private static double Median(double[] v, int from, int to)
    {
        var list = new List<double>();
        for (int i = from; i <= to; i++) if (!double.IsNaN(v[i])) list.Add(v[i]);
        list.Sort();
        return list.Count > 0 ? list[list.Count / 2] : double.NaN;
    }

    /// <summary>The WGS84 ellipsoid's surface along a geocentric direction.</summary>
    private static EcefPos OnEllipsoid(EcefPos d)
    {
        double a = Ellipsoid.Wgs84.A, b = a * (1 - Ellipsoid.Wgs84.F);
        double r = 1 / Math.Sqrt((d.X * d.X + d.Y * d.Y) / (a * a) + d.Z * d.Z / (b * b));
        return new EcefPos(d.X * r, d.Y * r, d.Z * r);
    }

    private const double Deg = Math.PI / 180;

    private static double Wrap(double lonDeg) => Math.IEEERemainder(lonDeg, 360);

    private static EcefPos ToEcef(FrameAxes axes, Vector3 w) => new(
        axes.X.X * w.X + axes.Y.X * w.Y + axes.Z.X * w.Z,
        axes.X.Y * w.X + axes.Y.Y * w.Y + axes.Z.Y * w.Z,
        axes.X.Z * w.X + axes.Y.Z * w.Y + axes.Z.Z * w.Z);

    private static Vector3 ToWorld(FrameAxes axes, EcefPos p)
    {
        var (x, y, z) = axes.FromEcef(p);
        return new Vector3((float)x, (float)y, (float)z);
    }

    private static EcefPos Unit(EcefPos v)
    {
        double l = v.Length;
        return new EcefPos(v.X / l, v.Y / l, v.Z / l);
    }

    private static EcefPos Cross(EcefPos a, EcefPos b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static double Angle(EcefPos a, EcefPos b)
    {
        a = Unit(a);
        b = Unit(b);
        return Math.Acos(Math.Clamp(FrameAxes.Dot(a, b), -1, 1)) / Deg;
    }

    private void Expect(bool ok, string what)
    {
        if (ok) return;
        _failures++;
        GD.PrintErr($"[terminatorcheck] FAIL: {what}");
    }

    private void Fail(string what) => Expect(false, what);

    private void Done()
    {
        GD.Print(_failures == 0 ? "[terminatorcheck] RESULT: ok" : $"[terminatorcheck] RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = RealClock.Now + seconds;
        while (!condition())
        {
            if (RealClock.Now > end) return false;
            await Frames(1);
        }
        return true;
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>
    /// NOAA's solar position (the Global Monitoring Laboratory's solar calculator spreadsheet, after Meeus): the
    /// subsolar point's latitude (the declination) and longitude (from the equation of time), degrees east. Written
    /// apart from <see cref="Ephemeris"/> on purpose: the check's truth must not be the code it checks.
    /// </summary>
    public static class Noaa
    {
        public static (double LatDeg, double LonDeg) Subsolar(DateTime utc)
        {
            double jd = utc.ToOADate() + 2415018.5;
            double jc = (jd - 2451545) / 36525;
            double l0 = (280.46646 + jc * (36000.76983 + jc * 0.0003032)) % 360;
            double m = 357.52911 + jc * (35999.05029 - 0.0001537 * jc);
            double e = 0.016708634 - jc * (0.000042037 + 0.0000001267 * jc);
            double c = Math.Sin(m * Deg) * (1.914602 - jc * (0.004817 + 0.000014 * jc))
                + Math.Sin(2 * m * Deg) * (0.019993 - 0.000101 * jc) + Math.Sin(3 * m * Deg) * 0.000289;
            double trueLong = l0 + c;
            double omega = 125.04 - 1934.136 * jc;
            double appLong = trueLong - 0.00569 - 0.00478 * Math.Sin(omega * Deg);
            double meanObliq = 23 + (26 + (21.448 - jc * (46.815 + jc * (0.00059 - jc * 0.001813))) / 60) / 60;
            double obliq = meanObliq + 0.00256 * Math.Cos(omega * Deg);
            double decl = Math.Asin(Math.Sin(obliq * Deg) * Math.Sin(appLong * Deg)) / Deg;
            double y = Math.Pow(Math.Tan(obliq / 2 * Deg), 2);
            double eot = 4 / Deg * (y * Math.Sin(2 * l0 * Deg) - 2 * e * Math.Sin(m * Deg)
                + 4 * e * y * Math.Sin(m * Deg) * Math.Cos(2 * l0 * Deg)
                - 0.5 * y * y * Math.Sin(4 * l0 * Deg) - 1.25 * e * e * Math.Sin(2 * m * Deg));
            double minutes = utc.TimeOfDay.TotalMinutes;
            double lon = -(minutes + eot - 720) / 4;
            return (decl, Math.IEEERemainder(lon, 360));
        }
    }
}
