using Godot;

namespace UnitSport.Avatar;

/// <summary>
/// Puts the avatars on a turntable against a plain backdrop, with no terrain in the way.
///
/// <para>
/// <c>godot --path . -- --avatars [seconds] [out.png]</c>
/// </para>
///
/// <para>
/// Exists because a figure that looks right beside a road at fifty metres can be wrong up close
/// in ways nothing else reveals — a knee bending the wrong way, a saddle the rider hovers over.
/// Checking that in the world means flying to a player and hoping the light is useful; this
/// shows all four in a row at a fixed distance, every time.
/// </para>
/// </summary>
public partial class AvatarPreview : Node3D
{
    private double _elapsed;
    private double _seconds = 6;
    private Node3D? _mirrorRig;
    private string _output = "";
    private float _viewDegrees = 90;
    private int _focus = -1;
    private float _crank = float.NaN;
    private float _stride = float.NaN;
    private (Audio.Cd.MusicStyle Style, int Move)? _dance;
    private MeshInstance3D? _danceStill, _danceWalk;
    private float _walkPhase;
    private Cyclist? _cyclist;
    private readonly List<Node3D> _turntables = new();

    public static bool Requested(out double seconds, out string output)
    {
        seconds = 6;
        output = "avatars.png";

        var args = OS.GetCmdlineUserArgs();
        int i = Array.IndexOf(args, "--avatars");
        if (i < 0) return false;

        if (i + 1 < args.Length && double.TryParse(args[i + 1],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double s)) seconds = s;
        if (i + 2 < args.Length && !args[i + 2].StartsWith("--")) output = args[i + 2];
        return true;
    }

    public static AvatarPreview Create(double seconds, string output, float viewDegrees = 90,
        int focus = -1, float crank = float.NaN, float stride = float.NaN,
        (Audio.Cd.MusicStyle Style, int Move)? dance = null) =>
        new()
        {
            Name = "AvatarPreview", _seconds = seconds, _output = output,
            _viewDegrees = viewDegrees, _focus = focus, _crank = crank, _stride = stride, _dance = dance,
        };

