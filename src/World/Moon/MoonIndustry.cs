using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Bodies;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Moon;

/// <summary>
/// The Moon's industry (#1942, docs/plans/moon-surface.md section 4): solar arrays whose wings turn to the sun, bucket-wheel
/// regolith diggers driving a loop round their pit, spoil heaps, the ice mine's headframe with its sheave turning, ore
/// bins, flood lights that light the permanently dark crater, radio dishes that slew to a new source every few minutes,
/// and the mass driver's rail with its bucket flung along it. Meshes are shared (built once a kind, size and theme);
/// what moves is a node turned or moved (<see cref="IndustryMotion"/>), never a rebuilt mesh. Everything moving runs on
/// the simulation clock on the server's time (<see cref="SimClock.SimAt"/>), so every peer sees the digger at the same place. All in the
/// model viewer under "Moon industry".
/// </summary>
public static class MoonIndustry
{
    private static Color C(string hex) { var c = new Color(hex); c.A = 0; return c; }

    private static readonly Color Cell = C("27365c"), CellLine = C("8ea4c8"), Steel = C("8a8f94"), Dark = C("3a3d41"),
        Hazard = C("f2b21b"), Regolith = C("8d8a84"), RegolithDark = C("6f6c66"), Dish = C("e9ebee"), Rail = C("5d6166"),
        Coil = C("b87333"), Lamp = C("fff4d6"), Red = C("ff3b2f"), Dug = C("3e3c39"), DugRidge = C("57544f");

    private static readonly Dictionary<string, ArrayMesh> Cache = new();
    private static readonly object Lock = new();

    private static ArrayMesh Cached(string key, Action<MeshScratch> make)
    {
        lock (Lock)
        {
            if (Cache.TryGetValue(key, out var m)) return m;
            var s = new MeshScratch();
            make(s);
            m = s.Build();
            Cache[key] = m;
            return m;
        }
    }

    private static MeshInstance3D Inst(ArrayMesh m) => ColonyKit.Instance(m);

    // ---- the viewer --------------------------------------------------------------------------------------------------

    [Showcase("Moon industry")]
    public static IEnumerable<(string Name, Func<Node3D> Make)> Pieces()
    {
        yield return ("Solar array", () => One(IndustryKind.SolarArray, 10));
        yield return ("Regolith digger", () => One(IndustryKind.Digger, 0));
        yield return ("Spoil heap", () => One(IndustryKind.SpoilHeap, 11));
        yield return ("Ice mine headframe", () => One(IndustryKind.Headframe, 22));
        yield return ("Outpost hut", () => One(IndustryKind.Hut, 10));
        yield return ("Ore bins", () => One(IndustryKind.OreBins, 4));
        yield return ("Flood light", () => One(IndustryKind.FloodLight, 12));
        yield return ("Radio dish", () => One(IndustryKind.Dish, 12));
        yield return ("Mass driver (120 m)", () => One(IndustryKind.DriverTrack, 120));
        yield return ("Crew lander", () => One(IndustryKind.Lander, 20));
        yield return ("Runway (300 m)", () => One(IndustryKind.Runway, 300));
        yield return ("Glazed alley (Moon Village)", () => ColonyKit.Instance(ColonyKit.Alleys([(new(-6, 0, 0), new(6, 0, 0))], ColonyTheme.MoonVillage)).Glazed());
    }

    private static Node3D One(IndustryKind kind, double size) =>
        Build([new ColonyIndustry(kind, 0, 0, 0, size)], ColonyTheme.Artemis, 0, 0, (x, z) => new Vector3((float)x, 0, (float)-z), null);

    private static MeshInstance3D Glazed(this MeshInstance3D mi)
    {
        MeshScratch.Paint(mi, HumanMeshBuilder.Material(), ColonyKit.GlassMaterial());
        return mi;
    }

