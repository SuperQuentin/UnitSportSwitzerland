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
    public static int Run()
    {
        int failed = 0;
        GD.Print("[cockpitcheck] car                      shape      recline  eye (x, y, z)         head  ahead  behind  reach  pedals");
        foreach (var spec in CarCatalog.All)
        {
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
        int total = CarCatalog.All.Count + HeavyCatalog.All.Count;
        failed += heavyFailed;
        GD.Print(failed == 0 ? $"[cockpitcheck] RESULT: all {CarCatalog.All.Count} cars and {HeavyCatalog.All.Count} trucks and buses fit their driver"
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
}
