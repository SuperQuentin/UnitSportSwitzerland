using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--ringcheck</c> (#1854), under <c>--frame earth --bodies DIR</c>: Saturn's ring chunks. The spectator is put in the
/// B ring's mid-plane: the chunks round it are made (hundreds drawn, some solid); a ray at the nearest chunk the field
/// names hits a collider where that chunk is; moved 400 m, a new field is made and that chunk is still at the same
/// place. Windowed, pictures among the chunks, edge-on from past the outer edge (its thickness), from 2 km over it and from afar
/// (<c>test_output/rings/</c>).
/// </summary>
public partial class RingCheck : Node
{
    public static bool Requested => CmdArgs.Has("--ringcheck");

    private readonly WorldOrigin _origin;
    private int _failed;

    public RingCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "RingCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ringcheck] {what}"));

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failed++;
            GD.PrintErr($"[ringcheck] FAIL {what}");
        }
    }

    private async Task Wait(double seconds)
    {
        double until = GameClock.Now + seconds;
        while (GameClock.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task<bool> Until(System.Func<bool> done, double seconds)
    {
        double until = GameClock.Now + seconds;
        while (!done() && GameClock.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        return done();
    }

    /// <summary>The camera at a body-fixed place of Saturn's (the origin on the ring's mid-plane there), looking along the ring.</summary>
    private void Put(Camera3D cam, OriginShifter shifter, double r, double z, float pitchDeg)
    {
        _origin.Rebase(BodyId.Saturn, SkyClock.Now);
        // the anchor's axes there: X along the orbit, Y up the pole (the ring's normal), Z outward (X × Y), origin on the
        // mid-plane: the ring is the world's horizontal plane
        var at = new EcefPos(r, 0, 0);
        var axes = new FrameAxes(at, new EcefPos(0, 1, 0), new EcefPos(0, 0, 1), new EcefPos(1, 0, 0)) { Body = BodyId.Saturn };
        shifter.ShiftTo(0, 0, exact: true, axes: axes);
        cam.MakeCurrent();
        cam.GlobalPosition = new Vector3(0, (float)z, 0);
        cam.Rotation = new Vector3(Mathf.DegToRad(-pitchDeg), 0, 0);
    }

    private async Task Run()
    {
        await Wait(3);
        var cam = GetTree().Root.FindChild("SpectatorCamera", recursive: true, owned: false) as Camera3D;
        if (cam == null || OriginShifter.Instance is not { } shifter) { Finish("no spectator camera or shifter"); return; }
        if (RingChunks.Active is not { } rings) { Finish("no ring chunks (--bodies with Saturn's pyramid)"); return; }

        // in the B ring's mid-plane, 100,000 km from Saturn's centre
        const double R = 100_000_000;
        Put(cam, shifter, R, 0, 0);
        bool built = await Until(() => rings.Built >= 1, 10);
        Expect(built && rings.Drawn > 200 && rings.Solid > 10, $"chunks made round the camera: {rings.Drawn} drawn, {rings.Solid} solid (field {rings.Built})");

        // the nearest chunk the field names, hit by a ray where it is
        var chunk = rings.Field.Near(R, 0, 0, RingChunks.ColliderReach * 0.6)
            .OrderBy(c => (c.X - R) * (c.X - R) + c.Y * c.Y + c.Z * c.Z).FirstOrDefault();
        if (_origin.Frame.Axes is not { } axes) { Finish("no anchor frame"); return; }
        var (cx, cy, cz) = axes.FromEcef(new EcefPos(chunk.X, chunk.Y, chunk.Z));
        var target = new Vector3((float)cx, (float)cy, (float)cz);
        await Wait(0.2);
        var space = cam.GetWorld3D().DirectSpaceState;
        var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(cam.GlobalPosition, target + (target - cam.GlobalPosition).Normalized() * (float)chunk.Radius * 2));
        float off = hit.Count > 0 ? ((Vector3)hit["position"]).DistanceTo(target) : float.NaN;
        Expect(hit.Count > 0 && off <= chunk.Radius * 1.05, string.Create(CultureInfo.InvariantCulture,
            $"a ray at the nearest chunk ({chunk.Radius:F1} m, {target.Length():F0} m away) hits it: {(hit.Count > 0 ? $"{off:F2} m from its centre" : "no hit")}"));
        Shot("among_chunks");

        // moved 400 m along the ring: a new field, the same chunk at the same place
        int before = rings.Built;
        cam.GlobalPosition += new Vector3(400, 0, 0);
        bool rebuilt = await Until(() => rings.Built > before, 10);
        Expect(rebuilt, $"a new field after 400 m (fields {before} → {rings.Built})");
        await Wait(0.2);
        if (_origin.Frame.Axes is { } axes2)
        {
            var (dx, dy, dz) = axes2.FromEcef(new EcefPos(chunk.X, chunk.Y, chunk.Z));
            var again = new Vector3((float)dx, (float)dy, (float)dz);
            var hit2 = cam.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(cam.GlobalPosition, again + (again - cam.GlobalPosition).Normalized() * (float)chunk.Radius * 2));
            float off2 = hit2.Count > 0 ? ((Vector3)hit2["position"]).DistanceTo(again) : float.NaN;
            Expect(hit2.Count > 0 && off2 <= chunk.Radius * 1.05, string.Create(CultureInfo.InvariantCulture,
                $"the same chunk in the new field, at the same place: {(hit2.Count > 0 ? $"{off2:F2} m from its centre" : "no hit")}"));
        }

        // its thickness edge-on from 3 km past its outer edge, its face from 2 km over it, and the whole ring from afar
        Put(cam, shifter, SaturnRings.Outer + 3_000, 0, 0);
        await Wait(1.5);
        Shot("edge_on");
        Put(cam, shifter, R, 2_000, 3);
        await Wait(1.5);
        Shot("over_2km");
        Put(cam, shifter, R, 40_000_000, 60);
        await Wait(1.5);
        Shot("afar");
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    private void Shot(string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        var path = ProjectSettings.GlobalizePath($"res://test_output/rings/{name}.png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"saved {path}");
    }

    private void Finish(string? fail)
    {
        if (fail == null) GD.Print("[ringcheck] RESULT: ok");
        else GD.PrintErr($"[ringcheck] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
