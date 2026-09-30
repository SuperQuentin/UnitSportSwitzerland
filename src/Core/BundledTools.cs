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
}