    // ---- building ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The pieces in their frame (x east, y up, z south) under one <see cref="IndustryMotion"/> node that moves what
    /// moves. <paramref name="ground"/> gives the ground's point in the frame at an east, north offset (the plateau's y = 0
    /// in a colony; the outpost's own ground otherwise); <paramref name="body"/> takes the solid shapes (none in the
    /// viewer). <paramref name="lat"/>, <paramref name="lon"/>: where the sun is reckoned for the arrays.
    /// </summary>
    public static IndustryMotion Build(IEnumerable<ColonyIndustry> pieces, ColonyTheme theme, double lat, double lon,
        Func<double, double, Vector3> ground, StaticBody3D? body)
    {
        var root = new IndustryMotion(lat, lon) { Name = "Industry" };
        var pal = ColonyKit.Of(theme);
        void Solid(Transform3D at, Vector3 size)
        {
            if (body == null) return;
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size }, Transform = at });
        }
        foreach (var p in pieces)
        {
            var at = ground(p.X, -p.Z);
            var t = new Transform3D(new Basis(Vector3.Up, -(float)p.Yaw), at);
            switch (p.Kind)
            {
                case IndustryKind.SolarArray:
                {
                    var mast = Inst(Cached("mast", MastMesh));
                    mast.Transform = t;
                    root.AddChild(mast);
                    var wing = Inst(Cached($"wing/{p.Size:F0}", s => WingMesh(s, (float)p.Size)));
                    wing.Position = at + Vector3.Up * MastH;
                    root.AddChild(wing);
                    root.Trackers.Add(wing);
                    Solid(t * new Transform3D(Basis.Identity, Vector3.Up * MastH / 2), new Vector3(0.5f, MastH, 0.5f));
                    break;
                }
                case IndustryKind.Digger:
                {
                    var rig = new Node3D { Name = "Digger", Transform = t };
                    rig.AddChild(Inst(Cached($"digger/{theme}", s => DiggerMesh(s, pal))));
                    var wheel = new Node3D { Position = new Vector3(0, WheelY, WheelZ) };
                    wheel.AddChild(Inst(Cached("bucketwheel", WheelMesh)));
                    rig.AddChild(wheel);
                    var solid = new AnimatableBody3D { Name = "DiggerBody" };
                    solid.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(5.6f, 4.2f, 8f) }, Position = new Vector3(0, 2.1f, 0.5f) });
                    rig.AddChild(solid);
                    root.AddChild(rig);
                    root.Wheels.Add((wheel, Vector3.Right, 0.6f));
                    if (p.Size > 0)
                    {
                        root.Diggers.Add((rig, at, (float)p.Size));
                        var pit = Inst(Cached($"pit/{p.Size:F0}", s => PitMesh(s, (float)p.Size)));
                        pit.Position = at;
                        root.AddChild(pit);
                    }
                    break;
                }
                case IndustryKind.SpoilHeap:
                {
                    var heap = Inst(Cached($"heap/{p.Size:F0}", s => HeapMesh(s, (float)p.Size)));
                    heap.Transform = t;
                    root.AddChild(heap);
                    float r = (float)p.Size, hh = r * 0.45f;
                    for (int k = 0; k < 3; k++)
                    {
                        float y0 = hh * k / 3, rr = r * (1 - (k + 0.5f) / 3);
                        if (body != null)
                            body.AddChild(new CollisionShape3D
                            {
                                Shape = new CylinderShape3D { Radius = rr, Height = hh / 3 },
                                Position = at + Vector3.Up * (y0 + hh / 6),
                            });
                    }
                    break;
                }
                case IndustryKind.Headframe:
                {
                    float h = (float)p.Size;
                    var frame = Inst(Cached($"headframe/{h:F0}", s => HeadframeMesh(s, h)));
                    frame.Transform = t;
                    root.AddChild(frame);
                    var sheave = new Node3D { Position = at + Vector3.Up * (h + 1.2f), Rotation = new Vector3(0, -(float)p.Yaw, 0) };
                    sheave.AddChild(Inst(Cached("sheave", SheaveMesh)));
                    root.AddChild(sheave);
                    root.Wheels.Add((sheave, Vector3.Right, 1.4f));
                    foreach (var (x, z) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
                        Solid(t * new Transform3D(Basis.Identity, new Vector3(x * 3.6f, h / 2, z * 3.6f)), new Vector3(0.6f, h, 0.6f));
                    Solid(t * new Transform3D(Basis.Identity, Vector3.Up * 0.6f), new Vector3(6, 1.2f, 6));
                    var red = new OmniLight3D { Position = at + Vector3.Up * (h + 3), LightColor = new Color(1, 0.3f, 0.2f), LightEnergy = 1.5f, OmniRange = 30 };
                    root.AddChild(red);
                    break;
                }
                case IndustryKind.Hut:
                {
                    float l = (float)p.Size;
                    var hut = Inst(ColonyKit.Module(ModuleKind.Can, l, 4.6, 5.5, 1, theme));
                    hut.Transform = t;
                    root.AddChild(hut);
                    var doors = Inst(Cached($"hutdoors/{l:F0}/{theme}", s => HutDoors(s, l, pal)));
                    doors.Transform = t;
                    root.AddChild(doors);
                    Solid(t * new Transform3D(Basis.Identity, Vector3.Up * 3.2f), new Vector3(4.6f, 4.6f, l));
                    Solid(t * new Transform3D(Basis.Identity, Vector3.Up * 0.45f), new Vector3(3.6f, 0.9f, l * 0.8f));
                    break;
                }
                case IndustryKind.OreBins:
                {
                    var bins = Inst(Cached($"bins/{theme}", s => BinsMesh(s, pal)));
                    bins.Transform = t;
                    root.AddChild(bins);
                    Solid(t * new Transform3D(Basis.Identity, Vector3.Up * 2), new Vector3(14, 4, 3.4f));
                    break;
                }
                case IndustryKind.FloodLight:
                {
                    float h = (float)p.Size;
                    var mast = Inst(Cached($"flood/{h:F0}", s => FloodMesh(s, h)));
                    mast.Transform = t;
                    root.AddChild(mast);
                    // the light: from the head out along the mast's facing and down, onto the work
                    var dir = new Vector3(Mathf.Sin((float)p.Yaw), -0.42f, -Mathf.Cos((float)p.Yaw)).Normalized();
                    var spot = new SpotLight3D
                    {
                        Name = "Flood", LightColor = new Color(0.93f, 0.97f, 1f), LightEnergy = 3, SpotRange = 110, SpotAngle = 62,
                        SpotAttenuation = 0.6f, ShadowEnabled = false,
                    };
                    // a light shines along its -Z
                    spot.Transform = new Transform3D(Basis.LookingAt(dir, Vector3.Up), at + Vector3.Up * (h + 0.6f));
                    root.AddChild(spot);
                    root.Lamps.Add(spot);
                    Solid(t * new Transform3D(Basis.Identity, Vector3.Up * h / 2), new Vector3(0.5f, h, 0.5f));
                    break;
                }
                case IndustryKind.Dish:
                {
                    float d = (float)p.Size;
                    var ped = Inst(Cached("pedestal", PedestalMesh));
                    ped.Transform = t;
                    root.AddChild(ped);
                    var dish = Inst(Cached($"dish/{d:F0}", s => DishMesh(s, d)));
                    dish.Position = at + Vector3.Up * PedestalH;
                    root.AddChild(dish);
                    root.Dishes.Add(dish);
                    Solid(t * new Transform3D(Basis.Identity, Vector3.Up * PedestalH / 2), new Vector3(2.4f, PedestalH, 2.4f));
                    break;
                }
                case IndustryKind.Lander:
                {
                    var lander = Inst(Cached($"lander/{theme}", s => LanderMesh(s, pal)));
                    lander.Transform = t;
                    root.AddChild(lander);
                    Solid(t * new Transform3D(Basis.Identity, Vector3.Up * 4.2f), new Vector3(4.2f, 5.4f, 4.2f));
                    foreach (var (x, z) in new[] { (-1, -1), (-1, 1), (1, -1), (1, 1) })
                        Solid(t * new Transform3D(Basis.Identity, new Vector3(x * 3.6f, 0.15f, z * 3.6f)), new Vector3(1.0f, 0.3f, 1.0f));
                    break;
                }
                case IndustryKind.Runway:
                {
                    // the ground's points every 50 m along the centre line (the strip stamp slopes and the sphere bends)
                    float len = (float)p.Size;
                    int n = Math.Max(1, (int)Math.Round(len / 50));
                    var line = new List<Vector3>();
                    for (int i = 0; i <= n; i++)
                    {
                        double s = len * i / n;
                        line.Add(ground(p.X + Math.Sin(p.Yaw) * s, -p.Z + Math.Cos(p.Yaw) * s));
                    }
                    root.AddChild(Inst(RunwayMesh(line)));
                    break;
                }
                case IndustryKind.DriverTrack:
                {
                    // pylons every 40 m on the ground along the heading; the rail from top to top
                    float len = (float)p.Size;
                    int n = Math.Max(1, (int)Math.Round(len / 40));
                    var tops = new List<Vector3>();
                    for (int i = 0; i <= n; i++)
                    {
                        double s = len * i / n;
                        var g = ground(p.X + Math.Sin(p.Yaw) * s, -p.Z + Math.Cos(p.Yaw) * s);
                        tops.Add(g + Vector3.Up * DriverY);
                        Solid(new Transform3D(new Basis(Vector3.Up, -(float)p.Yaw), g + Vector3.Up * (DriverY / 2)), new Vector3(1.0f, DriverY, 1.0f));
                    }
                    root.AddChild(Inst(DriverMesh(tops, len)));
                    var sled = Inst(Cached("sled", SledMesh));
                    root.AddChild(sled);
                    root.Sleds.Add((sled, tops.ToArray()));
                    break;
                }
            }
        }
        return root;
    }

    // ---- meshes ------------------------------------------------------------------------------------------------------

    /// <summary>A solar mast's height to the wing's pivot, metres.</summary>
    public const float MastH = 5.5f;

    private static void MastMesh(MeshScratch s)
    {
        s.Box(new(0, 0.1f, 0), new(1.6f, 0.2f, 1.6f), Dark);
        s.Tube(new(0, 0.2f, 0), new(0, MastH - 0.3f, 0), 0.18f, 0.14f, Steel, 8);
        s.Box(new(0, MastH - 0.2f, 0), new(0.6f, 0.4f, 0.6f), Dark);
    }

    /// <summary>A wing: a panel of cells <paramref name="w"/> wide and 3.6 m deep on a frame, its face up (+Y), its pivot at the origin.</summary>
    private static void WingMesh(MeshScratch s, float w)
    {
        const float d = 3.6f;
        s.Box(new(0, 0.05f, 0), new(w, 0.06f, d), Cell);
        for (int i = 1; i < 8; i++) s.Box(new(-w / 2 + i * w / 8, 0.085f, 0), new(0.04f, 0.01f, d), CellLine);
        s.Box(new(0, 0.085f, 0), new(w, 0.01f, 0.04f), CellLine);
        s.Box(new(0, -0.05f, 0), new(w, 0.12f, 0.2f), Steel);
        foreach (float x in new[] { -w / 3, 0, w / 3 }) s.Box(new(x, -0.05f, 0), new(0.15f, 0.12f, d), Steel);
        s.Box(new(0, -0.2f, 0), new(0.5f, 0.3f, 0.5f), Dark);
    }

    private const float WheelY = 3.4f, WheelZ = 9.2f;

    /// <summary>
    /// A bucket-wheel digger: two crawler tracks, a deck, the cab with its windows, the counterweight boom at the back,
    /// the boom forward (+Z) carrying the wheel (a node) at its tip.
    /// </summary>
    private static void DiggerMesh(MeshScratch s, ColonyKit.Palette p)
    {
        foreach (float x in new[] { -2.0f, 2.0f })
        {
            s.Box(new(x, 0.6f, 0), new(1.2f, 1.2f, 6.4f), Dark);
            for (int k = 0; k < 8; k++) s.Box(new(x, 1.22f, -2.8f + k * 0.8f), new(1.25f, 0.06f, 0.3f), Steel);
        }
        s.Box(new(0, 1.5f, 0), new(4.6f, 0.6f, 5.4f), Hazard);
        s.Box(new(-0.8f, 2.9f, -0.6f), new(2.4f, 2.2f, 2.6f), p.Shell);
        s.Box(new(-0.8f, 4.05f, -0.6f), new(2.6f, 0.1f, 2.8f), p.Trim);
        foreach (float x in new[] { -2.05f, 0.45f })
            s.Pane([new(x, 2.6f, -1.5f), new(x, 2.6f, 0.3f), new(x, 3.6f, 0.3f), new(x, 3.6f, -1.5f)], C("bfe3ff"));
        s.Pane([new(-1.9f, 2.6f, 0.71f), new(0.3f, 2.6f, 0.71f), new(0.3f, 3.6f, 0.71f), new(-1.9f, 3.6f, 0.71f)], C("bfe3ff"));
        s.Box(new(1.3f, 2.6f, -1.2f), new(1.4f, 1.6f, 2.4f), p.Band);
        // the boom: a lattice of two chords out to the wheel
        foreach (float x in new[] { -0.5f, 0.5f })
        {
            s.Tube(new(x, 2.0f, 1.0f), new(x, WheelY + 0.3f, WheelZ - 1.4f), 0.16f, Hazard, 5);
            s.Tube(new(x, 3.4f, 1.0f), new(x, WheelY + 0.9f, WheelZ - 1.4f), 0.12f, Hazard, 5);
        }
        for (int k = 0; k < 5; k++)
        {
            float z = 1.5f + k * (WheelZ - 3) / 5;
            s.Box(new(0, 2.4f + k * 0.25f, z), new(1.1f, 0.12f, 0.12f), Steel);
        }
        s.Box(new(0, WheelY, WheelZ - 1.1f), new(1.6f, 0.8f, 0.8f), Dark);
        // the counterweight boom and a conveyor out the back
        s.Tube(new(0, 2.2f, -2.4f), new(0, 3.2f, -7.0f), 0.18f, Hazard, 5);
        s.Box(new(0, 3.0f, -7.2f), new(1.8f, 1.4f, 1.4f), Dark);
        s.Box(new(0, 2.7f, -4.6f), new(0.8f, 0.12f, 4.4f), Steel, new Basis(Vector3.Right, -0.2f));
        // a lamp on the cab roof
        s.Pane([new(-1.4f, 4.12f, 0.5f), new(-0.2f, 4.12f, 0.5f), new(-0.2f, 4.12f, 0.75f), new(-1.4f, 4.12f, 0.75f)], Lamp);
    }

    /// <summary>The bucket wheel: a ring in the y-z plane (it turns about x) with eight buckets on its rim.</summary>
    private static void WheelMesh(MeshScratch s)
    {
        const float r = 2.4f;
        s.Ring(Vector3.Zero, Vector3.Right, r - 0.3f, r, 0.5f, Steel, 16);
        s.Ring(Vector3.Zero, Vector3.Right, 0.2f, 0.45f, 0.6f, Dark, 8);
        for (int i = 0; i < 8; i++)
        {
            float a = Mathf.Tau * i / 8;
            var dir = new Vector3(0, Mathf.Sin(a), Mathf.Cos(a));
            s.Box(dir * (r - 1.2f), new(0.12f, 0.12f, 2.0f), Steel, new Basis(Vector3.Right, -a));
            s.Box(dir * (r + 0.15f), new(0.9f, 0.55f, 0.7f), Hazard, new Basis(Vector3.Right, -a));
        }
    }

    /// <summary>The digger's pit: a ring of darker dug regolith under its loop, with a ridge either side.</summary>
    private static void PitMesh(MeshScratch s, float r)
    {
        s.Ring(new(0, 0.03f, 0), Vector3.Up, r - 4.5f, r + 4.5f, 0.04f, Dug, 48);
        s.Ring(new(0, 0.12f, 0), Vector3.Up, r + 4.5f, r + 6f, 0.25f, DugRidge, 48);
        s.Ring(new(0, 0.12f, 0), Vector3.Up, r - 6f, r - 4.5f, 0.25f, DugRidge, 48);
    }

    private static void HeapMesh(MeshScratch s, float r)
    {
        var rng = new Random((int)(r * 7));
        float h = r * 0.45f;
        s.Tube(new(0, -0.2f, 0), new(0, h, 0), r, r * 0.12f, Regolith, 14);
        for (int i = 0; i < 18; i++)
        {
            float a = (float)rng.NextDouble() * Mathf.Tau, k = 0.2f + 0.7f * (float)rng.NextDouble();
            var at = new Vector3(Mathf.Sin(a) * r * k, h * (1 - k) - 0.1f, Mathf.Cos(a) * r * k);
            s.Box(at, new(1.4f, 0.5f, 1.0f), i % 2 == 0 ? RegolithDark : Regolith, new Basis(Vector3.Up, a) * new Basis(Vector3.Right, -0.4f));
        }
    }

    /// <summary>
    /// The ice mine's headframe: four lattice legs leaning in to a platform at <paramref name="h"/>, cross braces each
    /// level, the shaft's collar at the foot with its hazard rail, a back-stay to the winder house, a red light on top.
    /// </summary>
    private static void HeadframeMesh(MeshScratch s, float h)
    {
        s.Box(new(0, 0.6f, 0), new(6.2f, 1.2f, 6.2f), Dark);
        s.Box(new(0, 1.21f, 0), new(3.2f, 0.02f, 3.2f), C("15171a"));
        foreach (var (x, z) in new[] { (-1f, -1f), (-1f, 1f), (1f, -1f), (1f, 1f) })
            s.Tube(new(x * 3.6f, 0, z * 3.6f), new(x * 1.6f, h, z * 1.6f), 0.22f, Steel, 6);
        for (int k = 1; k <= 5; k++)
        {
            float y = h * k / 6, w = 3.6f - 2.0f * k / 6;
            s.Box(new(0, y, w), new(2 * w, 0.2f, 0.2f), Hazard);
            s.Box(new(0, y, -w), new(2 * w, 0.2f, 0.2f), Hazard);
            s.Box(new(w, y, 0), new(0.2f, 0.2f, 2 * w), Hazard);
            s.Box(new(-w, y, 0), new(0.2f, 0.2f, 2 * w), Hazard);
        }
        s.Box(new(0, h + 0.2f, 0), new(4.2f, 0.4f, 4.2f), Dark);
        s.Tube(new(0, h + 0.4f, 0), new(0, h + 3f, 0), 0.08f, Steel, 4);
        s.Box(new(0, h + 3.05f, 0), new(0.3f, 0.3f, 0.3f), Red);
        // the cable down the shaft, the back-stay and the winder house behind
        s.Tube(new(0, h + 1.2f, 0), new(0, 0.5f, 0), 0.05f, Dark, 4);
        s.Tube(new(0, h, -1.6f), new(0, 0, -h * 0.55f), 0.25f, Steel, 6);
        s.Box(new(0, 2.5f, -h * 0.55f - 3), new(7, 5, 6), C("c9cfd6"));
        s.Box(new(0, 5.1f, -h * 0.55f - 3), new(7.4f, 0.2f, 6.4f), Hazard);
        s.Tube(new(0, h + 1.2f, 0), new(0, 4, -h * 0.55f - 0.5f), 0.04f, Dark, 4);
        // the collar's rail
        foreach (var (x, z) in new[] { (-1.8f, -1.8f), (-1.8f, 1.8f), (1.8f, -1.8f), (1.8f, 1.8f) })
            s.Tube(new(x, 1.2f, z), new(x, 2.3f, z), 0.05f, Hazard, 4);
        s.Box(new(0, 2.25f, 1.8f), new(3.6f, 0.08f, 0.08f), Hazard);
        s.Box(new(0, 2.25f, -1.8f), new(3.6f, 0.08f, 0.08f), Hazard);
        s.Box(new(1.8f, 2.25f, 0), new(0.08f, 0.08f, 3.6f), Hazard);
        s.Box(new(-1.8f, 2.25f, 0), new(0.08f, 0.08f, 3.6f), Hazard);
    }

    /// <summary>The headframe's sheave: a spoked wheel in the y-z plane, turning about x.</summary>
    private static void SheaveMesh(MeshScratch s)
    {
        s.Ring(Vector3.Zero, Vector3.Right, 1.25f, 1.4f, 0.2f, Hazard, 16);
        for (int i = 0; i < 6; i++)
        {
            float a = Mathf.Tau * i / 6;
            s.Box(new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) * 0.65f, new(0.08f, 0.08f, 1.3f), Steel, new Basis(Vector3.Right, -a));
        }
        s.Tube(new(-0.3f, 0, 0), new(0.3f, 0, 0), 0.15f, Dark, 6);
    }

    /// <summary>A hut's doors: a shut leaf over each end's doorway (an outpost hut is entered by the crew's suitport, not walked into).</summary>
    private static void HutDoors(MeshScratch s, float l, ColonyKit.Palette p)
    {
        foreach (float z in new[] { -l / 2 - 0.12f, l / 2 + 0.12f })
        {
            s.Box(new(0, ColonyKit.FloorY + ColonyKit.DoorH / 2, z), new(ColonyKit.DoorW, ColonyKit.DoorH, 0.08f), p.Frame);
            s.Box(new(0, ColonyKit.FloorY + ColonyKit.DoorH * 0.7f, z + Math.Sign(z) * 0.05f), new(0.5f, 0.35f, 0.02f), p.Window);
        }
    }

    /// <summary>Ore bins: four hoppers on a frame, a chute under each.</summary>
    private static void BinsMesh(MeshScratch s, ColonyKit.Palette p)
    {
        for (int i = 0; i < 4; i++)
        {
            float x = -5.1f + i * 3.4f;
            s.Tube(new(x, 1.6f, 0), new(x, 4f, 0), 0.4f, 1.5f, i % 2 == 0 ? p.Trim : p.Band, 4);
            s.Box(new(x, 4.05f, 0), new(2.2f, 0.1f, 2.2f), Steel);
            foreach (var (dx, dz) in new[] { (-1f, -1f), (-1f, 1f), (1f, -1f), (1f, 1f) })
                s.Tube(new(x + dx * 1.0f, 0, dz * 1.0f), new(x + dx * 1.0f, 2.6f, dz * 1.0f), 0.08f, Steel, 4);
            s.Box(new(x, 1.3f, 0), new(0.5f, 0.6f, 0.5f), Dark);
        }
        s.Box(new(0, 0.05f, 0), new(14, 0.1f, 3.4f), Dark);
    }

    /// <summary>A flood light mast <paramref name="h"/> high, a bar of four lamps at the top facing +Z.</summary>
    private static void FloodMesh(MeshScratch s, float h)
    {
        s.Box(new(0, 0.15f, 0), new(1.4f, 0.3f, 1.4f), Dark);
        s.Tube(new(0, 0.3f, 0), new(0, h, 0), 0.16f, 0.1f, Steel, 6);
        s.Box(new(0, h + 0.4f, 0.2f), new(2.8f, 0.9f, 0.4f), Dark);
        for (int i = 0; i < 4; i++)
        {
            float x = -1.05f + i * 0.7f;
            s.Pane([new(x - 0.3f, h + 0.05f, 0.41f), new(x + 0.3f, h + 0.05f, 0.41f), new(x + 0.3f, h + 0.75f, 0.41f), new(x - 0.3f, h + 0.75f, 0.41f)], Lamp);
        }
    }

    /// <summary>A radio dish's pedestal height to the dish's pivot, metres.</summary>
    public const float PedestalH = 6f;

    private static void PedestalMesh(MeshScratch s)
    {
        s.Tube(new(0, 0, 0), new(0, 0.5f, 0), 2.0f, Dark, 10);
        s.Tube(new(0, 0.5f, 0), new(0, PedestalH - 1.2f, 0), 1.0f, 0.8f, Dish, 10);
        s.Box(new(0, PedestalH - 0.9f, 0), new(2.2f, 0.6f, 1.4f), Steel);
        foreach (float x in new[] { -0.9f, 0.9f }) s.Box(new(x, PedestalH - 0.3f, 0), new(0.3f, 1.2f, 0.8f), Steel);
    }

    /// <summary>A dish <paramref name="d"/> across, opening up (+Y) from its pivot: a shallow bowl (seen from both sides), a rim, the feed on three struts.</summary>
    private static void DishMesh(MeshScratch s, float d)
    {
        float r = d / 2, depth = d * 0.14f;
        const int rings = 4;
        float prevR = 0.6f, prevY = 0.4f;
        for (int k = 1; k <= rings; k++)
        {
            float rr = r * k / rings, yy = 0.4f + depth * (k / (float)rings) * (k / (float)rings);
            s.Skirt(new(0, prevY, 0), new(0, yy, 0), prevR, rr, k % 2 == 0 ? Dish : C("dfe2e6"), 20);
            prevR = rr;
            prevY = yy;
        }
        s.Ring(new(0, prevY, 0), Vector3.Up, r - 0.1f, r + 0.1f, 0.2f, Steel, 20);
        s.Tube(new(0, -0.3f, 0), new(0, 0.45f, 0), 0.7f, Steel, 8);
        float focus = 0.4f + d * 0.4f;
        for (int i = 0; i < 3; i++)
        {
            float a = Mathf.Tau * i / 3;
            s.Tube(new Vector3(Mathf.Sin(a) * r * 0.8f, 0.4f + depth * 0.64f, Mathf.Cos(a) * r * 0.8f), new(0, focus, 0), 0.06f, Steel, 4);
        }
        s.Box(new(0, focus, 0), new(0.6f, 0.8f, 0.6f), Dark);
    }

    /// <summary>The mass driver's rail over its pylon tops, in its frame (pre-turned): pylon heads, two rails, coils on the last 60 %.</summary>
    public const float DriverY = 3.2f;

    private static ArrayMesh DriverMesh(List<Vector3> tops, float len)
    {
        var s = new MeshScratch();
        static Vector3 T(Vector3 v) => new(-v.X, v.Y, -v.Z);
        for (int i = 0; i < tops.Count; i++)
        {
            var top = T(tops[i]);
            s.Tube(top - Vector3.Up * DriverY, top - Vector3.Up * 0.3f, 0.35f, 0.25f, Steel, 6);
            s.Box(top - Vector3.Up * 0.2f, new(2.4f, 0.3f, 0.6f), Dark, Facing(i + 1 < tops.Count ? T(tops[i + 1]) - top : top - T(tops[i - 1])));
            if (i + 1 >= tops.Count) continue;
            var a = top;
            var b = T(tops[i + 1]);
            var dir = (b - a).Normalized();
            var side = Vector3.Up.Cross(dir).Normalized();
            var basis = Facing(b - a);
            float l = a.DistanceTo(b);
            foreach (float sd in new[] { -0.7f, 0.7f })
                s.Box((a + b) / 2 + side * sd, new(0.22f, 0.3f, l), Rail, basis);
            s.Box((a + b) / 2 - Vector3.Up * 0.1f, new(1.6f, 0.12f, l), Dark, basis);
            // the accelerator's coils along the last 60 %
            if (i >= (tops.Count - 1) * 0.4f)
                for (int k = 0; k < 5; k++)
                    s.Ring(a.Lerp(b, (k + 0.5f) / 5) + Vector3.Up * 0.5f, dir, 1.0f, 1.25f, 0.35f, Coil, 10);
        }
        // the loading cradle at the start, the end marker lights
        var st = T(tops[0]);
        s.Box(st + Vector3.Up * 0.8f, new(3.4f, 1.6f, 6f), Hazard, Facing(T(tops[1]) - st));
        var en = T(tops[^1]);
        s.Box(en + Vector3.Up * 1.6f, new(3f, 0.3f, 0.3f), Red, Facing(en - T(tops[^2])));
        return s.Build();
    }

    private static Basis Facing(Vector3 along)
    {
        var d = along.Normalized();
        var side = Vector3.Up.Cross(d).Normalized();
        return new Basis(side, d.Cross(side), d);
    }

    /// <summary>
    /// A crew lander standing on a pad (#1943): a descent stage of foil and tanks on four splayed legs with round pads,
    /// the crew cabin on top (white, in the colony's trim) with its windows, hatch, platform and ladder, thruster pods, a
    /// dish. About 9 m tall; its front (the ladder) along +Z.
    /// </summary>
    private static void LanderMesh(MeshScratch s, ColonyKit.Palette p)
    {
        // the descent stage: an octagonal drum of foil, tanks round it, the engine bell under it
        s.Tube(new(0, 1.6f, 0), new(0, 3.6f, 0), 2.1f, C("c9a54a"), 8);
        s.Tube(new(0, 3.6f, 0), new(0, 3.75f, 0), 2.2f, Steel, 8);
        for (int i = 0; i < 4; i++)
        {
            float a = Mathf.Tau * (i + 0.5f) / 4;
            var at = new Vector3(Mathf.Sin(a) * 1.9f, 2.6f, Mathf.Cos(a) * 1.9f);
            s.Tube(at - Vector3.Up * 0.8f, at + Vector3.Up * 0.8f, 0.55f, Dish, 10);
        }
        s.Tube(new(0, 1.6f, 0), new(0, 0.9f, 0), 0.35f, 0.8f, Dark, 12);
        // four legs splayed out to their pads, a strut from each
        foreach (var (x, z) in new[] { (-1f, -1f), (-1f, 1f), (1f, -1f), (1f, 1f) })
        {
            var hip = new Vector3(x * 1.5f, 3.2f, z * 1.5f);
            var foot = new Vector3(x * 3.6f, 0.35f, z * 3.6f);
            s.Tube(hip, foot, 0.12f, Steel, 6);
            s.Tube(new Vector3(x * 1.8f, 1.8f, z * 1.8f), foot + Vector3.Up * 0.6f, 0.07f, Steel, 5);
            s.Tube(foot - Vector3.Up * 0.3f, foot, 0.5f, 0.42f, Dark, 10);
        }
        // the crew cabin: a squat cylinder, its windows forward, a hatch and a platform with the ladder down at +Z
        s.Tube(new(0, 3.75f, 0), new(0, 6.9f, 0), 1.6f, p.Shell, 14);
        s.Tube(new(0, 6.9f, 0), new(0, 7.6f, 0), 1.6f, 0.9f, p.Shell, 14);
        s.Ring(new(0, 5.2f, 0), Vector3.Up, 1.6f, 1.7f, 0.2f, p.Trim, 14);
        foreach (float a in new[] { -0.35f, 0.35f })
            s.Pane([new(Mathf.Sin(a - 0.12f) * 1.61f, 5.9f, Mathf.Cos(a - 0.12f) * 1.61f), new(Mathf.Sin(a + 0.12f) * 1.61f, 5.9f, Mathf.Cos(a + 0.12f) * 1.61f),
                new(Mathf.Sin(a + 0.12f) * 1.61f, 6.5f, Mathf.Cos(a + 0.12f) * 1.61f), new(Mathf.Sin(a - 0.12f) * 1.61f, 6.5f, Mathf.Cos(a - 0.12f) * 1.61f)], p.Window);
        s.Box(new(0, 4.6f, 1.62f), new(0.9f, 1.3f, 0.06f), p.Frame);
        s.Box(new(0, 3.85f, 2.2f), new(1.6f, 0.1f, 1.2f), Steel);
        for (int k = 0; k < 9; k++) s.Box(new(0, 0.4f + k * 0.38f, 2.75f), new(0.7f, 0.05f, 0.08f), Steel);
        foreach (float x in new[] { -0.38f, 0.38f }) s.Tube(new(x, 0.3f, 2.8f), new(x, 3.85f, 2.75f), 0.04f, Steel, 4);
        // thruster pods and a dish
        for (int i = 0; i < 4; i++)
        {
            float a = Mathf.Tau * i / 4;
            s.Box(new Vector3(Mathf.Sin(a) * 1.7f, 6.6f, Mathf.Cos(a) * 1.7f), new(0.35f, 0.35f, 0.35f), Dark, new Basis(Vector3.Up, a));
        }
        s.Tube(new(0.6f, 7.6f, -0.4f), new(0.6f, 8.4f, -0.4f), 0.04f, Steel, 4);
        s.Tube(new(0.6f, 8.4f, -0.4f), new(0.6f, 8.5f, -0.25f), 0.45f, 0.05f, Dish, 12);
        s.Box(new(0, 6.95f, 0.6f), new(1.0f, 0.5f, 0.02f), p.Accent);
    }

    /// <summary>
    /// The runway along its ground points (pre-turned): sintered strips 60 m wide segment by segment, a dashed centre line,
    /// threshold bars and aiming marks at both ends, edge lights every 60 m (lit, unshaded) and red ones across each end.
    /// </summary>
    private static ArrayMesh RunwayMesh(List<Vector3> line)
    {
        var s = new MeshScratch();
        static Vector3 T(Vector3 v) => new(-v.X, v.Y, -v.Z);
        const float half = 30;
        float total = 0;
        void Light(Vector3 at, Color c)
        {
            s.Box(at + Vector3.Up * 0.2f, new Vector3(0.3f, 0.4f, 0.3f), Dark);
            s.Pane([at + new Vector3(-0.15f, 0.41f, -0.15f), at + new Vector3(0.15f, 0.41f, -0.15f), at + new Vector3(0.15f, 0.41f, 0.15f),
                at + new Vector3(-0.15f, 0.41f, 0.15f)], c);
        }
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = T(line[i]) + Vector3.Up * 0.04f;
            var b = T(line[i + 1]) + Vector3.Up * 0.04f;
            var basis = Facing(b - a);
            float l = a.DistanceTo(b);
            var dir = (b - a).Normalized();
            s.Box((a + b) / 2, new Vector3(2 * half, 0.08f, l + 0.4f), C("5f5c57"), basis);
            // the centre line's dashes: 30 m painted, 20 m bare
            for (float d = 0; d + 30 <= l + 0.1f; d += 50)
                s.Box(a + dir * (d + 15) + Vector3.Up * 0.045f, new Vector3(0.9f, 0.02f, 30), C("e8e6df"), basis);
            // edge lights every 60 m along the whole runway
            for (float d = (60 - total % 60) % 60; d < l; d += 60)
                foreach (float sd in new[] { -half - 1.5f, half + 1.5f })
                    Light(a + dir * d + basis.X * sd, Lamp);
            total += l;
        }
        // the ends: threshold bars, aiming marks 300 m in, red lights across
        foreach (bool far in new[] { false, true })
        {
            var e = T(far ? line[^1] : line[0]) + Vector3.Up * 0.045f;
            var inward = (T(far ? line[^2] : line[1]) - T(far ? line[^1] : line[0])).Normalized();
            var basis = Facing(inward);
            for (int k = 0; k < 8; k++)
            {
                float x = -half + 4 + k * (2 * half - 8) / 7f;
                if (Mathf.Abs(x) < 3) continue;
                s.Box(e + inward * 25 + basis.X * x, new Vector3(1.8f, 0.02f, 30), C("e8e6df"), basis);
            }
            foreach (float x in new[] { -9f, 9f }) s.Box(e + inward * 300 + basis.X * x, new Vector3(6f, 0.02f, 45), C("e8e6df"), basis);
            for (int k = 0; k <= 10; k++) Light(e + basis.X * (-half + k * half / 5) - inward * 2, Red);
        }
        return s.Build();
    }

    /// <summary>The mass driver's bucket: a sled with a payload canister, its front along +Z (turned to −Z by the build).</summary>
    private static void SledMesh(MeshScratch s)
    {
        s.Box(new(0, 0.25f, 0), new(1.6f, 0.3f, 3f), Dark);
        s.Tube(new(0, 0.85f, -1.4f), new(0, 0.85f, 1.2f), 0.5f, Dish, 10);
        s.Tube(new(0, 0.85f, 1.2f), new(0, 0.85f, 1.9f), 0.5f, 0.1f, Hazard, 10);
    }
}

