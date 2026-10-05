using Godot;
using UnitSport.Audio;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --headless --path . -- --truckcheck</c> (#70): the trucks and buses on flat ground with
/// no world, their numbers against what the real machines do and against the textbook geometry.
/// Non-zero exit on the first thing that is not so. Printed per vehicle, then per behaviour:
/// <list type="bullet">
/// <item>0-50 and 0-80 km/h and the top speed, loaded; 80-0 on the service brakes; pulling away
/// on a 12% grade at 40 t;</item>
/// <item>off-tracking: a semi's trailer axles in a steady 12 m turn against √(R² − L²) along the chain;</item>
/// <item>a jackknife: the retarder on the drive axle on snow in a bend, Sim against Game's assist;</item>
/// <item>the articulated bus through a lane change, the drawbar train through a turn, a semi reversing;</item>
/// <item>the gearbox by hand: pulling away on the clutch, stalling in twelfth, grinding without the clutch;</item>
/// <item>the air: pumping the brakes with the engine off lets the spring brakes on;</item>
/// <item>rollover: a full tanker thrown into a bend too fast;</item>
/// <item>the pickup and the boat trailers (#463): what takes a ball trailer, the nose weight on the
/// ball, launching and winching the boat, the server's count of what is driven.</item>
/// </list>
/// </summary>
public static class HeavyCheck
{
    private const float Dt = 1f / 60f;
    private static int _failures;
    /// <summary><c>--truckcheck trace</c>: the hill start and the rollover, printed as they go.</summary>
    private static bool _trace;

    private static void Trace(Run2 r, string what)
    {
        if (!_trace || Mathf.PosMod(r.Time, 0.5f) >= Dt) return;
        var t = r.T;
        GD.Print($"[truck]     {what} t {F(r.Time)} v {F(r.U * 3.6f)} {t.GearLabel} rpm {F(t.Rpm, "F0")} clutch {F(t.Box.Clutch, "F2")}{(t.Box.Locked ? " locked" : "")} thr {F(t.Throttle, "F2")}"
            + $" γ {string.Join(",", t.Articulation.Take(t.SectionCount - 1).Select(a => F(Mathf.RadToDeg(a), "F0")))}"
            + $" lat {string.Join(",", t.Train.Bodies.Select(b => F(Mathf.Abs(b.V.Dot(b.Forward) * b.W) / 9.81f, "F2")))} g");
    }
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    public static int Run()
    {
        _failures = 0;
        _trace = CmdArgs.Has("trace");
        var settings = Core.GameSettings.Current;
        var was = settings.RideProfile;
        settings.RideProfile = Core.RideProfile.Sim;

        foreach (var spec in HeavyCatalog.All) Performance(spec);
        PartThrottle();
        HillHold();
        OffTracking();
        Jackknife();
        BusLaneChange();
        DrawbarTurn();
        Reversing();
        Gearbox();
        Air();
        Rollover();
        Roads();
        BoatTrailers();

        settings.RideProfile = was;
        GD.Print(_failures == 0 ? "[truck] RESULT: ok" : $"[truck] RESULT: FAILED ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        GD.Print($"[truck]   {(ok ? "ok    " : "FAILED")} {what}");
        if (!ok) _failures++;
    }

    private static string F(float v, string f = "F1") => v.ToString(f, Inv);

    /// <summary>A heavy vehicle with the trailer it usually pulls, loaded full.</summary>
    private static Truck Loaded(HeavySpec spec, float load = 1f)
    {
        int trailer = spec.Takes switch
        {
            Coupling.FifthWheel => TrailerCatalog.Code(0, load),
            // a farm tractor (#494) pulls the tipping trailer, its harvest the load
            Coupling.Drawbar when spec.Farm => TrailerTipper(load),
            Coupling.Drawbar => TrailerCatalog.Code(3, load),
            Coupling.Ball => TrailerCatalog.Code(TrailerCatalog.TrailerFor(RideKind.Speedboat), load),
            _ => 0,
        };
        return new Truck(spec, trailer, load) { ShiftOverride = HeavyShift.Automatic };
    }

    /// <summary>The tipping trailer (#494) with <paramref name="load"/> of its sacks of wheat.</summary>
    private static int TrailerTipper(float load)
    {
        int i = System.Array.FindIndex(TrailerCatalog.All.ToArray(), t => t.Body == TrailerBody.Tipper);
        return TrailerCatalog.WithTank(TrailerCatalog.Code(i, 0f), new Farming.Tank(Terrain.Format.CropKind.Wheat, Mathf.RoundToInt(TrailerCatalog.All[i].TankItems * load)));
    }

    /// <summary>A train driven on flat ground by a function of time; the tractor's pose integrated alongside.</summary>
    private sealed class Run2
    {
        public readonly Truck T;
        public RideMotion M;
        public Vector2 P;
        public float Time;
        public Surface Ground = Surface.Asphalt;
        public float Grade;
        public Run2(Truck t) { T = t; }

        public void Step(RideInput input)
        {
            T.Step(input, new RideGround(true, Grade, Ground), Dt, ref M);
            float dir = M.Yaw + M.Slip;
            P += new Vector2(Mathf.Cos(dir), Mathf.Sin(dir)) * M.Speed * Dt;
            Time += Dt;
        }

        public float U => M.Speed * Mathf.Cos(M.Slip);

        /// <summary>A section's point, metres behind its front, in the world.</summary>
        public Vector2 World(int k, float at)
        {
            var (local, yaw) = T.Local(k);
            var b = T.Train.Bodies[k];
            var point = local + new Vector2(Mathf.Cos(yaw), Mathf.Sin(yaw)) * (b.CgAt - at);
            return P + point.Rotated(M.Yaw);
        }

        public bool Finite => float.IsFinite(M.Speed) && float.IsFinite(M.Yaw) && T.Articulation.All(float.IsFinite);
    }

    /// <summary>Holds a speed on the throttle and brake, steering as asked.</summary>
    private static RideInput Hold(Run2 r, float kmh, float steer)
    {
        float err = kmh / 3.6f - r.U;
        return new RideInput(Mathf.Clamp(err * 0.6f, 0f, 1f), Mathf.Clamp(-err * 0.4f, 0f, 1f), steer, false);
    }

    private static void Performance(HeavySpec spec)
    {
        var r = new Run2(Loaded(spec));
        float t50 = float.NaN, t80 = float.NaN;
        int shifts = 0, gear = r.T.Gear;
        for (int i = 0; i < 150 * 60 && r.Finite; i++)
        {
            r.Step(new RideInput(1f, 0f, 0f, false));
            if (r.T.Gear != gear) { shifts++; gear = r.T.Gear; }
            if (float.IsNaN(t50) && r.U >= 50f / 3.6f) t50 = r.Time;
            if (float.IsNaN(t80) && r.U >= 80f / 3.6f) t80 = r.Time;
        }
        float top = r.U * 3.6f;
        float mass = r.T.Train.Mass / 1000f;
        GD.Print($"[truck] {spec.Label}: {mass:F1} t{(r.T.Trailer != null ? " with a " + r.T.Trailer.Label : "")}");
        GD.Print($"[truck]   0-50 {F(t50)} s  0-80 {F(t80)} s  top {F(top, "F0")} km/h (limiter {F(spec.LimiterKmh, "F0")})  {shifts} shifts, top gear {r.T.GearLabel}");
        Check(r.Finite && top > Mathf.Min(spec.LimiterKmh, 85f) - 6f && top < spec.LimiterKmh + 4f, "reaches its limiter on the flat");
        // a 40 t truck is slow: 0-80 in 35-80 s; a bus 0-50 in 12-30 s
        if (spec.Class is HeavyClass.Tractor or HeavyClass.Rigid)
            Check(t80 is > 30f and < 90f, $"0-80 km/h at {mass:F0} t in {F(t80)} s (a 450 hp 40 t truck: ~40-60 s)");
        else if (spec.Class == HeavyClass.Coach)
            Check(t50 is > 7f and < 20f, $"0-50 km/h in {F(t50)} s (a 430 hp coach ~9-12 s)");
        else if (spec.Farm)
        {
            // a farm machine (#494) never sees 50: 0-25 km/h, a tractor with 10 t of grain behind ~10-25 s
            float t25 = float.NaN;
            var f = new Run2(Loaded(spec));
            for (int i = 0; i < 90 * 60 && float.IsNaN(t25); i++) { f.Step(new RideInput(1f, 0f, 0f, false)); if (f.U >= 24f / 3.6f) t25 = f.Time; }
            Check(t25 is > 3f and < 45f, $"0-24 km/h in {F(t25)} s at {mass:F0} t");
        }
        else if (spec.Class == HeavyClass.Pickup)
            Check(t50 is > 2f and < 7f, $"0-50 km/h in {F(t50)} s with 2.6 t of boat behind (a Raptor alone ~2.5 s)");
        else
            Check(t50 is > 10f and < 32f, $"0-50 km/h in {F(t50)} s (a city bus ~15-25 s)");

        // 80 (or its limiter) to 0 on the service brakes
        float v0 = Mathf.Min(80f, spec.LimiterKmh) / 3.6f;
        var b = new Run2(Loaded(spec)) { M = new RideMotion { Speed = v0 } };
        b.T.Box.Reset(true, v0);
        float dist = 0f;
        for (int i = 0; i < 60 * 60 && b.U > 0.1f; i++)
        {
            b.Step(new RideInput(0f, 1f, 0f, false));
            dist += b.U * Dt;
        }
        float ideal = v0 * v0 / (2f * spec.BrakeDecel);
        Check(b.Finite && dist > ideal * 0.85f && dist < ideal * 1.6f + 12f, $"{F(v0 * 3.6f, "F0")}-0 in {F(dist)} m (air lag and the ABS: {F(ideal)} m at a perfect {F(spec.BrakeDecel)} m/s²)");

        // pulling away up 12% full: the box finds a start gear, the clutch slips, it climbs
        var g = new Run2(Loaded(spec)) { Grade = 0.12f };
        float back = 0f;
        for (int i = 0; i < 40 * 60 && g.Finite; i++)
        {
            g.Step(new RideInput(1f, 0f, 0f, false));
            back = Mathf.Min(back, g.P.X);
            if (spec.Class == HeavyClass.Rigid) Trace(g, "hill");
        }
        Check(g.Finite && g.U > 8f / 3.6f && back > -1f, $"pulls away up 12% at {mass:F0} t: {F(g.U * 3.6f)} km/h after 40 s in {g.T.GearLabel}, rolled back {F(-back, "F2")} m");
    }

    /// <summary>Half throttle from rest on full lock, loaded: the automated clutch must let the engine up to where it pulls.</summary>
    private static void PartThrottle()
    {
        GD.Print("[truck] pulling away at half throttle on full lock, 39 t");
        var r = new Run2(Loaded(HeavyCatalog.All[0]));
        for (int i = 0; i < 10 * 60; i++) r.Step(new RideInput(0.5f, 0f, 1f, false));
        Check(r.Finite && r.U > 3f / 3.6f, $"{F(r.U * 3.6f)} km/h after 10 s in {r.T.GearLabel}, engine {F(r.T.Rpm, "F0")} rpm");
        // and up a 5% slope, as the multiplayer check's spawn is
        var s = new Run2(Loaded(HeavyCatalog.All[0])) { Grade = 0.05f };
        for (int i = 0; i < 10 * 60; i++) s.Step(new RideInput(0.5f, 0f, 1f, false));
        Check(s.Finite && s.U > 3f / 3.6f, $"up 5%: {F(s.U * 3.6f)} km/h after 10 s in {s.T.GearLabel}, engine {F(s.T.Rpm, "F0")} rpm");
    }

    /// <summary>Stopped on 10% with no pedal: the automatic holds itself; with the clutch pedal it is the driver's job.</summary>
    private static void HillHold()
    {
        GD.Print("[truck] stopped on a 10% slope, no pedal, 10 s");
        float Rolled(HeavyShift mode)
        {
            var t = new Truck(HeavyCatalog.All[0], TrailerCatalog.Code(0, 1f), 1f) { ShiftOverride = mode };
            t.Box.Mode = mode;
            t.Box.Reset(true);
            var r = new Run2(t) { Grade = 0.1f };
            for (int i = 0; i < 10 * 60; i++)
            {
                r.Step(new RideInput(0f, 0f, 0f, false));
                if (_trace && i % 60 == 0)
                    GD.Print($"[truck]     {mode} t {F(r.Time)} u {F(r.U, "F2")} gear {t.Gear} hold {t.HillHold} brake {F(t.Box.ServiceBrake, "F2")} springs {t.Box.SpringBrakes} clutch {F(t.Box.Clutch, "F2")} rpm {F(t.Rpm, "F0")}");
            }
            return r.P.Length();
        }
        float held = Rolled(HeavyShift.Automatic), manual = Rolled(HeavyShift.SequentialClutch);
        Check(held < 0.3f, $"automatic: hill hold, rolled {F(held, "F2")} m");
        Check(manual > 3f, $"sequential with the clutch: no hold, rolled {F(manual)} m back");
    }

    private static void OffTracking()
    {
        GD.Print("[truck] off-tracking: tractor and curtainsider, steady turn at 8 km/h");
        var r = new Run2(Loaded(HeavyCatalog.All[0], 0.5f));
        var s0 = r.T.Section(0);
        var s1 = r.T.Section(1);
        var front = new List<Vector2>();
        var trailer = new List<Vector2>();
        for (int i = 0; i < 90 * 60; i++)
        {
            r.Step(Hold(r, 8f, 0.5f));
            if (r.Time > 45f)
            {
                front.Add(r.World(0, HeavyTrain.FrontAxleAt(s0)));
                trailer.Add(r.World(1, HeavyTrain.RearGroupAt(s1)));
            }
        }
        var (cf, rf) = Circle(front);
        var (ct, rt) = Circle(trailer);
        var model = HeavyTrain.SteadyRadii(new[] { s0, s1 }, rf);
        float offtrack = rf - rt;
        Check(r.Finite && Mathf.Abs(rt - model[1]) < 0.08f * model[1] && cf.DistanceTo(ct) < 1.5f,
            $"front axle on R {F(rf)} m, trailer axles on {F(rt)} m (the chain: {F(model[1])} m): {F(offtrack)} m of off-tracking");
    }

    /// <summary>Centre and radius of the circle through points on an arc (algebraic least squares, Kåsa).</summary>
    private static (Vector2, float) Circle(List<Vector2> pts)
    {
        // x² + y² + a·x + b·y + c = 0, least squares on (a, b, c), in doubles about the mean
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sz = 0, sxz = 0, syz = 0;
        foreach (var p in pts)
        {
            double x = p.X - mx, y = p.Y - my, z = x * x + y * y;
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y; sz += z; sxz += x * z; syz += y * z;
        }
        int n = pts.Count;
        var m = new double[,] { { sxx, sxy, sx }, { sxy, syy, sy }, { sx, sy, n } };
        var rhs = new[] { -sxz, -syz, -sz };
        var sol = Solve3(m, rhs);
        double cx = -sol[0] / 2, cy = -sol[1] / 2;
        double r = System.Math.Sqrt(cx * cx + cy * cy - sol[2]);
        return (new Vector2((float)(cx + mx), (float)(cy + my)), (float)r);
    }

    private static double[] Solve3(double[,] a, double[] b)
    {
        double Det(double[,] m) => m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double d = Det(a);
        var x = new double[3];
        for (int col = 0; col < 3; col++)
        {
            var m = (double[,])a.Clone();
            for (int row = 0; row < 3; row++) m[row, col] = b[row];
            x[col] = Det(m) / d;
        }
        return x;
    }

    private static float JackknifeRun(bool arcade)
    {
        Core.GameSettings.Current.RideProfile = arcade ? Core.RideProfile.Game : Core.RideProfile.Sim;
        var t = Loaded(HeavyCatalog.All[0], 0f);   // an empty trailer: nothing on the drive axle
        var r = new Run2(t) { Ground = Surface.Snow, M = new RideMotion { Speed = 60f / 3.6f } };
        t.Box.Reset(true, r.M.Speed);
        t.Box.RetarderLevel = 4;
        float worst = 0f;
        for (int i = 0; i < 8 * 60 && r.Finite; i++)
        {
            // a gentle bend, off the throttle, the retarder full on, a stab of brake
            r.Step(new RideInput(0f, r.Time < 2f ? 0.35f : 0f, 0.12f, false));
            worst = Mathf.Max(worst, Mathf.Abs(t.Articulation[0]));
        }
        Core.GameSettings.Current.RideProfile = Core.RideProfile.Sim;
        return Mathf.RadToDeg(worst);
    }

    private static void Jackknife()
    {
        GD.Print("[truck] jackknife: empty trailer, 60 km/h on snow, retarder full on in a bend");
        float sim = JackknifeRun(false), game = JackknifeRun(true);
        Check(sim > 35f, $"Sim folds up: {F(sim, "F0")}° of articulation");
        Check(game < sim * 0.6f && game < 25f, $"Game's stretch braking and stability control hold it: {F(game, "F0")}°");
    }

    private static void BusLaneChange()
    {
        GD.Print("[truck] articulated bus: a lane change at 50 km/h");
        var r = new Run2(new Truck(HeavyCatalog.All[3], 0, 0.5f) { ShiftOverride = HeavyShift.Automatic }) { M = new RideMotion { Speed = 50f / 3.6f } };
        r.T.Box.Reset(true, r.M.Speed);
        float worst = 0f;
        for (int i = 0; i < 12 * 60 && r.Finite; i++)
        {
            float steer = r.Time < 3f ? 0.35f * Mathf.Sin(r.Time / 3f * Mathf.Tau) : 0f;
            r.Step(Hold(r, 50f, steer));
            worst = Mathf.Max(worst, Mathf.Abs(r.T.Articulation[0]));
        }
        Check(r.Finite && Mathf.RadToDeg(worst) < 20f && Mathf.Abs(r.T.Articulation[0]) < 0.05f,
            $"the rear half follows and settles: {F(Mathf.RadToDeg(worst))}° at most, {F(Mathf.RadToDeg(r.T.Articulation[0]), "F2")}° after");
    }

    private static void DrawbarTurn()
    {
        GD.Print("[truck] drawbar train: pull away and a right-angle turn at 15 km/h");
        var r = new Run2(Loaded(HeavyCatalog.All[1], 0.5f));
        float worst0 = 0f, worst1 = 0f;
        for (int i = 0; i < 40 * 60 && r.Finite; i++)
        {
            float steer = r.Time > 10f && r.Time < 16f ? -0.7f : 0f;
            r.Step(Hold(r, 15f, steer));
            worst0 = Mathf.Max(worst0, Mathf.Abs(r.T.Articulation[0]));
            worst1 = Mathf.Max(worst1, Mathf.Abs(r.T.Articulation[1]));
        }
        Check(r.Finite && r.U > 3f && Mathf.Abs(r.T.Articulation[0]) < 0.1f && Mathf.Abs(r.T.Articulation[1]) < 0.1f,
            $"drawbar {F(Mathf.RadToDeg(worst0), "F0")}°, turntable {F(Mathf.RadToDeg(worst1), "F0")}° at most, both straight again after; still {F(r.U * 3.6f)} km/h");
    }

    private static void Reversing()
    {
        GD.Print("[truck] reversing a semi, wheel straight, 20 s");
        var t = Loaded(HeavyCatalog.All[0], 0.5f);
        var r = new Run2(t);
        t.Articulation[0] = 0.03f;   // a hair off straight, as always
        for (int i = 0; i < 60; i++) r.Step(new RideInput(0f, 1f, 0f, false));   // brake held at a stop: reverse
        bool reverse = t.Gear < 0;
        for (int i = 0; i < 20 * 60 && r.Finite; i++)
        {
            float err = 5f / 3.6f + r.U;   // backwards at 5 km/h: the brake pedal drives
            r.Step(new RideInput(Mathf.Clamp(-err * 0.5f, 0f, 1f), Mathf.Clamp(err * 0.6f, 0f, 1f), 0f, false));
        }
        float g = Mathf.RadToDeg(t.Articulation[0]);
        Check(reverse && r.Finite && r.U < -0.5f && Mathf.Abs(g) > 3f && Mathf.Abs(g) <= Mathf.RadToDeg(t.Section(1).MaxArticulation) + 1f,
            $"reverse selected {reverse}; the trailer runs away to one side as it does: {F(g)}° after 20 s at {F(-r.U * 3.6f)} km/h");
    }

    private static void Gearbox()
    {
        GD.Print("[truck] the gearbox by hand (MAN rigid, sequential with the clutch; Scania, H-pattern)");
        var man = new Truck(HeavyCatalog.All[1], 0, 0.5f) { ShiftOverride = HeavyShift.SequentialClutch };
        man.Box.Mode = HeavyShift.SequentialClutch;
        man.Box.Reset(true);
        var r = new Run2(man);
        man.Box.ShiftUp(0f);
        bool refused = man.Gear == 0 && man.Box.Event == "grind";
        man.Box.Event = null;
        man.Box.ClutchHeld = true;
        for (int i = 0; i < 30; i++) r.Step(new RideInput(0f, 0f, 0f, false));
        man.Box.ShiftUp(0f);
        man.Box.ShiftUp(0f);
        int picked = man.Gear;
        man.Box.ClutchHeld = false;
        bool stalled = false;
        for (int i = 0; i < 6 * 60; i++)
        {
            r.Step(new RideInput(0.35f, 0f, 0f, false));
            stalled |= man.Box.Stalled;
        }
        Check(refused, "no gear without the clutch: it grinds and stays in neutral");
        Check(picked == 2 && !stalled && r.U > 1.5f, $"second on the clutch, let out with a little throttle: {F(r.U * 3.6f)} km/h after 6 s, no stall");

        var stall = new Truck(HeavyCatalog.All[1], 0, 0.5f) { ShiftOverride = HeavyShift.SequentialClutch };
        stall.Box.Mode = HeavyShift.SequentialClutch;
        stall.Box.Reset(true);
        var s = new Run2(stall);
        stall.Box.ClutchHeld = true;
        for (int i = 0; i < 30; i++) s.Step(new RideInput(0f, 0f, 0f, false));
        for (int i = 0; i < 12; i++) stall.Box.ShiftUp(0f);
        stall.Box.ClutchHeld = false;
        bool died = false;
        for (int i = 0; i < 3 * 60 && !died; i++) { s.Step(new RideInput(0f, 0f, 0f, false)); died = stall.Box.Stalled; }
        Check(died, $"let out in {stall.Gear} from a standstill without throttle: it stalls");

        var sc = new Truck(HeavyCatalog.All[0], 0, 0.5f) { ShiftOverride = HeavyShift.HPatternSplitter };
        sc.Box.Mode = HeavyShift.HPatternSplitter;
        sc.Box.Reset(true);
        sc.Box.SelectGate(3, 0f);
        bool grind = sc.Gear == 0;
        var h = new Run2(sc);
        sc.Box.ClutchHeld = true;
        for (int i = 0; i < 30; i++) h.Step(new RideInput(0f, 0f, 0f, false));
        sc.Box.SelectGate(2, 0f);
        sc.Box.ShiftDown(0f);
        for (int i = 0; i < 2; i++) h.Step(new RideInput(0f, 0f, 0f, false));
        Check(grind && sc.Gear == 3 && sc.GearLabel == "2L", $"H-pattern: gate 2 with the clutch, splitter down: {sc.GearLabel} (gear {sc.Gear})");
    }

    private static void Air()
    {
        GD.Print("[truck] air: pumping the brakes with the engine off");
        // sequential: the automatic would take the brake held at a stop for reverse
        var t = new Truck(HeavyCatalog.All[0], 0, 0.5f) { ShiftOverride = HeavyShift.Sequential };
        var r = new Run2(t);
        t.EngineRunning = false;
        float first = t.Box.AirTank;
        for (int i = 0; i < 40 * 60 && !t.Box.SpringBrakes; i++)
            r.Step(new RideInput(0f, (int)(r.Time / 0.7f) % 2 == 0 ? 1f : 0f, 0f, false));
        Check(t.Box.SpringBrakes, $"tank {F(first)} -> {F(t.Box.AirTank)} bar after {F(r.Time)} s of pumping: spring brakes on");
        t.EngineRunning = true;
        for (int i = 0; i < 30 * 60; i++) r.Step(new RideInput(0f, 0f, 0f, false));
        Check(!t.Box.SpringBrakes && t.Box.AirTank > 7f, $"engine running 30 s: {F(t.Box.AirTank)} bar, springs released");
    }

    private static void Rollover()
    {
        GD.Print("[truck] rollover: a full tanker thrown into a bend at 70 km/h");
        var t = new Truck(HeavyCatalog.All[0], TrailerCatalog.Code(1, 1f), 0f) { ShiftOverride = HeavyShift.Automatic };
        var r = new Run2(t) { M = new RideMotion { Speed = 70f / 3.6f } };
        t.Box.Reset(true, r.M.Speed);
        int rolled = -1;
        for (int i = 0; i < 8 * 60 && rolled < 0; i++) { r.Step(Hold(r, 70f, 0.2f)); rolled = t.Train.Rolling; Trace(r, "roll"); }
        Check(rolled == 1, $"the tanker goes over first: section {rolled} after {F(r.Time)} s (SRT tanker {F(t.Train.Bodies[1].Srt, "F2")} g)");

        var bus = new Truck(HeavyCatalog.All[2], 0, 0.3f) { ShiftOverride = HeavyShift.Automatic };
        var b = new Run2(bus) { M = new RideMotion { Speed = 40f / 3.6f } };
        bus.Box.Reset(true, b.M.Speed);
        int busRolled = -1;
        for (int i = 0; i < 6 * 60 && busRolled < 0; i++) { b.Step(Hold(b, 40f, 0.5f)); busRolled = bus.Train.Rolling; }
        Check(busRolled < 0, $"a city bus through the same at 40 km/h stays up (SRT {F(bus.Train.Bodies[0].Srt, "F2")} g)");
    }

    /// <summary>
    /// The pickup and the boat trailers (#463): the Raptor alone against its published 0-100, which
    /// vehicles take a ball trailer, the nose weight its ball carries, the boat launched and winched
    /// back aboard, and the units the server counts for each state.
    /// </summary>
    private static void BoatTrailers()
    {
        GD.Print("[truck] the pickup and the boat trailers");
        var raptor = HeavyCatalog.All.First(h => h.Class == HeavyClass.Pickup);
        var alone = new Run2(new Truck(raptor, 0, 0f) { ShiftOverride = HeavyShift.Automatic });
        float t100 = float.NaN;
        for (int i = 0; i < 20 * 60 && float.IsNaN(t100); i++)
        {
            alone.Step(new RideInput(1f, 0f, 0f, false));
            if (alone.U >= 100f / 3.6f) t100 = alone.Time;
        }
        Check(t100 is > 4.5f and < 9f, $"Raptor alone 0-100 km/h in {F(t100)} s (published ~5.5-6 s), {alone.T.GearLabel}");

        int jetski = TrailerCatalog.TrailerFor(RideKind.Jetski), speedboat = TrailerCatalog.TrailerFor(RideKind.Speedboat);
        Check(jetski >= 0 && speedboat >= 0 && TrailerCatalog.TrailerFor(RideKind.Steamer) < 0, "a trailer for the jetski and the speedboat, none for the steamer");
        var tractor = HeavyCatalog.All.First(h => h.Class == HeavyClass.Tractor);
        var rigid = HeavyCatalog.All.First(h => h.Class == HeavyClass.Rigid);
        foreach (int i in new[] { jetski, speedboat })
        {
            var t = TrailerCatalog.All[i];
            Check(raptor.Accepts(t) && rigid.Accepts(t) && !tractor.Accepts(t), $"{t.Label}: on the pickup's ball and the rigid's combination coupling, not a fifth wheel");
        }
        Check(rigid.Accepts(TrailerCatalog.All[3]) && !raptor.Accepts(TrailerCatalog.All[3]) && !raptor.Accepts(TrailerCatalog.All[0]),
            "the rigid still takes its drawbar trailer; the pickup takes no truck trailer");

        foreach (int i in new[] { jetski, speedboat })
        {
            var truck = new Truck(raptor, TrailerCatalog.Code(i, 1f), 0f);
            var trailer = truck.Train.Bodies[1];
            float weight = trailer.Mass * HeavyTrain.Gravity;
            float nose = (weight - trailer.StaticLoad.Sum()) / weight;
            Check(truck.Trailer != null && nose is > 0.04f and < 0.12f,
                $"{truck.Trailer?.Label}: {F(trailer.Mass, "F0")} kg, {F(nose * 100f)} % of it on the ball (4-10 % is right)");

            float before = truck.Train.Mass;
            Check(truck.SetBoatAboard(false) && TrailerCatalog.BoatAboard(truck.TrailerCode) == 0 && before - truck.Train.Mass > 300f,
                $"launched: {F(before, "F0")} -> {F(truck.Train.Mass, "F0")} kg, the trailer empty");
            Check(truck.SetBoatAboard(true) && TrailerCatalog.BoatAboard(truck.TrailerCode) == TrailerCatalog.All[i].Boat,
                "winched back aboard");
        }
        Check(TrailerCatalog.BoatAboard(TrailerCatalog.Code(speedboat, 0.3f)) == 0 && TrailerCatalog.BoatAboard(TrailerCatalog.Code(speedboat, 0.6f)) == RideKind.Speedboat,
            "a boat trailer's load is the boat or nothing");

        // the boat is a boat of its own in the trailer's cradle: the train counts as a truck and a trailer
        var at = new GlobalPos();
        int Units(RideKind kind, int train) => new Vehicles.VehicleState(kind, at, 0f, Vector3.Zero, 100f, false, false, 0f, 0, Train: train).Units;
        int full = TrailerCatalog.Code(speedboat, 1f);
        Check(Units(raptor.Kind, full) == 2 && Units(RideKind.Trailer, full) == 1 && Units(RideKind.Speedboat, 0) == 1,
            "units: pickup + trailer 2, a lone trailer 1, its boat 1 of its own");

        // the cradle: a hold on the trailer for its own kind of boat, that boat's hull fits it, its spot on the bunks is in it
        foreach (int i in new[] { jetski, speedboat })
        {
            var t = TrailerCatalog.All[i];
            foreach (var host in new[] { raptor, rigid })
            {
                var train = new Truck(host, TrailerCatalog.Code(i, 1f), 0f);
                var cradle = train.Decks.Where(d => d.CargoOnly).SelectMany(d => d.CargoBays.Select(b => (d.Section, b))).ToArray();
                var hull = Rideable.Create(t.Boat)!.ParkedBox.Size;
                var spot = Avatar.TrailerMeshBuilder.BoatSpot(t, 0, 1f);
                Check(cradle.Length == 1 && cradle[0].Section == train.SectionCount - 1 && cradle[0].b.Takes(t.Boat) && !cradle[0].b.Takes(RideKind.Jetski + (t.Boat == RideKind.Jetski ? 1 : 0))
                    && cradle[0].b.Fits(hull) && cradle[0].b.Contains(spot, 0f) && !train.Walkable,
                    $"{host.Label} + {t.Label}: a cradle in section {(cradle.Length > 0 ? cradle[0].Section : -1)} for the {t.Boat} only, its {F(hull.Z)} m hull fits, the bunks in it, nothing to walk");
            }
        }

        // the pickup's doors are a car's: four, the driver's (CarRig.DriverDoor) the front left one
        var parts = Avatar.PickupMeshBuilder.Build(raptor, 0, 0.5f);
        var rig = Avatar.HeavyRig.Create(raptor, 0, 0.5f);
        var driver = parts.Doors.First(d => 1 << d.Door == Avatar.CarRig.DriverDoor).Centre;
        Check(parts.CarDoors && rig.DoorCount == 4 && rig.DoorPivot(Avatar.CarRig.DriverDoor) != null && driver.X < -0.5f && driver.Z < 0f && new Truck(raptor).CarDoors,
            $"the Raptor's doors: {rig.DoorCount}, the driver's at ({F(driver.X, "F2")}, {F(driver.Z, "F2")}): front left");
        rig.Free();
    }

    private static void Roads()
    {
        GD.Print("[truck] swept path in a 12.5 m turn (outer front corner), the width of road it takes");
        foreach (var spec in HeavyCatalog.All)
        {
            var t = Loaded(spec);
            var sections = t.Train.Bodies.Select(b => b.Spec).ToArray();
            float w = HeavyTrain.SweptWidth(sections, 12.5f);
            GD.Print($"[truck]   {spec.Label}{(t.Trailer != null ? " + " + t.Trailer.Label : ""),-24} {F(w, "F2")} m");
            Check(w > spec.Sections[0].Width && w < 12.5f, "a plausible swept width");
        }
    }
}
