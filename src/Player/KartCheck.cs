using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;
using UnitSport.Core;

namespace UnitSport.Player;

/// <summary>
/// <c>--kartcheck</c> (#715), headless, pure numbers like <c>--driftcheck</c>: the rental kart is
/// the car the issue describes. Both profiles: governed to 60-70 km/h and 0-50 in a few seconds; it
/// grips hard (more than 1 g round a skidpad) and is twitchier than a road car (its yaw rate answers
/// the wheel quicker than an AE86's); the Game profile keeps a flat-out lap of plain driving upright,
/// and in Sim a handbrake slide at speed trips it onto its side, scrapes it to a stop and stands it
/// up again; loose ground trips it sooner than tarmac. The seat fits its driver, the catalog number
/// is 36, the rental colours differ and the army skin has its military plate.
/// </summary>
public static class KartCheck
{
    private const float Dt = 1f / 60f;

    public static int Run()
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); GD.Print($"[kartcheck] {(ok ? "ok  " : "FAIL")} {what}"); }

        var spec = CarCatalog.All.First(c => c.Body.Shape == BodyShape.Kart);
        Check(spec.Kind == (RideKind)36, $"the kart is RideKind {(int)spec.Kind} (36), car number {(int)spec.Kind - CarCatalog.First}");
        Check(Rideable.Create(spec.Kind) is Car { IsKart: true }, "Rideable.Create gives a Car that knows it is a kart");

        var was = GameSettings.Current.RideProfile;
        foreach (var profile in new[] { RideProfile.Sim, RideProfile.Game })
        {
            GameSettings.Current.RideProfile = profile;
            var (top, to50) = Straight(spec);
            Check(top is >= 60f and <= 70f, $"{profile}: governed top speed {top:F1} km/h (60-70)");
            Check(to50 is >= 3.5f and <= 9f, $"{profile}: 0-50 km/h in {to50:F1} s (3.5-9: a 6.6 kW kart on one fixed ratio)");

            float g = Skidpad(spec, 40f / 3.6f);
            Check(g >= 1.0f, $"{profile}: {g:F2} g round a skidpad at 40 km/h (grips hard: 1 g or more)");

            float kart = YawRise(spec), ae86 = YawRise(CarCatalog.All[0]);
            Check(kart > ae86 * (profile == RideProfile.Sim ? 1.5f : 1.2f), $"{profile}: twitchy: yaw rate {kart:F2} rad/s 0.3 s after a half-lock step at 50 km/h, an AE86's {ae86:F2}");

            var plain = FlatOutBends(spec, Surface.Asphalt);
            Check(!plain.Tipped, $"{profile}: a flat-out lap of S-bends on tarmac stays upright (peak {plain.PeakG:F2} g, {Mathf.RadToDeg(plain.PeakAngle):F0}° across)");
        }

        // the limit: Sim, a handbrake slide at speed trips it, it scrapes to a stop and is stood up again
        GameSettings.Current.RideProfile = RideProfile.Sim;
        var slide = HandbrakeSlide(spec, Surface.Asphalt);
        Check(slide.TippedAt > 0f, $"Sim: a handbrake slide from 60 km/h on tarmac trips it at {slide.TippedAt:F2} s");
        Check(slide.Upright && slide.End < 1f, $"Sim: and it is stood up again, at rest ({slide.End * 3.6f:F1} km/h) after {slide.Total:F1} s");
        Check(slide.Rolled, "Sim: it lies on its side for over a second before it is stood up");
        var grass = HandbrakeSlide(spec, Surface.Grass);
        Check(grass.TippedAt > 0f && Car.TripAngle(Surface.Grass) < Car.TripAngle(Surface.Gravel) && Car.TripAngle(Surface.Gravel) < Car.TripAngle(Surface.Asphalt),
            $"Sim: on grass it trips too ({grass.TippedAt:F2} s), and at a smaller angle than on gravel or tarmac ({Mathf.RadToDeg(Car.TripAngle(Surface.Grass)):F0}°, {Mathf.RadToDeg(Car.TripAngle(Surface.Gravel)):F0}°, {Mathf.RadToDeg(Car.TripAngle(Surface.Asphalt)):F0}°)");
        GameSettings.Current.RideProfile = was;

        // another peer draws what this one drove: the pose flags carry the tip and the lifted wheel
        var (tipTip, tipLift, liftSide) = Replicated(spec);
        Check(Mathf.Abs(tipTip) == 1f, $"a remote copy lies over as the driven kart does (Tip {tipTip:F0} from the pose flags)");
        Check(tipLift > 0.3f && liftSide, $"and lifts the inside rear wheel of a hard left turn (Lift {tipLift:F2}, left {liftSide})");

        // the driver in the seat, hands on the wheel and feet on the pedals, however the wheel is turned
        var seat = KartMeshBuilder.SeatFor(spec.Wheelbase);
        float reach = 0f;
        foreach (float turn in new[] { 0f, HumanMeshBuilder.MaxGripTurn, -HumanMeshBuilder.MaxGripTurn })
        {
            var (hr, hl, fr, fl) = HumanMeshBuilder.DriverReach(seat, turn);
            reach = Mathf.Max(reach, Mathf.Max(Mathf.Max(hr, hl), Mathf.Max(fr, fl)));
        }
        Check(reach < 0.01f, $"the driver reaches the wheel and the pedals (falls short by {reach * 1000f:F0} mm)");
        float eye = HumanMeshBuilder.DriverEye(seat.Hip, seat.Recline).Y;
        Check(eye is > 0.7f and < 1.0f, $"the driver sits low: eye {eye:F2} m over the road");

        // the look: rental colours differ, the army skin has its plate
        var paints = new HashSet<Color>(KartMeshBuilder.Liveries.Select(l => l.Paint));
        Check(paints.Count == KartMeshBuilder.Liveries.Length, $"{paints.Count} rental colours, all different");
        Check(Enumerable.Range(-50, 100).All(s => KartMeshBuilder.NumberOf(s) is >= 1 and <= 99 && KartMeshBuilder.LiveryOf(s).Name.Length > 0),
            "every seed, negative ones too, has a colour and a number from 1 to 99");
        var plate = KartMeshBuilder.MilitaryPlate(40);
        Check(System.Text.RegularExpressions.Regex.IsMatch(plate, @"^M [1-9]\d [1-9]\d\d$"), $"the military plate reads like \"{plate}\": M, two digits, three");
        var army = KartMeshBuilder.Army(spec.Body, 40);
        Check(army.Plate == plate && army.Paint == KartMeshBuilder.Olive && KartMeshBuilder.Dress(spec.Body, 40).Plate == "",
            "the army skin is olive with a military plate, a rental kart has none");
        Check(CarSetups.All[CarSetups.ArmyId].Name == "Army" && CarSetups.For(CarSetups.ArmyId).Apply(spec) is { SetupId: CarSetups.ArmyId } armyKart
            && armyKart.Mass == spec.Mass && armyKart.Grip == spec.Grip && armyKart.Travel == spec.Travel,
            "the Army preset (id 8) keeps the kart's physics whole");

        // built, a rig takes both looks and a kart has no glass, doors or mirrors to speak of
        foreach (var body in new[] { KartMeshBuilder.Dress(spec.Body, 3), army })
        {
            var rig = CarRig.Create(body, spec.Wheelbase, spec.Gauges, HumanPalette.Default);
            bool ok = rig.DoorCount == 0 && rig.Seats.Length == 1 && rig.GetNode<Node3D>("Body").HasNode("Driver");
            rig.Free();
            Check(ok, $"a rig builds with the {(body.Plate.Length > 0 ? "army" : "rental")} look: one seat, no doors, the driver in it");
        }

        GD.Print(fails.Count == 0 ? "[kartcheck] RESULT: ok" : $"[kartcheck] RESULT: FAILED — {fails.Count}: {string.Join("; ", fails)}");
        return fails.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// What a remote copy of the kart shows, read back from <see cref="Car.WritePose"/>'s flags into a rig
    /// by <see cref="Car.AnimateRemote"/>: the tip after a Sim handbrake slide, and the lifted wheel
    /// (+ left) of a hard left turn.
    /// </summary>
    private static (float Tip, float Lift, bool LeftWheel) Replicated(CarSpec spec)
    {
        var was = GameSettings.Current.RideProfile;
        GameSettings.Current.RideProfile = RideProfile.Sim;
        var ground = new RideGround(true, 0f);
        var car = new Car(spec);
        var m = new RideMotion { Speed = 60f / 3.6f };
        for (float t = 0; t < 1f; t += Dt) { m.Speed = 60f / 3.6f; car.Step(new RideInput(0.3f, 0f, 0f, false), ground, Dt, ref m); }
        for (float t = 0; t < 3f && !car.Tipped; t += Dt) car.Step(new RideInput(0f, 0f, t < 0.5f ? -1f : 0f, false, Handbrake: t < 0.3f), ground, Dt, ref m);
        var remote = new Car(spec);
        var rig = CarRig.Create(spec.Body, spec.Wheelbase, spec.Gauges);
        remote.AnimateRemote(rig, car.WritePose(null!, m, default), Dt);
        float tip = rig.Tip;

        // a left turn at the limit: the left (inside) rear wheel comes up
        var turning = new Car(spec);
        var tm = new RideMotion { Speed = 40f / 3.6f };
        for (float t = 0; t < 4f; t += Dt)
            turning.Step(new RideInput(Mathf.Clamp(0.4f + (40f / 3.6f - tm.Speed) * 0.5f, 0f, 1f), 0f, -Mathf.Min(1f, t / 2f), false), ground, Dt, ref tm);
        remote.AnimateRemote(rig, turning.WritePose(null!, tm, default), Dt);
        float lift = Mathf.Abs(rig.Lift);
        bool left = rig.Lift > 0f;
        rig.Free();
        GameSettings.Current.RideProfile = was;
        return (tip, lift, left);
    }

    /// <summary>Governed top speed, km/h, and the time to 50 km/h, s, full throttle on the flat.</summary>
    private static (float Top, float To50) Straight(CarSpec spec)
    {
        var car = new Car(spec);
        var m = new RideMotion();
        float to50 = float.NaN;
        for (float t = 0; t < 40f; t += Dt)
        {
            car.Step(new RideInput(1f, 0f, 0f, false), new RideGround(true, 0f), Dt, ref m);
            if (float.IsNaN(to50) && m.Speed * 3.6f >= 50f) to50 = t;
        }
        return (m.Speed * 3.6f, to50);
    }

    /// <summary>
    /// The most side force a kart holds, in g: held at <paramref name="speed"/> by the throttle while the
    /// wheel is wound on slowly to full lock; the highest lateral acceleration it ever reached.
    /// </summary>
    private static float Skidpad(CarSpec spec, float speed)
    {
        var car = new Car(spec);
        var m = new RideMotion { Speed = speed };
        float peak = 0f;
        for (float t = 0; t < 12f; t += Dt)
        {
            float steer = Mathf.Min(1f, t / 8f);
            // the throttle that holds the speed: a P on the error
            float throttle = Mathf.Clamp(0.4f + (speed - m.Speed) * 0.5f, 0f, 1f);
            car.Step(new RideInput(throttle, 0f, steer, false), new RideGround(true, 0f), Dt, ref m);
            peak = Mathf.Max(peak, Mathf.Abs(car.AccelY) / Rideable.Gravity);
        }
        return peak;
    }

    /// <summary>The yaw rate 0.3 s after the wheel is jerked half way over at 50 km/h, rad/s.</summary>
    private static float YawRise(CarSpec spec)
    {
        var car = new Car(spec);
        var m = new RideMotion { Speed = 50f / 3.6f };
        var ground = new RideGround(true, 0f);
        for (float t = 0; t < 1f; t += Dt) { m.Speed = 50f / 3.6f; car.Step(new RideInput(0.3f, 0f, 0f, false), ground, Dt, ref m); }
        for (float t = 0; t < 0.3f; t += Dt) car.Step(new RideInput(0.3f, 0f, 0.5f, false), ground, Dt, ref m);
        return Mathf.Abs(m.YawRate);
    }

    /// <summary>Flat out through S-bends: the wheel swung ±0.35 every 2 s, as a player weaves. Did it ever trip?</summary>
    private static (bool Tipped, float PeakG, float PeakAngle) FlatOutBends(CarSpec spec, Surface surface)
    {
        var car = new Car(spec);
        var m = new RideMotion { Speed = 15f };
        var ground = new RideGround(true, 0f, surface);
        float peakG = 0f, peakAngle = 0f;
        bool tipped = false;
        for (float t = 0; t < 30f; t += Dt)
        {
            float steer = 0.35f * Mathf.Sign(Mathf.Sin(t * Mathf.Pi / 2f));
            car.Step(new RideInput(1f, 0f, steer, false), ground, Dt, ref m);
            tipped |= car.Tipped;
            peakG = Mathf.Max(peakG, Mathf.Abs(car.AccelY) / Rideable.Gravity);
            peakAngle = Mathf.Max(peakAngle, Mathf.Abs(Mathf.Atan2(Mathf.Abs(Mathf.Sin(m.Slip)), Mathf.Abs(Mathf.Cos(m.Slip)))));
        }
        return (tipped, peakG, peakAngle);
    }

    /// <summary>
    /// From 60 km/h: full lock and the handbrake for a third of a second, then the wheel straightened and
    /// the gas off. When it tripped (s), whether it was lying over for a while, stood up at the end, how
    /// fast it was going then (m/s) and how long the run took (s).
    /// </summary>
    private static (float TippedAt, bool Rolled, bool Upright, float End, float Total) HandbrakeSlide(CarSpec spec, Surface surface)
    {
        var car = new Car(spec);
        var m = new RideMotion { Speed = 60f / 3.6f };
        var ground = new RideGround(true, 0f, surface);
        for (float t = 0; t < 1f; t += Dt) { m.Speed = 60f / 3.6f; car.Step(new RideInput(0.3f, 0f, 0f, false), ground, Dt, ref m); }
        float at = -1f, over = 0f, total = 0f;
        for (float t = 0; t < 12f; t += Dt)
        {
            bool turn = t < 0.5f;
            car.Step(new RideInput(0f, 0f, turn ? -1f : 0f, false, Handbrake: t < 0.3f), ground, Dt, ref m);
            if (car.Tipped) { if (at < 0f) at = t; over += Dt; }
            total = t;
            if (at >= 0f && !car.Tipped && m.Speed < 0.5f) break;
        }
        return (at, over > 1f, !car.Tipped, m.Speed, total);
    }
}