/// <summary>
/// What moves in a colony's or an outpost's industry (#1942), on the shared clock: the solar wings turned to the sun
/// (every 2 s, eased), the diggers round their loops, wheels and sheaves turning, the dishes slewing to a new source every
/// ten minutes, the mass driver's bucket loaded, flung and gone each minute. Node transforms only: no mesh is rebuilt.
/// </summary>
public partial class IndustryMotion : Node3D
{
    public readonly List<Node3D> Trackers = new();
    public readonly List<Node3D> Dishes = new();
    public readonly List<(Node3D Rig, Vector3 Centre, float Radius)> Diggers = new();
    public readonly List<(Node3D Node, Vector3 Axis, float Rate)> Wheels = new();
    public readonly List<(Node3D Sled, Vector3[] Tops)> Sleds = new();

    /// <summary>Lights that also light the body's ground (<see cref="Bodies.GroundLamps"/>), while this is in the tree.</summary>
    public readonly List<Light3D> Lamps = new();

    private readonly double _lat, _lon;
    private double _sunAge = 99;
    private Vector3 _sun = Vector3.Up, _sunNow = Vector3.Up;

    /// <summary>A digger's speed round its loop, m/s.</summary>
    public const float DigSpeed = 0.5f;

    /// <summary>The mass driver's cycle, seconds: loading, the run, gone.</summary>
    public const double DriverCycle = 60, DriverLoad = 20, DriverRun = 5;

