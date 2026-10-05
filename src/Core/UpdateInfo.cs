using System.Text.Json;

namespace UnitSport.Core;

/// <summary>
/// The pure half of the update check (#532): reads GitHub's list of releases, says whether the
/// latest is newer than this build and which of its assets this platform downloads: the full
/// archive, or the chain of delta files (<see cref="UpdatePackage"/>) from this version to the
/// latest. No Godot, so tier-0 tests cover it; <c>Ui/UpdatePrompt</c> does the HTTP and the dialogs.
/// </summary>
public static class UpdateInfo
{
    public const string Repo = "SuperQuentin/UnitSportSwitzerland";
    /// <summary>Every published release (the API's maximum page), newest first.</summary>
    public const string ReleasesUrl = $"https://api.github.com/repos/{Repo}/releases?per_page=100";
    /// <summary>Patch the install only when the deltas weigh less than this share of the full archive.</summary>
    public const double DeltaWorthIt = 0.6;

    public sealed record Asset(string Name, string Url, long Size);
    public sealed record Release(string Tag, string PageUrl, IReadOnlyList<Asset> Assets, bool Prerelease = false);

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

    /// <summary>The releases in GitHub's list JSON; empty for an error body.</summary>
    public static IReadOnlyList<Release> ParseList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<Release>();
            return doc.RootElement.EnumerateArray().Select(Read).OfType<Release>().ToList();
        }
        catch (JsonException) { return Array.Empty<Release>(); }
    }

    /// <summary>One release of the list, or null without a tag or for a draft.</summary>
    private static Release? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tag)
            || tag.GetString() is not { Length: > 0 } tagName) return null;
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        bool pre = root.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
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
        return new Release(tagName, page, assets, pre);
    }

    /// <summary>The newest stable release, by version (not by date).</summary>
    public static Release? Latest(IEnumerable<Release> releases) =>
        Stable(releases).LastOrDefault();

    /// <summary>The stable releases with a version tag, oldest first.</summary>
    private static List<Release> Stable(IEnumerable<Release> releases) =>
        releases.Where(r => !r.Prerelease && ParseVersion(r.Tag) != null).OrderBy(r => ParseVersion(r.Tag)).ToList();

    /// <summary>The platform part of the asset names <c>tools/release.sh</c> uploads, for a Godot <c>OS.GetName()</c>.</summary>
    public static string? PlatformFor(string osName) => osName switch
    {
        "Windows" => "windows",
        "Linux" or "FreeBSD" => "linux-x86_64",
        "macOS" => "macos",
        _ => null,
    };

    /// <summary>The full archive's suffix for a Godot <c>OS.GetName()</c>.</summary>
    public static string? SuffixFor(string osName) => PlatformFor(osName) switch
    {
        "windows" => "-windows.zip",
        { } p => $"-{p}.tar.gz",
        null => null,
    };

    /// <summary>The delta asset from one release to the next, uploaded with the newer one.</summary>
    public static string DeltaName(string fromTag, string toTag, string platform) =>
        $"UnitSportSwitzerland-{fromTag}-to-{toTag}-{platform}.delta";

    /// <summary>
    /// The deltas that take <paramref name="current"/> to the latest stable release, one per step
    /// between consecutive releases, oldest first; null when this version is not a release or a
    /// step has no delta for this platform (then the full archive).
    /// </summary>
    public static IReadOnlyList<(string From, string To, Asset Delta)>? DeltaChain(IEnumerable<Release> releases, string current, string osName)
    {
        var cur = ParseVersion(current);
        if (cur == null || PlatformFor(osName) is not { } platform) return null;
        var list = Stable(releases);
        int at = list.FindIndex(r => ParseVersion(r.Tag) == cur);
        if (at < 0 || at == list.Count - 1) return null;
        var chain = new List<(string, string, Asset)>();
        for (int i = at + 1; i < list.Count; i++)
        {
            string name = DeltaName(list[i - 1].Tag, list[i].Tag, platform);
            if (list[i].Assets.FirstOrDefault(a => a.Name == name) is not { } asset) return null;
            chain.Add((list[i - 1].Tag, list[i].Tag, asset));
        }
        return chain;
    }

    /// <summary>This platform's download in <paramref name="release"/>, or null (then the release page).</summary>
    public static Asset? AssetFor(Release release, string osName) =>
        SuffixFor(osName) is { } suffix
            ? release.Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            : null;
}
