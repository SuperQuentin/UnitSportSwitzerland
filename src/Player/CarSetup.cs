using Godot;
using UnitSport.Audio;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// A kind of tyre: its grip on tarmac against the car's own tyre (<see cref="CarSpec.Grip"/>), and
/// the peak friction it finds off tarmac, where the ground rather than the rubber sets the limit
/// (the same scale as <see cref="Motorbike.SurfaceGrip"/>: a road tyre gets 0.65 on gravel, 0.5 on
/// grass). <see cref="Tread"/> is how it looks: 0 smooth, 1 blocks, 2 big mud lugs.
/// </summary>
public sealed record TyreType(string Name, float Tarmac, float Gravel, float Grass, float Snow, float Ice, int Tread = 0)
{
    /// <summary>The catalog's own road tyre: exactly the motorbikes' road-tyre table.</summary>
    public static readonly TyreType Road = new("Road", 1f, 0.65f, 0.5f, 0.3f, 0.1f);
    public static readonly TyreType Touring = new("All-season", 0.95f, 0.67f, 0.52f, 0.38f, 0.13f);
    public static readonly TyreType Performance = new("Performance", 1.08f, 0.6f, 0.45f, 0.22f, 0.08f);
    public static readonly TyreType SemiSlick = new("Semi-slick", 1.14f, 0.55f, 0.38f, 0.16f, 0.06f);
    public static readonly TyreType GravelRally = new("Gravel", 0.9f, 0.9f, 0.72f, 0.45f, 0.12f, 1);
    public static readonly TyreType AllTerrain = new("All-terrain", 0.92f, 0.8f, 0.7f, 0.45f, 0.13f, 1);
    public static readonly TyreType Desert = new("Rally-raid", 0.9f, 0.88f, 0.78f, 0.5f, 0.13f, 1);
    public static readonly TyreType Mud = new("Mud", 0.82f, 0.85f, 0.84f, 0.55f, 0.15f, 2);

    /// <summary>Peak friction on <paramref name="surface"/> for a car whose tyres grip <paramref name="tarmacGrip"/> on tarmac.</summary>
    public float Grip(Surface surface, float tarmacGrip) => surface switch
    {
        Surface.Gravel => Gravel,
        Surface.Rock => Mathf.Min(tarmacGrip, Gravel + 0.15f),
        Surface.Grass => Grass,
        Surface.Forest => Grass * 0.95f,
        Surface.Water => Grass * 0.8f,
        Surface.Snow => Snow,
        Surface.Ice => Ice,
        _ => tarmacGrip,   // asphalt, wood, indoor
    };
}

/// <summary>
/// A preset over a car's real spec (#40): what makes an AE86 an SUV, a road racer or a rally-raid
/// car. Every car can take every preset. Physics first (tyre, suspension, mass, power, gearing,
/// diff, drivetrain, drag), then the look (ride height, wheels, roof rack, bumper bar).
/// Id 0 is Stock: the catalog car untouched. <see cref="CarSetups.All"/> is <b>append-only</b>: the
/// index is what travels (<c>FootPlayer.CarSetupId</c>, <c>VehicleState.Setup</c>).
/// </summary>
public sealed record CarSetup(string Name, string Blurb)
{
    public int Id { get; init; }
    /// <summary>Null: the car's own tyre (<see cref="TyreType.Road"/>).</summary>
    public TyreType? Tyre { get; init; }
    /// <summary>Body raised (+) or lowered (−) from stock, m. Raises the centre of mass by 70% of it.</summary>
    public float Lift { get; init; }
    /// <summary>Suspension travel, m (a road car ~0.15): how much rough ground it swallows at speed.</summary>
    public float Travel { get; init; } = CarSpec.StockTravel;
    /// <summary>Spring and damper rate against stock: less body roll and pitch, harsher off-road.</summary>
    public float Stiffness { get; init; } = 1f;
    /// <summary>Wheel and tyre diameter against stock: rolling radius (so gearing) and the drawn wheel.</summary>
    public float WheelScale { get; init; } = 1f;
    public float MassDelta { get; init; }
    /// <summary>Engine torque (and so power) against stock.</summary>
    public float Power { get; init; } = 1f;
    public float FinalDrive { get; init; } = 1f;
    public Differential? Diff { get; init; }
    public Drivetrain? Drive { get; init; }
    /// <summary>Drag area Cd·A against stock.</summary>
    public float Drag { get; init; } = 1f;
    public bool RoofRack { get; init; }
    public bool BullBar { get; init; }
    public WingSize? Wing { get; init; }

