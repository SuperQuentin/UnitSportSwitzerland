namespace UnitSport.Core;

/// <summary>
/// Finds external tools (yt-dlp, ffmpeg): the copy shipped in <c>bin/</c> next to the game
/// executable wins, otherwise the bare name is returned and the OS looks it up on PATH.
/// </summary>
public static class BundledTools
{
    /// <summary>
    /// <c>bin/</c> beside the game executable. Not <see cref="AppContext.BaseDirectory"/>: an export
    /// keeps its assemblies in <c>data_*_x86_64/</c>, so that would miss the shipped tools.
    /// </summary>
    private static readonly string BinDir = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "bin");

    /// <summary>The macOS release ships Apple Silicon tools only: an Intel Mac uses PATH (Homebrew).</summary>
    private static readonly bool Usable = !OperatingSystem.IsMacOS()
        || System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

    public static string Resolve(string tool) => Bundled(tool) ?? tool;

    /// <summary>The shipped copy, made executable (an archive may have dropped the bit), or null.</summary>
    private static string? Bundled(string tool)
    {
        string path = Path.Combine(BinDir, OperatingSystem.IsWindows() ? tool + ".exe" : tool);
        if (!Usable || !File.Exists(path)) return null;
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute); }
            catch (Exception) { }   // read-only install: the archive's own bit has to do
        }
        return path;
    }

    /// <summary>
    /// yt-dlp needs a JavaScript runtime to solve YouTube's signature challenge, else many
    /// links answer 403. The release ships a tiny QuickJS in <c>bin/</c>; elsewhere yt-dlp
    /// finds deno on its own. Needs a native (backslash) path on Windows.
    /// </summary>
    public static string[] YtDlpJsArgs()
    {
        string? qjs = Bundled("qjs");
        return qjs != null ? new[] { "--js-runtimes", "quickjs:" + Path.GetFullPath(qjs) } : Array.Empty<string>();
    }
}
