using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using UnitSport.Core;

namespace UnitSport.Items;

/// <summary>Where and when a photo was taken; the sidecar <c>&lt;id&gt;.json</c> next to its print.</summary>
public sealed class PhotoMeta
{
    public string Id { get; set; } = "";
    public double E { get; set; }
    public double N { get; set; }
    public double Altitude { get; set; }
    /// <summary>Local date-time, ISO 8601 ("yyyy-MM-ddTHH:mm:ss"), culture-invariant.</summary>
    public string Taken { get; set; } = "";
    public float FocalMm { get; set; }
    /// <summary>The full-size frame in <c>user://photos</c> it was cropped from.</summary>
    public string Source { get; set; } = "";
    /// <summary>The photo spot it frames (#1227, <c>Adventure.Rewards.PhotoSpots</c>), or "".</summary>
    public string Spot { get; set; } = "";
}

/// <summary>
/// The Polaroid prints: a square centre crop of the frame on a white card, stored as a small JPEG
/// named by its content hash (<see cref="IdOf"/>), with a sidecar of where and when.
///
/// <para>
/// Three folders. <see cref="LocalDir"/> (<c>user://photos/polaroid</c>) is the archive of every
/// photo taken here — the album. <see cref="CacheDir"/> (<c>user://photo_cache</c>) holds other
/// players' prints fetched from the server (<see cref="PhotoTransfer"/>). The server keeps what
/// was uploaded in <c>user://placed/photos</c>. <c>--photo-dir &lt;abs&gt;</c> moves the first two,
/// so loopback checks can run several clients that do not share a disk. Docs:
/// <c>docs/notes/items/polaroid.md</c>.
/// </para>
/// </summary>
public static class PhotoStore
{
    /// <summary>The card, in pixels: Polaroid proportions (88 x 107 mm, a 79 mm square image).</summary>
    public const int CardW = 256, CardH = 304, ImageSize = 224, Border = 16;

    /// <summary>The card in the world, in metres.</summary>
    public static readonly Vector2 CardSize = new(0.088f, 0.088f * CardH / CardW);

    /// <summary>The image area in UV (u0, v0, u1, v1): outside it is the white frame.</summary>
    public static readonly Vector4 ImageRect = new(
        (float)Border / CardW, (float)Border / CardH, (float)(Border + ImageSize) / CardW, (float)(Border + ImageSize) / CardH);

    public static readonly Color Paper = new(0.96f, 0.95f, 0.91f);

    private static readonly Dictionary<string, ImageTexture> Textures = new();
    private static readonly Dictionary<string, PhotoMeta?> Metas = new();

    private static string? _root;

    /// <summary><c>--photo-dir</c>, globalized, or the project's user:// folder.</summary>
    private static string Root => _root ??= CmdArgs.Value("--photo-dir") ?? ProjectSettings.GlobalizePath("user://");

    public static string LocalDir => Path.Combine(Root, "photos", "polaroid");
    public static string CacheDir => Path.Combine(Root, "photo_cache");
    public static string ServerDir => ProjectSettings.GlobalizePath(Core.WorldChoice.SavePath("user://placed/photos"));

    /// <summary>A photo id: 16 lowercase hex characters, the start of the SHA-256 of the JPEG.</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: 16 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string IdOf(byte[] jpeg) => Convert.ToHexString(SHA256.HashData(jpeg), 0, 8).ToLowerInvariant();

    // ---- making a print -------------------------------------------------------------------------