    /// <summary>The car with this preset on: the same car, other parts. Stock returns the spec itself.</summary>
    public CarSpec Apply(CarSpec spec)
    {
        if (Id == 0) return spec;
        var tyre = Tyre ?? TyreType.Road;
        var body = spec.Body;
        // bigger wheels need the body up at least by their growth, or the tyre tops go through the arches
        float lift = WheelScale > 1f ? Mathf.Max(Lift, 2f * body.WheelRadius * (WheelScale - 1f)) : Lift;
        return spec with
        {
            SetupId = Id,
            TyreType = tyre,
            Grip = spec.Grip * tyre.Tarmac,
            // the published braking was on the car's own tyres
            BrakeDecel = spec.BrakeDecel * tyre.Tarmac,
            Mass = spec.Mass + MassDelta,
            CgHeight = spec.CgHeight + 0.7f * lift,
            PeakKw = spec.PeakKw * Power,
            Torque = spec.Torque.Select(p => (p.Rpm, p.Nm * Power)).ToArray(),
            FinalDrive = spec.FinalDrive * FinalDrive,
            Diff = Diff ?? spec.Diff,
            Drive = Drive ?? spec.Drive,
            RearBias = Drive == Drivetrain.All && spec.Drive != Drivetrain.All ? 0.6f : spec.RearBias,
            DragArea = spec.DragArea * Drag,
            Travel = Travel,
            Stiffness = Stiffness,
            WheelScale = WheelScale,
            // not the published car any more
            RefZeroTo100 = 0f,
            RefTopKmh = 0f,
            Body = body with
            {
                Lift = lift,
                WheelRadius = body.WheelRadius * WheelScale,
                Tread = tyre.Tread,
                RoofRack = RoofRack,
                BullBar = BullBar,
                Wing = Wing ?? body.Wing,
            },
        };
    }
}

/// <summary>The presets, as data. Append-only: the index is the wire id.</summary>
public static class CarSetups
{
    public static readonly IReadOnlyList<CarSetup> All = Number(new[]
    {
        new CarSetup("Stock", "The car as it left the factory."),
        new CarSetup("Everyday", "Lambda daily: all-season tyres, soft and a touch higher, open diff, economy gearing and map.")
        {
            Tyre = TyreType.Touring, Lift = 0.03f, Travel = 0.17f, Stiffness = 0.8f, MassDelta = 40f,
            Power = 0.85f, FinalDrive = 0.9f, Diff = Differential.Open, Drag = 1.02f,
        },
        new CarSetup("SUV", "Raised on all-terrain tyres, four-wheel drive, heavy and tall: happy on a farm track, lazy on a pass.")
        {
            Tyre = TyreType.AllTerrain, Lift = 0.16f, Travel = 0.21f, Stiffness = 0.85f, WheelScale = 1.12f, MassDelta = 280f,
            Power = 1.15f, FinalDrive = 1.1f, Diff = Differential.Viscous, Drive = Drivetrain.All, Drag = 1.4f, RoofRack = true,
        },
        new CarSetup("Road racing", "Circuit car: semi-slicks, lowered and stiff, stripped, a tuned engine and a wing. Tarmac only.")
        {
            Tyre = TyreType.SemiSlick, Lift = -0.05f, Travel = 0.07f, Stiffness = 1.8f, MassDelta = -120f,
            Power = 1.2f, FinalDrive = 1.08f, Diff = Differential.Mechanical, Drag = 1.05f, Wing = WingSize.Big,
        },
        new CarSetup("Rally-raid", "Off-road race car: long-travel suspension, big desert tyres, 4WD, bumper bar and spares on the roof.")
        {
            Tyre = TyreType.Desert, Lift = 0.24f, Travel = 0.32f, Stiffness = 1.1f, WheelScale = 1.22f, MassDelta = 180f,
            Power = 1.35f, FinalDrive = 1.12f, Diff = Differential.Mechanical, Drive = Drivetrain.All, Drag = 1.35f,
            RoofRack = true, BullBar = true,
        },
        new CarSetup("All-terrain", "Crawler: mud tyres, short gearing, locked 4WD. Goes anywhere, slowly.")
        {
            Tyre = TyreType.Mud, Lift = 0.22f, Travel = 0.28f, Stiffness = 0.8f, WheelScale = 1.28f, MassDelta = 320f,
            Power = 1.05f, FinalDrive = 1.4f, Diff = Differential.Torsen, Drive = Drivetrain.All, Drag = 1.5f,
            RoofRack = true, BullBar = true,
        },
        new CarSetup("Supercar", "Everything the engine can give: performance tyres, low, stiff, light, long gearing.")
        {
            Tyre = TyreType.Performance, Lift = -0.06f, Travel = 0.09f, Stiffness = 1.5f, MassDelta = -100f,
            Power = 1.7f, FinalDrive = 0.95f, Diff = Differential.Mechanical, Drag = 0.92f,
        },
        new CarSetup("Rally", "Gravel stage car: gravel tyres, a little lift and travel, 4WD, short gears.")
        {
            Tyre = TyreType.GravelRally, Lift = 0.06f, Travel = 0.2f, Stiffness = 1.1f, MassDelta = -60f,
            Power = 1.2f, FinalDrive = 1.15f, Diff = Differential.Mechanical, Drive = Drivetrain.All, Drag = 1.05f,
        },
    });

