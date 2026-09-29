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
            foreach (var spec in new[] { CarSpec.Coupe86, CarSpec.RotaryFd, CarSpec.Rally4wd })
                failures += Drive(spec, profile) ? 0 : 1;
        }
        GD.Print(failures == 0 ? "[drift] RESULT: every car drifts and recovers" : $"[drift] RESULT: FAILED ({failures})");
        return failures == 0 ? 0 : 1;
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
        float hold = spec.Drive == Drivetrain.All ? 0.8f : 1.5f;
        bool ok = straightSlip < 0.02f && maxSlip > 0.35f && drifting > hold && !spun && endSlip < 0.1f
            && !float.IsNaN(m.Speed);
        GD.Print($"[drift] {profile,-4} {spec.Label,-10} 0-100 {to100,4:F1}s  top {tm.Speed * 3.6f,4:F0} km/h  "
            + $"entry {entrySpeed * 3.6f,4:F0} km/h  max angle {Mathf.RadToDeg(maxSlip),3:F0}°  "
            + $"drifting {drifting:F1}s  end {Mathf.RadToDeg(endSlip),3:F0}° at {m.Speed * 3.6f,3:F0} km/h"
            + (spun ? "  SPUN" : "") + (straightSlip >= 0.02f ? "  UNSTABLE" : "") + (ok ? "  ok" : "  FAIL"));
        return ok;
    }
}
