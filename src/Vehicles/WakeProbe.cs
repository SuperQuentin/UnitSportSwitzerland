using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain;

namespace UnitSport.Vehicles;

/// <summary>
/// <c>godot --path . -- --wakecheck[,SHOT.png] [--wakekind car|boat|heavy|artic] [--at E,N]</c> (#560):
/// wakes the dormant slot of that kind nearest the spawn and checks the swap is seamless. The live
/// vehicle's sections, on the frame it appears and two seconds later, against the poses its dormant
/// copy was drawn at (<see cref="DormantVehicles.DrawnPoses"/>): a drop, a tilt or a lorry posed a
/// frame late shows as metres and degrees, headless. With a shot, the same view before, on the
/// first frame after and two seconds after (<c>_before</c>, <c>_after1</c>, <c>_after2s</c>), and
/// the mean pixel difference of each against the first.
///
/// <para>
/// The fixture courses hold car parks (<c>--chunks fixture:parking</c>) and jetties
/// (<c>fixture:lake</c>) but no industrial yard, so <c>heavy</c> runs on real terrain, near a depot.
/// </para>
/// </summary>
public partial class WakeProbe : Node
{
    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--wakecheck");

    private static string Kind => CmdArgs.Value("--wakekind", notFlag: true) ?? "car";

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private readonly string? _shot;
    private readonly List<string> _fail = new();

    public WakeProbe(ChunkManager chunks, WorldOrigin origin, string? shot)
    {
        Name = "WakeProbe";
        _chunks = chunks;
        _origin = origin;
        _shot = shot;
    }

    public override void _Ready() => _ = Run();

    private static bool IsBoat(VehicleSlot s) => s.KindId is (int)RideKind.Speedboat or (int)RideKind.Jetski;

    private static bool IsHeavy(VehicleSlot s) =>
        s.Train != 0 || s.KindId == (int)RideKind.Trailer || HeavyCatalog.For((RideKind)s.KindId) != null;

    private static bool Wanted(VehicleSlot s) => Kind switch
    {
        "boat" => IsBoat(s),
        "heavy" => IsHeavy(s),
        // a tractor or a rigid with a trailer coupled (and posed behind it: see Run)
        "artic" => s.Train != 0 && s.KindId != (int)RideKind.Trailer,
        _ => !IsBoat(s) && !IsHeavy(s),
    };

    private async Task Run()
    {
        // the kind of slot nearest the spawn (the streaming anchor), once the fleets round it are worked out
        VehicleSlot? found = null;
        double t0 = Time.GetTicksMsec() / 1000.0;
        while (found == null)
        {
            await Frame();
            if (Time.GetTicksMsec() / 1000.0 - t0 > 90) { Done($"no dormant {Kind} near the spawn"); return; }
            if (DormantVehicles.Instance is not { } d || _chunks.Anchors.Count == 0) continue;
            var here = _chunks.Anchors[0].GlobalPosition;
            found = d.Slots().Where(s => Wanted(s) && !d.IsAwake(s) && (Kind != "artic" || d.DrawnPoses(s).Length > 1))
                .OrderBy(s => _origin.ToWorld(s.E, s.N, s.Height).DistanceSquaredTo(here))
                .Cast<VehicleSlot?>().FirstOrDefault();
        }
        var slot = found.Value;
        var dormant = DormantVehicles.Instance!;
        var at = _origin.ToWorld(slot.E, slot.N, slot.Height);
        var side = new Basis(Vector3.Up, slot.Yaw) * Vector3.Right;
        GD.Print($"[wake] {Kind}: {slot.NodeName} (kind {(RideKind)slot.KindId}, train {slot.Train}) at LV95 {slot.E:F1},{slot.N:F1}, height {slot.Height:F2}");

        // a body beside it, so the ground under it has collision as it has for a player walking up
        var beside = at + side * 6f + Vector3.Up * 1.5f;
        var player = new FootPlayer { Name = "WakeProbeBody", Terrain = _chunks };
        AddChild(player);
        player.DebugLaunch(beside, Vector3.Zero);
        double c0 = Time.GetTicksMsec() / 1000.0;
        while (!_chunks.HasCollisionAt(at))
        {
            player.GlobalPosition = beside;
            player.Velocity = Vector3.Zero;
            await Frame();
            if (Time.GetTicksMsec() / 1000.0 - c0 > 60) { Done("no collision at the slot after 60 s"); return; }
        }
        await Seconds(2);

        Camera3D? camera = null;
        if (_shot != null)
        {
            var eye = at + side * 11f + Vector3.Up * 3.5f;
            camera = new Camera3D { Current = true };
            AddChild(camera);
            camera.GlobalTransform = new Transform3D(Basis.LookingAt(at + Vector3.Up * 1f - eye, Vector3.Up), eye);
            await Seconds(1.5);
        }

        var before = dormant.DrawnPoses(slot);
        Image? shotBefore = camera != null ? await Capture("_before") : null;

        dormant.Wake(slot);
        var vehicles = VehicleManager.Instance!;
        VehicleBody? live = null;
        double w0 = Time.GetTicksMsec() / 1000.0;
        while ((live = vehicles.GetNodeOrNull<VehicleBody>(slot.NodeName)) == null)
        {
            await Frame();
            if (Time.GetTicksMsec() / 1000.0 - w0 > 10) { Done($"{slot.NodeName} never woke"); return; }
        }
        Expect(dormant.IsAwake(slot), "the dormant copy is gone the frame the vehicle appears");

        // the first frame drawn with the live vehicle in it
        Image? shotAfter1 = camera != null ? await Capture("_after1") : null;
        if (camera == null) await Frame();
        Compare(before, live, "first frame", IsBoat(slot) ? 0.05f : 0.03f, 1.0f);

        await Seconds(2);
        Image? shotAfter2 = camera != null ? await Capture("_after2s") : null;
        // a boat rides the waves once it is live: it moves on purpose
        Compare(before, live, "2 s later", IsBoat(slot) ? 0.6f : 0.03f, IsBoat(slot) ? 8f : 1.0f);

        if (shotBefore != null)
        {
            GD.Print($"[wake] picture against before: first frame {Difference(shotBefore, shotAfter1!):F2} %, 2 s later {Difference(shotBefore, shotAfter2!):F2} % (mean of every pixel's channels)");
        }
        Done(null);
    }

