using Godot;
using UnitSport.Core;
using FaceGenome = UnitSport.Avatar.Face.FaceGenome;

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

        if (!CmdArgs.Has("--avatars")) return false;
        if (CmdArgs.Double("--avatars") is double s) seconds = s;
        if (CmdArgs.Value("--avatars", 2, notFlag: true) is { } o) output = o;
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
        var material = HumanMeshBuilder.FigureMaterial();

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
        if (CmdArgs.Has("--hats"))
        {
            // every hat, then the cycling helmet
            var hats = Enum.GetValues<Headwear>();
            for (int i = 0; i <= hats.Length; i++)
            {
                bool helmet = i == hats.Length;
                var figure = new MeshInstance3D
                {
                    Mesh = HumanMeshBuilder.Build(HumanPalette.ForRider(i), helmet: helmet, hat: helmet ? Headwear.None : hats[i]),
                    MaterialOverride = material,
                    Rotation = new Vector3(0, Mathf.Pi - 0.55f, 0),
                };
                Place((i - hats.Length * 0.5f) * 1.0f, figure);
            }
            // --close: the heads only, to see how each hat sits
            bool close = CmdArgs.Has("--close");
            var hatCam = new Camera3D { Position = new Vector3(0, close ? 1.62f : 1.3f, 9f), Fov = close ? 19 : 30 };
            AddChild(hatCam);
            hatCam.LookAt(new Vector3(0, close ? 1.6f : 1.1f, 0), Vector3.Up);
            hatCam.Current = true;
            return;
        }

        // "--bodies [builds|looks|faces|walk]" (#394): the refined figure next to the current one
        if (CmdArgs.Has("--bodies"))
        {
            BuildBodies();
            return;
        }

        // "--outfits [page] [--walk]" (#251): figures in the clothes, turned toward the camera (or by
        // --view degrees). Page 0 (default) is whole outfits, gothic, kawaii and the finishes; a slot
        // name (top, bottom, legs, head, …) lines up every look for that slot. --walk strides them.
        if (CmdArgs.Has("--outfits"))
        {
            BuildOutfits();
            return;
        }

        // "--cockpit --heavy N [--section k] [--turn deg] [--throttle t] [--outside|--side|--saloon] [--bare]
        // [--mirrors] [--lights] [--front] [--pitch rad]" (#157): a truck or bus (HeavyCatalog index) with its driver,
        // from the driver's eye, a three-quarter front view, the left side, through the windscreen, or
        // down a bus's aisle from the back
        if (CmdArgs.Has("--cockpit") && CmdArgs.Has("--heavy"))
        {
            float Number(string flag, float fallback) => CmdArgs.Float(flag) ?? fallback;
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
            rig.Headlights = CmdArgs.Has("--lights");
            string view = CmdArgs.Has("--outside") ? "outside" : CmdArgs.Has("--side") ? "side" : CmdArgs.Has("--saloon") ? "saloon"
                : CmdArgs.Has("--front") ? "front" : CmdArgs.Has("--door") ? "door" : "eye";
            rig.View = view != "eye" ? CockpitView.Outside : CmdArgs.Has("--bare") ? CockpitView.Bare : CockpitView.Body;
            rig.MirrorsOn = CmdArgs.Has("--mirrors");
            AddChild(rig);
            // --deck (#162): what the walk collides with, see-through: solid grey, shut doors red, door steps green
            if (CmdArgs.Has("--deck") && rig.Deck is { } deck)
                foreach (var box in deck.Boxes)
                    rig.AddChild(new MeshInstance3D
                    {
                        Mesh = new BoxMesh { Size = box.Size },
                        Transform = new Transform3D(box.Basis, box.Centre),
                        MaterialOverride = new StandardMaterial3D
                        {
                            AlbedoColor = box.Part switch
                            {
                                DeckPart.DoorShut => new Color(1f, 0.2f, 0.2f, 0.5f),
                                DeckPart.DoorStep => new Color(0.2f, 1f, 0.3f, 0.7f),
                                _ => new Color(0.6f, 0.6f, 0.9f, 0.35f),
                            },
                            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                        },
                    });
            // --fill (#158): somebody in every other seat, as passengers sit
            if (CmdArgs.Has("--fill"))
                for (int i = 1; i < rig.Seats.Length; i++)
                    if (rig.Seats[i].Section == section)
                        rig.AddChild(new MeshInstance3D
                        {
                            Mesh = SeatedFigure.Build(HumanPalette.ForRider(i + 2), rig.Seats[i]),
                            MaterialOverride = HumanMeshBuilder.FigureMaterial(),
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
                case "door":
                    // the right side, at the middle of the section, low: the doors and their steps
                    cam.Fov = 60;
                    cam.LookAtFromPosition(new Vector3(4.5f, 1.2f, (front + back) * 0.5f + 1.5f), new Vector3(1.2f, 0.4f, (front + back) * 0.5f), Vector3.Up);
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
        if (CmdArgs.Has("--cockpit"))
        {
            float Number(string flag, float fallback) => CmdArgs.Float(flag) ?? fallback;
            var cars = Player.CarCatalog.All;
            var spec = cars[Mathf.Clamp((int)Number("--car", 0), 0, cars.Count - 1)];
            var rig = CarRig.Create(spec.Body, spec.Wheelbase, spec.Gauges, HumanPalette.ForRider(1));
            bool outside = CmdArgs.Has("--outside");
            rig.WheelTurn = Mathf.DegToRad(Number("--turn", 0f));
            rig.SteerAngle = rig.WheelTurn / spec.SteerRatio;
            rig.Throttle = Number("--throttle", 0.4f);
            rig.Rpm = Mathf.Lerp(spec.IdleRpm, spec.Redline, rig.Throttle);
            rig.SpeedKmh = 88f;
            rig.Gear = 3;
            rig.Headlights = CmdArgs.Has("--lights");
            rig.View = outside ? CockpitView.Outside : CmdArgs.Has("--bare") ? CockpitView.Bare : CockpitView.Body;
            rig.MirrorsOn = CmdArgs.Has("--mirrors");
            AddChild(rig);
            // --fill (#158): somebody in every other seat, as passengers sit
            if (CmdArgs.Has("--fill"))
                for (int i = 1; i < rig.Seats.Length; i++)
                    rig.AddChild(new MeshInstance3D
                    {
                        Mesh = SeatedFigure.Build(HumanPalette.ForRider(i + 2), rig.Seats[i]),
                        MaterialOverride = HumanMeshBuilder.FigureMaterial(),
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
        if (CmdArgs.Has("--cartops"))
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
        if (CmdArgs.Has("--carsetups"))
        {
            var car = Player.CarCatalog.All[CmdArgs.Int("--car") is int n ? Mathf.Clamp(n, 0, Player.CarCatalog.All.Count - 1) : 0];
            var setups = (CmdArgs.Value("--setups") ?? "0,3,2,4").Split(',').Select(w => Player.CarSetups.Parse(w) ?? Player.CarSetups.All[0]).ToArray();
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
        string page = CmdArgs.Value("--outfits", notFlag: true) ?? "0";
        _outfitWalk = CmdArgs.Has("--walk");

        if (page == "riders")
        {
            BuildRiders();
            return;
        }

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
            int count = CmdArgs.Int("--count") ?? 1;
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

    /// <summary>
    /// "--bodies [page]" (#394): the figures. <c>builds</c> (default): every build in the same
    /// jersey; <c>crowd</c>: ten seeded figures, as NPCs and players who never chose get them; <c>looks</c>: six styled looks; <c>faces</c>: every
    /// face close up, four to a row; <c>expressions</c> / <c>expressions2</c>: every <see cref="Face.FaceExpression"/> (#657), on a big-eyed and a small-eyed face;
    /// <c>seeded</c>: sixteen faces made up from seeds (<see cref="FaceGenome.ForSeed"/>); <c>heads</c>: the looks' heads and hair; <c>walk</c>: the builds mid-stride. Long lens, turned
    /// three-quarters (or by --view degrees).
    /// </summary>
    private void BuildBodies()
    {
        string page = CmdArgs.Value("--bodies", notFlag: true) ?? "builds";
        var skin = new Color(0.90f, 0.74f, 0.62f);
        var jersey = new BodyLook(BodyBuild.Slim, skin) { Top = new Color(0.85f, 0.24f, 0.20f), Bottom = new Color(0.16f, 0.17f, 0.20f) };
        var figures = new List<(string Name, Func<ArrayMesh> Mesh, Face.FaceState? State)>();
        void Add(string name, BodyLook look, Face.FaceState? state = null) => figures.Add((name, () => HumanMeshBuilder.BuildBody(look), state));

        var black = new Color(0.08f, 0.08f, 0.10f);
        var looks = new (string Name, BodyLook Look)[]
        {
            ("coat", new BodyLook(BodyBuild.Slim, new Color(0.96f, 0.86f, 0.74f))
            {
                Top = new Color(0.09f, 0.08f, 0.11f), SleeveTo = 2f, TopFrom = 1.2f, Bottom = new Color(0.10f, 0.09f, 0.12f),
                LegTo = 2f, Shoes = new Color(0.08f, 0.08f, 0.09f), BootFrom = 1.55f, Platform = 1.6f,
                Gloves = new Color(0.12f, 0.11f, 0.13f), GloveFrom = 1.85f, Hair = new Color(0.20f, 0.72f, 0.74f),
                HairStyle = HairStyle.SideSwept, Face = 2, Eyes = new Color(0.20f, 0.75f, 0.70f), BottomPattern = Finish.Studs,
            }),
            ("jacket", new BodyLook(BodyBuild.Lean, new Color(0.95f, 0.80f, 0.42f))
            {
                Top = new Color(0.42f, 0.55f, 0.36f), SleeveTo = 2f, Bottom = new Color(0.22f, 0.32f, 0.55f), LegTo = 2f,
                Shoes = new Color(0.20f, 0.18f, 0.18f), Belt = new Color(0.60f, 0.70f, 0.50f),
                Hair = new Color(0.86f, 0.52f, 0.20f), HairStyle = HairStyle.Spiky, Face = 1, Eyes = new Color(0.30f, 0.45f, 0.20f),
            }),
            ("punk", new BodyLook(BodyBuild.Curvy, new Color(0.93f, 0.90f, 0.92f))
            {
                Top = black, TopFrom = 2.6f, SleeveTo = 0f, Bottom = new Color(0.07f, 0.07f, 0.09f),
                LegTo = 0.28f, Legwear = new Color(0.20f, 0.30f, 0.55f), LegPattern = Finish.Checker, Shoes = new Color(0.06f, 0.06f, 0.07f),
                BootFrom = 1.15f, Platform = 2.6f, Gloves = black, GloveFrom = 1.55f,
                Belt = new Color(0.75f, 0.75f, 0.78f), Hair = new Color(0.12f, 0.45f, 0.75f), HairStyle = HairStyle.Mohawk, Face = 2,
                Eyes = new Color(0.85f, 0.20f, 0.40f),
            }),
            ("hoodie", new BodyLook(BodyBuild.Curvy, new Color(0.95f, 0.70f, 0.60f))
            {
                Top = new Color(0.20f, 0.13f, 0.26f), SleeveTo = 2f, TopFrom = 1.6f, Bottom = new Color(0.16f, 0.11f, 0.22f), LegTo = 2f,
                Shoes = new Color(0.12f, 0.10f, 0.14f), Hair = new Color(0.10f, 0.08f, 0.12f), HairStyle = HairStyle.BluntBangs, Face = 0,
                Eyes = new Color(0.55f, 0.30f, 0.86f),
            }),
            ("crop", new BodyLook(BodyBuild.Slim, new Color(0.92f, 0.76f, 0.52f))
            {
                Top = new Color(0.16f, 0.22f, 0.18f), TopFrom = 2.55f, SleeveTo = 0.35f, Bottom = new Color(0.24f, 0.16f, 0.30f), LegTo = 2f,
                Waistband = 1.75f, Shoes = new Color(0.14f, 0.12f, 0.16f), Gloves = new Color(0.22f, 0.15f, 0.26f), GloveFrom = 1.85f,
                Hair = new Color(0.86f, 0.10f, 0.08f), HairStyle = HairStyle.Ponytail, Face = 1, Eyes = new Color(0.75f, 0.55f, 0.15f),
            }),
            ("sporty", new BodyLook(BodyBuild.Curvy, new Color(0.80f, 0.56f, 0.38f))
            {
                Top = new Color(0.12f, 0.24f, 0.20f), SleeveTo = 0f, TopFrom = 2.1f, Bottom = new Color(0.12f, 0.24f, 0.20f), LegTo = 0.25f,
                Shoes = new Color(0.95f, 0.95f, 0.95f), Hair = new Color(0.45f, 0.14f, 0.10f), HairStyle = HairStyle.Twintails, Face = 3,
                Eyes = new Color(0.25f, 0.55f, 0.95f),
            }),
        };
        // #394 feedback: the masculine side of the wardrobe
        var guys = new (string Name, BodyLook Look)[]
        {
            ("skater", new BodyLook(BodyBuild.Lean, new Color(0.88f, 0.70f, 0.56f))
            {
                Top = new Color(0.85f, 0.85f, 0.82f), TopPattern = Finish.Stripes, SleeveTo = 0.5f, TopFrom = 1.6f,
                Bottom = new Color(0.45f, 0.42f, 0.30f), LegTo = 1.6f, Shoes = new Color(0.15f, 0.15f, 0.18f), BootFrom = 1.8f,
                Hair = new Color(0.30f, 0.20f, 0.12f), HairStyle = HairStyle.Shaggy, Face = 6, Eyes = new Color(0.35f, 0.25f, 0.15f),
            }),
            ("biker", new BodyLook(BodyBuild.Stocky, new Color(0.78f, 0.58f, 0.44f))
            {
                Top = black, TopPattern = Finish.Studs, SleeveTo = 0f, TopFrom = 1.5f, Bottom = new Color(0.18f, 0.22f, 0.32f), LegTo = 2f,
                Shoes = black, BootFrom = 1.5f, Platform = 1.5f, Gloves = black, GloveFrom = 1.8f, Belt = new Color(0.55f, 0.55f, 0.58f),
                Hair = new Color(0.85f, 0.15f, 0.20f), HairStyle = HairStyle.Mohawk, Face = 7, Eyes = new Color(0.30f, 0.35f, 0.40f),
            }),
            ("smart", new BodyLook(BodyBuild.Broad, new Color(0.62f, 0.42f, 0.30f))
            {
                Top = new Color(0.85f, 0.82f, 0.74f), SleeveTo = 2f, TopFrom = 1.7f, Bottom = new Color(0.20f, 0.22f, 0.26f), LegTo = 2f,
                Shoes = new Color(0.30f, 0.18f, 0.10f), Belt = new Color(0.30f, 0.18f, 0.10f),
                Hair = new Color(0.08f, 0.06f, 0.05f), HairStyle = HairStyle.Quiff, Face = 5, Eyes = new Color(0.25f, 0.15f, 0.08f),
            }),
            ("runner", new BodyLook(BodyBuild.Lean, new Color(0.95f, 0.80f, 0.66f))
            {
                Top = new Color(0.20f, 0.45f, 0.85f), SleeveTo = 0f, TopFrom = 1.75f, Bottom = black, LegTo = 0.3f,
                Shoes = new Color(0.95f, 0.60f, 0.15f), Hair = new Color(0.75f, 0.60f, 0.35f), HairStyle = HairStyle.Short, Face = 6,
                Eyes = new Color(0.30f, 0.55f, 0.85f),
            }),
            ("goth", new BodyLook(BodyBuild.Lean, new Color(0.94f, 0.88f, 0.86f))
            {
                Top = black, SleeveTo = 2f, TopFrom = 1.1f, Bottom = black, LegTo = 2f, Shoes = black, BootFrom = 1.3f, Platform = 2f,
                Gloves = black, GloveFrom = 1.8f, Hair = new Color(0.06f, 0.05f, 0.08f), HairStyle = HairStyle.Long, Face = 2,
                Eyes = new Color(0.60f, 0.10f, 0.15f),
            }),
            ("grunge", new BodyLook(BodyBuild.Broad, new Color(0.90f, 0.72f, 0.58f))
            {
                Top = new Color(0.65f, 0.15f, 0.12f), TopPattern = Finish.Tartan, SleeveTo = 2f, TopFrom = 1.5f,
                Bottom = new Color(0.30f, 0.38f, 0.55f), LegTo = 2f, Shoes = new Color(0.25f, 0.20f, 0.15f), BootFrom = 1.6f,
                Hair = new Color(0.55f, 0.40f, 0.20f), HairStyle = HairStyle.Bun, Face = 7, Eyes = new Color(0.40f, 0.50f, 0.30f),
            }),
        };

        float spacing = 0.95f, lookAtY = 0.92f, height = 2.1f;
        int columns = 0;   // >0: heads in a grid, this many to a row, each row lower and nearer
        const float rowDrop = 0.36f, rowNear = 0.3f;
        void Heads() { spacing = 0.42f; lookAtY = 1.68f; height = 0.55f; }
        void Grid(int across) { Heads(); columns = across; spacing = 0.34f; }
        switch (page)
        {
            case "looks":
                foreach (var (name, look) in looks) Add(name, look);
                break;
            case "guys":
                foreach (var (name, look) in guys) Add(name, look);
                break;
            case "faces":
                // every face, bald so nothing hides it, heads only in frame
                for (int f = 0; f < FaceGenome.PresetCount; f++)
                    Add(FaceGenome.PresetName(f), (f % 2 == 0 ? looks[f / 2 % looks.Length] : guys[f / 2 % guys.Length]).Look with { Face = f, HairStyle = HairStyle.None });
                Grid(4);
                break;
            case "expressions":
            case "expressions2":
            {
                // every expression (#657), on a big-eyed face, or (2) a small-eyed one
                bool big = page == "expressions";
                var look = (big ? looks[3] : guys[1]).Look with { Face = big ? 0 : 6, HairStyle = HairStyle.None };
                foreach (var e in Enum.GetValues<Face.FaceExpression>())
                    Add(e.ToString(), look, Face.FaceExpressions.Of(e));
                Grid(5);
                break;
            }
            case "seeded":
                for (int f = 0; f < 16; f++)
                {
                    int f2 = f;
                    var look = (f % 2 == 0 ? looks[f / 2 % looks.Length] : guys[f / 2 % guys.Length]).Look with { HairStyle = HairStyle.None };
                    Add($"seed {f}", look with { Genome = FaceGenome.ForSeed((uint)f2 * 7919u + 13u) });
                }
                Grid(4);
                break;
            case "eyes":
                // one face, the eye colour its own choice
                foreach (var c in new[] { "6b3e1f", "3a78d8", "3f8f4a", "8e4fd8", "d83a5c", "d8a83a", "8a9aa8", "1a1a1a" })
                    Add(c, looks[3].Look with { Face = 0, Eyes = new Color(c), HairStyle = HairStyle.Short, Hair = new Color(0.12f, 0.1f, 0.12f) });
                Heads();
                break;
            case "hair":
            case "hair2":
            {
                // every style, half on each page, in one face and colour so the shapes compare
                var all = Enum.GetValues<HairStyle>().Where(h => h != HairStyle.None).ToArray();
                int firstHalf = (all.Length + 1) / 2;
                foreach (var h in page == "hair" ? all.Take(firstHalf) : all.Skip(firstHalf))
                    Add(h.ToString(), looks[4].Look with { HairStyle = h, Face = 1 });
                Heads();
                break;
            }
            case "heads":
                foreach (var (name, look) in looks.Concat(guys)) Add(name, look);
                Heads();
                break;
            case "walk":
                foreach (var b in Enum.GetValues<BodyBuild>())
                {
                    var look = (b < BodyBuild.Broad ? looks[(int)b * 2] : guys[(int)b - 2]).Look;
                    figures.Add(($"{b}", () => HumanMeshBuilder.BuildBodyStride(look, 1.6f, 0.15f), null));
                }
                break;
            case "crowd":
                // what everyone who never chose gets: the figure from their seed (Appearance.ForSeed)
                for (int i = 0; i < 10; i++)
                {
                    int seed = i;
                    figures.Add(($"seed {seed}", () => HumanMeshBuilder.Build(HumanPalette.ForRider(seed)), null));
                }
                break;
            default:
                foreach (var b in Enum.GetValues<BodyBuild>())
                {
                    bool masc = b >= BodyBuild.Broad;
                    Add(b.ToString(), jersey with
                    {
                        Build = b, Face = masc ? 5 + (int)b - 2 : (int)b,
                        HairStyle = masc ? (HairStyle)((int)HairStyle.Short + (int)b - 2) : (HairStyle)((int)b + 1),
                        Hair = new Color(0.25f, 0.15f, 0.09f),
                    });
                }
                break;
        }

        // a fill light from the camera's side: the PS1 style's stepped light leaves a figure's
        // front in the dark under the turntable's one sun, and faces are what this page is for
        AddChild(new DirectionalLight3D { Rotation = new Vector3(Mathf.DegToRad(-15), Mathf.DegToRad(20), 0), LightEnergy = 0.7f });

        var material = HumanMeshBuilder.FigureMaterial();
        // stills: the faces neither blink nor glance (#657)
        material.SetShaderParameter(Face.FaceAnimator.IdleParam, 0f);
        float yaw = _viewDegrees == 90 ? Mathf.Pi - 0.45f : Mathf.DegToRad(_viewDegrees);
        int across = columns > 0 ? columns : figures.Count;
        int rows = (figures.Count + across - 1) / across;
        for (int i = 0; i < figures.Count; i++)
        {
            var mesh = figures[i].Mesh();
            int col = i % across, row = i / across;
            var node = new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = material,
                Position = new Vector3((col - (across - 1) * 0.5f) * spacing, -row * rowDrop, row * rowNear),
                Rotation = new Vector3(0, yaw, 0),
            };
            if (figures[i].State is { } state) Face.FaceAnimator.Apply(node, state);
            AddChild(node);
            GD.Print($"[bodies] {figures[i].Name}: {mesh.SurfaceGetArrayIndexLen(0) / 3} triangles");
        }
        if (rows > 1)
        {
            // the grid's middle row in the middle of the frame
            lookAtY -= (rows - 1) * rowDrop * 0.5f + 0.05f;
            height += (rows - 1) * rowDrop + 0.25f;
        }
        // a long lens (docs/notes/avatar/judge-model-proportions-long-lens.md): framed to the row, or
        // to the figures' height, whichever needs more distance
        const float fov = 14f;
        float half = Mathf.Tan(Mathf.DegToRad(fov * 0.5f));
        var size = GetViewport().GetVisibleRect().Size;
        float aspect = size.X / Mathf.Max(1f, size.Y);
        float distance = Mathf.Max(across * spacing * 0.55f / (half * aspect), height * 0.55f / half);
        var cam = new Camera3D { Fov = fov, Position = new Vector3(0, lookAtY + distance * 0.06f, distance), Far = distance * 3f };
        AddChild(cam);
        cam.LookAt(new Vector3(0, lookAtY, 0), Vector3.Up);
        cam.Current = true;
        GD.Print($"[bodies] page {page}: {figures.Count} figures");
    }

    private Node3D? _convoy;
    private float _convoySpeed;

    /// <summary>
    /// "--outfits riders [--speed m/s]": dressed riders on a bike, a motorbike, in a car and on foot,
    /// all moving together at --speed (default 12) with the camera alongside, so the skirts feel
    /// the wind of their own motion (<see cref="FigureWind"/>) exactly as in the game.
    /// </summary>
    private void BuildRiders()
    {
        _convoySpeed = CmdArgs.Float("--speed") ?? 12f;
        _convoy = new Node3D { Name = "Convoy" };
        AddChild(_convoy);
        var yaw = new Vector3(0, Mathf.Pi * 0.5f, 0);   // facing −X: side-on to the camera, riding left
        Outfit Of(params Items.ItemId[] items) => Outfit.Of(items);

        var bike = Cyclist.Create(1, Of(Items.ItemId.PinkBow, Items.ItemId.PinkCropTop, Items.ItemId.PinkPleated,
            Items.ItemId.PinkStockings, Items.ItemId.PinkSneakers));
        bike.CadenceRpm = 80;
        bike.Position = new Vector3(-6.5f, 0, 0);
        bike.Rotation = yaw;
        _convoy.AddChild(bike);

        var moto = Motorcyclist.Create(Player.MotorbikeCatalog.All[0].Look, 2, outfit: Of(Items.ItemId.BuckleCorset,
            Items.ItemId.TartanSkirt, Items.ItemId.GothStockings, Items.ItemId.PlatformBoots, Items.ItemId.LaceArmWarmers));
        moto.Position = new Vector3(-2.5f, 0, 0);
        moto.Rotation = yaw;
        _convoy.AddChild(moto);

        var spec = Player.CarCatalog.All[0];
        var car = CarRig.Create(spec.Body, spec.Wheelbase, spec.Gauges, HumanPalette.ForRider(3) with
        {
            Outfit = Of(Items.ItemId.CatHeadset, Items.ItemId.HeartShades, Items.ItemId.GalaxyHoodie),
        });
        car.View = CockpitView.Outside;
        car.Position = new Vector3(2.5f, 0, 0);
        car.Rotation = yaw;
        _convoy.AddChild(car);

        var walker = new MeshInstance3D { MaterialOverride = HumanMeshBuilder.FigureMaterial(), Position = new Vector3(7f, 0, 0), Rotation = yaw };
        _convoy.AddChild(walker);
        _walkers.Add((walker, HumanPalette.ForRider(4) with { Outfit = Of(Items.ItemId.LolitaDress, Items.ItemId.KneeSocks, Items.ItemId.MaryJanes) }, Headwear.None));
        _riderWind = new FigureWind();
        _outfitWalk = true;

        // the ground rides along too, so a fast convoy does not leave it behind
        _convoy.AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(80, 80) },
            Position = Vector3.Down * 0.005f,
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.34f, 0.38f, 0.31f), SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled },
        });
        // --focus 0..3: the bike, the motorbike, the car or the walker close up, from a little ahead
        float[] at = { -6.5f, -2.5f, 2.5f, 7f };
        bool close = _focus is >= 0 and < 4;
        float x = close ? at[_focus] : 0f;
        var cam = new Camera3D { Fov = close ? 32 : 45, Position = close ? new Vector3(x - 1.6f, 1.5f, 4.2f) : new Vector3(0, 1.8f, 15f) };
        _convoy.AddChild(cam);
        cam.LookAt(new Vector3(x, 0.95f, 0), Vector3.Up);
        cam.Current = true;
        GD.Print($"[outfits] riders at {_convoySpeed:F1} m/s");
    }

    private FigureWind? _riderWind;

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
        // the riders' convoy rides to −X, the way they all face
        if (_convoy != null) _convoy.Position += Vector3.Left * _convoySpeed * (float)delta;
        if (_outfitWalk)
        {
            // in the convoy the walker runs at its speed (capped to a sprint) in the wind it measures
            float speed = _convoy != null ? Mathf.Min(_convoySpeed, 7f) : 1.4f;
            _walkPhase = HumanMeshBuilder.AdvancePhase(_walkPhase, speed, (float)delta);
            foreach (var (mesh, palette, hat) in _walkers)
            {
                var p = _riderWind != null ? palette with { Wind = _riderWind.Update(mesh, (float)delta) } : palette;
                mesh.Mesh = HumanMeshBuilder.BuildStride(p, speed, _walkPhase, hat: hat);
            }
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