    private static IReadOnlyList<CarSetup> Number(CarSetup[] all)
    {
        for (int i = 0; i < all.Length; i++) all[i] = all[i] with { Id = i };
        return all;
    }

    /// <summary>A preset by wire id; anything out of range (from another peer) is Stock.</summary>
    public static CarSetup For(int id) => id >= 0 && id < All.Count ? All[id] : All[0];

    /// <summary>A wire id made safe: out of range is 0.</summary>
    public static int Clamp(int id) => For(id).Id;

    /// <summary>A preset by name ("suv", "road-racing", "rally_raid", "roadracing"), or by number; null if none.</summary>
    public static CarSetup? Parse(string word)
    {
        if (int.TryParse(word, out int n)) return n >= 0 && n < All.Count ? All[n] : null;
        static string Key(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        string k = Key(word);
        return All.FirstOrDefault(s => Key(s.Name) == k) ?? All.FirstOrDefault(s => k.Length >= 3 && Key(s.Name).StartsWith(k));
    }

    /// <summary>A slug for chat commands: "Road racing" -> "road-racing".</summary>
    public static string Slug(CarSetup s) => s.Name.ToLowerInvariant().Replace(' ', '-');

    /// <summary>
    /// <see cref="Rideable.Create"/>, with this preset and these garage parts (<see cref="CarTuning"/>
    /// bits, #56) on it if it is a car: the preset over the stock spec, the garage parts over that.
    /// </summary>
    public static Rideable? Ride(RideKind kind, int setup, long tuning = 0) =>
        (setup != 0 || tuning != 0) && CarCatalog.For(kind) is { } spec
            ? new Car(For(setup).Apply(spec), CarTuning.Unpack(tuning)) : Rideable.Create(kind);

    /// <summary>
    /// How rough and soft a surface is for a car, 0 (tarmac) .. ~1 (rock, forest floor): the bumps
    /// the suspension has to swallow, and the extra rolling resistance of soft ground.
    /// </summary>
    public static float Roughness(Surface surface) => surface switch
    {
        Surface.Gravel => 0.35f,
        Surface.Grass => 0.7f,
        Surface.Forest => 0.9f,
        Surface.Rock => 0.8f,
        Surface.Snow => 0.4f,
        Surface.Ice => 0.05f,
        Surface.Water => 0.8f,
        _ => 0f,
    };

    /// <summary>
    /// How badly the ground shakes the car at this speed, 0 = not at all: roughness × speed over
    /// what the suspension can absorb. A stock road car (0.15 m of travel) at 72 km/h on grass is
    /// 0.7; a rally-raid car (0.32 m) at the same speed 0.34; a lowered, stiff road racer (0.07 m) 2.0.
    /// Stiffer springs pass more of it on.
    /// </summary>
    public static float Harshness(Surface surface, float speed, float travel, float stiffness) =>
        Roughness(surface) * Mathf.Abs(speed) / 20f * (CarSpec.StockTravel / Mathf.Max(travel, 0.03f)) * Mathf.Sqrt(Mathf.Max(stiffness, 0.1f));

    /// <summary>Grip left while the wheels skip over the bumps: none lost on smooth ground, a third at harshness 1.5.</summary>
    public static float BumpGrip(float harsh) => 1f / (1f + 0.35f * harsh);

    /// <summary>
    /// Extra rolling-resistance coefficient off tarmac: soft ground at any speed, plus the energy
    /// the bumps take out of a car bottoming out on them (with the square of the harshness).
    /// </summary>
    public static float RoughDrag(Surface surface, float harsh) => Roughness(surface) * (0.03f + 0.08f * harsh * harsh);

    // ------------------------------------------------------------------------------------
    // --setupcheck: every preset on one car, straight-line runs on flat ground, no world
    // ------------------------------------------------------------------------------------

    private const float Dt = 1f / 60f;

    /// <summary>Time to <paramref name="kmh"/> (NaN if never) and the speed after <paramref name="seconds"/>, flat out in a straight line on <paramref name="surface"/>.</summary>
    public static (float To, float Speed) Run(CarSpec spec, Surface surface, float kmh, float seconds)
    {
        var car = new Car(spec);
        var m = new RideMotion();
        var ground = new RideGround(true, 0f, surface);
        float to = float.NaN;
        for (float t = 0; t < seconds; t += Dt)
        {
            car.Step(new RideInput(1f, 0f, 0f, false), ground, Dt, ref m);
            if (float.IsNaN(to) && m.Speed >= kmh / 3.6f) to = t;
        }
        return (to, m.Speed * 3.6f);
    }

    /// <summary>
    /// <c>--setupcheck [car]</c>: the wire id round-trips and clamps; every preset builds on every
    /// car; on one car (default the AE86) 0-100 and top speed on tarmac, 0-80 and the speed after
    /// 30 s on gravel and on grass, Sim. Fails unless the off-road presets beat the road racer off
    /// tarmac and the road racer and supercar beat the SUV and crawler on it.
    /// </summary>
    public static int Check()
    {
        var args = OS.GetCmdlineUserArgs();
        int at = System.Array.IndexOf(args, "--setupcheck");
        int index = at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int n) ? n : 0;
        var fails = new List<string>();
        foreach (var s in All)
            if (Clamp(s.Id) != s.Id || Parse(Slug(s)) != s) fails.Add($"{s.Name} does not round-trip");
        if (Clamp(-1) != 0 || Clamp(All.Count) != 0 || Clamp(int.MaxValue) != 0) fails.Add("out-of-range id is not Stock");
        foreach (var spec in CarCatalog.All)
            foreach (var s in All)
                if (s.Apply(spec) is var c && (c.Kind != spec.Kind || c.Mass <= 0 || c.WheelRadius <= 0.1f || (s.WheelScale > 1f && c.Body.Lift < 2f * spec.Body.WheelRadius * (s.WheelScale - 1f) - 1e-4f)))
                    fails.Add($"{s.Name} on {spec.Label} builds a bad car");
        if (!ReferenceEquals(All[0].Apply(CarCatalog.All[0]), CarCatalog.All[0])) fails.Add("Stock changed the spec");

        var was = Core.GameSettings.Current.RideProfile;
        Core.GameSettings.Current.RideProfile = Core.RideProfile.Sim;
        var stock = CarCatalog.All[Mathf.Clamp(index, 0, CarCatalog.All.Count - 1)];
        GD.Print($"[setup] {stock.Label}, Sim, flat, straight line, full throttle");
        GD.Print($"[setup] {"preset",-12} {"tarmac 0-100",12} {"top",6} | {"gravel 0-80",11} {"30 s",6} | {"grass 0-80",10} {"30 s",6}");
        var result = new Dictionary<string, (float T100, float Top, float G80, float G30, float M80, float M30)>();
        foreach (var s in All)
        {
            var spec = s.Apply(stock);
            var (t100, _) = Run(spec, Surface.Asphalt, 100f, 60f);
            var (_, top) = Run(spec, Surface.Asphalt, 999f, 90f);
            var (g80, g30) = Run(spec, Surface.Gravel, 80f, 30f);
            var (m80, m30) = Run(spec, Surface.Grass, 80f, 30f);
            result[s.Name] = (t100, top, g80, g30, m80, m30);
            GD.Print($"[setup] {s.Name,-12} {t100,10:F2} s {top,6:F0} | {g80,9:F2} s {g30,6:F0} | {m80,8:F2} s {m30,6:F0}");
        }
        Core.GameSettings.Current.RideProfile = was;

        var r = result;
        void Faster(string a, string b, System.Func<(float T100, float Top, float G80, float G30, float M80, float M30), float> f, string what)
        {
            if (!(f(r[a]) > f(r[b]))) fails.Add($"{a} not ahead of {b} on {what}");
        }
        foreach (var off in new[] { "Rally-raid", "All-terrain", "SUV", "Rally" })
        {
            Faster(off, "Road racing", x => x.M30, "grass");
            Faster(off, "Road racing", x => x.G30, "gravel");
        }
        foreach (var road in new[] { "Road racing", "Supercar" })
            foreach (var off in new[] { "SUV", "All-terrain" })
                Faster(road, off, x => -x.T100, "tarmac 0-100");
        Faster("Rally-raid", "Stock", x => x.M30, "grass");

        GD.Print(fails.Count == 0 ? $"[setup] RESULT: ok, {All.Count} presets on {CarCatalog.All.Count} cars; off-road presets ahead off tarmac, road presets ahead on it"
            : $"[setup] RESULT: FAILED — {string.Join("; ", fails)}");
        return fails.Count == 0 ? 0 : 1;
    }
}
