using Godot;
using UnitSport.Avatar;

namespace UnitSport.Player;

/// <summary>
/// <c>--cockpitcheck</c> (#69): every car's driver's seat, derived from its body
/// (<see cref="CarMeshBuilder.SeatFor(CarBody, float)"/>), must fit the person in it — the head
/// under the headlining, the eye behind the windscreen with room behind the head, both hands on
/// the rim wherever the wheel is turned, the right foot on the throttle, the pedals behind the
/// bulkhead — and the car must build with its glass as a separate surface and a driver in it.
/// Headless, no world. Non-zero exit on the first car that does not.
/// </summary>
public static class CockpitCheck
{
    /// <summary>
    /// A kart has no roof, glass or bulkhead to fit the driver under: only that the hands and feet reach
    /// the wheel and the pedals however the wheel is turned, and that the rig seats a driver (#715).
    /// </summary>
    private static int CheckKart(CarSpec spec)
    {
        var seat = CarMeshBuilder.SeatFor(spec.Body, spec.Wheelbase);
        float reach = 0f;
        foreach (float turn in new[] { 0f, HumanMeshBuilder.MaxGripTurn, -HumanMeshBuilder.MaxGripTurn })
        {
            var (hr, hl, fr, fl) = HumanMeshBuilder.DriverReach(seat, turn);
            reach = Mathf.Max(reach, Mathf.Max(Mathf.Max(hr, hl), Mathf.Max(fr, fl)));
        }
        var rig = CarRig.Create(spec.Body, spec.Wheelbase, spec.Gauges, HumanPalette.Default);
        bool driver = rig.GetNode<Node3D>("Body").HasNode("Driver");
        rig.Free();
        bool ok = reach < 0.01f && driver;
        GD.Print($"[cockpitcheck] {spec.Label,-24} {spec.Body.Shape,-10} reach {reach * 1000,4:F0}mm  {(ok ? "ok" : "FAIL" + (driver ? "" : " no driver"))}");
        return ok ? 0 : 1;
    }

