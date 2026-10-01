namespace UnitSport.Core;

/// <summary>
/// Finds external tools (yt-dlp, ffmpeg): the copy shipped in <c>bin/</c> next to the game
/// executable wins, otherwise the bare name is returned and the OS looks it up on PATH.
/// </summary>
public static class BundledTools
{
    public static string Resolve(string tool)
    {
        string file = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        string bundled = Path.Combine(AppContext.BaseDirectory, "bin", file);
        return File.Exists(bundled) ? bundled : tool;
    }

    /// <summary>
    /// yt-dlp needs a JavaScript runtime to solve YouTube's signature challenge, else many
    /// links answer 403. The release ships a tiny QuickJS in <c>bin/</c>; elsewhere yt-dlp
    /// finds deno on its own. Needs a native (backslash) path on Windows.
    /// </summary>
    public static string[] YtDlpJsArgs()
    {
        string qjs = Path.Combine(AppContext.BaseDirectory, "bin", OperatingSystem.IsWindows() ? "qjs.exe" : "qjs");
        return File.Exists(qjs) ? new[] { "--js-runtimes", "quickjs:" + Path.GetFullPath(qjs) } : Array.Empty<string>();
    }
}
