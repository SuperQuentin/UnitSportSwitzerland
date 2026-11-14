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
        if (!await Until(() => im.SpaceOf(id).Length > 0, 300)) { Fail($"the bot never went into a building (space \"{im.SpaceOf(id)}\")"); return; }
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
}
