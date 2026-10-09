using Godot;

namespace UnitSport.Core;

/// <summary>
/// Which world systems this run boots (#221, <c>docs/notes/general/testing.md</c>). A test names
/// only what it needs and nothing else starts:
/// <list type="bullet">
/// <item><c>--systems a,b,c</c>: only these (names in <see cref="All"/>);</item>
/// <item><c>--world flat</c>: <see cref="TestWorld"/>, a flat plane and a fixed sun, no
/// <c>ChunkManager</c>, for the probes that support it; with no <c>--systems</c>, physics only;</item>
/// <item><c>--world fixture</c>: the client world on <c>--chunks fixture:&lt;course&gt;</c>
/// (<c>flat</c> by default), tiles built in code (<c>Terrain/Fixture</c>), no generated fill;</item>
/// <item><c>--world real</c> or no flag: everything, as players get it.</item>
/// </list>
/// Without <c>terrain</c> the client streams the flat fixture tiles instead of the map.
/// </summary>
public static class Systems
{
    public const string Terrain = "terrain", Generated = "generated", Traffic = "traffic", Trains = "trains",
        Npcs = "npcs", Birds = "birds", Physics = "physics", Audio = "audio", Network = "network", Sky = "sky",
        Interiors = "interiors", Loot = "loot", Occasions = "occasions", Ui = "ui", Build = "build",
        // the cars already parked in the car parks, and later the industrial yards' fleets (#499)
        Dormant = "dormant",
        Airports = "airports",
        // the farm fields, their state and the hand/machine farming on them (#494)
        Farming = "farming";

    public static readonly string[] All =
        { Terrain, Generated, Traffic, Trains, Npcs, Birds, Physics, Audio, Network, Sky, Interiors, Loot, Occasions, Ui, Build, Dormant, Airports, Farming };

    public enum WorldKind { Real, Fixture, Flat }

    private static readonly (WorldKind World, HashSet<string>? On) Parsed = Parse(OS.GetCmdlineUserArgs());

    public static WorldKind World => Parsed.World;

    /// <summary>True when this run boots <paramref name="system"/>: always, unless a test narrowed it.</summary>
    public static bool On(string system) => Parsed.On == null || Parsed.On.Contains(system);

    /// <summary>A test narrowed the systems (<c>--systems</c> or <c>--world flat|fixture</c>).</summary>
    public static bool Narrowed => Parsed.On != null;

    /// <summary>The fixture course to stream, or null for the real map: <c>--chunks fixture:x</c>, else flat when terrain is off.</summary>
    public static string? FixtureCourse
    {
        get
        {
            if (TerrainPaths.ParseChunkDirArg() is { } c && c.StartsWith(FixturePrefix)) return c[FixturePrefix.Length..];
            return World == WorldKind.Fixture || !On(Terrain) ? "flat" : null;
        }
    }

    public const string FixturePrefix = "fixture:";

    /// <summary>Pure, for the args given: the world kind and the systems on (null = all).</summary>
    public static (WorldKind World, HashSet<string>? On) Parse(string[] args)
    {
        var world = WorldKind.Real;
        HashSet<string>? on = null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--world")
                world = args[i + 1] switch { "flat" => WorldKind.Flat, "fixture" => WorldKind.Fixture, _ => WorldKind.Real };
            else if (args[i] == "--systems")
            {
                on = new HashSet<string>();
                foreach (string s in args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Array.IndexOf(All, s) < 0) GD.PushWarning($"[systems] unknown system '{s}' (known: {string.Join(',', All)})");
                    on.Add(s);
                }
            }
        }
        // a fixture world is a client world on code-built tiles: everything but the generated fill and the map
        if (world == WorldKind.Fixture) on ??= All.Where(s => s is not (Generated or Terrain)).ToHashSet();
        // a playtest stages its own vehicles: no ambient traffic, trains or parked fleets crossing the scene (#751)
        if (world == WorldKind.Fixture && Array.IndexOf(args, "--playtest") >= 0 && Array.IndexOf(args, "--systems") < 0)
            on!.ExceptWith(new[] { Traffic, Trains, Dormant });
        if (world == WorldKind.Flat) on ??= new HashSet<string> { Physics };
        if (on != null) GD.Print($"[systems] world {world.ToString().ToLowerInvariant()}, on: {(on.Count == 0 ? "none" : string.Join(',', on.Order()))}");
        return (world, on);
    }
}