    public IndustryMotion(double lat, double lon)
    {
        _lat = lat;
        _lon = lon;
    }

    public override void _Ready() => Step(0, snap: true);

    public override void _EnterTree()
    {
        foreach (var l in Lamps) Bodies.GroundLamps.Add(l);
    }

    public override void _ExitTree()
    {
        foreach (var l in Lamps) Bodies.GroundLamps.Remove(l);
    }

    public override void _Process(double delta) => Step(delta, snap: false);

    /// <summary>The sun's direction in this frame (x east, y up, z south), or straight up.</summary>
    public static Vector3 SunIn(double lat, double lon, SkyMoment sky)
    {
        var sun = Frames.Convert(BodyId.Sun, DVec3.Zero, BodyId.Moon, sky);
        double l = Math.Sqrt(sun.X * sun.X + sun.Y * sun.Y + sun.Z * sun.Z);
        var f = new EnuFrame(new Geodetic(lat, lon, 0), Ellipsoid.Moon);
        var (x, y, z) = f.DirectionToLocal(new EcefPos(sun.X / l, sun.Y / l, sun.Z / l));
        return new Vector3((float)x, (float)y, (float)z);
    }

    private void Step(double delta, bool snap)
    {
        double now = SimClock.SimAt(ClockSync.ServerNow);
        _sunAge += delta;
        if (_sunAge > 2 && Trackers.Count > 0)
        {
            _sunAge = 0;
            _sun = SunIn(_lat, _lon, SkyClock.Now);
        }
        if (Trackers.Count > 0)
        {
            // stowed flat (face up) while the sun is down
            var want = _sun.Y > -0.02f ? _sun.Normalized() : Vector3.Up;
            _sunNow = snap ? want : _sunNow.Slerp(want, Mathf.Clamp((float)delta * 0.5f, 0, 1));
            var basis = Facing(_sunNow);
            foreach (var w in Trackers) w.Basis = basis;
        }
        if (Dishes.Count > 0)
        {
            // all the dishes on one source; a new one every ten minutes, slewed to over a minute
            long slot = (long)Math.Floor(now / 600);
            double into = now - slot * 600;
            var from = Source(slot - 1);
            var to = Source(slot);
            var d = from.Slerp(to, (float)Math.Clamp(into / 60, 0, 1));
            var basis = Facing(d);
            foreach (var dish in Dishes) dish.Basis = basis;
        }
        foreach (var (rig, centre, r) in Diggers)
        {
            double a = now * DigSpeed / r;
            var pos = centre + new Vector3((float)(Math.Sin(a) * r), 0, (float)(-Math.Cos(a) * r));
            // the build turns the authored front (+Z) to -Z: -Z along the way round
            float yaw = Mathf.Atan2(-(float)Math.Cos(a), -(float)Math.Sin(a));
            rig.Transform = new Transform3D(new Basis(Vector3.Up, yaw), pos);
        }
        foreach (var (node, axis, rate) in Wheels)
        {
            var b = node.Basis.Orthonormalized();
            node.Basis = b * new Basis(axis, rate * (float)delta);
        }
        foreach (var (sled, tops) in Sleds) PlaceSled(sled, tops, now);
    }

