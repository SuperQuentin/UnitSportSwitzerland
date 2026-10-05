// Delta updates (#532), see docs/notes/tools/delta-updates.md.
//   make  --from vA --to vB --old <dir> --new <dir> --out <file.delta> [--exec <path>]...
//   apply [--sh] <root> <vA> <vB> <file.delta> [<vB> <vC> <file.delta>]...
// apply stages the chain over <root> and runs the same swap script the game starts, then exits;
// --sh runs the Linux / macOS script (with the sh on PATH: Git Bash on Windows).
using System.Diagnostics;
using UnitSport.Core;

if (args.Length == 0) return Usage();
try
{
    return args[0] switch { "make" => Make(args[1..]), "apply" => Apply(args[1..]), _ => Usage() };
}
catch (Exception e) when (e is InvalidDataException or IOException)
{
    Console.Error.WriteLine($"deltagen: {e.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: deltagen make --from vA --to vB --old DIR --new DIR --out FILE [--exec PATH]...\n"
        + "       deltagen apply [--sh] ROOT vA vB FILE [vB vC FILE]...");
    return 2;
}

static int Make(string[] a)
{
    string? Opt(string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    string? from = Opt("--from"), to = Opt("--to"), oldDir = Opt("--old"), newDir = Opt("--new"), output = Opt("--out");
    if (from == null || to == null || oldDir == null || newDir == null || output == null) return Usage();
    var exec = new HashSet<string>(StringComparer.Ordinal);
    for (int i = 0; i + 1 < a.Length; i++) if (a[i] == "--exec") exec.Add(a[i + 1]);

    var clock = Stopwatch.StartNew();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    string tmp = output + ".tmp";
    UpdatePackage.Stats stats;
    using (var f = File.Create(tmp))
        stats = UpdatePackage.Create(oldDir, newDir, from, to, f, exec, Console.WriteLine);
    File.Move(tmp, output, overwrite: true);
    Console.WriteLine($"{from} -> {to}: {stats.Patched} patched, {stats.Added} added, {stats.Deleted} deleted, "
        + $"{stats.Unchanged} unchanged; {new FileInfo(output).Length:N0} bytes in {clock.Elapsed.TotalSeconds:0.0} s");
    return 0;
}

static int Apply(string[] a)
{
    bool sh = a.Length > 0 && a[0] == "--sh";
    if (sh) a = a[1..];
    if (a.Length < 4 || (a.Length - 1) % 3 != 0) return Usage();
    string root = Path.GetFullPath(a[0]);
    var staging = new UpdatePackage.Staging(root);
    try
    {
        for (int i = 1; i < a.Length; i += 3)
        {
            using var f = File.OpenRead(a[i + 2]);
            staging.Apply(f, a[i], a[i + 1]);
            Console.WriteLine($"staged {a[i]} -> {a[i + 1]}");
        }
        staging.Finish();
    }
    catch
    {
        Directory.Delete(staging.Dir, true);
        throw;
    }

    // the script waits for a process to end: give it one that already has
    bool windows = OperatingSystem.IsWindows() && !sh;
    using var gone = Process.Start(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/true", OperatingSystem.IsWindows() ? "/c exit" : "")!;
    gone.WaitForExit();
    string script = Path.Combine(staging.Dir, UpdateScript.FileName(windows));
    // Git Bash's sh wants /c/... paths
    string scriptRoot = sh && OperatingSystem.IsWindows() ? "/" + char.ToLowerInvariant(root[0]) + root[2..].Replace(Path.DirectorySeparatorChar, '/') : root;
    File.WriteAllText(script, UpdateScript.Text(windows, gone.Id, scriptRoot, null), new System.Text.UTF8Encoding(windows));
    var (program, args) = UpdateScript.Command(windows, script);
    if (sh) program = "sh";
    using var run = Process.Start(program, args)!;
    run.WaitForExit();
    string error = Path.Combine(root, "update-error.txt");
    if (run.ExitCode != 0 || File.Exists(error))
    {
        Console.Error.WriteLine($"swap script failed ({run.ExitCode}): {(File.Exists(error) ? File.ReadAllText(error) : "")}");
        return 1;
    }
    Console.WriteLine($"applied to {root}");
    return 0;
}
