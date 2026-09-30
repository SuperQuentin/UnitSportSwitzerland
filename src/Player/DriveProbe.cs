using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --drivecheck[,out_prefix] [--cars 0,1,3,4 | --car N] [--seconds S] [--finish M]
/// [--record prefix] [--trace] [--tyrewear on] [--brakewear on] [--at E,N] [--traffic 0]</c>
///
/// <para>
/// A race down the real road from the spawn (<see cref="RaceRoute"/>): every listed car on a
/// single-file grid, all at once, in the same world — they touch, and a hard enough touch puts one
/// out, its wreck left on the road for the others to go round. Each is driven by its own
/// <see cref="AutoPilot"/> working from its own car's specs. Filmed live by a cinematic camera on
/// the leader; <c>--record prefix</c> writes one GPX per car (nose yaw included) for the replay,
/// Absolute Cinema and Absolute Racing. Prints the classification. Non-zero exit if nobody reached
/// the finish or no drift car held a planned drift.
/// </para>
/// </summary>
public partial class DriveProbe : Node
{
    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string? _shotPrefix;
    private readonly int[] _cars;
    private readonly double _seconds;
    private static readonly bool Trace = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--trace") >= 0;

    private bool _done, _routeRequested, _started;
    private double _t, _wait, _countdown = 2.5;
    private RaceRoute? _route;
    private float _finish;

    private readonly List<Entry> _entries = new();
    private readonly List<string> _log = new();

    // cinematic camera
    private Camera3D? _cine;
    private bool _cinePlaced;
    private int _cineCorner = -1;
    private int _shots;
    private bool _shotThisDrift;

    private readonly string? _record = ArgAfter("--record");

    /// <summary>One car in the race: its driver and what is measured of it.</summary>
    private sealed class Entry
    {
        public CarSpec Spec = null!;
        public FootPlayer Player = null!;
        public AutoPilot? Pilot;
        public bool Mounted, Out;
        public double FinishTime = -1;
        public int Impacts, Contacts;
        public float OffRoad, Top, PeakBrake, Arc;
        /// <summary>Where it crashed out: the wreck stays on the road, and the others must go round it.</summary>
        public Vector3? Wreck;
        public readonly StringBuilder Gpx = new();
        public double SinceFix;
    }

