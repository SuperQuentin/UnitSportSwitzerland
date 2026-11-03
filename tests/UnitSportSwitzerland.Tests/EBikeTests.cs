using Godot;
using UnitSport.Player;
using Xunit;

namespace UnitSport.Tests;

/// <summary>The Moustache Samedi 28.4 e-bike (#1159): its frame from the chart, the fit, and the motor.</summary>
public class EBikeTests
{
    private static readonly BicycleSpec Samedi = BicycleCatalog.MoustacheSamedi;
    private static readonly BikeFrame Frame = new(Samedi);
    private static readonly ElectricAssist Motor = Samedi.Assist!;
    private static readonly float Sport = ElectricAssist.Support(AssistLevel.Sport);

    [Fact]
    public void Every_catalogue_bike_is_its_own_ride_kind()
    {
        Assert.Equal(RideKind.RoadBike, BicycleCatalog.Default.Kind);
        Assert.Equal(RideKind.EBike, Samedi.Kind);
        foreach (var bike in BicycleCatalog.All)
        {
            Assert.Same(bike, BicycleCatalog.For(bike.Kind));
            Assert.True(BicycleCatalog.IsBicycle(bike.Kind));
        }
        Assert.Null(BicycleCatalog.For(RideKind.Skis));
        Assert.False(BicycleCatalog.IsBicycle(RideKind.Skis));
    }

    [Fact]
    public void The_low_frame_is_the_same_bike_otherwise()
    {
        var open = BicycleCatalog.MoustacheSamediOpen;
        Assert.Equal(RideKind.EBikeOpen, open.Kind);
        Assert.Equal(BikeFrameStyle.LowFrame, open.FrameStyle);
        Assert.Equal(BikeFrameStyle.Diamond, Samedi.FrameStyle);
        Assert.Same(Samedi.Assist, open.Assist);
        // the same seat tube and cockpit: the rider sits exactly where they do on the diamond
        var fit = new BikeFrame(open);
        Assert.Equal(Frame.Hip, fit.Hip);
        Assert.Equal(Frame.Wrist, fit.Wrist);
    }

    [Fact]
    public void Derived_wheelbase_matches_the_chart()
    {
        Assert.InRange(Frame.Wheelbase, Samedi.Geometry.Wheelbase - 0.015f, Samedi.Geometry.Wheelbase + 0.015f);
    }

    [Fact]
    public void Trail_is_a_trekking_bikes()
    {
        // 69.5° head, 50 mm offset, 700×50: a steady touring front end, 70-95 mm
        Assert.InRange(Frame.Trail, 0.070f, 0.095f);
    }

    [Fact]
    public void The_rider_sits_up_with_the_hands_high()
    {
        var road = BikeFrame.Default;
        // hands over the hips by more than on the road bike, and nearer them: a sat-up rider
        Assert.True(Frame.Wrist.Y - Frame.Hip.Y > road.Wrist.Y - road.Hip.Y + 0.08f,
            $"e-bike hands {Frame.Wrist.Y - Frame.Hip.Y:F3} over the hips, road {road.Wrist.Y - road.Hip.Y:F3}");
        Assert.True(Frame.Wrist.Z - Frame.Hip.Z < road.Wrist.Z - road.Hip.Z,
            $"e-bike reach {Frame.Wrist.Z - Frame.Hip.Z:F3}, road {road.Wrist.Z - road.Hip.Z:F3}");
        Assert.True(Samedi.Upright);
        Assert.False(BicycleCatalog.Default.Upright);
    }

    [Fact]
    public void The_motor_helps_only_while_pedalling()
    {
        Assert.Equal(0f, BikePhysics.AssistWatts(Motor, Sport, 0f, 80f, 5f));
        Assert.Equal(0f, BikePhysics.AssistWatts(Motor, Sport, 150f, 0f, 5f));
    }