    /// <summary>The live vehicle's sections against where the dormant copy drew them.</summary>
    private void Compare(Transform3D[] dormant, VehicleBody live, string when, float maxM, float maxDeg)
    {
        var sections = new List<Transform3D>();
        if (live.Visual is { } visual)
        {
            sections.Add(visual.GlobalTransform);
            for (int k = 1; k < dormant.Length; k++)
                if (visual.GetNodeOrNull<Node3D>($"Section{k}") is { } rig) sections.Add(rig.GlobalTransform);
        }
        else sections.Add(live.GlobalTransform);

        float worstM = 0f, worstDeg = 0f;
        for (int k = 0; k < Math.Min(sections.Count, dormant.Length); k++)
        {
            worstM = Mathf.Max(worstM, sections[k].Origin.DistanceTo(dormant[k].Origin));
            var q = (sections[k].Basis.Orthonormalized().GetRotationQuaternion().Inverse() * dormant[k].Basis.Orthonormalized().GetRotationQuaternion()).Normalized();
            worstDeg = Mathf.Max(worstDeg, Mathf.RadToDeg(q.GetAngle()));
        }
        GD.Print($"[wake] {when}: {sections.Count} of {dormant.Length} sections, {worstM:F3} m and {worstDeg:F2} deg off the dormant copy at most");
        for (int k = 0; k < Math.Min(sections.Count, dormant.Length); k++)
        {
            var d = sections[k].Origin - dormant[k].Origin;
            GD.Print($"[wake]   section {k}: live - dormant = {d.Y:+0.000;-0.000} m up, {new Vector2(d.X, d.Z).Length():F3} m across; body at {live.GlobalPosition.Y:F3}");
        }
        Expect(sections.Count == dormant.Length, $"{when}: every section drawn");
        Expect(worstM <= maxM && worstDeg <= maxDeg, $"{when}: within {maxM} m and {maxDeg} deg of the dormant copy");
    }

    private async Task<Image> Capture(string suffix)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GetViewport().GetTexture().GetImage();
        string file = _shot!.Replace(".png", suffix + ".png");
        GD.Print(image.SavePng(file) == Error.Ok ? $"[wake] wrote {file}" : $"[wake] could not write {file}");
        return image;
    }

    /// <summary>Mean absolute difference of two pictures, % of full scale.</summary>
    private static double Difference(Image a, Image b)
    {
        if (a.GetSize() != b.GetSize()) return 100;
        var pa = a.GetData();
        var pb = b.GetData();
        long sum = 0;
        int n = Math.Min(pa.Length, pb.Length);
        for (int i = 0; i < n; i++) sum += Math.Abs(pa[i] - pb[i]);
        return n == 0 ? 0 : sum * 100.0 / (n * 255.0);
    }

    private void Expect(bool ok, string what)
    {
        GD.Print($"[wake] {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _fail.Add(what);
    }

    private void Done(string? failure)
    {
        if (failure != null) _fail.Add(failure);
        GD.Print(_fail.Count == 0 ? "[wake] RESULT: ok" : $"[wake] RESULT: FAILED ({string.Join("; ", _fail)})");
        GetTree().Quit(_fail.Count == 0 ? 0 : 1);
    }

    private SignalAwaiter Frame() => ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Seconds(double s)
    {
        double end = GameClock.Now + s;
        while (GameClock.Now < end) await Frame();
    }
}
