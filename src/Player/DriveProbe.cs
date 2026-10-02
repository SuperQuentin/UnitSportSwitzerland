using System.Globalization;
using System.Text;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --path . -- --drivecheck[,out_prefix] [--cars 0,1,3,4 | --car N] [--seconds S] [--finish M]
/// [--record prefix] [--trace] [--tyrewear on] [--brakewear on] [--at E,N] [--traffic 0]
/// [--mount K [--riders N]] [--verge 0] [--setups 0,4,3] [--to E,N]</c>
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
///
/// <para>
/// <c>--mount K</c> races N (<c>--riders</c>, default 1) of another mount instead of cars — its
/// <see cref="RideKind"/> number, 0 on foot, 1 the road bike, 2 skis — each on the pilot
/// <see cref="AutoPilot.For"/> picks for it. <c>--verge 0</c> skips the verge survey, so the line
/// keeps to the tarmac (for comparison). <c>--setups</c> gives each listed car a preset
/// (<see cref="CarSetups"/> id or name, in the order of <c>--cars</c>; one value for all). <c>--to E,N</c>
/// (LV95): the road from the spawn toward that point, finishing level with it (overrides <c>--finish</c>).
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
    private readonly int? _mount = int.TryParse(ArgAfter("--mount"), out int k) ? k : null;

    /// <summary>One car in the race: its driver and what is measured of it.</summary>
    private sealed class Entry
    {
        public CarSpec? Spec;
        public RideKind Kind;
        public string Label = "";
        public bool Grip;
        public FootPlayer Player = null!;
        public AutoPilot? Pilot;
        public bool Mounted, Out;
        public double FinishTime = -1;
        public int Impacts, Contacts;
        /// <summary>The speed the current knock has taken so far, and when it last took some.</summary>
        public float HitLoss;
        public double LastHit = -1;
        public bool HitCounted;
        public float OffRoad, Top, PeakBrake, Arc;
        /// <summary>Seconds racing, and of them at ≥ 95% of the profile's speed there; metres with the centre off the tarmac.</summary>
        public float RaceTime, PaceTime, OffTarmac;
        /// <summary>Counted knocks by what was hit (car, traffic, tree, other), and passes made.</summary>
        public readonly Dictionary<string, int> Hits = new();
        public int Passes;
        /// <summary>Where the top speed was reached, and where this entry started, m along the line.</summary>
        public float TopArc, StartArc = -1;
        /// <summary>Where it crashed out: the wreck stays on the road, and the others must go round it.</summary>
        public Vector3? Wreck;
        public readonly StringBuilder Gpx = new();
        public double SinceFix;
    }

    public DriveProbe(ChunkManager chunks, WorldOrigin origin, string? shotPrefix, int car, double seconds)
    {
        _chunks = chunks;
        _origin = origin;
        // the probe keeps its route, cars and logs in world space for the whole run: no floating-origin
        // shift under it (#185 moved the origin 34 km mid-build and the route came out in a village)
        if (Core.OriginShifter.Instance is { } shifter) shifter.ThresholdM = double.MaxValue;
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
            // --to E,N: the race runs from the spawn toward that point and finishes level with it
            Vector3? to = ArgAfter("--to")?.Split(',') is [var te, var tn]
                && double.TryParse(te, NumberStyles.Float, CultureInfo.InvariantCulture, out double toE)
                && double.TryParse(tn, NumberStyles.Float, CultureInfo.InvariantCulture, out double toN) ? _origin.ToWorld(toE, toN, 0) : null;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var route = await RaceRoute.BuildAsync(source, _origin, at, toward: to);
                if (route != null && ArgAfter("--verge") != "0") route.Line = await RaceLine.Widen(route, source, _origin);
                Callable.From(() =>
                {
                    if (route == null) { GD.Print("[drive] no road near the spawn"); Finish(1); return; }
                    _route = route;
                    if (to is { } end)
                    {
                        var pts = route.Line.Points;
                        int k = Enumerable.Range(0, pts.Count).MinBy(i => RaceRoute.Flat(pts[i] - end).LengthSquared());
                        _finish = route.Line.Arc[k];
                        GD.Print($"[drive] --to: finish at {_finish:F0} m, {RaceRoute.Flat(pts[k] - end).Length():F0} m from the point");
                    }
                    _finish = Mathf.Min(_finish, route.Length - 40f);
                    GD.Print($"[drive] route: {route.Class}, {route.Arc[^1]:F0} m, racing line {route.Length:F0} m "
                        + $"(max {route.Line.RoomLeft.Concat(route.Line.RoomRight).DefaultIfEmpty(0).Max():F1} m of room to a side)");
                    PrintVerge(route);
                    // bridged junctions: centreline points further apart than the 2 m step
                    var gaps = Enumerable.Range(1, route.Arc.Count - 1).Where(i => route.Arc[i] - route.Arc[i - 1] > 5f)
                        .Select(i => $"{route.Arc[i - 1]:F0}+{route.Arc[i] - route.Arc[i - 1]:F0}").ToList();
                    GD.Print($"[drive] route gaps over 5 m ({gaps.Count}): {string.Join(" ", gaps)}");
                    // where the route runs, to find a spot again (LV95 every 500 m)
                    GD.Print("[drive] route LV95: " + string.Join(", ", Enumerable.Range(0, (int)(route.Length / 500f) + 1)
                        .Select(k => { var (e, n) = _origin.ToLv95(route.Line.PointAt(k * 500f)); return $"{k * 500} m {e:F0},{n:F0}"; })));
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
            int count = _mount != null ? (int.TryParse(ArgAfter("--riders"), out int riders) ? riders : 1) : _cars.Length;
            for (int k = 0; k < count; k++)
            {
                var spec = _mount == null ? CarCatalog.All[Mathf.Clamp(_cars[k], 0, CarCatalog.All.Count - 1)] : null;
                if (spec != null && ArgAfter("--setups")?.Split(',') is { } setups
                    && CarSetups.Parse(setups[Mathf.Min(k, setups.Length - 1)]) is { Id: > 0 } preset)
                    spec = preset.Apply(spec);
                var kind = spec?.Kind ?? (RideKind)_mount!.Value;
                string label = spec is { SetupId: > 0 } ? $"{spec.Label} {CarSetups.For(spec.SetupId).Name}" : spec?.Label ?? (kind == RideKind.OnFoot ? $"Runner {k + 1}" : $"{Rideable.Create(kind)?.Label ?? kind.ToString()} {k + 1}");
                float s = 12f + 15f * (count - 1 - k);
                var at = line.PointAt(s);
                var fwd = RaceRoute.Flat(line.PointAt(s + 2f) - line.PointAt(s - 2f)).Normalized();
                float g = _chunks.TryGetHeight(at, out float gh) ? gh : at.Y;
                var player = new FootPlayer { Name = $"Driver{k}", Terrain = _chunks };
                AddChild(player);
                player.GlobalPosition = at with { Y = g + 1.2f };
                player.Rotation = new Vector3(0, Mathf.Atan2(-fwd.X, -fwd.Z), 0);
                var entry = new Entry { Spec = spec, Kind = kind, Label = label, Grip = spec?.Style == DriveStyle.Grip, Player = player };
                player.Impacted += lost =>
                {
                    // FootPlayer takes a knock off the speed at most 25 m/s² a frame (0.4 m/s), so one
                    // event never reached the old 2 m/s bar and head-on crashes counted as 0 impacts:
                    // add up the losses of one knock (events less than 0.3 s apart)
                    if (_t - entry.LastHit > 0.3) { entry.HitLoss = 0f; entry.HitCounted = false; }
                    entry.LastHit = _t;
                    entry.HitLoss += lost;
                    // a knock against something (a trunk, a car, the traffic), or a big one against
                    // anything: a hard launch alone reads as a 2 m/s "knock" (the body lags the model)
                    if (entry.HitCounted || entry.HitLoss < 2f || (entry.HitLoss < 5f && !TouchingSomething(player))) return;
                    entry.HitCounted = true;
                    entry.Impacts++;
                    string what = HitKind(player);
                    entry.Hits[what] = entry.Hits.GetValueOrDefault(what) + 1;
                    _log.Add($"{_t,5:F1}s {label}: impact ({what}) at {entry.Arc:F0} m ({entry.Player.Motion.Speed * 3.6f:F0} km/h){(entry.Pilot?.Seen is { Length: > 0 } seen ? $" — saw {seen}" : "")}");
                };
                player.Announced += (text, _) =>
                {
                    _log.Add($"{_t,5:F1}s {label}: {text} (touching {HitKind(player, detail: true)}{(entry.Pilot?.Seen is { Length: > 0 } seen ? $", saw {seen}" : "")})");
                    if (entry.Pilot is { } p) foreach (var line in p.Trail) _log.Add($"        before: {line}");
                };
                _entries.Add(entry);
            }
            // the traffic yields to the local player only, and there is none here: without this it
            // drove through the race as if the cars were not there, and shunted them back up the pass
            if (FindTraffic(GetTree().Root) is { } traffic)
            {
                traffic.Obstacles = () => _entries.Where(e => !e.Out).Select(e => (e.Player.GlobalPosition, e.Player.WorldVelocity));
                // traffic spawned on the grid before there was one (every run began with racers held
                // up behind a car standing nose to nose with the front row, #85)
                foreach (var e in _entries) traffic.ClearAround(e.Player.GlobalPosition, 100f);
            }
            return;
        }

        // mount everyone once they are on the ground, then a countdown
        if (!_started)
        {
            foreach (var en in _entries)
            {
                if (en.Mounted || !en.Player.IsOnFloor()) continue;
                en.Mounted = en.Kind == RideKind.OnFoot || en.Player.SetRide(en.Kind);
                if (!en.Mounted) continue;
                if (en.Spec is { SetupId: > 0 } preset) en.Player.SetCarSetup(preset.SetupId);
                var entry = en;
                en.Pilot = AutoPilot.For(_route, en.Player);
                if (en.Pilot == null) { GD.Print($"[drive] no pilot for {en.Label}"); Finish(1); return; }
                en.Pilot.Log = s => _log.Add($"{_t,5:F1}s {s}");
                // every driver its own, the same each run, as a race's NPCs get them: skill 0.8..1.1 with
                // one ace (≥ 1.05; the grid slot seeded too), a calm temper 0..0.3 (--skill / --aggression
                // set them all)
                var rng = new System.Random(1000 + _entries.IndexOf(en));
                float drawn = AutoPilot.GridSkills(_entries.Count, new System.Random(77))[_entries.IndexOf(en)];
                float skill = float.TryParse(ArgAfter("--skill"), NumberStyles.Float, CultureInfo.InvariantCulture, out float sk) ? sk : drawn;
                float aggr = float.TryParse(ArgAfter("--aggression"), NumberStyles.Float, CultureInfo.InvariantCulture, out float ag) ? ag : 0.3f * (float)rng.NextDouble();
                en.Pilot.Temperament(skill, aggr, 1000 + _entries.IndexOf(en));
                en.Pilot.Go = false;
                en.Player.RideControls = () => entry.Pilot!.Drive((float)GetPhysicsProcessDeltaTime(), _started && !entry.Out, Others(entry));
                BeginGpx(en);
            }
            if (_entries.Any(e => !e.Mounted)) return;
            _countdown -= delta;
            if (_countdown > 0) return;
            _started = true;
            foreach (var en in _entries) en.Pilot!.Go = true;
            GD.Print($"[drive] GO: {string.Join(", ", _entries.Select(e => e.Label + (e.Grip ? " (grip)" : "") + $" [skill {e.Pilot!.Skill:F2} aggr {e.Pilot.Aggression:F2}]"))} "
                + $"over {_finish:F0} m of {line.Length:F0} m");
            return;
        }

        _t += delta;
        foreach (var en in _entries)
        {
            if (en.Out) continue;
            en.Arc = en.Pilot!.Arc;
            if (en.StartArc < 0) en.StartArc = en.Arc;
            float speedNow = en.Kind == RideKind.OnFoot ? RaceRoute.Flat(en.Player.Velocity).Length() : en.Player.Motion.Speed;
            if (speedNow > en.Top) { en.Top = speedNow; en.TopArc = en.Arc; }
            if (en.Pilot.Car is { } car) en.PeakBrake = Mathf.Max(en.PeakBrake, car.BrakeTemp);
            float past = _route.Off(en.Player.GlobalPosition) - _route.HalfWidthAt(en.Player.GlobalPosition);
            if (en.FinishTime < 0 && past > 1.5f) en.OffRoad += dt;
            if (en.FinishTime < 0)
            {
                en.RaceTime += dt;
                if (speedNow >= 0.95f * en.Pilot.Profile[en.Pilot.D.Near]) en.PaceTime += dt;
                if (past > 0f) en.OffTarmac += speedNow * dt;
            }
            if (en.FinishTime < 0 && en.Arc >= _finish) { en.FinishTime = _t; en.Pilot.Finished = true; _log.Add($"{_t,5:F1}s {en.Label} FINISHES"); }
            if (en.Player.Ride != en.Kind)
            {
                en.Out = true;
                en.Wreck = en.Player.GlobalPosition;
                var (outE, outN) = _origin.ToLv95(en.Player.GlobalPosition);
                _log.Add($"{_t,5:F1}s {en.Label} is OUT (crashed at {en.Arc:F0} m, LV95 {outE:F0},{outN:F0})");
            }
            RecordFix(en, delta);
            CountPasses(en);
            foreach (var q in _entries)
                if (q != en && !q.Out && en.FinishTime < 0 && q.FinishTime < 0 && _entries.IndexOf(q) > _entries.IndexOf(en)
                    && RaceRoute.Flat(q.Player.GlobalPosition - en.Player.GlobalPosition).Length() < 2.1f)
                {
                    en.Contacts++; q.Contacts++;
                }
        }
        Cinematic(dt);
        if (Trace && (int)(_t * 10) != (int)((_t - delta) * 10))
            foreach (var en in _entries.Where(e => !e.Out))
            {
                var m = en.Player.Motion;
                GD.Print($"[drive]   t={_t,5:F1} {en.Label,-12} s={en.Arc,6:F0} v={m.Speed * 3.6f,4:F0}/{en.Pilot!.Profile[en.Pilot.D.Near] * 3.6f,4:F0} "
                    + $"slip={Mathf.RadToDeg(MathX.WrapAngle(m.Slip)),4:F0} off={_route.Off(en.Player.GlobalPosition),4:F1} "
                    + $"R={1f / Mathf.Max(Mathf.Abs(_route.Line.Curvature[en.Pilot.D.Near]), 1e-4f),5:F0} drift={en.Pilot.D.Drifting}"
                    + $" in={en.Player.LastRideInput.Throttle:F2}/{en.Player.LastRideInput.Brake:F2}/{en.Player.LastRideInput.Steer:F2}{(en.Player.LastRideInput.Handbrake ? " HB" : "")} lat={en.Pilot.D.Lateral:F2} cap={(en.Pilot.D.Cap < 1e9f ? en.Pilot.D.Cap * 3.6f : 0f):F0} draft={en.Player.Draft:F2}"
                    + (en.Player.GetSlideCollisionCount() > 0 && Enumerable.Range(0, en.Player.GetSlideCollisionCount())
                        .Select(i => en.Player.GetSlideCollision(i).GetCollider()).FirstOrDefault(c => c is not StaticBody3D || c is AnimatableBody3D) is Node hit
                        ? $" touching {hit.GetType().Name}" : ""));
            }
        if (_t >= _seconds || _entries.All(e => e.Out || e.FinishTime >= 0)) End();
    }

    private readonly Dictionary<(Entry, Entry), bool> _ahead = new();

    /// <summary>
    /// A pass: this entry was a car length behind a running rival and is now a car length ahead of
    /// it, the two within 30 m (the band in between keeps two cars side by side from counting a
    /// "pass" every time their nearest line points swap).
    /// </summary>
    private void CountPasses(Entry en)
    {
        foreach (var q in _entries)
        {
            if (q == en || q.Out || en.FinishTime >= 0 || q.FinishTime >= 0) continue;
            float gap = en.Arc - q.Arc;
            if (Mathf.Abs(gap) < 4.5f || Mathf.Abs(gap) > 30f) continue;
            bool ahead = gap > 0f;
            if (_ahead.TryGetValue((en, q), out bool was) && ahead && !was)
            {
                en.Passes++;
                _log.Add($"{_t,5:F1}s {en.Label} passes {q.Label} at {en.Arc:F0} m ({en.Player.Motion.Speed * 3.6f:F0} km/h)");
            }
            _ahead[(en, q)] = ahead;
        }
    }

    /// <summary>The other cars as a driver sees them: running ones and the wrecks left behind.</summary>
    private IEnumerable<AutoPilot.Other> Others(Entry me)
    {
        foreach (var q in _entries)
        {
            if (q == me) continue;
            if (q.Out) { if (q.Wreck is { } w) yield return new AutoPilot.Other(w, Vector3.Zero, true); }
            else yield return new AutoPilot.Other(q.Player.GlobalPosition, q.Player.WorldVelocity, false);
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
            _cine.GlobalPosition = _cinePlaced ? _cine.GlobalPosition.Lerp(want, MathX.Damp(3f, dt)) : want;
        }
        _cinePlaced = true;
        var cp = _cine.GlobalPosition;
        if (_chunks.TryGetHeight(cp, out float ground) && cp.Y < ground + 1.5f) _cine.GlobalPosition = cp with { Y = ground + 1.5f };
        var look = car - _cine.GlobalPosition;
        if (look.LengthSquared() > 0.25f && Mathf.Abs(look.Normalized().Y) < 0.98f)
            _cine.GlobalBasis = Basis.LookingAt(look.Normalized(), Vector3.Up);
        _chunks.SetSightlineCut(_cine.GlobalPosition, car, 3f);
        _cine.Current = true;

        float slip = Mathf.Abs(MathX.WrapAngle(p.Motion.Slip));
        if (_shotPrefix != null && d.Drifting && d.Planned && _shots < 4 && slip > 0.45f && d.DriftTime > 0.3f && !_shotThisDrift)
        {
            _shotThisDrift = true;
            string file = $"{_shotPrefix}_drift{++_shots}.png";
            if (GetViewport().GetTexture().GetImage().SavePng(file) == Error.Ok)
                GD.Print($"[drive] wrote {file} ({lead.Label}, {Mathf.RadToDeg(slip):F0}° at {p.Motion.Speed * 3.6f:F0} km/h)");
        }
        if (!d.Drifting) _shotThisDrift = false;
    }

    private void BeginGpx(Entry en)
    {
        if (_record == null || en.Spec == null) return;
        en.Gpx.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<gpx version=\"1.1\" creator=\"UnitSportSwitzerland drivecheck\" xmlns=\"http://www.topografix.com/GPX/1/1\" ")
            .Append("xmlns:us=\"https://github.com/SuperQuentin/UnitSportSwitzerland\">\n")
            .Append($"<trk><name>{en.Label}</name><type>car:{(int)en.Kind - CarCatalog.First}</type><trkseg>\n");
    }

    /// <summary>
    /// One fix every 0.2 s. The yaw (the NOSE, not the direction of travel) rides in an extension:
    /// a track only knows where the car went, and a drift is exactly where the two differ.
    /// </summary>
    private void RecordFix(Entry en, double delta)
    {
        if (_record == null || en.Spec == null) return;
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
            // what the car's own model reaches flat out in a straight line over the same distance and
            // average grade, and with no end to the road: the top speed to hold the race's against
            string limit = "";
            if (en.Spec != null && _route != null)
            {
                // from where it started to where it peaked: after that it was braking for a corner
                float run = Mathf.Max(en.TopArc - en.StartArc, 1f);
                float grade = (_route.Line.PointAt(en.TopArc).Y - _route.Line.PointAt(en.StartArc).Y) / run;
                limit = $" at {en.TopArc:F0} m (flat out over those {run:F0} m {FlatOut(en.Spec, run, grade) * 3.6f:F0}, unlimited {FlatOut(en.Spec, 1e5f, 0f) * 3.6f:F0}, published {en.Spec.RefTopKmh:F0})";
            }
            GD.Print($"[drive]   {pos}. {en.Label,-14} {(en.Spec == null ? "     " : en.Grip ? "grip " : "drift")} {result,-14} "
                + $"avg {(en.FinishTime >= 0 ? _finish : en.Arc) / Mathf.Max((float)(en.FinishTime >= 0 ? en.FinishTime : _t), 1f) * 3.6f:F0} km/h, top {en.Top * 3.6f:F0}{limit}, "
                + $"{pilot?.Drifts ?? 0} held drifts (best {pilot?.BestDrift ?? 0:F0}°), {pilot?.Plans ?? 0} corners planned / {pilot?.Feasible ?? 0} feasible, "
                + $"off road {en.OffRoad:F1} s ({en.OffTarmac:F0} m off tarmac), at pace {(en.RaceTime > 0 ? en.PaceTime / en.RaceTime * 100f : 0):F0}%, "
                + $"{en.Impacts} impacts{(en.Hits.Count > 0 ? $" ({string.Join(" ", en.Hits.Select(kv => $"{kv.Key} {kv.Value}"))})" : "")}, "
                + $"{en.Passes} passes, {pilot?.Spins ?? 0} spins, {pilot?.Mistakes ?? 0} mistakes, {en.Contacts / 60f:F1} s in contact, "
                + $"verge {pilot?.VergeMetres ?? 0:F0} m safe"
                + (pilot?.VergeUnsafe.Count > 0 ? $" / {string.Join(" ", pilot.VergeUnsafe.Select(kv => $"{kv.Key} {kv.Value:F0} m"))} blocked (up to {pilot.UnsafeDepth:F2} m over, at {string.Join(",", pilot.UnsafeAt)} m)" : " / 0 m blocked")
                + (pilot?.Resets > 0 ? $", {pilot.Resets} reset(s) to the line" : "")
                + (GameSettings.Current.TyreWear && pilot?.Car is { } c1 ? $", tyres F {(1f - c1.TyreWearFront) * 100:F0}% R {(1f - c1.TyreWearRear) * 100:F0}%" : "")
                + (GameSettings.Current.BrakeWear && pilot?.Car is { } c2 ? $", brakes peaked {en.PeakBrake:F0}°C, pads {(1f - c2.PadWear) * 100:F0}%" : ""));
            if (_record != null && en.Spec != null)
            {
                en.Gpx.Append("</trkseg></trk></gpx>\n");
                string file = $"{_record}_{(int)en.Kind - CarCatalog.First}.gpx";
                System.IO.File.WriteAllText(file, en.Gpx.ToString());
                GD.Print($"[drive]      recorded {file}");
            }
        }
        // the race in one line, for before/after tables
        var hits = new Dictionary<string, int>();
        foreach (var en in _entries) foreach (var kv in en.Hits) hits[kv.Key] = hits.GetValueOrDefault(kv.Key) + kv.Value;
        float race = _entries.Sum(e => e.RaceTime), pace = _entries.Sum(e => e.PaceTime);
        GD.Print($"[drive] SUMMARY finishers {_entries.Count(e => e.FinishTime >= 0)}/{_entries.Count}, "
            + $"at pace {(race > 0 ? pace / race * 100f : 0):F0}%, off tarmac {_entries.Sum(e => e.OffTarmac):F0} m, "
            + $"blocked edge {_entries.Sum(e => e.Pilot?.VergeUnsafe.Values.Sum() ?? 0f):F0} m, "
            + $"impacts {(hits.Count == 0 ? "0" : string.Join(" ", hits.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}")))}, "
            + $"passes {_entries.Sum(e => e.Passes)}, spins {_entries.Sum(e => e.Pilot?.Spins ?? 0)}, mistakes {_entries.Sum(e => e.Pilot?.Mistakes ?? 0)}, "
            + $"resets {_entries.Sum(e => e.Pilot?.Resets ?? 0)}, out {_entries.Count(e => e.Out)}");
        // pace index (#159): a finisher's time against what a skill-1 driver of the same car would do on the same
        // line with a clear road (> 100% = faster); the ace (best skill) against the other finishers
        string Index(IEnumerable<Entry> es)
        {
            var l = es.Where(e => e.FinishTime >= 0 && e.Pilot != null).Select(e => e.Pilot!.ReferenceSeconds(_finish) / e.FinishTime * 100.0).ToList();
            return l.Count == 0 ? "n/a" : $"{l.Average():F1}%";
        }
        float best = _entries.Max(e => e.Pilot?.Skill ?? 0f);
        GD.Print($"[drive] PACE ace (skill {best:F2}) {Index(_entries.Where(e => (e.Pilot?.Skill ?? 0f) >= best))}, others {Index(_entries.Where(e => (e.Pilot?.Skill ?? 0f) < best))}");
        bool anyFinish = _entries.Any(e => e.FinishTime >= 0);
        bool driftOk = _entries.All(e => e.Spec == null || e.Grip) || _entries.Any(e => e.Pilot?.Drifts > 0);
        bool ok = anyFinish && driftOk;
        GD.Print(ok ? "[drive] RESULT: raced to the finish, drifting where it fit" : "[drive] RESULT: FAILED");
        if (_shotPrefix != null && GetViewport().GetTexture().GetImage().SavePng($"{_shotPrefix}_end.png") == Error.Ok)
            GD.Print($"[drive] wrote {_shotPrefix}_end.png");
        Finish(ok ? 0 : 1);
    }

    /// <summary>In contact with anything but the ground: a trunk (layer 2), a car, the traffic.</summary>
    private static bool TouchingSomething(FootPlayer p)
    {
        for (int i = 0; i < p.GetSlideCollisionCount(); i++)
            if (p.GetSlideCollision(i).GetCollider() is CollisionObject3D c
                && (c is not StaticBody3D || c is AnimatableBody3D || (c.CollisionLayer & World.TreeColliders.Layer) != 0))
                return true;
        return false;
    }

    /// <summary>What a knock was against: another racer, the traffic, a trunk, or anything else (terrain, a wall, a parked machine).</summary>
    /// <summary>What the car is touching; <paramref name="detail"/>: with a traffic car's state (for the log lines).</summary>
    private static string HitKind(FootPlayer p, bool detail = false)
    {
        string kind = "other";
        for (int i = 0; i < p.GetSlideCollisionCount(); i++)
            switch (p.GetSlideCollision(i).GetCollider())
            {
                case FootPlayer: return "car";
                case AnimatableBody3D a:
                    kind = a.Name.ToString().Contains("Train") ? "train" : "traffic";
                    if (detail && World.Traffic.Current?.Describe(a.GetInstanceId()) is { } what) kind += $" [{what}]";
                    break;
                case CollisionObject3D c when (c.CollisionLayer & World.TreeColliders.Layer) != 0 && kind == "other": kind = "tree"; break;
            }
        return kind;
    }

    private static World.Traffic? FindTraffic(Node node)
    {
        if (node is World.Traffic t) return t;
        foreach (var child in node.GetChildren())
            if (FindTraffic(child) is { } found) return found;
        return null;
    }

    /// <summary>
    /// Full throttle from a standstill in a straight line on a steady grade, stepping the car's own
    /// model: the highest speed reached within <paramref name="distance"/> m (or 10 minutes).
    /// </summary>
    private static float FlatOut(CarSpec spec, float distance, float grade)
    {
        var car = new Car(spec);
        var m = new RideMotion();
        float s = 0f, top = 0f;
        const float Dt = 1f / 60f;
        for (int i = 0; i < 60 * 600 && s < distance; i++)
        {
            car.Step(new RideInput(1f, 0f, 0f, false), new RideGround(true, grade), Dt, ref m);
            s += m.Speed * Dt;
            top = Mathf.Max(top, m.Speed);
        }
        return top;
    }

    /// <summary>What the verge survey found: metres of each side usable, and what blocked the rest.</summary>
    private static void PrintVerge(RaceRoute route)
    {
        var line = route.Line;
        if (!line.Surveyed) { GD.Print("[drive] verge: not surveyed, the line keeps to the tarmac"); return; }
        float usable = 0, used = 0;
        var why = new Dictionary<RaceLine.Block, float>();
        for (int i = 1; i < line.Points.Count; i++)
        {
            float ds = line.Arc[i] - line.Arc[i - 1];
            foreach (var (m, w) in new[] { (line.MarginLeft[i], line.WhyLeft[i]), (line.MarginRight[i], line.WhyRight[i]) })
                if (m > 0f) usable += ds; else why[w] = why.GetValueOrDefault(w) + ds;
            if (line.Beyond[i] > 0.05f) used += ds;
        }
        GD.Print($"[drive] verge: {usable:F0} m of edge safe for two wheels (of {2 * line.Length:F0} m, both sides), "
            + $"the line uses it over {used:F0} m; blocked: {string.Join(", ", why.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value:F0} m"))}");
    }

    private void Finish(int code)
    {
        _done = true;
        GetTree().Quit(code);
    }
}
