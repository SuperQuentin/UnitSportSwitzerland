using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;

namespace UnitSport.Net;

/// <summary>
/// <c>--arrival check --expect-spawn E,N</c> with <c>--connect</c> and no <c>--at</c> (driven by
/// <c>tools/arrivalcheck.sh</c>, #799), against a server started with <c>--spawn E,N --no-teleport</c>:
/// the player must start where the server said, not where the client would have (Riddes); the client
/// must know it may not teleport; the server must refuse <c>/city</c> and <c>/stadium &lt;venue&gt;</c>
/// while still listing the venues and naming the nearest (#994); and a trip on the fly camera
/// must bring the body back where it stood, not under the camera.
/// </summary>
public partial class ArrivalProbe : ChatProbe
{
    public static string? Role => RoleArg("--arrival");

    public ArrivalProbe(ItemController items) : base(items, "arrival") { }
    public ArrivalProbe() : this(null!) { }

    public override async void _Ready()
    {
        _role = Role ?? "CHECK";
        if (SpawnPoint.ParseLv95("--expect-spawn") is not { } expect) { Fail("no --expect-spawn E,N"); return; }
        if (!await Joined(150)) return;

        var at = Me!.Global;
        double off = Math.Sqrt((at.E - expect.E) * (at.E - expect.E) + (at.N - expect.N) * (at.N - expect.N));
        Expect(off < 30, $"started at the server's spawn ({at.E:F0}/{at.N:F0}, {off:F0} m from {expect.E:F0}/{expect.N:F0})");
        Expect(!Permissions.CanTeleport, "the client knows it may not teleport");

        Chat!.Send("/city Riddes");
        Expect(await Until(() => _heard.Any(l => l.Contains("does not allow teleporting")), 10), "the server refused /city");

        // /stadium on the server (#994): the list and the nearest venue for anyone, the jump refused like /city
        Chat.Send("/stadium list");
        Expect(await Until(() => _heard.Any(l => l.StartsWith("Stadiums (")) && _heard.Any(l => l.StartsWith("Open airs (")), 10), "the server lists the venues");
        Chat.Send("/stadium");
        Expect(await Until(() => _heard.Any(l => l.StartsWith("Nearest venue: #8 Stade de Tourbillon (Sion)")), 10),
            $"the server names the nearest venue: {_heard.LastOrDefault(l => l.StartsWith("Nearest"))}");
        Chat.Send("/stadium wankdorf");
        Expect(await Until(() => _heard.Any(l => l.StartsWith("Stadion Wankdorf (Bern):"))
            && _heard.Count(l => l.Contains("does not allow teleporting")) == 2, 10), "the server says what plays at Wankdorf and refuses the jump");

        // up on the fly camera, 300 m away, and back: the body must still be where it stood
        if (GetParent() is ClientWorld world)
        {
            var before = Me.GlobalPosition;
            world.ToggleMode();
            await Seconds(0.5);
            if (GetViewport().GetCamera3D() is { } cam) cam.GlobalPosition += new Vector3(300, 50, 0);
            await Seconds(0.5);
            world.ToggleMode();
            await Seconds(2);
            var after = Me?.GlobalPosition ?? Vector3.Zero;
            float moved = new Vector2(after.X - before.X, after.Z - before.Z).Length();
            Expect(Me != null && moved < 5, $"back from the fly camera where it stood ({moved:F1} m away)");
        }
        else Expect(false, "the probe is not under the client world");

        await Finish(1.0);
    }
}
