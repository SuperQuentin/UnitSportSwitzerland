using System.Text.RegularExpressions;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Nothing that moves rebuilds its mesh (#1202, docs/notes/general/no-mesh-rebuild-per-frame.md).
/// Every human figure used to be rebuilt and re-uploaded for every pose; 24 walking bots cost a
/// client 131 ms a frame, skinned 18 ms. A rebuild in a per-frame path looks harmless with one
/// thing moving and only shows with many.
///
/// <para>
/// So the per-frame methods under <c>src/</c> (<c>_Process</c>, <c>_PhysicsProcess</c>, <c>Step…</c>,
/// <c>Animate…</c>, <c>Apply…Pose</c>, <c>Steer…</c>), and every method of the same class they call
/// (partial files together, a few calls deep), must not build a mesh: no <c>new MeshScratch</c>,
/// <c>BuildInto</c>, <c>new ArrayMesh</c>, <c>ClearSurfaces</c>, <c>SurfaceBegin</c>,
/// <c>AddSurfaceFromArrays</c>, <c>SurfaceTool</c>, no <c>HumanMeshBuilder.Build…</c>/<c>Append…</c>
/// or other <c>…MeshBuilder.Build…</c>, no builder's mesh put on a node's <c>Mesh</c>. A method that
/// does on purpose is listed below (<c>file:Method</c>) with a reason. A build behind a call into
/// another class, or in a property set every frame, is not seen: the note's rule still holds there.
/// Pure source scan, no Godot.
/// </para>
/// </summary>
public class MeshRebuildDisciplineTests
{
    private static readonly Regex Builds = new(
        @"new\s+MeshScratch\b|\.BuildInto\(|new\s+ArrayMesh\b|\.ClearSurfaces\(|\.SurfaceBegin\(|AddSurfaceFromArrays\(|\bSurfaceTool\b"
        + @"|\bHumanMeshBuilder\.(Build|Append|DriverBody|DriverHead)\w*\(|\b\w+MeshBuilder\.Build\w*\("
        // a builder's mesh put on a node (a helm's driver per wheel turn, a ghost per frame)
        + @"|\.Mesh\s*=\s*[A-Z]\w*(MeshBuilder|Meshes)\.\w+\(",
        RegexOptions.Compiled);

    /// <summary>The methods a frame runs.</summary>
    private static readonly Regex PerFrame = new(@"^(_Process|_PhysicsProcess|Step\w*|Animate\w*|Apply\w*Pose|Steer\w*)$", RegexOptions.Compiled);

    private static readonly Regex Comment = new(@"//.*?$|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);
    private static readonly Regex Strings = new(@"@?""(?:[^""\\\n]|\\.)*""", RegexOptions.Compiled);

    // a method: its name, its parameter list, then a body or an arrow
    private static readonly Regex Method = new(
        @"(?<![\w.])(?<name>[A-Za-z_]\w*)\s*(<[^<>()]*>)?\s*\((?<args>[^()]*(\([^()]*\)[^()]*)*)\)\s*(where[^{;]*)?(?<open>\{|=>)",
        RegexOptions.Compiled);

    private static readonly Regex Call = new(@"(?<![\w.])(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    private static readonly HashSet<string> Keywords = new()
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "nameof", "typeof",
        "sizeof", "default", "when", "fixed", "checked", "unchecked", "base", "this", "stackalloc",
    };

    /// <summary>
    /// Why each file may build a mesh on a per-frame path. Kinds: <b>on change:</b> built only when
    /// what it is changes, behind a key; <b>cache:</b> a few steps, each built once and kept;
    /// <b>tool:</b> a developer tool or debug view, not the game; <b>live:</b> a line of a few
    /// vertices redrawn only while it is shown (tracked for a shader in #1203).
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        // ---- built when what it is changes ----
        ["src/Farming/FarmField.Draw.cs:Commit"] = "on change: a field chunk when its version, LOD, month or growth step changes, one commit a frame at most",
        ["src/Interiors/Bowling/BowlingAim.cs:UpdateCurve"] = "on change: the ball's curve on the lane when the spot, line or hook changes (#1323)",
        ["src/Gpx/TrackRibbon.cs:Rebuild"] = "on change: the ribbon when its resolved sample count changes",
        ["src/Interiors/InteriorManager.cs:BuildInterior"] = "on change: an interior when it is entered",
        ["src/Occasions/OccasionCreatures.cs:Mesh"] = "on change: a flock's mesh when it spawns",
        ["src/Terrain/ChunkManager.cs:StartBuild"] = "on change: a tile when it streams in, on a worker",
        ["src/Terrain/HorizonLayer.cs:Evaluate"] = "on change: a horizon block when the camera moves into new ground",
        // ---- a few steps, each built once ----
        ["src/Avatar/Cyclist.cs:UpdateLegs"] = "cache: a crank step (128 a turn), built once per bike look and shared",
        ["src/Build/BuildTool.cs:Step"] = "cache: the placing ghost shows a piece's mesh, built once per piece and material (StructureMeshes)",
        ["src/World/Snow/SlopeMarkers.cs:MeshOf"] = "cache: a marker per difficulty and side, built once",
        // ---- tools and debug views ----
        ["src/Avatar/AvatarPreview.cs:_Process"] = "tool: the avatar preview",
        ["src/Avatar/AvatarPreview.cs:UpdateDance"] = "tool: the avatar preview's dance sheet",
        ["src/Core/DebugOverlay.cs:Upload"] = "tool: the debug overlay, once a second while on",
        ["src/Terrain/LanePlanDebug.cs:Upload"] = "tool: the lane plan debug view",
        ["src/World/Traffic.Debug.cs:StepDebugDraw"] = "tool: the traffic debug lines",
        // ---- small lines while they show ----
        ["src/Items/Fishing/FishingVisuals.cs:_Process"] = "live: the fishing line (10 segments) while one is out, following the rod tip",
        ["src/Items/ThrowAim.cs:Step"] = "live: the throw's arc cleared when aiming stops (#1203)",
        ["src/Items/ThrowAim.cs:Draw"] = "live: the throw's arc while aiming (#1203)",
        ["src/Items/ThrowAim.cs:Ring"] = "live: the throw's landing ring while aiming (#1203)",
        ["src/Items/TrafficTool.cs:Draw"] = "live: the admin traffic tool's ghost while held (#1203)",
    };

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static bool IsHarness(string relative) =>
        relative.EndsWith("Probe.cs", StringComparison.Ordinal) || relative.EndsWith("Check.cs", StringComparison.Ordinal);

    /// <summary>A class's files: <c>Dir/Name.cs</c> and its partials <c>Dir/Name.Part.cs</c>.</summary>
    private static string ClassOf(string relative)
    {
        string dir = Path.GetDirectoryName(relative)!.Replace('\\', '/');
        string name = Path.GetFileName(relative);
        return dir + "/" + name[..name.IndexOf('.')];
    }

    /// <summary>Every method body of a source, by name (overloads merged), comments and strings out.</summary>
    private static Dictionary<string, List<string>> Bodies(string text)
    {
        var bodies = new Dictionary<string, List<string>>();
        foreach (Match m in Method.Matches(text))
        {
            string name = m.Groups["name"].Value;
            if (Keywords.Contains(name)) continue;
            int at = m.Groups["open"].Index;
            string body;
            if (text[at] == '{')
            {
                int depth = 0, i = at;
                for (; i < text.Length; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}' && --depth == 0) break;
                }
                body = text[at..Math.Min(i + 1, text.Length)];
            }
            else
            {
                int end = text.IndexOf(';', at);
                body = text[at..(end < 0 ? text.Length : end + 1)];
            }
            if (!bodies.TryGetValue(name, out var list)) bodies[name] = list = new List<string>();
            list.Add(body);
        }
        return bodies;
    }

    /// <summary>
    /// Every method (<c>file:Method</c>) that builds a mesh on a per-frame path (a per-frame method or
    /// a same-class callee of one), with the path and the build.
    /// </summary>
    private static Dictionary<string, string> Offenders(string root)
    {
        string src = Path.Combine(root, "src");
        var byClass = new Dictionary<string, List<string>>();
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsHarness(relative)) continue;
            string key = ClassOf(relative);
            if (!byClass.TryGetValue(key, out var files)) byClass[key] = files = new List<string>();
            files.Add(relative);
        }

        var found = new Dictionary<string, string>();
        foreach (var (_, files) in byClass)
        {
            var bodies = new Dictionary<string, List<string>>();
            var fileOf = new Dictionary<string, string>();
            foreach (string relative in files)
            {
                string text = Strings.Replace(Comment.Replace(File.ReadAllText(Path.Combine(root, relative)), ""), "\"\"");
                foreach (var (name, list) in Bodies(text))
                {
                    if (!bodies.TryGetValue(name, out var all)) bodies[name] = all = new List<string>();
                    all.AddRange(list);
                    fileOf.TryAdd(name, relative);
                }
            }
            foreach (string start in bodies.Keys.Where(n => PerFrame.IsMatch(n)).ToList())
            {
                // the per-frame method and what it calls in its own class, four calls deep
                var seen = new HashSet<string> { start };
                var frontier = new List<string> { start };
                for (int depth = 0; depth < 5 && frontier.Count > 0; depth++)
                {
                    var next = new List<string>();
                    foreach (string name in frontier)
                        foreach (string body in bodies[name])
                        {
                            var build = Builds.Match(body);
                            if (build.Success)
                            {
                                found.TryAdd($"{fileOf[name]}:{name}", $"{start}{(name == start ? "" : " -> " + name)}: {build.Value.Trim()}");
                            }
                            foreach (Match c in Call.Matches(body))
                            {
                                string callee = c.Groups["name"].Value;
                                if (bodies.ContainsKey(callee) && seen.Add(callee)) next.Add(callee);
                            }
                        }
                    frontier = next;
                }
            }
        }
        return found;
    }

    [Fact]
    public void No_per_frame_path_builds_a_mesh_without_a_stated_reason()
    {
        string root = Root();
        var offenders = Offenders(root).Where(e => !Allowed.ContainsKey(e.Key)).Select(e => $"{e.Key}: {e.Value}").OrderBy(s => s).ToList();
        Assert.True(offenders.Count == 0,
            "These per-frame paths build a mesh. Nothing that moves rebuilds its mesh: a figure is a\n"
            + "SkinnedFigure posed by its bones, a moving rigid part is a node (turned, scaled, sheared), cloth\n"
            + "and patterns go in a shader (docs/notes/general/no-mesh-rebuild-per-frame.md). A build that only\n"
            + "runs on change or fills a small cache: list file:Method in MeshRebuildDisciplineTests.Allowed with why.\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_allow_list_has_no_stale_entries()
    {
        string root = Root();
        var offenders = Offenders(root);
        var stale = Allowed.Keys.Where(k => !offenders.ContainsKey(k))
            .Select(k => File.Exists(Path.Combine(root, k[..k.LastIndexOf(':')])) ? $"{k}: no per-frame build there any more, drop the entry" : $"{k}: no such file")
            .ToList();
        Assert.True(stale.Count == 0, "Stale MeshRebuildDisciplineTests.Allowed entries:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Every_reason_names_its_kind()
    {
        var bad = Allowed.Where(e => !new[] { "on change:", "cache:", "tool:", "live:" }.Any(k => e.Value.StartsWith(k, StringComparison.Ordinal)))
            .Select(e => $"{e.Key}: \"{e.Value}\"").ToList();
        Assert.True(bad.Count == 0, "A reason starts with \"on change:\", \"cache:\", \"tool:\" or \"live:\":\n  " + string.Join("\n  ", bad));
    }
}
