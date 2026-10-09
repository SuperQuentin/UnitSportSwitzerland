using System.Linq;
using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --headless --path . -- --cargearcheck</c> (#290): the cars' sequential and manual boxes
/// and a wheel's H-shifter, on flat tarmac with no world. Non-zero exit on any miss.
/// <list type="bullet">
/// <item>every car (karts excepted) pulls away in first on a clutch let out over a second with a
/// little throttle, slipping at the biting point, and stalls when the clutch is dropped in third;</item>
/// <item>the manual box: into neutral at a standstill, no gear without the clutch, revs in neutral,
/// no reverse rolling forward, no downshift past the limiter, reverse driven on the gas, engine braking;</item>
/// <item>the sequential box: shifts without the clutch, pulls away in third, never stalls;</item>
/// <item>the H-shifter's lever (<see cref="HeldShifter"/>): a gate pushed without the clutch goes in
/// as the clutch goes down, and out of the gate is neutral;</item>
/// <item>a kart stays automatic.</item>
/// </list>
/// </summary>
public static class CarGearboxCheck
{
    private const float Dt = 1f / 60f;
    private static int _failures;
    /// <summary><c>--cargearcheck trace</c>: every launch printed as it goes.</summary>
    private static readonly bool Traced = CmdArgs.Has("trace");
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    public static int Run()
    {
        _failures = 0;
        var settings = GameSettings.Current;
        var was = settings.RideProfile;
        settings.RideProfile = RideProfile.Sim;

        // every car with a box to shift; a hybrid's e-CVT has none (#760: Hybrid below)
        foreach (var spec in CarCatalog.All.Where(s => s.Body.Shape != BodyShape.Kart && s.Transmission == CarTransmission.Stepped)) Launch(spec);
        Manual();
        Sequential();
        Shifter();
        Selector();
        Converter();
        Kart();
        Hybrid();

        settings.RideProfile = was;
        GD.Print(_failures == 0 ? "[cargear] RESULT: ok" : $"[cargear] RESULT: FAILED ({_failures})");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// A hybrid (#760) stays the automatic whichever box the driver picks: no neutral to start in,
    /// no clutch to stall on, away on the gas as on the pedals.
    /// </summary>
    private static void Hybrid()
    {
        foreach (var spec in CarCatalog.All.Where(s => s.Transmission == CarTransmission.ECvt))
            foreach (var box in new[] { CarGearbox.Sequential, CarGearbox.Manual })
            {
                var run = new Run1(new Car(spec), box);
                run.For(4f, 0.6f);
                Check(run.C.Gearbox == CarGearbox.Automatic && run.C.Gear == 1 && run.U > 8f,
                    $"{spec.Label} in {box}: still the automatic, {F(run.U * 3.6f)} km/h after 4 s at 60% throttle, gear {run.C.Gear}");
            }
    }

    private static void Check(bool ok, string what)
    {
        GD.Print($"[cargear]   {(ok ? "ok    " : "FAILED")} {what}");
        if (!ok) _failures++;
    }

    private static string F(float v, string f = "F1") => v.ToString(f, Inv);

    /// <summary>A car on flat tarmac, stepped at 60 Hz.</summary>
    private sealed class Run1
    {
        public readonly Car C;
        public RideMotion M;
        public float Time;
        public Run1(Car c, CarGearbox box)
        {
            C = c;
            C.SetGearbox(box, 0f);
        }

        public void Step(float throttle, float brake = 0f)
        {
            C.Step(new RideInput(throttle, brake, 0f, false), new RideGround(true, 0f, Surface.Asphalt), Dt, ref M);
            Time += Dt;
        }

        /// <summary>Seconds of the same pedals.</summary>
        public void For(float seconds, float throttle, float brake = 0f)
        {
            for (float t = 0f; t < seconds; t += Dt) Step(throttle, brake);
        }

        public float U => M.Speed * Mathf.Cos(M.Slip);
    }

    private static Car Ae86() => new(CarCatalog.All.First(s => s.Label == "AE86 hatch"));

    /// <summary>Every car: first on a clutch let out over a second, then dropped in third.</summary>
    private static void Launch(CarSpec spec)
    {
        var r = new Run1(new Car(spec), CarGearbox.Manual);
        var c = r.C;
        c.ClutchFoot = 1f;
        r.For(0.2f, 0f);
        bool first = c.SelectGate(1, 0f);
        bool slipped = false, stalled = false;
        for (float t = 0f; t < 6f; t += Dt)
        {
            c.ClutchFoot = Mathf.Clamp(1f - t / 1.2f, 0f, 1f);
            r.Step(0.35f);
            // the biting point: the engine turns faster than the wheels drive it
            float wheels = r.U / spec.WheelRadius * spec.Gears[0] * spec.FinalDrive * 60f / Mathf.Tau;
            slipped |= c.ClutchFoot is > 0.3f and < 0.6f && r.U > 0.05f && c.Rpm > wheels + 150f;
            stalled |= c.Stalled;
            if (Traced && Mathf.PosMod(t, 0.1f) < Dt)
                GD.Print($"[cargear]     t {F(t, "F2")} foot {F(c.ClutchFoot, "F2")} rpm {F(c.Rpm, "F0")} u {F(r.U * 3.6f)} gear {c.Gear}{(c.Stalled ? " STALLED" : "")}");
        }
        Check(first && slipped && !stalled && r.U > 15f / 3.6f,
            $"{spec.Label}: first on the clutch, a third of throttle: {F(r.U * 3.6f)} km/h after 6 s, slipped {slipped}, stalled {stalled}");

        var d = new Run1(new Car(spec), CarGearbox.Manual);
        d.C.ClutchFoot = 1f;
        d.For(0.2f, 0f);
        d.C.SelectGate(3, 0f);
        d.C.ClutchFoot = 0f;
        bool died = false;
        for (int i = 0; i < 120 && !died; i++) { d.Step(0f); died = d.C.Stalled; }
        Check(died && !d.C.EngineRunning, $"{spec.Label}: clutch dropped in third, no throttle: stalled");
    }

    private static void Manual()
    {
        GD.Print("[cargear] the manual box (AE86 hatch)");
        var r = new Run1(Ae86(), CarGearbox.Manual);
        var c = r.C;
        Check(c.Gear == 0, "into the manual box at a standstill: neutral");

        bool refused = !c.SelectGate(1, 0f) && c.Gear == 0 && c.Event == "grind";
        c.Event = null;
        Check(refused, "no gear without the clutch: it grinds, still in neutral");

        r.For(1f, 0.6f);
        Check(c.Rpm > c.Spec.IdleRpm * 2.5f && r.U < 0.01f, $"revved in neutral: {F(c.Rpm, "F0")} rpm, standing still");
        r.For(2f, 0f);
        Check(Mathf.Abs(c.Rpm - c.Spec.IdleRpm) < 120f, $"let go in neutral: back to {F(c.Rpm, "F0")} rpm (idle {F(c.Spec.IdleRpm, "F0")})");

        // away in first, up to second at 40 km/h
        c.ClutchHeld = true;
        r.For(0.3f, 0f);
        c.SelectGate(1, r.U);
        c.ClutchHeld = false;
        r.For(1.5f, 0.4f);
        for (int i = 0; i < 20 * 60 && r.U < 40f / 3.6f; i++) r.Step(1f);
        c.ClutchHeld = true;
        r.For(0.25f, 0f);
        bool second = c.SelectGate(2, r.U);
        c.ClutchHeld = false;
        r.For(1f, 0.5f);
        Check(second && c.Gear == 2 && !c.Stalled, $"first to 40 km/h, second on the clutch: gear {c.Gear}, {F(r.U * 3.6f)} km/h");

        // first at 70 km/h would throw the engine past the limiter
        for (int i = 0; i < 20 * 60 && r.U < 70f / 3.6f; i++) r.Step(1f);
        c.ClutchHeld = true;
        r.For(0.25f, 0f);
        bool overrev = !c.SelectGate(1, r.U) && c.Event == "overrev";
        c.Event = null;
        bool noReverse = !c.SelectGate(-1, r.U) && c.Event == "grind";
        c.Event = null;
        Check(overrev && noReverse, $"at {F(r.U * 3.6f)} km/h: first refused (over the limiter), reverse refused (rolling)");

        // engine braking: off the throttle in second against coasting in neutral
        c.ClutchHeld = false;
        r.For(0.6f, 0f);
        float before = r.U;
        r.For(2f, 0f);
        float inGear = before - r.U;
        var n = new Run1(Ae86(), CarGearbox.Manual) { M = new RideMotion { Speed = before } };
        n.For(2f, 0f);
        float coasting = before - n.U;
        Check(c.Gear == 2 && inGear > coasting * 1.5f, $"off the throttle in second: {F(inGear * 3.6f)} km/h lost in 2 s, coasting in neutral {F(coasting * 3.6f)}");

        // reverse at a standstill, driven on the gas
        var b = new Run1(Ae86(), CarGearbox.Manual);
        b.C.ClutchFoot = 1f;
        b.For(0.2f, 0f);
        bool reverse = b.C.SelectGate(-1, 0f);
        for (float t = 0f; t < 4f; t += Dt)
        {
            b.C.ClutchFoot = Mathf.Clamp(1f - t / 1.2f, 0f, 1f);
            b.Step(0.3f);
        }
        Check(reverse && b.U < -1f && !b.C.Stalled, $"reverse at a standstill, on the gas: {F(b.U * 3.6f)} km/h (stalled {b.C.Stalled})");

        // stalled: the engine is off, nothing drives
        var s = new Run1(Ae86(), CarGearbox.Manual);
        s.C.ClutchFoot = 1f;
        s.For(0.2f, 0f);
        s.C.SelectGate(2, 0f);
        s.C.ClutchFoot = 0f;
        for (int i = 0; i < 120 && !s.C.Stalled; i++) s.Step(0f);
        s.C.EngineRunning = false;
        s.For(3f, 1f);
        Check(s.C.Stalled && s.U < 0.2f, $"stalled in second: the gas does nothing with the engine off ({F(s.U * 3.6f)} km/h)");
    }

    private static void Sequential()
    {
        GD.Print("[cargear] the sequential box (AE86 hatch)");
        var r = new Run1(Ae86(), CarGearbox.Sequential);
        var c = r.C;
        Check(c.Gear == 1, "into the sequential box at a standstill: first");
        c.ShiftUp(0f);
        c.ShiftUp(0f);
        Check(c.Gear == 3, $"up twice at a standstill without a clutch: gear {c.Gear}");
        r.For(8f, 0.3f);
        Check(!c.Stalled && c.EngineRunning && r.U > 10f / 3.6f, $"away in third on a third of throttle: {F(r.U * 3.6f)} km/h, no stall");
        c.ShiftDown(r.U);
        c.ShiftDown(r.U);
        Check(c.Gear == 1 || c.Event == "overrev", $"down twice: gear {c.Gear}");
        c.Event = null;
        var n = new Run1(Ae86(), CarGearbox.Sequential);
        n.C.ShiftDown(0f);
        bool neutral = n.C.Gear == 0;
        n.C.ShiftDown(0f);
        Check(neutral && n.C.Gear == -1, $"down from first at a standstill: neutral, then reverse ({n.C.Gear})");
    }

    private static void Shifter()
    {
        GD.Print("[cargear] a wheel's H-shifter (HeldShifter) in the manual AE86");
        var r = new Run1(Ae86(), CarGearbox.Manual);
        var c = r.C;
        var lever = new HeldShifter();
        void Frame(int gate)
        {
            if (lever.Step(gate, c.Gear, c.ClutchPedal, out bool retry) is { } g)
            {
                bool took = c.SelectGate(g, r.U);
                lever.Took(took);
                if (retry && !took) c.Event = null;
            }
            r.Step(0f);
        }

        Frame(2);
        bool ground = c.Gear == 0 && c.Event == "grind";
        c.Event = null;
        for (int i = 0; i < 10; i++) Frame(2);
        bool quiet = c.Event == null && c.Gear == 0;
        c.ClutchFoot = 1f;
        for (int i = 0; i < 3; i++) Frame(2);
        Check(ground && quiet && c.Gear == 2, $"lever into second without the clutch: grinds once, then in as the clutch goes down (gear {c.Gear})");
        for (int i = 0; i < 3; i++) Frame(0);
        Check(c.Gear == 0, "lever out of the gate: neutral");
        for (int i = 0; i < 3; i++) Frame(-1);
        Check(c.Gear == -1, "lever into reverse with the clutch down: reverse");
        // back out of reverse with the clutch still down, then let it up in neutral
        for (int i = 0; i < 3; i++) Frame(0);
        c.ClutchFoot = 0f;
        r.Step(0f);
        Frame(5);
        bool refused = c.Gear == 0 && c.Event == "grind";
        c.Event = null;
        for (int i = 0; i < 10; i++) Frame(5);
        Check(refused && c.Gear == 0 && c.Event == null, "lever into fifth without the clutch: grinds once, stays in neutral while held there");
    }

    /// <summary>The automatic with the H-shifter as its selector: P R N D.</summary>
    private static void Selector()
    {
        GD.Print("[cargear] the automatic's selector from the H-shifter (AE86 hatch)");
        Check(HeldShifter.Selector(1) == DriveSelector.Park && HeldShifter.Selector(3) == DriveSelector.Reverse
            && HeldShifter.Selector(-1) == DriveSelector.Reverse && HeldShifter.Selector(0) == DriveSelector.Neutral
            && HeldShifter.Selector(4) == DriveSelector.Drive && HeldShifter.Selector(2) == DriveSelector.Drive
            && HeldShifter.Selector(6) == DriveSelector.Drive, "gate 1 P, 3 and R reverse, out of a gate N, the others D");

        var r = new Run1(Ae86(), CarGearbox.Automatic);
        var c = r.C;
        c.Selector = DriveSelector.Drive;
        r.For(4f, 0.5f);
        Check(c.Gear >= 1 && r.U > 5f && c.GearText.StartsWith('D'), $"D on the gas: {c.GearText}, {F(r.U * 3.6f)} km/h");
        r.For(8f, 0f, 1f);
        r.For(1f, 0f, 1f);
        Check(c.Gear >= 1 && Mathf.Abs(r.U) < 0.1f, $"D, the brake held at a standstill: stays in drive ({c.GearText}), does not back up, does not creep");
        r.For(10f, 0f);
        Check(r.U > 4f / 3.6f && r.U < 9f / 3.6f, $"D off the pedals: creeps at {F(r.U * 3.6f)} km/h");
        r.For(8f, 0f, 1f);
        c.EngineRunning = false;
        r.For(3f, 0f);
        bool still = Mathf.Abs(r.U) < 0.1f;
        c.EngineRunning = true;
        Check(still, "engine off in D: no creep");

        c.Selector = DriveSelector.Reverse;
        r.For(3f, 0.4f);
        Check(c.Gear == -1 && r.U < -1f && c.GearText == "R", $"R on the gas, not the brake: {F(r.U * 3.6f)} km/h");
        r.For(4f, 0f, 1f);
        r.For(8f, 0f);
        Check(r.U < -4f / 3.6f && r.U > -9f / 3.6f, $"R off the pedals: creeps back at {F(-r.U * 3.6f)} km/h");
        r.For(4f, 0f, 1f);

        c.Selector = DriveSelector.Neutral;
        r.For(2f, 0.6f);
        Check(c.Gear == 0 && Mathf.Abs(r.U) < 0.1f && c.Rpm > c.Spec.IdleRpm * 2f && c.GearText == "N", $"N on the gas: revs to {F(c.Rpm, "F0")} rpm, stands still");
        var pedals = new Run1(Ae86(), CarGearbox.Automatic);
        pedals.For(3f, 0f);
        Check(Mathf.Abs(pedals.U) < 0.05f, "no selector (keyboard, pad): no creep, the car stands");

        // rolling at 50 km/h, the lever into R: neutral until it has slowed, never reverse at speed
        var m = new Run1(Ae86(), CarGearbox.Automatic) { M = new RideMotion { Speed = 50f / 3.6f } };
        m.C.Selector = DriveSelector.Reverse;
        m.For(0.5f, 0.5f);
        Check(m.C.Gear == 0 && m.U > 10f, $"R at {F(m.U * 3.6f)} km/h forward: neutral until stopped");

        // P: rolling slowly, it holds; on a steep grade too
        var p = new Run1(Ae86(), CarGearbox.Automatic) { M = new RideMotion { Speed = 1f } };
        p.C.Selector = DriveSelector.Park;
        p.For(2f, 0.8f);
        Check(p.C.GearText == "P" && Mathf.Abs(p.U) < 0.05f, $"P at walking pace, gas down: held ({F(p.U * 3.6f, "F2")} km/h)");
    }

    /// <summary>The automatic's engine speed through its converter: it flares, floats down, never under idle.</summary>
    private static void Converter()
    {
        GD.Print("[cargear] the automatic's converter (AE86 hatch): what the tach and the sound follow");
        var r = new Run1(Ae86(), CarGearbox.Automatic);
        var c = r.C;
        var s = c.Spec;
        float idle = s.IdleRpm, stall = idle + 0.25f * (s.Redline - idle);
        r.For(0.5f, 0f);
        bool atIdle = Mathf.Abs(c.Rpm - idle) < 1f;
        r.For(0.4f, 1f);
        float flare = c.Rpm;
        float coupled = r.U / s.WheelRadius * s.Gears[0] * s.FinalDrive * 60f / Mathf.Tau;
        Check(atIdle && flare > idle + 0.6f * (stall - idle) && flare > coupled + 500f,
            $"standing at idle, then floored: {F(flare, "F0")} rpm after 0.4 s while the wheels turn it at {F(coupled, "F0")} (stall {F(stall, "F0")})");

        // up through the box: no drop on an upshift faster than the converter lets the revs float down
        float lowest = float.MaxValue, steepest = 0f, last = c.Rpm;
        int shifts = 0, gear = c.Gear;
        for (float t = 0f; t < 15f; t += Dt)
        {
            r.Step(1f);
            lowest = Mathf.Min(lowest, c.Rpm);
            steepest = Mathf.Max(steepest, (last - c.Rpm) / Dt);
            last = c.Rpm;
            if (c.Gear != gear) { shifts++; gear = c.Gear; }
        }
        Check(shifts >= 2 && steepest < 15000f, $"{shifts} upshifts flat out: the revs fall at most {F(steepest, "F0")} rpm/s (they used to drop in one frame)");

        float before = c.Rpm;
        r.Step(0f);
        float oneFrame = before - c.Rpm;
        for (float t = 0f; t < 3f; t += Dt) { r.Step(0f); lowest = Mathf.Min(lowest, c.Rpm); }
        Check(oneFrame < 400f && lowest >= idle - 1f, $"lifted at {F(r.U * 3.6f, "F0")} km/h: {F(oneFrame, "F0")} rpm lost in the first frame; never under idle (lowest {F(lowest, "F0")})");
    }

    private static void Kart()
    {
        var kart = CarCatalog.All.FirstOrDefault(s => s.Body.Shape == BodyShape.Kart);
        if (kart == null) return;
        var c = new Car(kart);
        c.SetGearbox(CarGearbox.Manual, 0f);
        Check(c.Gearbox == CarGearbox.Automatic && c.Gear == 1, $"{kart.Label}: a kart's centrifugal clutch stays automatic");
        // FootPlayer gives a kart no selector; one set anyway must not stop it
        var r = new Run1(new Car(kart), CarGearbox.Automatic);
        r.For(3f, 1f);
        Check(r.U > 3f, $"{kart.Label}: drives on the gas with no selector ({F(r.U * 3.6f)} km/h)");
    }
}
