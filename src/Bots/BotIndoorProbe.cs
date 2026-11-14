using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Bots;

/// <summary>
/// <c>--botindoorcheck A|B</c> with <c>--connect</c> (driven by <c>tools/botindoorcheck.sh</c>, #1330): a bot goes into a
/// building on a dedicated server and comes back out, seen from two clients. The server runs one bot with
/// <c>--botsindoors</c> beside a generated village: its every goal is a visit (<see cref="BotVisit"/>). Each client sees a
/// street door open, the bot's space become that building's (the server's broadcast, as for a player), the bot's body
/// indoors (under the ground, in the interior's space) and not falling, then the space go back to the street and the
/// body stand outside on the ground again.
/// </summary>
public partial class BotIndoorProbe : ChatProbe
{
    public static string? Role => RoleArg("--botindoorcheck");

    public BotIndoorProbe(ItemController items) : base(items, "botindoor", "BI", "botindoor_") { }
    public BotIndoorProbe() : this(null!) { }

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => InteriorManager.Instance != null)) return;
        await Run();
        await Finish(1.0);
    }

    private FootPlayer? Bot() => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => p.Bot);

    /// <summary>A bot body's id, from its node name (<c>npc_1_&lt;n&gt;</c>).</summary>
    private static long IdOf(FootPlayer bot) =>
        int.TryParse(bot.Name.ToString().Split('_').Last(), out int n) ? PlayerReplication.BotId(n) : 0;

    private async Task Run()
    {
        var im = InteriorManager.Instance!;
        if (!await Until(() => Bot() != null, 90)) { Fail("no bot came into view"); return; }
        var bot = Bot()!;
        long id = IdOf(bot);
        GD.Print($"{Log} bot {bot.Name} \"{bot.BotName}\" (id {id})");

        // in: the server says so, as it does for a player through a door
        if (ShotDir != null) _ = Follow(bot, id);
        if (!await Until(() => im.SpaceOf(id).Length > 0, 300)) { Fail($"the bot never went into a building (space \"{im.SpaceOf(id)}\")"); return; }
        if (ShotDir != null) SaveFrames();
        string plan = im.SpaceOf(id);
        GD.Print($"{Log} the bot is in {plan}");
        Expect(true, $"the bot's space is a building's: {plan}");
        // its body under the ground with the interiors, standing (the server built the interior's collision)
        bool indoors = await Until(() => GodotObject.IsInstanceValid(bot) && InteriorManager.InInteriorSpace(bot.GlobalPosition), 10);
        Expect(indoors, $"its body is in the interiors' space (y {bot.GlobalPosition.Y:F0})");
        if (indoors)
        {
            var y0 = bot.GlobalPosition.Y;
            await Seconds(3.0);
            Expect(GodotObject.IsInstanceValid(bot) && Mathf.Abs(bot.GlobalPosition.Y - y0) < 1.5f && InteriorManager.InInteriorSpace(bot.GlobalPosition),
                $"it stands on the interior's floor, not falling (y {y0:F1} -> {bot.GlobalPosition.Y:F1})");
            // walking about inside: rooms and doorways
            var at = bot.GlobalPosition;
            bool walked = await Until(() => GodotObject.IsInstanceValid(bot) && Flat(bot.GlobalPosition - at) > 2f, 30);
            Expect(walked, $"it walks inside ({(GodotObject.IsInstanceValid(bot) ? Flat(bot.GlobalPosition - at) : 0):F1} m)");
        }
        Say("in");
        if (!await Heard(_role == "A" ? "B" : "A", "in", 40)) Expect(false, "the other client saw it go in too");

        // out: the street again
        if (!await Until(() => im.SpaceOf(id).Length == 0, 180)) { Fail($"the bot never came out of {plan}"); return; }
        bool outside = await Until(() => GodotObject.IsInstanceValid(bot) && !InteriorManager.InInteriorSpace(bot.GlobalPosition), 10);
        Expect(outside, "its body is back outside");
        if (outside)
        {
            // standing on the street at some moment of its first seconds out (it strolls on from there)
            bool grounded = await Until(() => GodotObject.IsInstanceValid(bot) && Me?.Terrain?.TryGetSurface(bot.GlobalPosition, out float g) == true
                && Mathf.Abs(bot.GlobalPosition.Y - g) < 2f, 4);
            Expect(grounded, $"it stands on the street ({bot.GlobalPosition.Y:F1} m)");
        }
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>
    /// <c>--botindoorshot DIR</c> (client A, windowed): pictures of the bot walking in. The client's own body stays 5 m
    /// behind the bot and a little to the side, looking at it; the last frames are kept, and the ones before its space
    /// turned to the building's (the step through the door) are saved as PNG.
    /// </summary>
    private string? ShotDir => _role == "A" && DisplayServer.GetName() != "headless" ? CmdArgs.Value("--botindoorshot") : null;

    private readonly System.Collections.Generic.Queue<Image> _frames = new();
    private bool _followed;

    private async Task Follow(FootPlayer bot, long id)
    {
        var im = InteriorManager.Instance!;
        var last = bot.GlobalPosition;
        var heading = Vector3.Forward;
        while (GodotObject.IsInstanceValid(bot) && im.SpaceOf(id).Length == 0 && Me is { } me)
        {
            var pos = bot.GlobalPosition;
            var moved = (pos - last) with { Y = 0 };
            if (moved.Length() > 0.3f) heading = moved.Normalized();
            last = pos;
            // behind it and to its right, on the ground, looking at its chest
            var side = heading.Cross(Vector3.Up);
            var at = pos - heading * 5f + side * 2f;
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
            _followed = true;
        }
    }

    private void SaveFrames()
    {
        if (!_followed) return;
        System.IO.Directory.CreateDirectory(ShotDir!);
        var frames = _frames.ToArray();
        // the last moments before the crossing: every second frame of the last three seconds
        for (int i = Math.Max(0, frames.Length - 12), n = 0; i < frames.Length; i += 2, n++)
        {
            string path = System.IO.Path.Combine(ShotDir!, $"bot_in_{n:00}.png");
            frames[i].SavePng(path);
            GD.Print($"{Log} wrote {path}");
        }
    }
}
