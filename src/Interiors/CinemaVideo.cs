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

    /// <summary>
    /// Through the server (phase 2): JPEG frames this size and rate (about 10-15 kB each, 1.2-1.8 Mbit/s a watcher),
    /// scaled up to <see cref="Width"/> x <see cref="Height"/> on the client; the sound mono at this rate as 8-bit
    /// µ-law (192 kbit/s), in chunks of <see cref="RelayChunk"/> samples.
    /// </summary>
    public const int RelayWidth = 480, RelayHeight = 200, RelayFps = 15, RelaySoundRate = 24000, RelayChunk = 4800;

    /// <summary>How far ahead of the hall's moment the server sends, s: the round trip and a stall's room.</summary>
    public const double RelayAhead = 2.5;

    /// <summary>The relay's sample index of a moment of the video (video time × rate), and back.</summary>
    public static long RelaySampleAt(double videoTime) => (long)Math.Floor(videoTime * RelaySoundRate);

    /// <summary>
    /// A live source (phase 3: a screen, a window, a webcam; phase 4 a file, phase 5 NDI) is no link but a player's: the
    /// server writes it as <c>live:&lt;peer&gt;:&lt;label&gt;</c> in the hall's link, never taken from a player as a link
    /// (<see cref="Allowed"/> refuses the scheme), and every client watches it through the server.
    /// </summary>
    public const string LivePrefix = "live:";

    public static bool IsLive(string link) => link.StartsWith(LivePrefix, StringComparison.Ordinal);

    /// <summary>The hall's link for <paramref name="peer"/>'s live source, its label cut to printable and short.</summary>
    public static string LiveLink(long peer, string label)
    {
        var clean = new System.Text.StringBuilder();
        foreach (char c in label)
            if (c >= ' ' && c != 0x7f && clean.Length < 80) clean.Append(c);
        return $"{LivePrefix}{peer}:{clean}";
    }

    /// <summary>Whose a live link is (0 for none) and what it shows.</summary>
    public static (long Peer, string Label) LiveOf(string link)
    {
        if (!IsLive(link)) return (0, "");
        int colon = link.IndexOf(':', LivePrefix.Length);
        if (colon < 0 || !long.TryParse(link.AsSpan(LivePrefix.Length, colon - LivePrefix.Length), out long peer)) return (0, "");
        return (peer, link[(colon + 1)..]);
    }

    /// <summary>A live source's picture: the streamer's choice (owner, Oct 2026). Text on a shared screen wants High.</summary>
    public enum LiveQuality { Low, High, Smooth }

    public static (int Width, int Height, int Fps, int JpegQ) SizeOf(LiveQuality q) => q switch
    {
        LiveQuality.Low => (RelayWidth, RelayHeight, RelayFps, 7),
        LiveQuality.Smooth => (960, 400, 20, 6),
        _ => (960, 400, 10, 5),
    };

    /// <summary>
    /// How long after its capture a live frame or sample is shown, s, on every peer, the streamer included: the way up to
    /// the server and down to the hall, with room for a stall.
    /// </summary>
    public const double LiveDelay = 1.5;

    /// <summary>
    /// A stereo film's layout (#1298 phase 6), per hall: 2D, the two eyes side by side (left eye left), or one above the
    /// other (left eye on top). Each client decodes either into one picture with both eyes side by side, each fitted to the
    /// screen on its own (<see cref="StereoFit"/>), which the screen's shader shows per eye or as anaglyph.
    /// </summary>
    public enum Stereo : byte { Mono = 0, SideBySide = 1, TopBottom = 2 }

    /// <summary>The layout a link's name suggests (SBS, HSBS, LR, 3D; TB, OU, HOU, ABL as Blender's 3D releases), for the panel's default: 2D otherwise.</summary>
    public static Stereo Guess(string link)
    {
        string name = (Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(uri.AbsolutePath) : Path.GetFileName(link)).ToUpperInvariant();
        var words = name.Split(new[] { '.', '_', '-', ' ', '/', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w is "TB" or "OU" or "HOU" or "HTB" or "TAB" or "ABL" or "OVERUNDER")) return Stereo.TopBottom;
        if (words.Any(w => w is "SBS" or "SBSL" or "HSBS" or "FSBS" or "LR" or "3D" or "SIDEBYSIDE")) return Stereo.SideBySide;
        return Stereo.Mono;
    }

    /// <summary>The picture a client decodes to for a layout: twice as wide for a stereo film (both eyes side by side).</summary>
    public static (int Width, int Height) PackedSize(Stereo stereo, int width, int height) =>
        stereo == Stereo.Mono ? (width, height) : (width * 2, height);

    /// <summary>
    /// ffmpeg's filter fitting a picture into <paramref name="width"/> x <paramref name="height"/> with black bars: for a stereo
    /// film each eye cut out and fitted on its own, then the two side by side. A "half" layout (each eye squeezed into half
    /// the frame, the common kind) is stretched back: an eye narrower than 1.2:1 side by side, or wider than 2.5:1 one above
    /// the other, is doubled across or down; a full one (each eye its true shape) is kept.
    /// </summary>
    public static string StereoFit(Stereo stereo, int width, int height)
    {
        string fit = $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1";
        if (stereo == Stereo.Mono) return fit;
        string left, right, unsqueeze;
        if (stereo == Stereo.SideBySide)
        {
            left = "crop=iw/2:ih:0:0";
            right = "crop=iw/2:ih:iw/2:0";
            unsqueeze = "scale='if(lt(iw/ih,1.2),iw*2,iw)':ih";
        }
        else
        {
            left = "crop=iw:ih/2:0:0";
            right = "crop=iw:ih/2:0:ih/2";
            unsqueeze = "scale=iw:'if(gt(iw/ih,2.5),ih*2,ih)'";
        }
        return $"split[a][b];[a]{left},{unsqueeze},{fit}[l];[b]{right},{unsqueeze},{fit}[r];[l][r]hstack";
    }

    /// <summary>A film a hall can be showing when the cinema is first seen: its title, its link, its length in seconds.</summary>
    public readonly record struct Film(string Title, string Link, double Seconds);

    /// <summary>
    /// The open films every cinema can be showing (#1298, the owner's: "free open films"): Blender's open movies, CC-BY,
    /// from Blender's own YouTube channels (yt-dlp takes them at 480p, light for every client). Lengths from yt-dlp,
    /// Oct 2026. A server adds its own list (<c>/cinema pool</c>). Append only: the halls' picks are by index.
    /// </summary>
    public static readonly Film[] OpenFilms =
    {
        new("Sintel", "https://www.youtube.com/watch?v=eRsGyueVLvQ", 888),
        new("Tears of Steel", "https://www.youtube.com/watch?v=R6MlUcmOul8", 734),
        new("Spring", "https://www.youtube.com/watch?v=WhWc3b3KhnY", 464),
        new("Cosmos Laundromat", "https://www.youtube.com/watch?v=Y-rmzh0PI3c", 731),
        new("Agent 327: Operation Barbershop", "https://www.youtube.com/watch?v=mN0zPOpADL4", 232),
        new("Big Buck Bunny", "https://www.youtube.com/watch?v=aqz-KE-bpKQ", 635),
        new("Caminandes 3: Llamigos", "https://www.youtube.com/watch?v=SkVqJ1SGeL0", 150),
        new("Sprite Fright", "https://www.youtube.com/watch?v=_cMxraX_5RE", 630),
        new("Charge", "https://www.youtube.com/watch?v=UXqq0ZvbOnk", 263),
    };

    /// <summary>
    /// What a showing hall plays from its seed: -1 the cat slides (one entry among the films, the owner's), else the index
    /// of the film among <paramref name="films"/>.
    /// </summary>
    public static int PickShowing(uint seed, int films) => (int)(seed % (uint)(films + 1)) - 1;

    /// <summary>
    /// A looping film's start on the shared clock: the last start before <paramref name="now"/> of a film that starts
    /// every <paramref name="seconds"/> from <paramref name="phase"/>. The same on every peer at any moment, so a hall
    /// showing a film needs nothing sent; at the loop all peers start it again together.
    /// </summary>
    public static double LoopStart(double now, double seconds, double phase) =>
        seconds <= 0 ? phase : phase + Math.Floor((now - phase) / seconds) * seconds;

    /// <summary>
    /// The share of a frame's width its picture fills, as the screen's curtains frame it (#1298): its columns from the
    /// first to the last with a lit pixel on any of a few rows across it (<paramref name="lit"/>), within the first
    /// <paramref name="width"/> columns (a 3D frame's left eye); -1 for a frame lit nowhere (a fade, a black slate), which
    /// says nothing of the picture's shape.
    /// </summary>
    public static float ContentWidth(int width, int height, Func<int, int, bool> lit)
    {
        const int rows = 9;
        bool Column(int x)
        {
            for (int r = 1; r <= rows; r++)
                if (lit(x, r * height / (rows + 1))) return true;
            return false;
        }
        int left = 0;
        while (left < width && !Column(left)) left++;
        if (left >= width) return -1;
        int right = width - 1;
        while (right > left && !Column(right)) right--;
        return (right - left + 1) / (float)width;
    }

    /// <summary>
    /// A feed's picture width from its frames: the widest seen (a dark scene never narrows it), and only once frames worth
    /// <see cref="ShapeAfter"/> s were measured (a black start says nothing); 1 (the curtains open) until then.
    /// </summary>
    public sealed class Shape
    {
        public const double ShapeAfter = 1.5;
        private float _widest = -1;
        private int _frames;
        private readonly int _need;

        /// <summary>A shape measured <paramref name="perSecond"/> times a second.</summary>
        public Shape(int perSecond) => _need = (int)(ShapeAfter * perSecond);

        /// <summary>A frame's measure (<see cref="ContentWidth"/>); any thread, one writer.</summary>
        public void Add(float width)
        {
            if (width <= 0) return;
            _frames++;
            if (width > _widest) _widest = width;
        }

        public float Width => _frames >= _need && _widest > 0 ? _widest : 1f;
    }

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
    /// link is always the web's. Set by <see cref="CinemaBooths.Create"/> from the command line.
    /// </summary>
    public static bool AllowLocal { get; set; }

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
        if (IsLive(link)) return false;
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
    /// Whether a link goes through yt-dlp first (on each client): a site's page, or a stream's playlist (HLS
    /// <c>.m3u8</c>, DASH <c>.mpd</c>), whose renditions it picks the one near the screen's size from and says live or not.
    /// A plain file (mp4, webm) goes to ffmpeg as it is.
    /// </summary>
    public static bool NeedsLookup(string link)
    {
        if (IsSite(link)) return true;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        string path = uri.AbsolutePath.ToLowerInvariant();
        return path.EndsWith(".m3u8", StringComparison.Ordinal) || path.EndsWith(".mpd", StringComparison.Ordinal);
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
        if (IsLive(link)) return LiveOf(link).Label;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return Path.GetFileName(link);
        string last = uri.Segments.Length > 1 ? Uri.UnescapeDataString(uri.Segments[^1].TrimEnd('/')) : "";
        string host = uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host;
        if (IsSite(link) && uri.Query.Length > 0 && host.Contains("youtube")) return $"{host} {uri.Query.TrimStart('?')}";
        return last.Length > 0 ? $"{host}/{last}" : host;
    }
}
