using System.Text.Json;
using Godot;
using UnitSport.Items;

namespace UnitSport.Phone;

/// <summary>
/// The phone's photos (#889): the album's shots as the Photos app shows them, and which two are the
/// wallpapers (the lock screen on the cover, the home screen inside). The choice is kept beside the
/// album (<c>photos/phone.json</c>, so <c>--photo-dir</c> moves it with the shots); a shot is shown
/// whole from the frame it was printed from when that file is still there, else from the print's
/// picture without its white border.
/// </summary>
public static class PhoneWallpaper
{
    /// <summary>The two wallpapers, by photo id; null: the drawn gradient.</summary>
    public sealed record Choice(string? Lock = null, string? Home = null);

    private static readonly Dictionary<string, Texture2D> Full = new(), Square = new();

    private static string FilePath => Path.Combine(Path.GetDirectoryName(PhotoStore.LocalDir)!, "phone.json");

    public static Choice Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<Choice>(File.ReadAllText(FilePath)) is { } c)
                return c with { Lock = Valid(c.Lock), Home = Valid(c.Home) };
        }
        catch (Exception e) { GD.PushWarning($"[phone] cannot read {FilePath}: {e.Message}"); }
        return new Choice();

        static string? Valid(string? id) => id != null && PhotoStore.Has(id) ? id : null;
    }

    public static void Save(Choice choice)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        Core.JsonStore.Save(FilePath, choice, Core.JsonStore.Indented);
    }

    /// <summary>The shot square, as the print shows it (the gallery's tiles).</summary>
    public static Texture2D? SquareOf(string id)
    {
        if (Square.TryGetValue(id, out var t)) return t;
        if (PhotoStore.Texture(id) is not { } card) return null;
        var r = PhotoStore.ImageRect;
        var size = card.GetSize();
        return Square[id] = new AtlasTexture
        {
            Atlas = card,
            Region = new Rect2(r.X * size.X, r.Y * size.Y, (r.Z - r.X) * size.X, (r.W - r.Y) * size.Y),
        };
    }

    /// <summary>The whole shot: the frame it was printed from when that file is still there, else the square.</summary>
    public static Texture2D? FullOf(string id)
    {
        if (Full.TryGetValue(id, out var t)) return t;
        string? source = PhotoStore.Meta(id)?.Source;
        if (!string.IsNullOrEmpty(source))
        {
            string path = source.StartsWith("user://", StringComparison.Ordinal) || source.StartsWith("res://", StringComparison.Ordinal)
                ? ProjectSettings.GlobalizePath(source) : source;
            if (File.Exists(path))
            {
                var image = Image.LoadFromFile(path);
                if (image != null && !image.IsEmpty())
                {
                    // a wallpaper needs no more than a screen's worth
                    if (image.GetWidth() > 1600) image.Resize(1600, image.GetHeight() * 1600 / image.GetWidth(), Image.Interpolation.Bilinear);
                    return Full[id] = ImageTexture.CreateFromImage(image);
                }
            }
        }
        return SquareOf(id) is { } square ? Full[id] = square : null;
    }
}
