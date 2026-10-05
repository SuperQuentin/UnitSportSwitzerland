using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Every script and shader Godot imports must carry its <c>.uid</c> file in the repo (#503).
/// A script committed without one lets each worktree's import mint a different random UID for the
/// same path, which merges as an <c>add/add</c> conflict: 38 commits of the history are that
/// cleanup. Pure file-system logic, no Godot — see docs/notes/general/uid-files.md.
/// </summary>
public class UidFilesTests
{
    /// <summary>Extensions Godot gives a sibling <c>.uid</c> file. Scenes and resources carry
    /// their UID inside the file instead, so they are not listed.</summary>
    private static readonly string[] Imported = { ".cs", ".gd", ".gdshader", ".gdshaderinc" };

    /// <summary>The repo root, found by walking up to the directory holding project.godot.</summary>
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "project.godot"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Files under the Godot resource tree: a directory holding a .gdignore is skipped
    /// whole (tools/, tests/, docs/, terrain_chunks/, test_output/), as are dot directories.</summary>
    private static IEnumerable<string> Visible(string dir)
    {
        if (File.Exists(Path.Combine(dir, ".gdignore"))) yield break;
        foreach (var f in Directory.EnumerateFiles(dir)) yield return f;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.')) continue;
            foreach (var f in Visible(sub)) yield return f;
        }
    }

    [Fact]
    public void Every_imported_script_has_a_uid_file_and_every_uid_file_has_its_source()
    {
        var root = Root();
        var files = Visible(root).ToList();
        Assert.NotEmpty(files);

        var missing = files
            .Where(f => Imported.Contains(Path.GetExtension(f)) && !File.Exists(f + ".uid"))
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(p => p).ToList();

        var orphans = files
            .Where(f => Path.GetExtension(f) == ".uid" && !File.Exists(f[..^4]))
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(p => p).ToList();

        Assert.True(missing.Count == 0,
            $"{missing.Count} imported file(s) have no .uid beside them. Run the editor or "
            + $"`<godot> --headless --import --path .` and commit the .uid files with the script.\n"
            + "Two traps of that import run, neither visible in this failure "
            + "(docs/notes/general/uid-files.md): it mints OTHER features' .uid files too, so stage "
            + "only your own and never `git add -A`; and in a worktree whose Git LFS assets are still "
            + "pointers it rewrites the texture .import files to valid=false — `git checkout --` "
            + "those, do not commit them.\n  "
            + string.Join("\n  ", missing));

        Assert.True(orphans.Count == 0,
            $"{orphans.Count} .uid file(s) have no source file; delete them:\n  "
            + string.Join("\n  ", orphans));
    }
}
