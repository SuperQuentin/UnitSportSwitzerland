using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// A <c>--world flat</c> row of <c>tools/lib/checkmap.txt</c> boots <c>Core/TestWorld</c>, which runs only the probes
/// it lists; any other check there fails in two seconds, every run (#1702: <c>--cockpitcheck --world flat</c> did,
/// since #1492, while the plain row passed). So the class that defines a flat row's flag must appear in
/// <c>TestWorld.cs</c>. Pure file-system logic, no Godot — docs/notes/general/test-systems-optin.md.
/// </summary>
public class CheckMapWorldTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Every_flat_world_row_names_a_probe_TestWorld_runs()
    {
        string root = Root();
        string testWorld = File.ReadAllText(Path.Combine(root, "src", "Core", "TestWorld.cs"));
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (Class: Path.GetFileNameWithoutExtension(f).Split('.')[0], Text: File.ReadAllText(f)))
            .ToList();

        var flags = File.ReadAllLines(Path.Combine(root, "tools", "lib", "checkmap.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 3 && p[2].StartsWith("--") && p[2].Contains("--world flat"))
            .Select(p => p[2].Split(' ')[0])
            .Distinct()
            .ToList();
        Assert.NotEmpty(flags);

        var offenders = new List<string>();
        foreach (string flag in flags)
        {
            string quoted = $"\"{flag}\"";
            var classes = sources.Where(s => s.Text.Contains(quoted) && s.Class is not ("GameShell" or "CmdArgs"))
                .Select(s => s.Class).Distinct().ToList();
            bool ran = classes.Any(c => System.Text.RegularExpressions.Regex.IsMatch(testWorld, $@"\b{c}\b"));
            if (!ran) offenders.Add($"{flag} (defined in {(classes.Count == 0 ? "no src file" : string.Join(", ", classes))})");
        }
        Assert.True(offenders.Count == 0,
            "checkmap.txt rows on --world flat whose probe TestWorld does not run (they fail every time; drop the flag, "
            + "or make the probe flat-capable and list it in TestWorld._Ready):\n" + string.Join('\n', offenders));
    }
}
