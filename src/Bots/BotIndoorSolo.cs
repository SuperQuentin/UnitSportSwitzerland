using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Bots;

/// <summary>
/// <c>--botindoorsolo [--botindoorshot DIR] --bots 1 --botsat E,N --botsindoors</c> (offline, <c>tools/botindoorsolo.sh</c>):
/// bots run offline as on a dedicated server, and one goes into a building and back out, as <see cref="BotIndoorProbe"/>
/// checks it on a server. Offline the game is its own server: the bot's body runs here, the interior's bot collision and
/// walkable surface are built here (<see cref="InteriorManager.BotWantIn"/>), and its space is set here. Checks: the
/// bot's space becomes a building's, its body is in the interiors' space standing and walking, then it is outside again
/// on the street. With a window and <c>--botindoorshot</c>, the player keeps 5 m behind the bot and the frames before it
/// steps through the door are saved as PNG. RESULT ok or FAILED.
/// </summary>
public partial class BotIndoorSolo : Node
{
    public static bool Requested => CmdArgs.Has("--botindoorsolo");

    private readonly Func<FootPlayer?> _me;
    private readonly Action _onFoot;
    private int _failures;
    private const string Log = "[botindoorsolo]";

    /// <param name="me">The player on foot, else null.</param>
    /// <param name="onFoot">Out of the free camera a command-line game starts in, onto the ground (the game's own toggle).</param>
    public BotIndoorSolo(Func<FootPlayer?> me, Action onFoot)
    {
        _me = me;
        _onFoot = onFoot;
        Name = "BotIndoorSolo";
    }

    public override async void _Ready()
    {
        try { await Run(); }
        catch (Exception e) { Expect(false, $"threw: {e.Message}"); }
        GD.Print(_failures == 0 ? $"{Log} RESULT: ok" : $"{Log} RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task Run()
    {
        Expect(BotDirector.Instance is { Local: not null }, "offline has a bot director, the player its human");
        // the bot on its ground first (the tiles round --at in), then the player onto it, out of the free camera
        if (!await Until(() => BotDirector.Instance?.All.FirstOrDefault() is { } b && b.Body.IsOnFloor(), 300)) { Expect(false, "a bot stands on the ground"); return; }
        _onFoot();
        if (!await Until(() => _me() is { } me && me.IsOnFloor(), 60)) { Expect(false, "the player stands on the ground"); return; }
        var bot = BotDirector.Instance!.All.First();
        var body = bot.Body;
        var im = InteriorManager.Instance!;
        GD.Print($"{Log} bot {body.Name} \"{bot.Profile.Name}\" (id {bot.Id})");
        // the player counts as the human near it: the bot thinks at its own pace, not the far one
        await Seconds(1.0);
        Expect(bot.Watched, "the player is the human watching the bot (it thinks at its own pace)");

        if (ShotDir != null) _ = Follow(body, bot.Id);
        if (!await Until(() => im.SpaceOf(bot.Id).Length > 0, 300)) { Expect(false, "the bot went into a building"); return; }
        if (ShotDir != null) SaveFrames();
        string plan = im.SpaceOf(bot.Id);
        Expect(true, $"the bot's space is a building's: {plan}");
        bool indoors = await Until(() => GodotObject.IsInstanceValid(body) && InteriorManager.InInteriorSpace(body.GlobalPosition), 10);
        Expect(indoors, $"its body is in the interiors' space (y {body.GlobalPosition.Y:F0})");
        if (indoors)
        {
            float y0 = body.GlobalPosition.Y;
            await Seconds(3.0);
            Expect(Mathf.Abs(body.GlobalPosition.Y - y0) < 1.5f, $"it stands on the interior's floor (y {y0:F1} -> {body.GlobalPosition.Y:F1})");
            var at = body.GlobalPosition;
            bool walked = await Until(() => GodotObject.IsInstanceValid(body) && Flat(body.GlobalPosition - at) > 2f, 30);
            Expect(walked, $"it walks inside ({Flat(body.GlobalPosition - at):F1} m)");
        }
        if (!await Until(() => im.SpaceOf(bot.Id).Length == 0, 180)) { Expect(false, $"the bot came out of {plan}"); return; }
        bool outside = await Until(() => GodotObject.IsInstanceValid(body) && !InteriorManager.InInteriorSpace(body.GlobalPosition), 10);
        Expect(outside, "its body is back outside");
        if (outside)
        {
            bool grounded = await Until(() => body.Terrain?.TryGetSurface(body.GlobalPosition, out float g) == true
                && Mathf.Abs(body.GlobalPosition.Y - g) < 2f, 4);
            Expect(grounded, $"it stands on the street ({body.GlobalPosition.Y:F1} m)");
        }
    }

    // ---- pictures (as BotIndoorProbe's) ----

    private string? ShotDir => DisplayServer.GetName() != "headless" ? CmdArgs.Value("--botindoorshot") : null;
    private readonly System.Collections.Generic.Queue<Image> _frames = new();

    private async Task Follow(FootPlayer bot, long id)
    {
        var im = InteriorManager.Instance!;
        var last = bot.GlobalPosition;
        var heading = Vector3.Forward;
        while (GodotObject.IsInstanceValid(bot) && im.SpaceOf(id).Length == 0 && _me() is { } me)
        {
            var pos = bot.GlobalPosition;
            var moved = (pos - last) with { Y = 0 };
            if (moved.Length() > 0.3f) heading = moved.Normalized();
            last = pos;
            var at = pos - heading * 5f + heading.Cross(Vector3.Up) * 2f;
            if (me.Terrain?.TryGetSurface(at, out float g) == true) at.Y = g + 0.1f;
            var look = (pos + Vector3.Up * 1.1f - (at + Vector3.Up * 1.6f)).Normalized();
            float yaw = Mathf.Atan2(-look.X, -look.Z);
            me.LeaveInterior(at, yaw);
            me.LookYaw = yaw;
            me.LookPitch = Mathf.Asin(Mathf.Clamp(look.Y, -1f, 1f));
            me.Velocity = Vector3.Zero;
            await Seconds(0.25);
            _frames.Enqueue(GetViewport().GetTexture().GetImage());
            while (_frames.Count > 24) _frames.Dequeue();
        }
    }

    private void SaveFrames()
    {
        if (_frames.Count == 0) return;
        System.IO.Directory.CreateDirectory(ShotDir!);
        var frames = _frames.ToArray();
        for (int i = Math.Max(0, frames.Length - 12), n = 0; i < frames.Length; i += 2, n++)
        {
            string path = System.IO.Path.Combine(ShotDir!, $"solo_in_{n:00}.png");
            frames[i].SavePng(path);
            GD.Print($"{Log} wrote {path}");
        }
    }

    private void Expect(bool ok, string what)
    {
        GD.Print($"{Log} {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double waited = 0;
        while (!condition())
        {
            if (waited >= seconds) return false;
            await Seconds(0.2);
            waited += 0.2;
        }
        return true;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
}