    public static int Run()
    {
        int failed = 0;
        GD.Print("[cockpitcheck] car                      shape      recline  eye (x, y, z)         head  ahead  behind  reach  pedals");
        foreach (var spec in CarCatalog.All)
        {
            if (spec.Body.Shape == BodyShape.Kart) { failed += CheckKart(spec); continue; }
            var d = CarMeshBuilder.For(spec.Body, spec.Wheelbase);
            var seat = CarMeshBuilder.SeatFor(d);
            var eye = HumanMeshBuilder.DriverEye(seat.Hip, seat.Recline);

            // the head box's top is ~10 cm above the eye; the headlining hangs 7 cm under the roof
            float head = d.Roof - 0.07f - (eye.Y + 0.1f);
            float Rake(float y, float bottom, float top) => Mathf.Lerp(bottom, top, (y - d.Belt) / (d.Roof - d.Belt));
            float ahead = Rake(eye.Y, d.WsBase, d.WsTop) - eye.Z;
            // the back of the head is ~19 cm behind the eye
            float behind = eye.Z - 0.19f - Rake(eye.Y + 0.05f, d.RgBase, d.RgTop);
            float reach = 0f;
            foreach (float turn in new[] { 0f, HumanMeshBuilder.MaxGripTurn, -HumanMeshBuilder.MaxGripTurn })
            {
                var (hr, hl, fr, fl) = HumanMeshBuilder.DriverReach(seat, turn);
                reach = Mathf.Max(reach, Mathf.Max(Mathf.Max(hr, hl), Mathf.Max(fr, fl)));
            }
            // the hinges behind the bulkhead, the throttle inside the footwell between the wheel wells
            float pedals = Mathf.Min(CarMeshBuilder.Firewall(d) - (seat.Throttle + DriverSeat.PedalHinge).Z,
                CarMeshBuilder.Tub(d) - Mathf.Abs(seat.Throttle.X) - 0.03f);

            var rig = CarRig.Create(spec.Body, spec.Wheelbase, spec.Gauges, HumanPalette.Default);
            var shell = rig.GetNode<Node3D>("Body").GetNode<MeshInstance3D>("Shell");
            bool glass = shell.Mesh is ArrayMesh m && Enumerable.Range(0, m.GetSurfaceCount())
                .Any(i => m.SurfaceGetName(i) == MeshScratch.GlassSurface);
            bool driver = rig.GetNode<Node3D>("Body").HasNode("Driver");
            rig.Free();

            bool ok = head >= 0f && ahead >= 0.2f && behind >= 0f && reach < 0.01f && pedals >= 0f && glass && driver;
            if (!ok) failed++;
            GD.Print($"[cockpitcheck] {spec.Label,-24} {spec.Body.Shape,-10} {Mathf.RadToDeg(seat.Recline),5:F0}°  "
                + $"({eye.X,5:F2}, {eye.Y,4:F2}, {eye.Z,5:F2})  {head * 100,4:F0}  {ahead * 100,5:F0}  {behind * 100,6:F0}  "
                + $"{reach * 1000,4:F0}mm {pedals * 100,5:F0}  {(ok ? "ok" : "FAIL" + (glass ? "" : " no glass surface") + (driver ? "" : " no driver"))}");
        }
        GD.Print("[cockpitcheck] head/ahead/behind/pedals in cm of room (head under the headlining, windscreen ahead of the eye, "
            + "rear glass behind the head, pedal hinges behind the bulkhead); reach = how far a hand or foot falls short");
        int heavyFailed = CheckHeavy();
        failed += CheckAircraft();
        int total = CarCatalog.All.Count + HeavyCatalog.All.Count;
        failed += heavyFailed;
        GD.Print(failed == 0 ? $"[cockpitcheck] RESULT: all {CarCatalog.All.Count} cars and {HeavyCatalog.All.Count} trucks and buses fit their driver, and the {Airliner.Kinds.Length} aircraft's pilots see out of the windscreen"
            : $"[cockpitcheck] RESULT: FAILED — {failed} of {total} vehicles do not");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The trucks and buses (#157): the same fit from the cab (<see cref="HeavyCabin.SeatFor"/>), the
    /// eye between the dash and the top of the windscreen, the seat inside the walls, and the seats
    /// a passenger can take (#158): a truck's cab two, the pickup's five (#463), a bus at least twenty.
    /// </summary>
    private static int CheckHeavy()
    {
        int failed = 0;
        GD.Print("[cockpitcheck] heavy                    eye (x, y, z)         head  ahead  over dash  reach  pedals  wall  seats  width");
        foreach (var spec in HeavyCatalog.All)
            failed += CheckCab(spec.Label, Enumerable.Range(0, spec.Sections.Length)
                .Select(k => HeavyRig.Create(spec, k, 0.5f, k == 0 ? HumanPalette.Default : null)).ToList(),
                spec.Class switch { HeavyClass.Tractor or HeavyClass.Rigid or HeavyClass.FarmTractor or HeavyClass.Combine => 1, HeavyClass.Pickup => 4, _ => 20 });
        // the airstairs' cab (#417): a heavy cockpit in a low cab, its seat in front of the back wall
        var stairs = Avatar.AirstairsMeshBuilder.CreateRig(2.5f, HumanPalette.Default);
        float back = stairs.Cockpit!.Seat.Hip.Z - 0.3f - (Avatar.AirstairsLayout.CabRear + Avatar.AirstairsLayout.CabWall);
        failed += CheckCab("Airstairs", new List<HeavyRig> { stairs }, 1);
        if (back < 0f) failed++;
        GD.Print($"[cockpitcheck] Airstairs seat back {back * 100:F0} cm in front of the cab's back wall {(back >= 0f ? "ok" : "FAIL")}");
        GD.Print("[cockpitcheck] heavy: over dash = cm the eye has above the dash and under the top of the windscreen; "
            + "wall = cm between the seat's side and the cab wall; seats = passenger seats; width = the first section's mesh, mirrors and all, m");
        return failed;
    }

    /// <summary>One heavy cab's fit (its rigs, freed here), and the passenger seats it must have; 1 when it fails.</summary>
    private static int CheckCab(string label, List<HeavyRig> rigs, int wanted)
    {
        int failed = 0;
        {
            var c = rigs[0].Cockpit;
            var shell = rigs[0].GetNode<Node3D>("Body").GetNode<MeshInstance3D>("Shell");
            bool glass = shell.Mesh is ArrayMesh m && Enumerable.Range(0, m.GetSurfaceCount())
                .Any(i => m.SurfaceGetName(i) == MeshScratch.GlassSurface);
            bool driver = rigs[0].GetNode<Node3D>("Body").HasNode("Driver");
            // what the hull is measured from: the mirrors must not have widened it
            float span = MeshBounds.Of(rigs[0]).Size.X;
            int seats = rigs.Sum(r => r.Seats.Length) - 1;   // the driver's is not a passenger's
            foreach (var r in rigs) r.Free();
            if (c == null)
            {
                failed++;
                GD.Print($"[cockpitcheck] {label,-24} FAIL no cockpit");
                return 1;
            }

            var f = c.Frame;
            var seat = c.Seat;
            var eye = HumanMeshBuilder.DriverEye(seat.Hip, seat.Recline);
            float head = f.Ceiling - (eye.Y + 0.1f);
            float ahead = f.Front - eye.Z;
            // looking straight out, the eye is over the dash and under the top of the glass
            float overDash = Mathf.Min(eye.Y - (f.DashTop + 0.03f), f.WsTop - eye.Y);
            float reach = 0f;
            foreach (float turn in new[] { 0f, seat.MaxGrip, -seat.MaxGrip })
            {
                var (hr, hl, fr, fl) = HumanMeshBuilder.DriverReach(seat, turn);
                reach = Mathf.Max(reach, Mathf.Max(Mathf.Max(hr, hl), Mathf.Max(fr, fl)));
            }
            float pedals = f.Front - f.Nose - (seat.Throttle + DriverSeat.PedalHinge).Z;
            float wall = f.InnerHalf - (Mathf.Abs(seat.Hip.X) + 0.25f);

            bool ok = head >= 0.05f && ahead >= 0.5f && overDash >= 0.1f && reach < 0.01f && pedals >= 0f && wall >= 0f
                && glass && driver && seats >= wanted;
            if (!ok) failed++;
            GD.Print($"[cockpitcheck] {label,-24} ({eye.X,5:F2}, {eye.Y,4:F2}, {eye.Z,5:F2})  {head * 100,4:F0}  {ahead * 100,5:F0}  "
                + $"{overDash * 100,9:F0}  {reach * 1000,4:F0}mm {pedals * 100,6:F0}  {wall * 100,4:F0}  {seats,5}  {span,5:F2}  "
                + $"{(ok ? "ok" : "FAIL" + (glass ? "" : " no glass surface") + (driver ? "" : " no driver") + (seats >= wanted ? "" : " too few seats"))}");
        }
        return failed;
    }

    /// <summary>
    /// The aircraft (#421): from each pilot's eye a fan of rays forward (±30° across, −3..+6° up) must
    /// leave through the windscreen, past every opaque surface of the drawn model (the skin, the
    /// lining, the hump, the panel), not into the inside of the fuselage. The straight-ahead ray must
    /// be clear and at least 60 % of the fan. The AN-124's flight engineer is reported, not judged
    /// (his station faces the side wall).
    /// </summary>
    private static int CheckAircraft()
    {
        int failed = 0;
        foreach (var kind in Airliner.Kinds)
        {
            var rig = kind switch
            {
                RideKind.Freighter => AirlinerRig.CreateFreighter(),
                RideKind.An124 => AirlinerRig.CreateAn124(),
                _ => AirlinerRig.CreateA320(Colors.White),
            };
            var tris = OpaqueTriangles(rig);
            // the pilot's hands on the stick or yoke and the levers, the feet on the pedals, over their travel (#421)
            var (stick, levers, feet) = rig.Cockpit?.PilotReach() ?? (1f, 1f, 1f);
            bool fits = Mathf.Max(stick, Mathf.Max(levers, feet)) < 0.01f;
            if (!fits) failed++;
            GD.Print($"[cockpitcheck] {kind,-10} pilot falls short by: stick/yoke {stick * 1000f:F0} mm, thrust levers {levers * 1000f:F0} mm, "
                + $"pedals {feet * 1000f:F0} mm  {(fits ? "ok" : "FAIL the hands or feet do not reach the controls")}");
            rig.Free();
            var seats = Airliner.For(kind)!.Seats;
            int crew = kind == RideKind.An124 ? 3 : 2;
            for (int s = 0; s < crew; s++)
            {
                var seat = seats[s];
                var eye = SeatedFigure.Eye(seat);
                int clear = 0, rays = 0;
                bool ahead = false;
                var aheadAt = Vector3.Zero;
                string aheadBy = "";
                float nearest = float.MaxValue;
                foreach (float yaw in new[] { -30f, -15f, 0f, 15f, 30f })
                    foreach (float pitch in new[] { -3f, 2f, 6f })
                    {
                        var dir = new Basis(Vector3.Up, Mathf.DegToRad(yaw)) * new Basis(Vector3.Right, Mathf.DegToRad(pitch)) * Vector3.Forward;
                        float hit = FirstHit(tris, eye, dir, 12f);
                        rays++;
                        if (hit >= 12f) clear++;
                        else
                        {
                            nearest = Mathf.Min(nearest, hit);
                            if (Verbose) { var at = AircraftMeshBuilder.Flip(eye + dir * hit); GD.Print($"[cockpitcheck]   {kind} seat {s} ray {yaw:+0;-0;0}/{pitch:+0;-0;0} hits {OwnerOf(LastHit)} at ({at.X:F2}, {at.Y:F2}, {at.Z:F2})"); }
                        }
                        if (yaw == 0f && pitch == 2f)
                        {
                            ahead = hit >= 12f;
                            if (!ahead) { aheadAt = AircraftMeshBuilder.Flip(eye + dir * hit); aheadBy = OwnerOf(LastHit); }
                        }
                    }
                bool judged = s < 2;
                bool ok = !judged || ahead && clear >= rays * 0.6f;
                if (!ok) failed++;
                GD.Print($"[cockpitcheck] {kind,-10} seat {s} eye ({eye.X,5:F2}, {eye.Y,5:F2}, {eye.Z,6:F2})  windscreen {clear,2}/{rays} rays clear, "
                    + $"ahead {(ahead ? "clear" : $"blocked at authored ({aheadAt.X:F2}, {aheadAt.Y:F2}, {aheadAt.Z:F2}) by {aheadBy}")}, nearest hit {(nearest < 1e9f ? $"{nearest:F2} m" : "-")}  {(ok ? judged ? "ok" : "(reported)" : "FAIL view blocked")}");
            }
        }
        return failed;
    }

    /// <summary>Every opaque triangle of a rig's model in the rig's frame (glass surfaces left out).</summary>
    private static List<(Vector3 A, Vector3 B, Vector3 C)> OpaqueTriangles(Node3D rig)
    {
        var tris = new List<(Vector3, Vector3, Vector3)>();
        TriOwners.Clear();
        void Walk(Node n, Transform3D frame)
        {
            foreach (var child in n.GetChildren())
            {
                var t = child is Node3D n3 ? frame * n3.Transform : frame;
                if (child is MeshInstance3D { Mesh: ArrayMesh })
                    TriOwners.Add((tris.Count, child.Name));
                if (child is MeshInstance3D { Mesh: ArrayMesh mesh })
                    for (int i = 0; i < mesh.GetSurfaceCount(); i++)
                    {
                        if (mesh.SurfaceGetName(i) == MeshScratch.GlassSurface) continue;
                        var arrays = mesh.SurfaceGetArrays(i);
                        var v = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                        var idx = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                        if (idx.Length == 0) for (int k = 0; k + 2 < v.Length; k += 3) tris.Add((t * v[k], t * v[k + 1], t * v[k + 2]));
                        else for (int k = 0; k + 2 < idx.Length; k += 3) tris.Add((t * v[idx[k]], t * v[idx[k + 1]], t * v[idx[k + 2]]));
                    }
                Walk(child, t);
            }
        }
        Walk(rig, Transform3D.Identity);
        return tris;
    }

    private static readonly bool Verbose = Core.CmdArgs.Has("--verbose");
    private static int LastHit;
    private static readonly List<(int From, string Name)> TriOwners = new();
    private static string OwnerOf(int t) { string n = "?"; foreach (var (f, name) in TriOwners) if (t >= f) n = name; return n; }

    /// <summary>Distance along <paramref name="dir"/> to the first triangle (either face), or <paramref name="max"/>.</summary>
    private static float FirstHit(List<(Vector3 A, Vector3 B, Vector3 C)> tris, Vector3 from, Vector3 dir, float max)
    {
        float best = max;
        LastHit = -1;
        for (int ti = 0; ti < tris.Count; ti++)
        {
            var (a, b, c) = tris[ti];
            var e1 = b - a;
            var e2 = c - a;
            var p = dir.Cross(e2);
            float det = e1.Dot(p);
            if (Mathf.Abs(det) < 1e-9f) continue;
            float inv = 1f / det;
            var s = from - a;
            float u = s.Dot(p) * inv;
            if (u < 0f || u > 1f) continue;
            var q = s.Cross(e1);
            float v = dir.Dot(q) * inv;
            if (v < 0f || u + v > 1f) continue;
            float d = e2.Dot(q) * inv;
            if (d > 0.02f && d < best) { best = d; LastHit = ti; }
        }
        return best;
    }
}
