using System.Text.RegularExpressions;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Every prompt of a thing E acts on names what to outline (#1286): a cinema's booth panel was not
/// intuitive because nothing showed what E would act on, while a car's door had its border. The prompt's
/// words and the outline come from one call, an <c>Items.Interaction</c> (text and target), so the two
/// never disagree.
///
/// <para>
/// So under <c>src/</c> every interact prompt (<c>InputHints.Prompt(PlayerInput.InteractMount, …)</c>, a
/// line built on <c>InputHints.Tag(PlayerInput.InteractMount)</c>, a prompt bar row
/// <c>yield return (PlayerInput.InteractMount, …)</c>) is made in a method that returns an
/// <c>Interaction</c> (or in a helper building only the words, called only from such methods; or a bar row
/// taking an <c>Interaction</c>'s <c>.Text</c>), or is listed below (<c>file:Method</c>, how many, why): a row for something
/// outlined by its own pointer (a world item, a vehicle's door), or an act with no thing to outline (the
/// dance, letting go). A new prompt fails here until it names its target or is listed. Pure source scan, no Godot.
/// </para>
/// </summary>
public class InteractOutlineTests
{
    private static readonly Regex InteractPrompt = new(
        @"InputHints\.Prompt\(\s*(Core\.)?PlayerInput\.InteractMount\b|InputHints\.Tag\(\s*(Core\.)?PlayerInput\.InteractMount\s*\)"
        + @"|yield\s+return\s*\(\s*(Core\.)?PlayerInput\.InteractMount\b",
        RegexOptions.Compiled);

    /// <summary>A prompt bar row whose words are an <c>Interaction</c>'s: its target was named with them.</summary>
    private static readonly Regex RowOfInteraction = new(
        @"\Gyield\s+return\s*\(\s*(Core\.)?PlayerInput\.InteractMount\s*,\s*\w+\.Text\s*\)", RegexOptions.Compiled);

    private static readonly Regex Comment = new(@"//.*?$|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);

    // a method or a local function: what it returns, its name, its parameters, then a body or an arrow
    private static readonly Regex Method = new(
        @"(?<type>[\w.?<>\[\]]+(\s*<[^<>;{}]*>)?\??|\([^()]*\)\??)\s+(?<name>[A-Za-z_]\w*)\s*(<[^<>()]*>)?\s*\((?<args>[^()]*(\([^()]*\)[^()]*)*)\)\s*(where[^{;]*)?(?<open>\{|=>)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> NotTypes = new() { "return", "new", "else", "await", "yield", "throw", "in", "is", "case" };

    /// <summary>The prompts made outside an <c>Interaction</c> on purpose: how many in the method, and why.</summary>
    private static readonly Dictionary<string, (int Count, string Why)> Allowed = new()
    {
        ["src/Core/ClientWorld.cs:Prompts"] = (13,
            "the prompt bar: a world item and a radio (Highlight.Pointed outlines them), a vehicle's door (VehicleReach); "
            + "acts with no thing: the ski lift's wait and letting go, the crane's controls let go, a deck's hint, "
            + "standing up (three times: a stand, a deck, a cinema seat #1279), getting out, the dance (twice), the rope's let go / haul and the pot's seat (#1266)"),
        ["src/BattleRoyale/BrHud.cs:_Draw"] = (1, "the drop's jump from the plane: no thing"),
        ["src/Interiors/Pinball/PinballPlay.cs:UpdateHint"] = (2, "standing at a pinball machine: Start is the machine's own button, the machine is the whole view (#1325)"),
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

    /// <summary>Each method's span in the source (from its header to its body's end), its name and what it returns.</summary>
    private static List<(int From, int To, string Name, string Type)> Methods(string text)
    {
        var found = new List<(int, int, string, string)>();
        foreach (Match m in Method.Matches(text))
        {
            string type = m.Groups["type"].Value;
            if (NotTypes.Contains(type)) continue;
            int at = m.Groups["open"].Index, end;
            if (text[at] == '{')
            {
                int depth = 0;
                for (end = at; end < text.Length; end++)
                {
                    if (text[end] == '{') depth++;
                    else if (text[end] == '}' && --depth == 0) break;
                }
            }
            else
            {
                end = text.IndexOf(';', at);
                if (end < 0) end = text.Length;
            }
            found.Add((m.Index, end, m.Groups["name"].Value, type));
        }
        return found;
    }

    /// <summary>The innermost method round a point: a local function's own return type counts.</summary>
    private static (int From, int To, string Name, string Type)? Inner(List<(int From, int To, string Name, string Type)> methods, int at)
    {
        (int From, int To, string Name, string Type)? inner = null;
        foreach (var m in methods)
            if (m.From <= at && at <= m.To && (inner == null || m.From >= inner.Value.From)) inner = m;
        return inner;
    }

    private static bool Targeted((int From, int To, string Name, string Type) method) =>
        method.Type.Contains("Interaction", StringComparison.Ordinal);

    /// <summary>
    /// A helper that only builds the words (a prompt cached until it changes), called only from methods of
    /// the same file that return an <c>Interaction</c>: the words still come out with their target.
    /// </summary>
    private static bool OnlyCalledByTargeted(string text, List<(int From, int To, string Name, string Type)> methods,
        (int From, int To, string Name, string Type) helper)
    {
        int calls = 0;
        foreach (Match call in new Regex($@"(?<![\w]){Regex.Escape(helper.Name)}\s*\(").Matches(text))
        {
            if (call.Index >= helper.From && call.Index <= helper.To) continue;   // its own header (or a recursion)
            if (Inner(methods, call.Index) is not { } caller || !Targeted(caller)) return false;
            calls++;
        }
        return calls > 0;
    }

    /// <summary>Every interact prompt not made in a method returning an <c>Interaction</c>, counted by <c>file:Method</c>.</summary>
    private static Dictionary<string, int> Untargeted(string root)
    {
        var found = new Dictionary<string, int>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (IsHarness(relative)) continue;
            string text = Comment.Replace(File.ReadAllText(file), "");
            var prompts = InteractPrompt.Matches(text);
            if (prompts.Count == 0) continue;
            var methods = Methods(text);
            foreach (Match prompt in prompts)
            {
                if (RowOfInteraction.IsMatch(text, prompt.Index)) continue;
                var inner = Inner(methods, prompt.Index);
                if (inner is { } method && (Targeted(method) || OnlyCalledByTargeted(text, methods, method))) continue;
                string key = $"{relative}:{inner?.Name ?? "?"}";
                found[key] = found.GetValueOrDefault(key) + 1;
            }
        }
        return found;
    }

    [Fact]
    public void EveryInteractPromptNamesItsOutline()
    {
        var found = Untargeted(Root());
        var unlisted = found.Where(f => !Allowed.TryGetValue(f.Key, out var a) || f.Value > a.Count)
            .Select(f => $"{f.Key} ({f.Value})").ToList();
        Assert.True(unlisted.Count == 0,
            "interact prompts made without their outline target (return an Items.Interaction, text and target from one call, "
            + "or list the method with why): " + string.Join("; ", unlisted));
    }

    [Fact]
    public void TheListHoldsNoStaleEntries()
    {
        var found = Untargeted(Root());
        var stale = Allowed.Where(a => found.GetValueOrDefault(a.Key) < a.Value.Count)
            .Select(a => $"{a.Key} (listed {a.Value.Count}, found {found.GetValueOrDefault(a.Key)})").ToList();
        Assert.True(stale.Count == 0, "listed but no longer there (lower the count or drop the entry): " + string.Join("; ", stale));
    }

    [Fact]
    public void TheScanSeesAPromptOutsideAnInteraction()
    {
        // the scan itself: a string-returning prompt is caught, an Interaction-returning one is not
        const string bad = "public string? PromptFor(FootPlayer p) => InputHints.Prompt(PlayerInput.InteractMount, \"Open\");";
        const string good = "public Interaction? InteractionFor(FootPlayer p) => new(InputHints.Prompt(PlayerInput.InteractMount, \"Open\"), Outline.Of(null));";
        foreach (var (code, ok) in new[] { (bad, false), (good, true) })
        {
            var m = Methods(code).Single();
            Assert.Equal(ok, m.Type.Contains("Interaction", StringComparison.Ordinal));
            Assert.Single(InteractPrompt.Matches(code));
        }
    }
}
