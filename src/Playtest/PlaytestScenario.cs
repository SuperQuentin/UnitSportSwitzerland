using Godot;

namespace UnitSport.Playtest;

/// <summary>
/// Puts a scenario in the playtest suite (<c>--playtest</c>, docs/notes/general/playtest.md), which
/// finds these by reflection as the model viewer finds <c>[Showcase]</c>: tag the method, never edit
/// a list. On a static method with no parameters returning a <see cref="PlaytestScenario"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PlaytestScenarioAttribute(string id, string title, string category) : Attribute
{
    /// <summary>Stable id: the ledger file is <c>tests/playtests/&lt;id&gt;.json</c>. Never rename one that has results.</summary>
    public string Id { get; } = id;
    public string Title { get; } = title;
    /// <summary>The panel's group: "Collisions", "Movement", or "PR #N" for a feature check.</summary>
    public string Category { get; } = category;
}

/// <summary>
/// One thing a person checks by playing: set up in the running world, played, then judged
/// (Validate / Fail) in the playtest panel. Set up and torn down without a reload, so a whole batch
/// runs in one game.
/// </summary>
public sealed class PlaytestScenario
{
    /// <summary>What to do, in a few sentences: shown in the panel and sent to Claude.</summary>
    public required string Instructions { get; init; }

    /// <summary>What to look at before validating, one line each.</summary>
    public string[] Checklist { get; init; } = [];

    /// <summary>
    /// The files whose behaviour this scenario judges, as repo-relative globs (<c>src/Player/Car*.cs</c>,
    /// <c>src/Vehicles/**</c>). A verdict goes stale when any of them changes, so name the code the
    /// scenario is really about, not everything it touches on the way.
    /// </summary>
    public required string[] Covers { get; init; }

    /// <summary>The fixture course it needs (<c>junction</c>, <c>hairpin</c>...), or null for any open ground.</summary>
    public string? Course { get; init; }

    /// <summary>Knobs Claude may turn live (<c>set_param</c>), with their defaults; read them in <see cref="Setup"/> with <see cref="PlaytestContext.Param"/>.</summary>
    public Dictionary<string, double> Params { get; init; } = [];

    /// <summary>Builds the scene. Everything it spawns through the context is removed by the next scenario.</summary>
    public required Func<PlaytestContext, Task> Setup { get; init; }

    /// <summary>Runs every frame while the scenario is on (a bus kept at speed, a replay loop). Optional.</summary>
    public Action<PlaytestContext, double>? Tick { get; init; }
}

/// <summary>A discovered scenario: its tag and the factory that builds it.</summary>
public sealed record PlaytestEntry(string Id, string Title, string Category, Func<PlaytestScenario> Make, string Where)
{
    private PlaytestScenario? _built;

    /// <summary>Built once: the scenario's description does not change while the game runs.</summary>
    public PlaytestScenario Scenario => _built ??= Make();

    /// <summary>Every <c>[PlaytestScenario]</c> in the game assembly, ordered by category then id.</summary>
    public static List<PlaytestEntry> Discover()
    {
        var found = new List<PlaytestEntry>();
        var ids = new HashSet<string>();
        var types = typeof(PlaytestEntry).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("UnitSport", StringComparison.Ordinal) == true)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
        foreach (var type in types)
            foreach (var m in type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                         | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (m.GetCustomAttributes(typeof(PlaytestScenarioAttribute), false).FirstOrDefault() is not PlaytestScenarioAttribute tag) continue;
                string where = $"{type.Name}.{m.Name}";
                if (m.GetParameters().Length != 0 || m.ReturnType != typeof(PlaytestScenario))
                {
                    GD.PushError($"[playtest] [PlaytestScenario] on {where}: it must take no parameters and return a PlaytestScenario");
                    continue;
                }
                if (!ids.Add(tag.Id))
                {
                    GD.PushError($"[playtest] scenario id '{tag.Id}' is used twice ({where})");
                    continue;
                }
                found.Add(new PlaytestEntry(tag.Id, tag.Title, tag.Category, () => (PlaytestScenario)m.Invoke(null, null)!, where));
            }
        return found.OrderBy(e => e.Category, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }
}
