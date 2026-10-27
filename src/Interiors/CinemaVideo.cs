namespace UnitSport.Interiors;

/// <summary>
/// A video on a cinema hall's screen (#1298), the rules every peer shares: which links may play, the size
/// a client decodes, and the clock. The server keeps per hall a link and <c>T0</c>, the shared-clock moment
/// Play was served (<see cref="CinemaBooths"/>); each desktop client decodes the link itself with its bundled
/// ffmpeg (<see cref="CinemaVideoStream"/>) and shows the frame due at <c>ServerNow − T0</c>, so everyone in
/// the hall sees the same frame. A live stream (no duration) plays at its live edge instead.
/// </summary>
public static class CinemaVideo
{
    /// <summary>
    /// What a client decodes: 2.39:1, the screen's own shape (a wider or narrower video is letterboxed into
    /// it by ffmpeg), at a frame rate any source converts to. About 1.2 MB a frame to upload, 37 MB/s.
    /// </summary>
    public const int Width = 854, Height = 358, Fps = 30;

    /// <summary>The sound, decoded by a second ffmpeg at the same moment: 48 kHz stereo.</summary>
    public const int AudioRate = 48000;

    /// <summary>The longest link the server takes.</summary>
    public const int MaxLink = 1000;

    /// <summary>
    /// How far ahead of the clock a client starts its decoder: the time ffmpeg takes to connect and seek
    /// (yt-dlp's lookup comes on top for a site's link). Frames earlier than due are held, so a start that
    /// is quick only waits; one slower than this restarts further ahead (<see cref="RestartLead"/>).
    /// </summary>
    public const double StartLead = 2.5, SiteLead = 1.5;

    /// <summary>A decoder this far behind the clock (a stall, a slow start) starts again ahead of it.</summary>
    public const double FallBehind = 2.0;

    /// <summary>The lead after a restart: a stream that fell behind once gets more room.</summary>
    public static double RestartLead(int restarts) => StartLead + Math.Min(restarts, 4) * 1.5;

    /// <summary>
    /// Local files for the checks only (<c>--cinemavideolocal</c>, on the server and the clients): a player's
    /// link is always the web's.
    /// </summary>
    public static readonly bool AllowLocal = Core.CmdArgs.Has("--cinemavideolocal");

    /// <summary>
    /// Whether a link may go on a screen: http(s), printable, not too long; a check's local file with
    /// <see cref="AllowLocal"/>. It is user input that every client hands to ffmpeg (as one argument, never a
    /// shell string), so nothing but the web: no <c>file:</c>, <c>concat:</c> or device a link could reach.
    /// </summary>
    public static bool Allowed(string link)
    {
        if (link.Length is 0 or > MaxLink) return false;
        foreach (char c in link)
            if (c < ' ' || c == 0x7f) return false;
        if (AllowLocal && File.Exists(link)) return true;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "https" or "http" && uri.Host.Length > 0;
    }

    /// <summary>A link of a video site that yt-dlp turns into the stream's own URLs (on each client): YouTube, Twitch, Vimeo.</summary>
    public static bool IsSite(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal) || host.StartsWith("m.", StringComparison.Ordinal))
            host = host[(host.IndexOf('.') + 1)..];
        return host is "youtube.com" or "youtu.be" or "music.youtube.com" or "twitch.tv" or "clips.twitch.tv"
            or "vimeo.com" or "player.vimeo.com" or "dailymotion.com";
    }

    /// <summary>
    /// yt-dlp's choice for a screen 358 px high: H.264 at most 480p (cheap to decode) and the best sound, else
    /// any video at most 480p, else what there is (a live stream offers one muxed rendition at each size).
    /// </summary>
    public const string SiteFormat = "bv*[height<=480][vcodec^=avc1]+ba/bv*[height<=480]+ba/b[height<=480]/b";

    /// <summary>Where in the video the hall is at shared-clock time <paramref name="now"/>, s: since Play.</summary>
    public static double PositionAt(double now, double t0) => Math.Max(0, now - t0);

    /// <summary>
    /// The decoded frame to show at <paramref name="position"/> of a decoder started at <paramref name="start"/>:
    /// frame k is at <c>start + k / Fps</c>; -1 before its first frame.
    /// </summary>
    public static long FrameAt(double position, double start) =>
        position < start ? -1 : (long)Math.Floor((position - start) * Fps + 1e-6);

    /// <summary>The sound sample due at <paramref name="position"/> of a decoder started at <paramref name="start"/>.</summary>
    public static long SampleAt(double position, double start) => (long)Math.Floor((position - start) * AudioRate);

    /// <summary>A short name for a link, for the panel and the log: the host and the last part of its path.</summary>
    public static string Title(string link)
    {
        if (link.Length == 0) return "";
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return Path.GetFileName(link);
        string last = uri.Segments.Length > 1 ? Uri.UnescapeDataString(uri.Segments[^1].TrimEnd('/')) : "";
        string host = uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host;
        if (IsSite(link) && uri.Query.Length > 0 && host.Contains("youtube")) return $"{host} {uri.Query.TrimStart('?')}";
        return last.Length > 0 ? $"{host}/{last}" : host;
    }
}