    public DriveProbe(ChunkManager chunks, WorldOrigin origin, string? shotPrefix, int car, double seconds)
    {
        _chunks = chunks;
        _origin = origin;
        _shotPrefix = shotPrefix;
        _seconds = seconds;
        var list = ArgAfter("--cars");
        _cars = list != null
            ? list.Split(',').Select(x => int.TryParse(x, out int i) ? i : 0).ToArray()
            : new[] { car };
        _finish = float.TryParse(ArgAfter("--finish"), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 2000f;
    }

    public static (bool Requested, string? Shot, int Car, double Seconds) ParseArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        bool requested = false;
        string? shot = null;
        int car = 0;
        double seconds = 120;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--drivecheck"))
            {
                requested = true;
                var parts = args[i].Split(',');
                if (parts.Length > 1) shot = parts[1];
            }
            else if (args[i] == "--car" && i + 1 < args.Length) int.TryParse(args[i + 1], out car);
            else if (args[i] == "--seconds" && i + 1 < args.Length)
                double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
        }
        return (requested, shot, car, seconds);
    }

    private static string? ArgAfter(string flag)
    {
        var args = OS.GetCmdlineUserArgs();
        int i = System.Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_done) return;
        float dt = (float)delta;
        _wait += delta;
        if (_wait > _seconds + 150) { GD.Print("[drive] TIMEOUT"); End(); return; }

        if (!_routeRequested)
        {
            if (_chunks.Source == null) return;
            _routeRequested = true;
            var (e, n) = SpawnPoint.ParseTarget();
            var at = _origin.ToWorld(e, n, 0);
            var source = _chunks.Source!;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var route = await RaceRoute.BuildAsync(source, _origin, at);
                Callable.From(() =>
                {
                    if (route == null) { GD.Print("[drive] no road near the spawn"); Finish(1); return; }
                    _route = route;
                    _finish = Mathf.Min(_finish, route.Length - 40f);
                    GD.Print($"[drive] route: {route.Class}, {route.Arc[^1]:F0} m, racing line {route.Length:F0} m "
                        + $"(max {route.Line.Room.DefaultIfEmpty(0).Max():F1} m of room either side)");
                }).CallDeferred();
            });
            return;
        }
        if (_route == null) return;
        var line = _route.Line;

        // the grid: single file, 15 m apart, on the racing line near the start — two abreast on a
        // 6 m Jura road put six cars into the first corner together and the forest took them
        if (_entries.Count == 0)
        {
            if (!_chunks.TryGetHeight(line.Points[0], out _)) return;
            for (int k = 0; k < _cars.Length; k++)
            {
                var spec = CarCatalog.All[Mathf.Clamp(_cars[k], 0, CarCatalog.All.Count - 1)];
                float s = 12f + 15f * (_cars.Length - 1 - k);
                var at = line.PointAt(s);
                var fwd = RaceRoute.Flat(line.PointAt(s + 2f) - line.PointAt(s - 2f)).Normalized();
                float g = _chunks.TryGetHeight(at, out float gh) ? gh : at.Y;
                var player = new FootPlayer { Name = $"Driver{k}", Terrain = _chunks };
                AddChild(player);
                player.GlobalPosition = at with { Y = g + 1.2f };
                player.Rotation = new Vector3(0, Mathf.Atan2(-fwd.X, -fwd.Z), 0);
                var entry = new Entry { Spec = spec, Player = player };
                player.Impacted += lost =>
                {
                    if (lost < 2f) return;
                    entry.Impacts++;
                    _log.Add($"{_t,5:F1}s {spec.Label}: impact -{lost * 3.6f:F0} km/h at {entry.Arc:F0} m");
                };
                player.Announced += (text, _) => _log.Add($"{_t,5:F1}s {spec.Label}: {text}");
                _entries.Add(entry);
            }
            return;
        }

        // mount everyone once they are on the ground, then a countdown
        if (!_started)
        {
            foreach (var en in _entries)
            {
                if (en.Mounted || !en.Player.IsOnFloor()) continue;
                en.Mounted = en.Player.SetRide(en.Spec.Kind);
                var entry = en;
                en.Pilot = new AutoPilot(_route, en.Player, en.Spec) { Log = s => _log.Add($"{_t,5:F1}s {s}") };
                en.Player.RideControls = () => entry.Pilot.Drive((float)GetPhysicsProcessDeltaTime(), _started && !entry.Out, Others(entry));
                BeginGpx(en);
            }
            if (_entries.Any(e => !e.Mounted)) return;
            _countdown -= delta;
            if (_countdown > 0) return;
            _started = true;
            GD.Print($"[drive] GO: {string.Join(", ", _entries.Select(e => e.Spec.Label + (e.Spec.Style == DriveStyle.Grip ? " (grip)" : "")))} "
                + $"over {_finish:F0} m of {line.Length:F0} m");
            return;
        }

        _t += delta;
        foreach (var en in _entries)
        {
            if (en.Out) continue;
            en.Arc = en.Pilot!.Arc;
            en.Top = Mathf.Max(en.Top, en.Player.Motion.Speed);
            if (en.Pilot.Car is { } car) en.PeakBrake = Mathf.Max(en.PeakBrake, car.BrakeTemp);
            if (_route.Off(en.Player.GlobalPosition) - _route.HalfWidthAt(en.Player.GlobalPosition) > 1.5f) en.OffRoad += dt;
            if (en.FinishTime < 0 && en.Arc >= _finish) { en.FinishTime = _t; _log.Add($"{_t,5:F1}s {en.Spec.Label} FINISHES"); }
            if (en.Player.Ride != en.Spec.Kind)
            {
                en.Out = true;
                en.Wreck = en.Player.GlobalPosition;
                _log.Add($"{_t,5:F1}s {en.Spec.Label} is OUT (crashed at {en.Arc:F0} m)");
            }
            RecordFix(en, delta);
            foreach (var q in _entries)
                if (q != en && !q.Out && _entries.IndexOf(q) > _entries.IndexOf(en)
                    && RaceRoute.Flat(q.Player.GlobalPosition - en.Player.GlobalPosition).Length() < 2.1f)
                {
                    en.Contacts++; q.Contacts++;
                }
        }
        Cinematic(dt);
        if (Trace && (int)(_t * 2) != (int)((_t - delta) * 2))
            foreach (var en in _entries.Where(e => !e.Out))
            {
                var m = en.Player.Motion;
                GD.Print($"[drive]   t={_t,5:F1} {en.Spec.Label,-12} s={en.Arc,6:F0} v={m.Speed * 3.6f,4:F0}/{en.Pilot!.Profile[en.Pilot.D.Near] * 3.6f,4:F0} "
                    + $"slip={Mathf.RadToDeg(Mathf.Wrap(m.Slip, -Mathf.Pi, Mathf.Pi)),4:F0} off={_route.Off(en.Player.GlobalPosition),4:F1} drift={en.Pilot.D.Drifting}");
            }
        if (_t >= _seconds || _entries.All(e => e.Out || e.FinishTime >= 0)) End();
    }

    /// <summary>The other cars as a driver sees them: running ones and the wrecks left behind.</summary>
    private IEnumerable<AutoPilot.Other> Others(Entry me)
    {
        foreach (var q in _entries)
        {
            if (q == me) continue;
            if (q.Out) { if (q.Wreck is { } w) yield return new AutoPilot.Other(w, 0f, true); }
            else yield return new AutoPilot.Other(q.Player.GlobalPosition, q.Player.Motion.Speed, false);
        }
    }

    // ------------------------------------------------------------------------------------
    // filming, recording, results
    // ------------------------------------------------------------------------------------

    /// <summary>
    /// The race is filmed on its leader: for each drifted corner a camera stands on the outside of
    /// the bend a little ahead and pans with the car through it; otherwise it hangs back and above
    /// at three-quarters. Placed on its first frame, never under the ground, and it dissolves the
    /// trees in its sightline, so a forest never blocks the shot.
    /// </summary>
    private void Cinematic(float dt)
    {
        var lead = _entries.Where(e => !e.Out).OrderByDescending(e => e.Arc).FirstOrDefault();
        if (lead?.Pilot == null) return;
        if (_cine == null)
        {
            _cine = new Camera3D { Name = "Cinematic", Fov = 55f, Far = 20000f };
            AddChild(_cine);
        }
        var p = lead.Player;
        var d = lead.Pilot.D;
        var car = p.GlobalPosition + Vector3.Up * 0.8f;
        int corner = (int)(lead.Arc / 60f);
        var line = _route!.Line;
        if (d.Drifting && d.Handbrake > 0.2f && corner != _cineCorner)
        {
            _cineCorner = corner;
            float s = lead.Arc + 25f;
            var a = line.PointAt(s - 4f); var b = line.PointAt(s + 4f); var c = line.PointAt(s + 12f);
            var right = RaceRoute.Flat(b - a).Normalized().Cross(Vector3.Up);
            float side = RaceRoute.SignedAngle(RaceRoute.Flat(b - a), RaceRoute.Flat(c - b)) > 0 ? 1f : -1f;
            _cine.GlobalPosition = b + right * side * (_route.HalfWidthAt(b) + 7f) + Vector3.Up * 2.5f;
        }
        else if (!d.Drifting)
        {
            var want = car + new Basis(Vector3.Up, p.Motion.Yaw + p.Motion.Slip) * new Vector3(-4f, 5f, 11f);
            _cine.GlobalPosition = _cinePlaced ? _cine.GlobalPosition.Lerp(want, 1f - Mathf.Exp(-3f * dt)) : want;
        }
        _cinePlaced = true;
        var cp = _cine.GlobalPosition;
        if (_chunks.TryGetHeight(cp, out float ground) && cp.Y < ground + 1.5f) _cine.GlobalPosition = cp with { Y = ground + 1.5f };
        var look = car - _cine.GlobalPosition;
        if (look.LengthSquared() > 0.25f && Mathf.Abs(look.Normalized().Y) < 0.98f)
            _cine.GlobalBasis = Basis.LookingAt(look.Normalized(), Vector3.Up);
        _chunks.SetSightlineCut(_cine.GlobalPosition, car, 3f);
        _cine.Current = true;

        float slip = Mathf.Abs(Mathf.Wrap(p.Motion.Slip, -Mathf.Pi, Mathf.Pi));
        if (_shotPrefix != null && d.Drifting && d.Planned && _shots < 4 && slip > 0.45f && d.DriftTime > 0.3f && !_shotThisDrift)
        {
            _shotThisDrift = true;
            string file = $"{_shotPrefix}_drift{++_shots}.png";
            if (GetViewport().GetTexture().GetImage().SavePng(file) == Error.Ok)
                GD.Print($"[drive] wrote {file} ({lead.Spec.Label}, {Mathf.RadToDeg(slip):F0}° at {p.Motion.Speed * 3.6f:F0} km/h)");
        }
        if (!d.Drifting) _shotThisDrift = false;
    }

    private void BeginGpx(Entry en)
    {
        if (_record == null) return;
        en.Gpx.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<gpx version=\"1.1\" creator=\"UnitSportSwitzerland drivecheck\" xmlns=\"http://www.topografix.com/GPX/1/1\" ")
            .Append("xmlns:us=\"https://github.com/SuperQuentin/UnitSportSwitzerland\">\n")
            .Append($"<trk><name>{en.Spec.Label}</name><type>car:{(int)en.Spec.Kind - CarCatalog.First}</type><trkseg>\n");
    }

    /// <summary>
    /// One fix every 0.2 s. The yaw (the NOSE, not the direction of travel) rides in an extension:
    /// a track only knows where the car went, and a drift is exactly where the two differ.
    /// </summary>
    private void RecordFix(Entry en, double delta)
    {
        if (_record == null) return;
        en.SinceFix += delta;
        if (en.SinceFix < 0.2) return;
        en.SinceFix = 0;
        var (e, n) = _origin.ToLv95(en.Player.GlobalPosition);
        var (lat, lon) = SwissProjection.ToWgs84(e, n);
        var ic = CultureInfo.InvariantCulture;
        var time = new System.DateTime(2026, 9, 30, 12, 0, 0, System.DateTimeKind.Utc).AddSeconds(_t);
        en.Gpx.Append(ic, $"<trkpt lat=\"{lat:F7}\" lon=\"{lon:F7}\"><ele>{en.Player.GlobalPosition.Y:F1}</ele>")
            .Append(ic, $"<time>{time:yyyy-MM-ddTHH:mm:ss.fffZ}</time>")
            .Append(ic, $"<extensions><us:yaw>{en.Player.Motion.Yaw:F4}</us:yaw></extensions></trkpt>\n");
    }

    private void End()
    {
        if (_done) return;
        foreach (var line in _log) GD.Print($"[drive]   {line}");
        GD.Print("[drive] classification:");
        int pos = 0;
        foreach (var en in _entries.OrderBy(e => e.FinishTime < 0 ? 1e9 - e.Arc : e.FinishTime))
        {
            pos++;
            var pilot = en.Pilot;
            string result = en.FinishTime >= 0 ? $"{en.FinishTime:F1} s" : en.Out ? $"OUT at {en.Arc:F0} m" : $"{en.Arc:F0} m";
            GD.Print($"[drive]   {pos}. {en.Spec.Label,-14} {(en.Spec.Style == DriveStyle.Grip ? "grip " : "drift")} {result,-14} "
                + $"avg {(en.FinishTime >= 0 ? _finish : en.Arc) / Mathf.Max((float)(en.FinishTime >= 0 ? en.FinishTime : _t), 1f) * 3.6f:F0} km/h, top {en.Top * 3.6f:F0}, "
                + $"{pilot?.Drifts ?? 0} held drifts (best {pilot?.BestDrift ?? 0:F0}°), {pilot?.Plans ?? 0} corners planned / {pilot?.Feasible ?? 0} feasible, "
                + $"off road {en.OffRoad:F1} s, {en.Impacts} impacts, {en.Contacts / 60f:F1} s in contact"
                + (GameSettings.Current.TyreWear && pilot?.Car is { } c1 ? $", tyres F {(1f - c1.TyreWearFront) * 100:F0}% R {(1f - c1.TyreWearRear) * 100:F0}%" : "")
                + (GameSettings.Current.BrakeWear && pilot?.Car is { } c2 ? $", brakes peaked {en.PeakBrake:F0}°C, pads {(1f - c2.PadWear) * 100:F0}%" : ""));
            if (_record != null)
            {
                en.Gpx.Append("</trkseg></trk></gpx>\n");
                string file = $"{_record}_{(int)en.Spec.Kind - CarCatalog.First}.gpx";
                System.IO.File.WriteAllText(file, en.Gpx.ToString());
                GD.Print($"[drive]      recorded {file}");
            }
        }
        bool anyFinish = _entries.Any(e => e.FinishTime >= 0);
        bool driftOk = _entries.All(e => e.Spec.Style == DriveStyle.Grip) || _entries.Any(e => e.Pilot?.Drifts > 0);
        bool ok = anyFinish && driftOk;
        GD.Print(ok ? "[drive] RESULT: raced to the finish, drifting where it fit" : "[drive] RESULT: FAILED");
        if (_shotPrefix != null && GetViewport().GetTexture().GetImage().SavePng($"{_shotPrefix}_end.png") == Error.Ok)
            GD.Print($"[drive] wrote {_shotPrefix}_end.png");
        Finish(ok ? 0 : 1);
    }

    private void Finish(int code)
    {
        _done = true;
        GetTree().Quit(code);
    }
}
