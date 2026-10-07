using Godot;
using UnitSport.Core;

namespace UnitSport.Farming;

/// <summary>
/// <c>--buyercheck [shots]</c> (#494, real map: <c>--at E,N</c> at a specialty buyer, <c>--systems
/// ui,farming</c>): the nearest buyer's weighbridge office and sign go up within its yard, by an access
/// road of the map's data. With <c>shots</c> (windowed) it saves <c>test_output/494-buyer-&lt;key&gt;.png</c>
/// from the road and <c>-high.png</c> from above, where the factory shows round it.
/// </summary>
public partial class BuyerCheck : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--buyercheck") >= 0;
    private static bool Shots => Array.IndexOf(OS.GetCmdlineUserArgs(), "shots") > Array.IndexOf(OS.GetCmdlineUserArgs(), "--buyercheck")
                                 && DisplayServer.GetName() != "headless";

    private const string Tag = "[buyercheck]";
    private readonly WorldOrigin _origin;
    private int _failures;

    public BuyerCheck(WorldOrigin origin) => _origin = origin;
    public BuyerCheck() : this(null!) { }

    public override async void _Ready()
    {
        await Until(() => GetViewport().GetCamera3D() != null, 120);
        var (e, n) = _origin.ToLv95(GetViewport().GetCamera3D()!.GlobalPosition);
        var (b, km, _) = FarmBuyers.FromHere(e, n).First();
        GD.Print(FormattableString.Invariant($"{Tag} nearest buyer {b.Key} ({b.Name}, {b.Place}), {km * 1000:F0} m away"));
        var yards = GetParent().GetNodeOrNull<FarmBuyerYards>("FarmBuyerYards");
        Expect(await Until(() => yards?.YardOf(b.Key) != null, 120), "its office is built");
        if (yards?.YardOf(b.Key) is { } office)
        {
            var (oe, on) = _origin.ToLv95(office.GlobalPosition);
            double off = Math.Sqrt((oe - b.E) * (oe - b.E) + (on - b.N) * (on - b.N));
            Expect(off <= b.Reach, FormattableString.Invariant($"in the yard: {off:F0} m from the point (reach {b.Reach:F0} m)"));
            Expect(yards.ByRoad.Contains(b.Key), "by an access road of the map");
            // the terrain at the office's four corners: none above its floor, none below its 1.5 m plinth
            await Seconds(3);
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var c in new[] { new Vector3(-1.6f, 0, -1.3f), new Vector3(1.6f, 0, -1.3f), new Vector3(-1.6f, 0, 1.3f), new Vector3(1.6f, 0, 1.3f) })
                if (yards.GroundAt(office.GlobalTransform * c) is { } h) { lo = Math.Min(lo, h - office.GlobalPosition.Y); hi = Math.Max(hi, h - office.GlobalPosition.Y); }
            Expect(lo > -1.5f && hi < 0.3f, FormattableString.Invariant($"it stands on the ground (its plinth goes 1.5 m down): the corners {lo:F2}..{hi:F2} m from its floor"));
            if (Shots)
            {
                var cam = new Camera3D { Far = 4000 };
                AddChild(cam);
                cam.GlobalPosition = office.GlobalTransform * new Vector3(7f, 2.5f, 26f);
                cam.LookAt(office.GlobalTransform * new Vector3(2f, 3.5f, 0f), Vector3.Up);
                cam.MakeCurrent();
                await Seconds(1.5);
                GD.Print($"{Tag} shot {Shot($"494-buyer-{b.Key}")}");
                cam.GlobalPosition = office.GlobalTransform * new Vector3(0f, 110f, 45f);
                cam.LookAt(office.GlobalPosition, Vector3.Up);
                await Seconds(1.5);
                GD.Print($"{Tag} shot {Shot($"494-buyer-{b.Key}-high")}");
            }
        }
        GD.Print(_failures == 0 ? $"{Tag} RESULT: ok" : $"{Tag} RESULT: FAILED ({_failures})");
        await Seconds(0.5);
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private string Shot(string name)
    {
        System.IO.Directory.CreateDirectory(ProjectSettings.GlobalizePath("res://test_output"));
        string path = ProjectSettings.GlobalizePath($"res://test_output/{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        return path;
    }

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private void Expect(bool ok, string what)
    {
        GD.Print($"{Tag}   {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
