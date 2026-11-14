using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Net;

namespace UnitSport.World.Bodies;

/// <summary>
/// <c>--gotocheck</c> (#1850), under <c>--frame earth --chunks fixture:straight</c>: <c>/goto</c> through the chat line as a
/// player types it. The fly camera to the Apollo 15 site 5 km up, to Gale crater on Mars, back home over Bern 3 km up:
/// each time the anchor's body is the named one and the camera within 0.01° and 50 m of the place asked; a body that does
/// not exist moves nothing.
/// </summary>
public partial class GotoCheck : Node
{
    public static bool Requested => CmdArgs.Has("--gotocheck");

    private readonly WorldOrigin _origin;
    private int _failed;

    public GotoCheck(WorldOrigin origin)
    {
        _origin = origin;
        Name = "GotoCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print(string.Create(CultureInfo.InvariantCulture, $"[gotocheck] {what}"));

    private async Task Wait(double seconds)
    {
        double until = GameClock.Now + seconds;
        while (GameClock.Now < until) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private async Task Run()
    {
        await Wait(3);
        if (GetTree().Root.FindChild(ChatManager.NodeName, recursive: true, owned: false) is not ChatManager chat) { Finish("no chat"); return; }
        await Go(chat, "/goto moon 26.13 3.63 5", BodyId.Moon, 26.13, 3.63, 5_000);
        await Go(chat, "/goto Mars -4.6 137.4", BodyId.Mars, -4.6, 137.4, 30_000);
        await Go(chat, "/goto earth 46.95 7.45 3", BodyId.Earth, 46.95, 7.45, 3_000);
        // a body that does not exist: nothing moves
        var before = Camera()?.GlobalPosition ?? Vector3.Zero;
        chat.Send("/goto pluto 0 0");
        await Wait(0.5);
        Expect(_origin.Body == BodyId.Earth && (Camera()?.GlobalPosition ?? Vector3.Zero).DistanceTo(before) < 1f, "/goto pluto moves nothing");
        Finish(_failed == 0 ? null : $"{_failed} expectation(s) failed");
    }

    private Camera3D? Camera() => GetViewport()?.GetCamera3D();

    private async Task Go(ChatManager chat, string line, BodyId body, double lat, double lon, double height)
    {
        chat.Send(line);
        await Wait(1);
        var cam = Camera();
        if (cam == null || _origin.Frame.Axes is not { } axes) { Expect(false, $"{line}: no camera or anchor frame"); return; }
        var p = cam.GlobalPosition;
        var g = CelestialBody.Of(axes.Body).Shape.ToGeodetic(axes.ToEcef(p.X, p.Y, p.Z));
        bool there = axes.Body == body && System.Math.Abs(g.LatDeg - lat) < 0.01 && System.Math.Abs(System.Math.IEEERemainder(g.LonDeg - lon, 360)) < 0.01
            && System.Math.Abs(g.H - height) < 50;
        Expect(there, string.Create(CultureInfo.InvariantCulture, $"{line}: counted from {axes.Body}, at {g.LatDeg:F3}, {g.LonDeg:F3}, {g.H:F0} m"));
    }

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failed++;
            GD.PrintErr($"[gotocheck] FAIL {what}");
        }
    }

    private void Finish(string? fail)
    {
        if (fail == null) GD.Print("[gotocheck] RESULT: ok");
        else GD.PrintErr($"[gotocheck] RESULT: FAILED — {fail}");
        GetTree().Quit(fail == null ? 0 : 1);
    }
}