    [Fact]
    public void The_motor_multiplies_the_legs_up_to_its_limits()
    {
        // Sport: 2.25 × 100 W
        Assert.Equal(225f, BikePhysics.AssistWatts(Motor, Sport, 100f, 80f, 4f), 1);
        // 400 W of legs would ask 900: the motor peaks at 600
        Assert.Equal(Motor.PeakWatts, BikePhysics.AssistWatts(Motor, Sport, 400f, 90f, 4f), 1);
        // grinding at 40 rpm, 75 N·m is all it has: 75 × 40/60 × 2π = 314 W
        Assert.Equal(75f * 40f / 60f * Mathf.Tau, BikePhysics.AssistWatts(Motor, Sport, 250f, 40f, 2f), 1);
    }

    [Fact]
    public void The_help_fades_out_at_25_kmh()
    {
        float full = BikePhysics.AssistWatts(Motor, Sport, 100f, 80f, 20f / 3.6f);
        float half = BikePhysics.AssistWatts(Motor, Sport, 100f, 80f, 24.5f / 3.6f);
        Assert.Equal(full / 2f, half, 1);
        Assert.Equal(0f, BikePhysics.AssistWatts(Motor, Sport, 100f, 80f, 25f / 3.6f));
        Assert.Equal(0f, BikePhysics.AssistWatts(Motor, Sport, 300f, 80f, 40f / 3.6f));
    }

    /// <summary>The speed the legs and the motor hold, m/s: where the push at the wheel meets the resistance.</summary>
    private static float SteadySpeed(float riderWatts, float grade, bool motor, AssistLevel level = AssistLevel.Sport)
    {
        float mass = 72.5f + Samedi.Mass + 1.5f;
        float dragArea = BikePhysics.DragArea(72.5f, RidingPosition.Hoods, Samedi.DragArea);
        float crr = 0.0045f * BikePhysics.TyreWidthFactor(true, Samedi.TyreWidth);
        float air = BikePhysics.AirDensity(500f);
        float Surplus(float v)
        {
            float watts = riderWatts + (motor ? BikePhysics.AssistWatts(Motor, ElectricAssist.Support(level), riderWatts, 80f, v) : 0f);
            return BikePhysics.DrivetrainEfficiency * watts - v * BikePhysics.ResistingForce(v, mass, dragArea, 0f, crr, air, grade);
        }
        float lo = 0.3f, hi = 30f;
        for (int i = 0; i < 60; i++)
        {
            float mid = (lo + hi) / 2f;
            if (Surplus(mid) > 0f) lo = mid; else hi = mid;
        }
        return lo;
    }

    [Fact]
    public void On_the_flat_it_settles_at_the_cut_off()
    {
        float kmh = SteadySpeed(100f, 0f, motor: true) * 3.6f;
        Assert.InRange(kmh, 24f, 25f);
        // the same easy 100 W without the motor is a commuter's pace on a 27 kg bike sat up
        Assert.InRange(SteadySpeed(100f, 0f, motor: false) * 3.6f, 17f, 22f);
    }

    [Fact]
    public void Up_an_8_percent_ramp_it_climbs_three_times_as_fast()
    {
        float alone = SteadySpeed(120f, 0.08f, motor: false);
        float helped = SteadySpeed(120f, 0.08f, motor: true);
        Assert.True(helped > 2.8f * alone, $"helped {helped * 3.6f:F1} km/h, alone {alone * 3.6f:F1}");
    }

    [Fact]
    public void Balloon_tyres_roll_harder_on_tarmac_and_easier_off_it()
    {
        Assert.Equal(1f, BikePhysics.TyreWidthFactor(true, 0.028f), 3);
        Assert.Equal(1f, BikePhysics.TyreWidthFactor(false, 0.028f), 3);
        Assert.Equal(1.33f, BikePhysics.TyreWidthFactor(true, 0.050f), 2);
        Assert.InRange(BikePhysics.TyreWidthFactor(false, 0.050f), 0.70f, 0.80f);
    }

