using System.Text.Json;

namespace UnitSport.Core;

/// <summary>
/// The pure half of the update check (#532): reads GitHub's <c>releases/latest</c> answer, says
/// whether it is newer than this build and which of its assets this platform downloads. No Godot,
/// so tier-0 tests cover it; <c>Ui/UpdatePrompt</c> does the HTTP and the dialogs.
/// </summary>
public static class UpdateInfo
{
    public const string Repo = "SuperQuentin/UnitSportSwitzerland";
    public const string LatestUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

    public sealed record Asset(string Name, string Url, long Size);
    public sealed record Release(string Tag, string PageUrl, IReadOnlyList<Asset> Assets);

    /// <summary>"v1.2.3" or "1.2.3" as a <see cref="Version"/>; null when it is not one.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().TrimStart('v', 'V');
        int dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) text = text[..dash];
        return Version.TryParse(text, out var v) ? v : null;
    }

    /// <summary>True when <paramref name="latest"/> is a newer version than <paramref name="current"/>;
    /// false when either is not a version (a development build never asks).</summary>
    public static bool IsNewer(string? latest, string? current)
    {
        var l = ParseVersion(latest);
        var c = ParseVersion(current);
        return l != null && c != null && l > c;
    }

    /// <summary>The release in GitHub's JSON, or null when it has no tag (a drafts-only repo, an error body).</summary>
    public static Release? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag)
                || tag.GetString() is not { Length: > 0 } tagName) return null;
            string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            var assets = new List<Asset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var a in list.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long z) ? z : 0;
                    if (name != null && url != null) assets.Add(new Asset(name, url, size));
                }
            return new Release(tagName, page, assets);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The asset suffix <c>tools/release.sh</c> uploads for a Godot <c>OS.GetName()</c>.</summary>
    public static string? SuffixFor(string osName) => osName switch
    {
        "Windows" => "-windows.zip",
        "Linux" or "FreeBSD" => "-linux-x86_64.tar.gz",
        "macOS" => "-macos.tar.gz",
        _ => null,
    };

    /// <summary>This platform's download in <paramref name="release"/>, or null (then the release page).</summary>
    public static Asset? AssetFor(Release release, string osName) =>
        SuffixFor(osName) is { } suffix
            ? release.Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            : null;
}
