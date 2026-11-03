using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace UnitSport.Tests;

/// <summary>
/// The global tiling's ratchet (#1443, docs/plans/global-tiling.md): the game's code that treats LV95 as
/// one flat plane in metres may only shrink while the seams of phase G0 are built. Each pattern is
/// counted over <c>src/</c> (comments and strings left out) and must not exceed its baseline: new code
/// uses <c>GlobalPos</c> (its distances, <c>SwissGeodesy</c> for latitude and longitude) and asks the
/// tile for its frame instead. A G0 change that removes some lowers the baseline in the same commit,
/// so the numbers below are the migration's progress. Pure source scan, no Godot.
/// </summary>
public class FlatLv95RatchetTests
{
    private readonly ITestOutputHelper _out;

    public FlatLv95RatchetTests(ITestOutputHelper output) => _out = output;

    private sealed record Pattern(string Name, Regex Find, int Baseline, string Instead);

    // counted on 2026-11-03 after G0a (#1446)
    private static readonly Pattern[] Patterns =
    {
        // de * de + dn * dn: an LV95 distance by hand; breaks when E/N stop being metres on a plane
        new("hand-written LV95 distance",
            new(@"\b(d[eE]\w*)\s*\*\s*\1\s*\+\s*(d[nN]\w*)\s*\*\s*\2\b", RegexOptions.Compiled),
            Baseline: 61, Instead: "GlobalPos.HorizontalDistanceTo / DistanceTo"),
        // swisstopo's approximate polynomials: 10 m off at Paris, 276 m at Brest
        new("SwissProjection in the game",
            new(@"\bSwissProjection\.", RegexOptions.Compiled),
            Baseline: 0, Instead: "SwissGeodesy.ToLv95 / ToLatLon"),
        // a tile's LV95 corners: the tile-local frame of the 1 km LV95 grid
        new("LV95 tile corner",
            new(@"\.(MinE|MaxE|MinN|MaxN)\b", RegexOptions.Compiled),
            Baseline: 626, Instead: "the tile's own frame (G0c)"),
        // a neighbour by LV95 kilometre arithmetic
        new("LV95 tile neighbour arithmetic",
            new(@"new\s+TileId\(\s*[\w.]+\s*[+-]", RegexOptions.Compiled),
            Baseline: 48, Instead: "a neighbour lookup on the tile key (G0c)"),
    };

    private static readonly Regex Comment = new(@"//.*?$|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);
    private static readonly Regex Strings = new(@"@?""(?:[^""\\\n]|\\.)*""", RegexOptions.Compiled);

    [Fact]
    public void Flat_LV95_arithmetic_in_the_game_never_grows()
    {
        string root = Root();
        var counts = new int[Patterns.Length];
        var where = new List<string>[Patterns.Length];
        for (int i = 0; i < Patterns.Length; i++) where[i] = new();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            // the seam itself: GlobalPos is where the distances live, WorldOrigin where a tile's frame is
            // placed (both change inside in G2)
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative is "src/Core/GlobalPos.cs" or "src/Core/WorldOrigin.cs") continue;
            string text = Strings.Replace(Comment.Replace(File.ReadAllText(file), ""), "\"\"");
            for (int i = 0; i < Patterns.Length; i++)
            {
                int n = Patterns[i].Find.Matches(text).Count;
                if (n == 0) continue;
                counts[i] += n;
                where[i].Add($"{relative} ({n})");
            }
        }

        var grown = new List<string>();
        for (int i = 0; i < Patterns.Length; i++)
        {
            var p = Patterns[i];
            _out.WriteLine($"{p.Name}: {counts[i]} (baseline {p.Baseline})");
            if (counts[i] > p.Baseline)
                grown.Add($"{p.Name}: {counts[i]} > baseline {p.Baseline}; use {p.Instead}. In: {string.Join(", ", where[i].Take(12))}");
            else if (counts[i] < p.Baseline)
                _out.WriteLine($"  fewer than the baseline: lower it to {counts[i]} in this commit");
        }
        Assert.True(grown.Count == 0, "Flat LV95 arithmetic grew (#1443):\n" + string.Join("\n", grown));
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