    [Fact]
    public void One_chainring_shifts_through_all_ten_cogs()
    {
        var gears = new BikeDrivetrain(Samedi);
        gears.Set(0, Samedi.Cassette.Length - 1);
        int shifts = 0;
        while (gears.ShiftHarder()) shifts++;
        Assert.Equal(Samedi.Cassette.Length - 1, shifts);
        Assert.Equal("38×11", gears.ToString());
        while (gears.ShiftEasier()) shifts--;
        Assert.Equal(0, shifts);
        Assert.Equal("38×39", gears.ToString());
    }

    [Fact]
    public void The_remote_steps_through_the_modes_and_round()
    {
        var level = AssistLevel.Off;
        var seen = new System.Collections.Generic.List<AssistLevel>();
        for (int i = 0; i < 5; i++) seen.Add(level = ElectricAssist.Next(level));
        Assert.Equal(new[] { AssistLevel.Eco, AssistLevel.Tour, AssistLevel.Sport, AssistLevel.Turbo, AssistLevel.Off }, seen);
        Assert.Equal(AssistLevel.Tour, Motor.StartLevel);
        Assert.Equal("SPORT", ElectricAssist.Name(AssistLevel.Sport));
    }

    [Fact]
    public void Each_mode_multiplies_the_legs_by_its_factor_and_off_adds_nothing()
    {
        Assert.Equal(0f, BikePhysics.AssistWatts(Motor, ElectricAssist.Support(AssistLevel.Off), 100f, 80f, 4f));
        Assert.Equal(60f, BikePhysics.AssistWatts(Motor, ElectricAssist.Support(AssistLevel.Eco), 100f, 80f, 4f), 1);
        Assert.Equal(140f, BikePhysics.AssistWatts(Motor, ElectricAssist.Support(AssistLevel.Tour), 100f, 80f, 4f), 1);
        Assert.Equal(340f, BikePhysics.AssistWatts(Motor, ElectricAssist.Support(AssistLevel.Turbo), 100f, 80f, 4f), 1);
    }

    [Fact]
    public void An_hour_of_250_W_at_the_cranks_takes_320_Wh()
    {
        // 250 W out over 78% is 320 W drawn: half the battery in an hour of hard help
        Assert.Equal(250f / ElectricAssist.Efficiency, BikePhysics.BatteryDrawWh(250f, 3600f), 1);
        Assert.Equal(0f, BikePhysics.BatteryDrawWh(0f, 10f));
    }

    /// <summary>How far a full battery carries this rider up this grade in this mode, km.</summary>
    private static float RangeKm(float riderWatts, float grade, AssistLevel level)
    {
        float v = SteadySpeed(riderWatts, grade, motor: true, level);
        float motor = BikePhysics.AssistWatts(Motor, ElectricAssist.Support(level), riderWatts, 80f, v);
        float hours = Motor.BatteryWh / Mathf.Max(BikePhysics.BatteryDrawWh(motor, 3600f), 1e-3f);
        return v * 3.6f * hours;
    }

    [Fact]
    public void The_more_help_the_shorter_the_range()
    {
        // a 120 W rider on a 4% road: Bosch's range assistant gives a 625 Wh Performance Line some 40 km in
        // Turbo up to well over 100 in Eco on rolling roads
        float eco = RangeKm(120f, 0.04f, AssistLevel.Eco), tour = RangeKm(120f, 0.04f, AssistLevel.Tour);
        float sport = RangeKm(120f, 0.04f, AssistLevel.Sport), turbo = RangeKm(120f, 0.04f, AssistLevel.Turbo);
        Assert.True(eco > tour && tour > sport && sport > turbo, $"eco {eco:F0}, tour {tour:F0}, sport {sport:F0}, turbo {turbo:F0} km");
        Assert.InRange(turbo, 25f, 70f);
        Assert.InRange(eco, 90f, 300f);
    }
}
