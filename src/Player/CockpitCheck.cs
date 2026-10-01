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
        GD.Print(failed == 0 ? $"[cockpitcheck] RESULT: all {CarCatalog.All.Count} cars fit their driver"
            : $"[cockpitcheck] RESULT: FAILED — {failed} of {CarCatalog.All.Count} cars do not");
        return failed == 0 ? 0 : 1;
    }
}
