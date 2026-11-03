using System.Text.RegularExpressions;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The game reads the pointer mode as "a menu is open" or "in game", and on Android Godot's own
/// <c>Input.MouseMode</c> stops changing once a finger has touched the screen: it stayed Visible from
/// the menus and the touch controls never armed (#1020). So every read and write goes through
/// <c>Core.MouseCapture.Mode</c>, which remembers what the game asked for. Pure file-system logic, no
/// Godot — see docs/notes/core/touch-controls.md.
/// </summary>
public class MouseModeDisciplineTests
{
    private static readonly Regex RawMode = new(@"\bInput\.MouseMode\b(?!Enum)", RegexOptions.Compiled);
    private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Compiled | RegexOptions.Multiline);

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Every_pointer_mode_read_and_write_goes_through_MouseCapture()
    {
        string root = Root();
        string src = Path.Combine(root, "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == "src/Core/MouseCapture.cs") continue;
            if (RawMode.IsMatch(LineComment.Replace(File.ReadAllText(file), ""))) offenders.Add(relative);
        }

        Assert.True(offenders.Count == 0,
            "These files use Input.MouseMode directly. Use Core.MouseCapture.Mode (or MouseCapture.Capture()):\n"
            + "on Android, Input.MouseMode stops following what the game sets once the screen is touched (#1020).\n  "
            + string.Join("\n  ", offenders));
    }
}
