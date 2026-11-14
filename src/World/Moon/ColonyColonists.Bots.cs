using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Bots;
using UnitSport.Core;
using UnitSport.Net;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Moon;

/// <summary>
/// The crew as real bots (#1976): wherever a bot director runs (offline the game's own, #1974; a server hosting the
/// Moon later, C3b #1852), each colonist near the camera is a bot with a <see cref="ColonistBrain"/>, spawned through
/// <see cref="BotDirector.Add"/> and taken away again by <see cref="BotDirector.Remove"/>. The figures stay for where no
/// bot can run: a client of a server without the Moon, and a colony beyond <see cref="BotsNearM"/>, whose outside ground
/// (the body's 512 m square round the camera) a bot on an EVA would have nothing to stand on. A colonist asleep in their
/// bunk, or not yet up, has no body; one leaves and comes back only while nobody sees it, as the figures did.
/// </summary>
public partial class ColonyColonists
{
    /// <summary>The camera within this of the colony's middle: its crew are bots, metres.</summary>
    public const float BotsNearM = 250;

    /// <summary>The camera beyond this: back to the figures (a margin, no flicker at the edge), metres.</summary>
    public const float BotsFarM = 450;

    /// <summary><c>--colonisteva</c> (the check's): the first colonist with an EVA spot goes out there by rover now.</summary>
    public static bool ForceEva = CmdArgs.Has("--colonisteva");

    public MoonColony Colony { get; }
    public ColonyPlan Plan { get; }

    private bool _botsOn;

    /// <summary>Whether the crew are bots now (else the figures).</summary>
    public bool BotsOn => _botsOn;

    /// <summary>The crew's clock: the simulation clock's seconds (plus the check's offset), the same on every peer.</summary>
    public double CrewClock => SimClock.SimAt(ClockSync.ServerNow) + ClockOffset;

    /// <summary>The colony's hour for the sleep shifts: the sky's UTC.</summary>
    public double Hour
    {
        get
        {
            var utc = SkyClock.Now.Utc;
            return utc.Hour + utc.Minute / 60.0;
        }
    }

    /// <summary>The frame the colony's nodes are placed in (the bots' bodies may be in their own: <c>FromFrame</c>).</summary>
    public WorldOrigin? Origin => MoonSurface.Active?.Origin;

    /// <summary>The colony's airlocks (a sibling node).</summary>
    public ColonyAirlocks? Airlocks => GetParentOrNull<Node>()?.GetNodeOrNull<ColonyAirlocks>("Airlocks");

    /// <summary>A heading of the colony's frame (radians from north) as a world direction.</summary>
    public Vector3 Direction(double facing) => GlobalBasis * new Vector3(Mathf.Sin((float)facing), 0, -Mathf.Cos((float)facing));

    /// <summary>The bot running a colonist, for the checks.</summary>
    public Bot? BotOf(int member) => _members[member].Bot;

    public ColonistBrain? BrainOf(int member) => _members[member].Brain;

    // ---- the rovers a colonist has taken for an EVA: never two on one ------------------------------------------------

    private static readonly Dictionary<string, ColonistBrain> Rovers = new();

    public static bool RoverTaken(string name, ColonistBrain by) => Rovers.TryGetValue(name, out var b) && b != by;

    public static void TakeRover(string name, ColonistBrain by) => Rovers[name] = by;

    public static void ReleaseRovers(ColonistBrain by)
    {
        foreach (var (name, b) in new List<KeyValuePair<string, ColonistBrain>>(Rovers))
            if (b == by) Rovers.Remove(name);
    }

    // ---- the bots ----------------------------------------------------------------------------------------------------

    private void RunBots(Camera3D? cam)
    {
        bool can = BotDirector.Instance != null && Multiplayer.MultiplayerPeer != null && Multiplayer.IsServer()
            && MoonSurface.Active is { OnMoon: true } && Origin != null;
        float d = cam == null ? float.MaxValue : (cam.GlobalPosition - GlobalPosition) with { Y = 0 } is var flat ? flat.Length() : 0;
        bool want = can && (_botsOn ? d < BotsFarM : d < BotsNearM);
        if (want != _botsOn)
        {
            if (want)
            {
                _botsOn = true;
                GD.Print($"[colonists] {Colony.Id}: the crew are bots now ({d:F0} m)");
                foreach (var m in _members) Spawn(m, cam, first: true);
            }
            else
            {
                RemoveBots();
                GD.Print($"[colonists] {Colony.Id}: the crew are figures again ({d:F0} m)");
            }
            return;
        }
        if (!_botsOn) return;
        foreach (var m in _members)
        {
            if (m.Bot is { } bot)
            {
                if (!bot.Alive) { Forget(m); continue; }
                // asleep in the bunk: the body goes while nobody sees it
                if (m.Brain is { Asleep: true } && !Seen(cam, bot.Body.GlobalPosition)) Forget(m);
                continue;
            }
            Spawn(m, cam, first: false);
        }
    }

    /// <summary>A colonist awake and without a body: one at the spot their window has, while nobody sees it (or at once, on the first fill).</summary>
    private void Spawn(Member m, Camera3D? cam, bool first)
    {
        if (BotDirector.Instance is not { } dir || Origin is not { } origin) return;
        int spot;
        bool force = ForceEva && m.Who.Eva >= 0 && m.Who.Index == FirstEvaColonist();
        if (force) spot = m.Who.Work;
        else
        {
            double t = CrewClock;
            long w = (long)Math.Floor(t / ColonyCrew.Window);
            if (_crew.Plan(m.Who, w, Hour) is not { } plan) return;   // asleep: no body
            spot = plan.Spot;
        }
        if (spot < 0) return;
        var s = _crew.Spots[spot];
        var world = ToGlobal(new Vector3((float)s.X, (float)s.Y + 0.15f, (float)s.Z));
        if (!first && Seen(cam, world)) return;
        var taken = new HashSet<string>();
        foreach (var b in dir.All) taken.Add(b.Profile.Name);
        var bot = dir.Add(origin.ToGlobal(world), 0f, BotProfile.For(Colony.Seed, m.Who.Index, BotDifficulty.Easy, taken));
        if (bot == null) return;
        // a colonist, never one of the odd alien bots (#880): the bot's own seeded figure, in the suit
        if (Avatar.Aliens.IsAlienBot(bot.N))
        {
            bot.Body.AppearanceBits = 0;
            bot.Body.OutfitBits = 0;
        }
        var brain = new ColonistBrain(this, _members.IndexOf(m), spot) { ForceRoverEva = force };
        bot.Brain = brain;
        m.Bot = bot;
        m.Brain = brain;
    }

    private int FirstEvaColonist()
    {
        foreach (var m in _members) if (m.Who.Eva >= 0) return m.Who.Index;
        return -1;
    }

    private void Forget(Member m)
    {
        if (m.Brain != null) ReleaseRovers(m.Brain);
        if (m.Bot is { } bot && BotDirector.Instance is { } dir) dir.Remove(bot.Id);
        m.Bot = null;
        m.Brain = null;
    }

    private void RemoveBots()
    {
        foreach (var m in _members) Forget(m);
        _botsOn = false;
    }

    public override void _ExitTree()
    {
        if (_botsOn) RemoveBots();
    }
}