    /// <summary>A sky direction for a dish slot: 30-80° up, any azimuth, from the slot's number.</summary>
    private static Vector3 Source(long slot)
    {
        ulong h = (ulong)slot * 0x9E3779B97F4A7C15UL;
        h ^= h >> 29;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 32;
        float az = (h & 0xFFFF) / 65535f * Mathf.Tau, el = Mathf.DegToRad(30 + ((h >> 16) & 0xFFFF) / 65535f * 50);
        return new Vector3(Mathf.Cos(el) * Mathf.Sin(az), Mathf.Sin(el), -Mathf.Cos(el) * Mathf.Cos(az));
    }

    /// <summary>A basis whose +Y is <paramref name="up"/>, its x kept level (a wing tilts about a level axis).</summary>
    private static Basis Facing(Vector3 up)
    {
        var y = up.Normalized();
        var x = Vector3.Up.Cross(y);
        x = x.LengthSquared() < 1e-6f ? Vector3.Right : x.Normalized();
        var z = x.Cross(y);
        return new Basis(x, y, z);
    }

    /// <summary>
    /// The bucket on the rail at a time: loaded at the start, then flung the whole length in <see cref="DriverRun"/> s
    /// (constant acceleration: ~800 m/s at the end, near the Moon's escape speed... for a payload to orbit), gone till the next.
    /// </summary>
    public static void PlaceSled(Node3D sled, Vector3[] tops, double now)
    {
        double t = now - Math.Floor(now / DriverCycle) * DriverCycle;
        if (t > DriverLoad + DriverRun)
        {
            sled.Visible = false;
            return;
        }
        sled.Visible = true;
        double total = 0;
        for (int i = 0; i + 1 < tops.Length; i++) total += tops[i].DistanceTo(tops[i + 1]);
        double u = t <= DriverLoad ? 0 : Math.Pow((t - DriverLoad) / DriverRun, 2);
        double s = u * total;
        for (int i = 0; i + 1 < tops.Length; i++)
        {
            float l = tops[i].DistanceTo(tops[i + 1]);
            if (s > l && i + 2 < tops.Length) { s -= l; continue; }
            var dir = (tops[i + 1] - tops[i]).Normalized();
            var at = tops[i] + dir * (float)Math.Min(s, l) + Vector3.Up * 0.2f;
            // front along +Z authored, turned to -Z: -Z along the rail
            var z = -dir;
            var x = Vector3.Up.Cross(z).Normalized();
            sled.Transform = new Transform3D(new Basis(x, z.Cross(x), z), at);
            return;
        }
    }
}