    /// <summary>
    /// Makes the print from a full frame: centre square, scaled to <see cref="ImageSize"/>, on the
    /// white card, JPEG-encoded. Returns the card image too, for the develop animation. <paramref name="birthday"/>
    /// (#1423, a picture taken by a birthday cake) prints the birthday frame: confetti on the border, bunting in the margin.
    /// </summary>
    public static (byte[] Jpeg, Image Card) Print(Image frame, bool birthday = false)
    {
        var src = (Image)frame.Duplicate();
        if (src.IsCompressed()) src.Decompress();
        src.Convert(Image.Format.Rgb8);
        int w = src.GetWidth(), h = src.GetHeight(), side = Math.Min(w, h);
        var crop = src.GetRegion(new Rect2I((w - side) / 2, (h - side) / 2, side, side));
        crop.Resize(ImageSize, ImageSize, Image.Interpolation.Lanczos);

        var card = Image.CreateEmpty(CardW, CardH, false, Image.Format.Rgb8);
        card.Fill(Paper);
        card.BlitRect(crop, new Rect2I(0, 0, ImageSize, ImageSize), new Vector2I(Border, Border));
        if (birthday) BirthdayFrame(card);
        return (card.SaveJpgToBuffer(0.85f), card);
    }

    private static readonly Color[] PartyColours =
    [
        new(0.93f, 0.36f, 0.62f), new(0.25f, 0.55f, 0.92f), new(0.98f, 0.78f, 0.18f), new(0.35f, 0.75f, 0.42f), new(0.62f, 0.38f, 0.85f),
    ];

    /// <summary>
    /// The birthday frame (#1423), drawn on the white card around the picture: confetti scattered on the border (never on the
    /// picture), a string of bunting across the bottom margin. The same on every birthday print.
    /// </summary>
    private static void BirthdayFrame(Image card)
    {
        var rng = new Random(1423);
        var picture = new Rect2I(Border, Border, ImageSize, ImageSize);
        for (int placed = 0, tries = 0; placed < 70 && tries < 2000; tries++)
        {
            int x = rng.Next(1, CardW - 5), y = rng.Next(1, CardH - 5), size = 2 + rng.Next(3);
            var dot = new Rect2I(x, y, size, size);
            if (dot.Intersects(picture.Grow(1)) || y > Border + ImageSize + 4 && y < CardH - 14) continue;
            card.FillRect(dot, PartyColours[rng.Next(PartyColours.Length)]);
            placed++;
        }
        // the bunting: a sagging cord and pennants hanging from it, in the bottom margin
        int top = Border + ImageSize + 10;
        var cord = new Color(0.45f, 0.40f, 0.36f);
        const int Pennants = 11;
        float step = (CardW - 2f * Border) / Pennants;
        for (int x = Border; x < CardW - Border; x++)
        {
            float t = (x - Border) / (CardW - 2f * Border);
            card.SetPixel(x, top + (int)(6 * 4 * t * (1 - t)), cord);
        }
        for (int i = 0; i < Pennants; i++)
        {
            float cx = Border + (i + 0.5f) * step, t = (cx - Border) / (CardW - 2f * Border);
            int y0 = top + (int)(6 * 4 * t * (1 - t)) + 1;
            var colour = PartyColours[i % PartyColours.Length];
            for (int row = 0; row < 14; row++)
            {
                int half = (int)((step / 2 - 2) * (1 - row / 14f));
                if (half > 0) card.FillRect(new Rect2I((int)cx - half, y0 + row, half * 2, 1), colour);
            }
        }
    }

    /// <summary>Prints a frame, stores it in the archive with its sidecar and returns its id.</summary>
    public static string Save(Image frame, double e, double n, double altitude, float focalMm, string source, string spot = "", bool birthday = false)
    {
        var (jpeg, card) = Print(frame, birthday);
        string id = IdOf(jpeg);
        Directory.CreateDirectory(LocalDir);
        File.WriteAllBytes(Path.Combine(LocalDir, id + ".jpg"), jpeg);
        var meta = new PhotoMeta
        {
            Id = id, E = Math.Round(e, 1), N = Math.Round(n, 1), Altitude = Math.Round(altitude, 1),
            Taken = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            FocalMm = MathF.Round(focalMm), Source = source, Spot = spot,
        };
        // System.Text.Json writes numbers culture-invariantly
        Core.JsonStore.Save(Path.Combine(LocalDir, id + ".json"), meta, Core.JsonStore.Indented);
        Metas[id] = meta;
        Textures[id] = ImageTexture.CreateFromImage(card);
        return id;
    }

