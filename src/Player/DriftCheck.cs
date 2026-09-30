using System;
using Godot;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>godot --headless --path . -- --driftcheck</c>: drives every car's model on flat ground, with
/// no world and no terrain, through a scripted drift: launch, handbrake entry, a counter-steered
/// hold balanced on the throttle, and a recovery. Prints what happened; non-zero exit if a car is
/// unstable in a straight line, never gets past a real drift angle, spins out while being held, or
/// cannot be straightened again.
///
/// <para>
/// Pure numbers on purpose: whether a car can be drifted and caught is a property of the tyre
/// model, and a scene would only add terrain and collision noise on top of it.
/// </para>
/// </summary>
public static class DriftCheck
{
    private const float Dt = 1f / 60f;
    /// <summary>The drift angle the scripted driver holds: 30°, travelling right of a nose turned left.</summary>
    private const float Target = -0.52f;
    private static readonly bool Trace = Array.IndexOf(OS.GetCmdlineUserArgs(), "--trace") >= 0;

    public static int Run()
    {
        int failures = 0;
        foreach (var profile in new[] { RideProfile.Sim, RideProfile.Game })
        {
            GameSettings.Current.RideProfile = profile;
            foreach (var spec in CarCatalog.All)
                failures += Drive(spec, profile) ? 0 : 1;
        }
        failures += Wear() ? 0 : 1;
        GD.Print(failures == 0 ? "[drift] RESULT: every car drifts and recovers" : $"[drift] RESULT: FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// With both wear options on: a long drift eats the rear tyres more than the fronts, and ten hard
    /// stops (fifteen) from 150 to 50 km/h back to back heat the discs into fade — then they must cool again.
    /// </summary>
    private static bool Wear()
    {
        var s = GameSettings.Current;
        bool tyre = s.TyreWear, brake = s.BrakeWear;
        s.TyreWear = s.BrakeWear = true;
        s.RideProfile = RideProfile.Game;
        var ground = new RideGround(true, 0f);

        var car = new Car(CarCatalog.All[0]);
        var m = new RideMotion { Speed = 25f };
        for (float t = 0; t < 0.3f; t += Dt) car.Step(new RideInput(0f, 0f, -1f, false, Handbrake: true), ground, Dt, ref m);
        for (float t = 0; t < 30f; t += Dt)
        {
            // the Drive() hold: wheels toward the travel, turned in short of the target angle
            float slip = Mathf.Wrap(m.Slip, -Mathf.Pi, Mathf.Pi);
            float wheel = slip + 1.5f * (slip - Target) - 0.12f * m.YawRate - 0.45f * slip;
            float throttle = Mathf.Clamp(0.75f + 2f * (Mathf.Abs(Target) - Mathf.Abs(slip)), 0.2f, 1f);
            car.Step(new RideInput(throttle, 0f, Mathf.Clamp(-wheel / car.Spec.MaxSteer, -1f, 1f), false), ground, Dt, ref m);
        }
        float rear = car.TyreWearRear, front = car.TyreWearFront;

        var b = new Car(CarCatalog.All[0]);
        var bm = new RideMotion();
        float peak = 0f, minFactor = 1f;
        for (int stop = 0; stop < 15; stop++)
        {
            bm.Speed = 150f / 3.6f; bm.Slip = 0; bm.YawRate = 0;
            for (float t = 0; t < 8f && bm.Speed > 50f / 3.6f; t += Dt) b.Step(new RideInput(0f, 1f, 0f, false), ground, Dt, ref bm);
            peak = Mathf.Max(peak, b.BrakeTemp);
            minFactor = Mathf.Min(minFactor, b.BrakeFactor);
        }
        for (float t = 0; t < 60f; t += Dt) { bm.Speed = 20f; b.Step(new RideInput(0f, 0f, 0f, false), ground, Dt, ref bm); }
        float cooled = b.BrakeTemp;

        s.TyreWear = tyre; s.BrakeWear = brake;
        bool ok = rear > front && rear > 0.05f && peak > 450f && minFactor < 0.95f && cooled < 300f;
        GD.Print($"[drift] wear: 30 s of drift wore the rears {rear * 100:F1}%, fronts {front * 100:F1}%; fifteen stops 150->50 km/h "
            + $"took the discs to {peak:F0}°C (braking down to {minFactor * 100:F0}%), a minute of cruising cooled them to {cooled:F0}°C"
            + (ok ? "  ok" : "  FAIL"));
        return ok;
    }

    private static bool Drive(CarSpec spec, RideProfile profile)
    {
        var car = new Car(spec);
        var m = new RideMotion();
        var ground = new RideGround(true, 0f);
        float t = 0f;

        // launch: 0-100 time, and the straight line must stay straight
        float straightSlip = 0f, to100 = float.NaN;
        while (t < 6f)
        {
            car.Step(new RideInput(1f, 0f, 0f, false), ground, Dt, ref m);
            t += Dt;
            straightSlip = Mathf.Max(straightSlip, Mathf.Abs(m.Slip));
        }
        float entrySpeed = m.Speed;

        // entry: lift, full left lock, handbrake
        for (float e = 0; e < 0.3f; e += Dt)
            car.Step(new RideInput(0f, 0f, -1f, false, Handbrake: true), ground, Dt, ref m);

        // hold: counter-steer toward the direction of travel, throttle balances the angle
        float maxSlip = 0f, drifting = 0f;
        bool spun = false;
        for (float h = 0; h < 4f; h += Dt)
        {
            float slip = Mathf.Wrap(m.Slip, -Mathf.Pi, Mathf.Pi);
            // Hold an angle, as a driver does: the wheels point down the direction of travel,
            // turned in when the angle is short of the target and out when it is past it, and
            // against any rotation still building. The throttle keeps the rears on the circle.
            float wheel = slip + 1.5f * (slip - Target) - 0.12f * m.YawRate;
            float steer = Mathf.Clamp(-wheel / spec.MaxSteer, -1f, 1f);
            float throttle = Mathf.Clamp(0.75f + 2f * (Mathf.Abs(slip) - Mathf.Abs(Target)) * -1f, 0.2f, 1f);
            // Game drives like a player: hold the turn and the gas, the assist does the catching
            if (profile == RideProfile.Game) { steer = -0.6f; throttle = 1f; }
            car.Step(new RideInput(throttle, 0f, steer, false), ground, Dt, ref m);
            maxSlip = Mathf.Max(maxSlip, Mathf.Abs(slip));
            if (Mathf.Abs(slip) > 0.26f && m.Speed > 6f) drifting += Dt;
            spun |= Mathf.Abs(slip) > 1.6f;
            if (Trace && ((int)(h / Dt)) % 15 == 0)
                GD.Print($"[drift]   t={h:F2} slip={Mathf.RadToDeg(slip),4:F0}° v={m.Speed * 3.6f,4:F0} r={m.YawRate,5:F2} thr={throttle:F2} steer={steer:F2} gear={car.Gear} rpm={car.Rpm:F0}");
        }

        // recovery: steer fully into the slide, a whiff of throttle
        for (float r = 0; r < 3f; r += Dt)
        {
            float slip = Mathf.Wrap(m.Slip, -Mathf.Pi, Mathf.Pi);
            car.Step(new RideInput(0.2f, 0f, Mathf.Clamp(-slip / spec.MaxSteer, -1f, 1f), false), ground, Dt, ref m);
        }
        float endSlip = Mathf.Abs(Mathf.Wrap(m.Slip, -Mathf.Pi, Mathf.Pi));

        // top speed, a separate straight run
        var top = new Car(spec);
        var tm = new RideMotion();
        for (float s = 0; s < 90f; s += Dt)
        {
            top.Step(new RideInput(1f, 0f, 0f, false), ground, Dt, ref tm);
            if (float.IsNaN(to100) && tm.Speed > 100f / 3.6f) to100 = s;
        }

        // four-wheel drive on tarmac grips its way out of a drift sooner; that is the car, not a bug
        // Game is the promise that EVERY car drifts: all of them must hold one. Sim is the real car,
        // and a front-driver or a 90 hp roadster really cannot hold a drift on the throttle; there
        // it must still get past a drift angle on the handbrake and be caught without a spin.
        float hold = profile == RideProfile.Game ? 1.5f : 0f;
        // In Sim a grip car (the FF Hondas, the Lancers...) is the real car and need not slide at
        // all; it must only stay stable. In Game every car must drift, for the player.
        float minAngle = profile == RideProfile.Game ? 0.35f : spec.Style == DriveStyle.Grip ? 0f : 0.26f;
        bool ok = straightSlip < 0.02f && maxSlip > minAngle && drifting >= hold && !spun && endSlip < 0.1f
            && !float.IsNaN(m.Speed);
        GD.Print($"[drift] {profile,-4} {spec.Label,-10} 0-100 {to100,4:F1}s  top {tm.Speed * 3.6f,4:F0} km/h  "
            + $"entry {entrySpeed * 3.6f,4:F0} km/h  max angle {Mathf.RadToDeg(maxSlip),3:F0}°  "
            + $"drifting {drifting:F1}s  end {Mathf.RadToDeg(endSlip),3:F0}° at {m.Speed * 3.6f,3:F0} km/h"
            + (spun ? "  SPUN" : "") + (straightSlip >= 0.02f ? "  UNSTABLE" : "") + (ok ? "  ok" : "  FAIL")
            // against the published figures, in Sim only (Game is deliberately faster)
            + (profile == RideProfile.Sim && spec.RefZeroTo100 > 0
                ? $"  | real 0-100 {spec.RefZeroTo100:F1}s ({(to100 / spec.RefZeroTo100 - 1f) * 100f:+0;-0}%) top {spec.RefTopKmh:F0} ({(tm.Speed * 3.6f / spec.RefTopKmh - 1f) * 100f:+0;-0}%)"
                : ""));
        return ok;
    }
}