    public override void _Ready()
    {
        var material = HumanMeshBuilder.Material();

        AddChild(new DirectionalLight3D
        {
            Rotation = new Vector3(Mathf.DegToRad(-42), Mathf.DegToRad(-35), 0),
            LightEnergy = 1.1f,
        });

        var env = new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.44f, 0.50f, 0.56f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.55f, 0.58f, 0.62f),
                AmbientLightEnergy = 0.85f,
            },
        };
        AddChild(env);

        // ground disc, so the figures do not float in a void
        var ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(24, 24) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.34f, 0.38f, 0.31f),
                SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            },
        };
        AddChild(ground);

        // --stride lays one gait cycle out as a strip. A walk cycle cannot be judged from a
        // single frame any more than a crank can: what matters is whether the planted foot
        // stays put between frames, which needs the frames side by side.
        // "--hats": every Headwear side by side on a standing figure, turned three-quarters to the
        // camera, so the occasions' hats (#18) can be judged together.
        if (OS.GetCmdlineUserArgs().Contains("--hats"))
        {
            var hats = Enum.GetValues<Headwear>();
            for (int i = 0; i < hats.Length; i++)
            {
                var figure = new MeshInstance3D
                {
                    Mesh = HumanMeshBuilder.Build(HumanPalette.ForRider(i), hat: hats[i]),
                    MaterialOverride = material,
                    Rotation = new Vector3(0, Mathf.Pi - 0.55f, 0),
                };
                Place((i - (hats.Length - 1) * 0.5f) * 1.0f, figure);
            }
            var hatCam = new Camera3D { Position = new Vector3(0, 1.3f, 9f), Fov = 30 };
            AddChild(hatCam);
            hatCam.LookAt(new Vector3(0, 1.1f, 0), Vector3.Up);
            hatCam.Current = true;
            return;
        }

        // "--outfits [page] [--walk]" (#251): figures in the clothes, turned toward the camera (or by
        // --view degrees). Page 0 (default) is whole outfits, gothic, kawaii and the finishes; a slot
        // name (top, bottom, legs, head, …) lines up every look for that slot. --walk strides them.
        if (OS.GetCmdlineUserArgs().Contains("--outfits"))
        {
            BuildOutfits();
            return;
        }

        // "--cockpit --heavy N [--section k] [--turn deg] [--throttle t] [--outside|--side|--saloon] [--bare]
        // [--mirrors] [--lights] [--front] [--pitch rad]" (#157): a truck or bus (HeavyCatalog index) with its driver,
        // from the driver's eye, a three-quarter front view, the left side, through the windscreen, or
        // down a bus's aisle from the back
        if (OS.GetCmdlineUserArgs().Contains("--cockpit") && OS.GetCmdlineUserArgs().Contains("--heavy"))
        {
            var args = OS.GetCmdlineUserArgs();
            string? After(string flag) => Array.IndexOf(args, flag) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            float Number(string flag, float fallback) => float.TryParse(After(flag), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
            var spec = Player.HeavyCatalog.All[Mathf.Clamp((int)Number("--heavy", 0), 0, Player.HeavyCatalog.All.Count - 1)];
            int section = Mathf.Clamp((int)Number("--section", 0), 0, spec.Sections.Length - 1);
            var rig = HeavyRig.Create(spec, section, 0.5f, section == 0 ? HumanPalette.ForRider(1) : null);
            var s = spec.Sections[section];
            float cg = HeavyMesh.Cg(s, 0.5f);
            rig.WheelTurn = Mathf.DegToRad(Number("--turn", 0f));
            rig.SteerAngle = rig.WheelTurn / HeavyCockpit.SteerRatio;
            rig.Throttle = Number("--throttle", 0.4f);
            rig.Rpm = Mathf.Lerp(spec.IdleRpm, spec.Redline, rig.Throttle);
            rig.SpeedKmh = 62f;
            rig.Gear = "A9";
            rig.Air = 8.4f;
            rig.Retarder = 2;
            rig.Headlights = args.Contains("--lights");
            string view = args.Contains("--outside") ? "outside" : args.Contains("--side") ? "side" : args.Contains("--saloon") ? "saloon"
                : args.Contains("--front") ? "front" : "eye";
            rig.View = view != "eye" ? CockpitView.Outside : args.Contains("--bare") ? CockpitView.Bare : CockpitView.Body;
            rig.MirrorsOn = args.Contains("--mirrors");
            AddChild(rig);
            // --fill (#158): somebody in every other seat, as passengers sit
            if (args.Contains("--fill"))
                for (int i = 1; i < rig.Seats.Length; i++)
                    if (rig.Seats[i].Section == section)
                        rig.AddChild(new MeshInstance3D
                        {
                            Mesh = SeatedFigure.Build(HumanPalette.ForRider(i + 2), rig.Seats[i]),
                            MaterialOverride = HumanMeshBuilder.Material(),
                            Transform = SeatedFigure.FrameOf(rig, rig.Seats[i]),
                        });
            _mirrorRig = rig.MirrorsOn ? rig : null;
            var cam = new Camera3D { Fov = view == "eye" ? 70 : 40, Near = 0.05f };
            AddChild(cam);
            // node space: the front is at −cg, the back at length − cg
            float front = -cg, back = s.Length - cg;
            switch (view)
            {
                case "outside":
                    cam.Position = new Vector3(-6f, 3.4f, front - 8f);
                    cam.LookAt(new Vector3(0, 1.8f, front + 2.5f), Vector3.Up);
                    break;
                case "side":
                    cam.Fov = 55;
                    cam.Position = new Vector3(-(s.Length * 0.75f + 2f), 2.2f, (front + back) * 0.5f);
                    cam.LookAt(new Vector3(0, 1.6f, (front + back) * 0.5f), Vector3.Up);
                    break;
                case "front":
                    // up close through the windscreen, as --heavynet's watcher shoots it
                    var at = rig.EyeFrame.Origin;
                    cam.Fov = 60;
                    cam.LookAtFromPosition(at + new Vector3(0.6f, 0.1f, -2.6f), at + new Vector3(0, -0.45f, 0), Vector3.Up);
                    break;
                case "saloon":
                    cam.Fov = 75;
                    cam.Position = new Vector3(0.2f, s.Height - 0.75f, back - 0.4f);
                    cam.LookAt(new Vector3(0, 1.2f, front + 1f), Vector3.Up);
                    break;
                default:
                    cam.Transform = rig.EyeFrame * new Transform3D(new Basis(Vector3.Right, Number("--pitch", -0.1f)), Vector3.Zero);
                    break;
            }
            cam.Current = true;
            GD.Print($"[cockpit] {spec.Label}: eye {rig.EyeFrame.Origin}, {rig.Seats.Length} seats, view {view}");
            return;
        }

        // "--cockpit [--car N] [--turn deg] [--throttle t] [--outside] [--bare]" (#69): one car with
        // its driver, seen from the driver's own eye (head hidden, or the whole figure with
        // --bare), or with --outside from a three-quarter front view through the glass. --turn
        // turns the steering wheel (+ = anticlockwise, a left turn).
        if (OS.GetCmdlineUserArgs().Contains("--cockpit"))
        {
            var args = OS.GetCmdlineUserArgs();
            string? After(string flag) => Array.IndexOf(args, flag) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            float Number(string flag, float fallback) => float.TryParse(After(flag), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
            var cars = Player.CarCatalog.All;
            var spec = cars[Mathf.Clamp((int)Number("--car", 0), 0, cars.Count - 1)];
            var rig = CarRig.Create(spec.Body, spec.Wheelbase, spec.Gauges, HumanPalette.ForRider(1));
            bool outside = args.Contains("--outside");
            rig.WheelTurn = Mathf.DegToRad(Number("--turn", 0f));
            rig.SteerAngle = rig.WheelTurn / spec.SteerRatio;
            rig.Throttle = Number("--throttle", 0.4f);
            rig.Rpm = Mathf.Lerp(spec.IdleRpm, spec.Redline, rig.Throttle);
            rig.SpeedKmh = 88f;
            rig.Gear = 3;
            rig.Headlights = args.Contains("--lights");
            rig.View = outside ? CockpitView.Outside : args.Contains("--bare") ? CockpitView.Bare : CockpitView.Body;
            rig.MirrorsOn = args.Contains("--mirrors");
            AddChild(rig);
            // --fill (#158): somebody in every other seat, as passengers sit
            if (args.Contains("--fill"))
                for (int i = 1; i < rig.Seats.Length; i++)
                    rig.AddChild(new MeshInstance3D
                    {
                        Mesh = SeatedFigure.Build(HumanPalette.ForRider(i + 2), rig.Seats[i]),
                        MaterialOverride = HumanMeshBuilder.Material(),
                        Transform = SeatedFigure.FrameOf(rig, rig.Seats[i]),
                    });
            _mirrorRig = rig.MirrorsOn ? rig : null;
            if (rig.MirrorsOn)
                // something to see behind the car: posts in a row, red on its left, blue on its right
                for (int i = 0; i < 7; i++)
                {
                    var post = new MeshInstance3D
                    {
                        Mesh = new BoxMesh { Size = new Vector3(0.6f, 2.2f, 0.6f) },
                        MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromHsv(i / 7f, 0.8f, 0.9f) },
                        Position = new Vector3((i - 3) * 2.2f, 1.1f, 9f),
                    };
                    AddChild(post);
                }
            var cam = new Camera3D { Fov = outside ? 34 : 70, Near = 0.05f };
            AddChild(cam);
            if (outside)
            {
                cam.Position = new Vector3(3.6f, 2.2f, -5.2f);
                cam.LookAt(new Vector3(0, 0.8f, 0), Vector3.Up);
            }
            else
                cam.Transform = rig.EyeFrame * new Transform3D(new Basis(Vector3.Right, -0.1f), Vector3.Zero);
            cam.Current = true;
            GD.Print($"[cockpit] {spec.Label}: eye {rig.EyeFrame.Origin}, wheel {Number("--turn", 0f)}°");
            return;
        }

        // "--cartops": the moving parts of the cars (#48), from a three-quarter front view up high
        // enough to see into an open cockpit. Top up and lights off, then the same car switched to
        // top down, lights on half a second in (so the shot shows where the animation ENDS; a
        // short [seconds] catches it mid-fold), then the NB with both set from the start and a
        // pop-up coupe with its lights on.
        if (OS.GetCmdlineUserArgs().Contains("--cartops"))
        {
            var cars = Player.CarCatalog.All;
            Player.CarSpec Find(string label) => cars.First(c => c.Label == label);
            var line = new (Player.CarSpec Spec, bool Open, bool Lights)[]
            {
                (Find("NA6CE Roadster"), false, false),
                (Find("NA6CE Roadster"), false, false),
                (Find("NB8C Roadster"), true, true),
                (Find("FD3S"), false, true),
            };
            for (int i = 0; i < line.Length; i++)
            {
                if (_focus >= 0 && i != _focus) continue;   // --focus N: that car alone, close up
                var rig = CarRig.Create(line[i].Spec.Body, line[i].Spec.Wheelbase);
                rig.RoofOpen = line[i].Open;
                rig.Headlights = line[i].Lights;
                // nose toward the camera, turned three-quarters (or by --view degrees)
                rig.Rotation = new Vector3(0, Mathf.Pi - (_viewDegrees == 90 ? 0.6f : Mathf.DegToRad(_viewDegrees)), 0);
                Place(_focus >= 0 ? 0f : (i - (line.Length - 1) * 0.5f) * 3.4f, rig);
                if (i == 1)
                    GetTree().CreateTimer(0.5).Timeout += () => { rig.RoofOpen = true; rig.Headlights = true; };
            }
            var carCam = new Camera3D { Position = _focus >= 0 ? new Vector3(0, 3.2f, 7.5f) : new Vector3(0, 4.5f, 16f), Fov = _focus >= 0 ? 30 : 42 };
            AddChild(carCam);
            carCam.LookAt(new Vector3(0, 0.5f, 0), Vector3.Up);
            carCam.Current = true;
            _turntables.Clear();   // posed here, not turned side-on
            return;
        }

        // "--dance <style>,<move>": one standing and one walking (1.4 m/s) figure dancing that move
        // at 120 BPM, rebuilt every frame in _Process. Judge it with --view 180 (front) and 90 (side).
        if (_dance is { } dance)
        {
            _danceStill = new MeshInstance3D { MaterialOverride = material };
            _danceWalk = new MeshInstance3D { MaterialOverride = material };
            Place(-0.6f, _danceStill);
            Place(0.6f, _danceWalk);
            var danceCam = new Camera3D { Position = new Vector3(0, 1.15f, 6.5f), Fov = 28 };
            AddChild(danceCam);
            danceCam.LookAt(new Vector3(0, 0.9f, 0), Vector3.Up);
            danceCam.Current = true;
            UpdateDance(dance.Style, dance.Move, 0f);
            return;
        }

        // "--carsetups [--car N] [--setups 0,3,2,4]": one car in several presets (#40), side by side
        if (OS.GetCmdlineUserArgs().Contains("--carsetups"))
        {
            var args = OS.GetCmdlineUserArgs();
            string? After(string flag) => Array.IndexOf(args, flag) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            var car = Player.CarCatalog.All[int.TryParse(After("--car"), out int n) ? Mathf.Clamp(n, 0, Player.CarCatalog.All.Count - 1) : 0];
            var setups = (After("--setups") ?? "0,3,2,4").Split(',').Select(w => Player.CarSetups.Parse(w) ?? Player.CarSetups.All[0]).ToArray();
            for (int i = 0; i < setups.Length; i++)
            {
                var spec = setups[i].Apply(car);
                var rig = CarRig.Create(spec.Body, spec.Wheelbase);
                rig.Rotation = new Vector3(0, Mathf.Pi - (_viewDegrees == 90 ? 0.6f : Mathf.DegToRad(_viewDegrees)), 0);
                Place((i - (setups.Length - 1) * 0.5f) * 3.6f, rig);
                GD.Print($"[carsetups] {i + 1}. {car.Label} {setups[i].Name}: lift {spec.Body.Lift:F2} m, wheel {spec.Body.WheelRadius:F2} m, "
                    + $"box {MeshBounds.Of(rig).Position} .. {MeshBounds.Of(rig).End}");
            }
            var carCam = new Camera3D { Position = new Vector3(0, 3.6f, 8f + 2.2f * setups.Length), Fov = 34 };
            AddChild(carCam);
            carCam.LookAt(new Vector3(0, 0.7f, 0), Vector3.Up);
            carCam.Current = true;
            _turntables.Clear();
            return;
        }

        if (!float.IsNaN(_stride))
        {
            const int steps = 6;
            var strip = new List<Node3D>();
            for (int i = 0; i < steps; i++)
                strip.Add(new MeshInstance3D
                {
                    Mesh = HumanMeshBuilder.BuildStride(
                        HumanPalette.ForRider(i), _stride, i / (float)steps),
                    MaterialOverride = material,
                });

            for (int i = 0; i < steps; i++) Place((i - (steps - 1) * 0.5f) * 1.15f, strip[i]);
            var strideCam = new Camera3D { Position = new Vector3(0, 0.95f, 12.5f), Fov = 34 };
            AddChild(strideCam);
            strideCam.LookAt(new Vector3(0, 0.85f, 0), Vector3.Up);
            strideCam.Current = true;
            return;
        }

        var subjects = new List<Node3D>
        {
            new MeshInstance3D
            {
                Mesh = HumanMeshBuilder.Build(HumanPalette.ForRider(0)),
                MaterialOverride = material,
            },
            new MeshInstance3D
            {
                Mesh = HumanMeshBuilder.Build(HumanPalette.ForRider(2), HumanPose.Running),
                MaterialOverride = material,
            },
            new MeshInstance3D
            {
                Mesh = BikeMeshBuilder.Build(),
                MaterialOverride = material,
            },
        };

        var cyclist = Cyclist.Create(4);
        cyclist.CadenceRpm = 78;
        subjects.Add(cyclist);
        _cyclist = cyclist;

        subjects.Add(new MeshInstance3D
        {
            Mesh = SkierMeshBuilder.BuildSkier(HumanPalette.ForRider(6), SkiPalette.ForRider(6)),
            MaterialOverride = material,
        });

        // 5, 6: the motorbikes with their riders (--focus r1 / monster)
        // (5 + N: the catalog's Nth bike)
        for (int i = 0; i < Player.MotorbikeCatalog.All.Count; i++)
            subjects.Add(Motorcyclist.Create(Player.MotorbikeCatalog.All[i].Look, 1 + 2 * i));

        Camera3D camera;
        if (_focus >= 0 && _focus < subjects.Count)
        {
            Place(0f, subjects[_focus]);
            // Long lens from far back, i.e. near-orthographic. A close wide-angle view of a
            // bicycle exaggerates whichever end is nearer and makes correct proportions look
            // wrong — which cost an iteration before this was fixed.
            camera = new Camera3D { Position = new Vector3(0, 0.85f, 9.0f), Fov = 13 };
            AddChild(camera);
            camera.LookAt(new Vector3(0, 0.72f, 0), Vector3.Up);
        }
        else
        {
            // evenly spaced whatever the count, so adding a subject never needs a new table
            const float pitch = 1.85f;
            float first = -(subjects.Count - 1) * pitch * 0.5f;
            for (int i = 0; i < subjects.Count; i++) Place(first + i * pitch, subjects[i]);
            camera = new Camera3D { Position = new Vector3(0, 1.15f, 5.6f), Fov = 52 };
            AddChild(camera);
            camera.LookAt(new Vector3(0, 0.85f, 0), Vector3.Up);
        }

        camera.Current = true;
    }

    /// <summary>The dance figures at <paramref name="dt"/> further along a 120 BPM clock.</summary>
    private void UpdateDance(Audio.Cd.MusicStyle style, int move, float dt)
    {
        const float speed = 1.4f;
        _walkPhase = HumanMeshBuilder.AdvancePhase(_walkPhase, speed, dt);
        float t = (float)_elapsed;
        float bars = t / 2f;                                   // 4 beats of 0.5 s
        var dance = new DanceParams(style, move, (t * 2f) % 1f, bars % 1f, (int)bars, 1f);
        _danceStill!.Mesh = HumanMeshBuilder.BuildStride(HumanPalette.ForRider(1), 0f, 0f, dance: dance);
        _danceWalk!.Mesh = HumanMeshBuilder.BuildStride(HumanPalette.ForRider(3), speed, _walkPhase, dance: dance);
    }

    private readonly List<(MeshInstance3D Mesh, HumanPalette Palette, Headwear Hat)> _walkers = new();
    private bool _outfitWalk;

    /// <summary>The whole outfits of <c>--outfits</c>' first page: one figure each.</summary>
    private static readonly (Items.ItemId[] Items, Headwear Hat)[] Showcase =
    {
        (new[] { Items.ItemId.CatEarsBlack, Items.ItemId.GothShades, Items.ItemId.MaskFang, Items.ItemId.SpikePiercings, Items.ItemId.SpikedChoker,
            Items.ItemId.BuckleCorset, Items.ItemId.SlitMaxiSkirt, Items.ItemId.GothStockings, Items.ItemId.PlatformBoots, Items.ItemId.LaceArmWarmers }, Headwear.None),
        (new[] { Items.ItemId.CatHeadset, Items.ItemId.HeartShades, Items.ItemId.MaskUwu, Items.ItemId.StarStuds, Items.ItemId.BellCollar,
            Items.ItemId.PinkCropTop, Items.ItemId.PinkPleated, Items.ItemId.PinkStockings, Items.ItemId.MaryJanes, Items.ItemId.PawGloves }, Headwear.None),
        (new[] { Items.ItemId.LaceHeadband, Items.ItemId.PostalJacket, Items.ItemId.PostalSkirt, Items.ItemId.CombatBoots }, Headwear.None),
        (new[] { Items.ItemId.DevilHorns, Items.ItemId.BlackShades, Items.ItemId.MaskSkull, Items.ItemId.IndustrialSet, Items.ItemId.ChainNecklace,
            Items.ItemId.BandTee, Items.ItemId.TartanSkirt, Items.ItemId.Fishnets, Items.ItemId.CombatBoots, Items.ItemId.FingerlessGloves }, Headwear.None),
        (new[] { Items.ItemId.GamerHeadset, Items.ItemId.RoundGlasses, Items.ItemId.GreenPolo, Items.ItemId.JoggingShorts,
            Items.ItemId.KneeSocks, Items.ItemId.WhiteSneakers }, Headwear.None),
        (new[] { Items.ItemId.PinkBow, Items.ItemId.HeartChoker, Items.ItemId.LolitaDress, Items.ItemId.KneeSocks, Items.ItemId.MaryJanes }, Headwear.None),
        (new[] { Items.ItemId.WitchRobe, Items.ItemId.SilverHoops, Items.ItemId.PlatformBoots }, Headwear.WitchHat),
        (new[] { Items.ItemId.RainbowCatEars, Items.ItemId.DiscoShades, Items.ItemId.HoloMask, Items.ItemId.DiscoTop,
            Items.ItemId.HoloSkirt, Items.ItemId.RainbowStockings, Items.ItemId.DiscoPlatforms, Items.ItemId.NeonGloves }, Headwear.None),
        (new[] { Items.ItemId.NeonHeadset, Items.ItemId.GalaxyHoodie, Items.ItemId.CargoPants, Items.ItemId.PinkSneakers }, Headwear.None),
        (new[] { Items.ItemId.BunnyEars, Items.ItemId.GalaxyDress, Items.ItemId.BlackStockings, Items.ItemId.MaryJanes }, Headwear.None),
        (new[] { Items.ItemId.BlackBeanie, Items.ItemId.LavaTee, Items.ItemId.Jeans, Items.ItemId.CombatBoots }, Headwear.None),
        (new[] { Items.ItemId.StarGlasses, Items.ItemId.GlitchTee, Items.ItemId.BlackShorts, Items.ItemId.BeeStockings,
            Items.ItemId.PinkSneakers, Items.ItemId.StripedArmWarmers }, Headwear.None),
        (new[] { Items.ItemId.MaskCat, Items.ItemId.StripedLongsleeve, Items.ItemId.RuffledMini, Items.ItemId.GothStockings, Items.ItemId.PlatformBoots }, Headwear.None),
        (new[] { Items.ItemId.WhiteMarcel, Items.ItemId.Jeans, Items.ItemId.WhiteSneakers, Items.ItemId.SilverStuds }, Headwear.None),
        (new[] { Items.ItemId.GothicRobe, Items.ItemId.MaskBlack, Items.ItemId.ChainNecklace }, Headwear.None),
    };

    private void BuildOutfits()
    {
        var args = OS.GetCmdlineUserArgs();
        int at = Array.IndexOf(args, "--outfits");
        string page = at + 1 < args.Length && !args[at + 1].StartsWith("--") ? args[at + 1] : "0";
        _outfitWalk = args.Contains("--walk");

        var looks = new List<(Outfit Outfit, Headwear Hat)>();
        if (Enum.TryParse<WearSlot>(page, ignoreCase: true, out var slot) && slot != WearSlot.None)
        {
            // every look for one slot, on a figure that keeps the rest plain
            foreach (var g in Garments.All.Where(g => g.Slot == slot))
                looks.Add((Outfit.Empty.With(g.Slot, g.Code), Headwear.None));
        }
        else
            foreach (var (items, hat) in Showcase) looks.Add((Outfit.Of(items), hat));
        // --focus N: that one close up; --focus N --count k: k of them from N
        if (_focus >= 0 && _focus < looks.Count)
        {
            int count = Array.IndexOf(args, "--count") is var c and >= 0 && c + 1 < args.Length && int.TryParse(args[c + 1], out int n) ? n : 1;
            looks = looks.Skip(_focus).Take(Mathf.Max(1, count)).ToList();
        }

        var material = HumanMeshBuilder.FigureMaterial();
        int columns = Mathf.Min(looks.Count, 8);
        int rows = (looks.Count + columns - 1) / columns;
        float yaw = _viewDegrees == 90 ? Mathf.Pi - 0.45f : Mathf.DegToRad(_viewDegrees);
        for (int i = 0; i < looks.Count; i++)
        {
            int col = i % columns, row = i / columns;
            var palette = HumanPalette.ForRider(i) with { Outfit = looks[i].Outfit };
            var figure = new MeshInstance3D
            {
                Mesh = HumanMeshBuilder.Build(palette, hat: looks[i].Hat),
                MaterialOverride = material,
                Position = new Vector3((col - (columns - 1) * 0.5f) * 0.95f, 0, -row * 2.2f),
                Rotation = new Vector3(0, yaw, 0),
            };
            AddChild(figure);
            _walkers.Add((figure, palette, looks[i].Hat));
        }
        float width = columns * 0.95f;
        var cam = new Camera3D { Fov = 30 };
        AddChild(cam);
        // a lone figure fills the frame; a crowd is seen from a little above
        cam.Position = looks.Count == 1 ? new Vector3(0, 1.1f, 4.2f) : new Vector3(0, 1.1f + rows * 0.5f, width * 1.25f + 1.2f);
        cam.LookAt(new Vector3(0, 0.95f, -(rows - 1) * 1.1f), Vector3.Up);
        cam.Current = true;
        GD.Print($"[outfits] page {page}: {looks.Count} figures");
    }

    private void Place(float x, Node3D node)
    {
        var pivot = new Node3D { Position = new Vector3(x, 0, 0) };
        pivot.AddChild(node);
        AddChild(pivot);
        _turntables.Add(pivot);
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_dance is { } dance) UpdateDance(dance.Style, dance.Move, (float)delta);
        if (_outfitWalk)
        {
            _walkPhase = HumanMeshBuilder.AdvancePhase(_walkPhase, 1.4f, (float)delta);
            foreach (var (mesh, palette, hat) in _walkers)
                mesh.Mesh = HumanMeshBuilder.BuildStride(palette, 1.4f, _walkPhase, hat: hat);
        }

        // A fixed angle, not a turn. Bicycle and rider geometry is judged side-on — saddle
        // height against hip, hands against the drops, knee over the pedal spindle — and a
        // three-quarter view hides exactly those relationships.
        foreach (var pivot in _turntables)
            pivot.Rotation = new Vector3(0, Mathf.DegToRad(_viewDegrees), 0);

        // --crank parks the cranks so two runs can be compared; without it they spin freely
        if (!float.IsNaN(_crank) && _cyclist != null)
        {
            _cyclist.CadenceRpm = 0;
            _cyclist.SetCrankAngle(_crank);
        }

        if (_elapsed < _seconds) return;

        var image = GetViewport().GetTexture().GetImage();
        var error = image.SavePng(_output);
        // --cockpit --mirrors: what each mirror sees, as its own picture next to the shot
        foreach (var port in _mirrorRig?.GetChildren().OfType<SubViewport>() ?? Enumerable.Empty<SubViewport>())
        {
            var path = _output[..^4] + "_" + port.Name + ".png";
            GD.Print($"[avatars] wrote {path}: {port.GetTexture().GetImage().SavePng(path)}");
        }
        GD.Print(error == Error.Ok
            ? $"[avatars] wrote {_output} ({image.GetWidth()}x{image.GetHeight()})"
            : $"[avatars] FAILED to write {_output}: {error}");
        GetTree().Quit();
    }
}