    // ---- reading --------------------------------------------------------------------------------

    /// <summary>The JPEG of a print this machine has (taken here or fetched), or null.</summary>
    public static string? PathOf(string id)
    {
        if (!IsValidId(id)) return null;
        foreach (var dir in new[] { LocalDir, CacheDir })
        {
            string p = Path.Combine(dir, id + ".jpg");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static bool Has(string id) => PathOf(id) != null;

    public static byte[]? Bytes(string id) => PathOf(id) is { } p ? File.ReadAllBytes(p) : null;

    /// <summary>The print as a texture (cached), or null if this machine does not have it (yet).</summary>
    public static ImageTexture? Texture(string? id)
    {
        if (id == null || !IsValidId(id)) return null;
        if (Textures.TryGetValue(id, out var t)) return t;
        if (Bytes(id) is not { } bytes) return null;
        var img = new Image();
        if (img.LoadJpgFromBuffer(bytes) != Error.Ok) return null;
        return Textures[id] = ImageTexture.CreateFromImage(img);
    }

    private static readonly Dictionary<string, ImageTexture> Thumbs = new();

    /// <summary>A 40 x 48 px print for the inventory slots (drawn unfiltered), or null.</summary>
    public static ImageTexture? Thumbnail(string? id)
    {
        if (id == null || !IsValidId(id)) return null;
        if (Thumbs.TryGetValue(id, out var t)) return t;
        if (Texture(id)?.GetImage() is not { } img) return null;
        img = (Image)img.Duplicate();
        img.Resize(40, 40 * CardH / CardW, Image.Interpolation.Lanczos);
        return Thumbs[id] = ImageTexture.CreateFromImage(img);
    }

    public static PhotoMeta? Meta(string id)
    {
        if (Metas.TryGetValue(id, out var m)) return m;
        try
        {
            string p = Path.Combine(LocalDir, id + ".json");
            m = File.Exists(p) ? JsonSerializer.Deserialize<PhotoMeta>(File.ReadAllText(p)) : null;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[photo] {id}.json: {ex.Message}");
            m = null;
        }
        return Metas[id] = m;
    }

    /// <summary>Every photo taken on this machine, newest first: the album's archive.</summary>
    public static List<string> Archive()
    {
        if (!Directory.Exists(LocalDir)) return new();
        return Directory.GetFiles(LocalDir, "*.jpg")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(IsValidId)
            .OrderByDescending(id => Meta(id)?.Taken ?? "")
            .ToList();
    }

    /// <summary>A print received from the server: kept if its bytes hash to its id.</summary>
    public static bool StoreFetched(string id, byte[] jpeg)
    {
        if (!IsValidId(id) || IdOf(jpeg) != id) return false;
        Directory.CreateDirectory(CacheDir);
        File.WriteAllBytes(Path.Combine(CacheDir, id + ".jpg"), jpeg);
        Textures.Remove(id);
        return true;
    }

    /// <summary>
    /// One line under a print. There is no place-name lookup in the game yet, so it is the LV95
    /// position, altitude, date and focal length; a fetched print without a sidecar says so.
    /// </summary>
    public static string Caption(string id)
    {
        if (Meta(id) is not { } m) return $"Photo {id[..Math.Min(8, id.Length)]}";
        string when = DateTime.TryParse(m.Taken, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) : m.Taken;
        return string.Format(CultureInfo.InvariantCulture, "E {0:0} N {1:0} · {2:0} m · {3} · {4:0} mm",
            m.E, m.N, m.Altitude, when, m.FocalMm);
    }

    public static string ShortCaption(string id)
    {
        if (Meta(id) is not { } m) return id[..Math.Min(8, id.Length)];
        string when = DateTime.TryParse(m.Taken, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("d MMM HH:mm", CultureInfo.InvariantCulture) : m.Taken;
        return string.Format(CultureInfo.InvariantCulture, "{0} · {1:0} m", when, m.Altitude);
    }
}
