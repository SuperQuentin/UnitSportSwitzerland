using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;

namespace UnitSport.Trailer;

/// <summary>
/// What a render was made with (#924): the game's commit, the Godot build and the GPU, the terrain's
/// fingerprint, the date and occasions, the style, the song, the settings and the seeds. Written
/// beside each filmed shot as <c>shotNN.pins.json</c>, and for the run as <c>[trailer] pin:</c> lines
/// that <c>tools/trailer.sh</c> copies into the version's <c>version.txt</c>.
/// </summary>
public static class RenderPins
{
    /// <summary>The chunk manifest as the director reads it once: each tile's top (an eye's height over it) and content hash.</summary>
    public sealed record ManifestInfo(Dictionary<(int, int), float> Peaks, Dictionary<(int, int), string?> Hashes,
        int Version, string? Algorithm, SortedDictionary<string, string>? Shared, string Dir);

    private static ManifestInfo? _manifest;

    /// <summary>The terrain's manifest (empty without one: a fixture, or no drive).</summary>
    public static ManifestInfo Manifest => _manifest ??= LoadManifest();

    private static ManifestInfo LoadManifest()
    {
        var peaks = new Dictionary<(int, int), float>();
        var hashes = new Dictionary<(int, int), string?>();
        string dir = CmdArgs.Value("--chunks") ?? TerrainPaths.FindChunkDir();
        // a fixture course is built in code: no manifest, no hashes
        if (dir.StartsWith("fixture:", StringComparison.Ordinal)) return new(peaks, hashes, 0, null, null, dir);
        try
        {
            string path = Path.Combine(TerrainPaths.FindChunkDir(), "manifest.json");
            if (!File.Exists(path)) return new(peaks, hashes, 0, null, null, dir);
            var m = TerrainManifest.FromJson(File.ReadAllText(path));
            foreach (var t in m.Tiles)
            {
                peaks[(t.E, t.N)] = t.Max;
                hashes[(t.E, t.N)] = t.Hash;
            }
            return new(peaks, hashes, m.ManifestVersion, m.HashAlgorithm, m.Shared, dir);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[trailer] manifest unreadable: {e.Message}");
            return new(peaks, hashes, 0, null, null, dir);
        }
    }

    /// <summary>
    /// The fingerprint of <paramref name="tiles"/> (<see cref="TileFingerprint.OfSet"/>); how many of
    /// them have no hash (not built, or a manifest from before #924) in <paramref name="unhashed"/>.
    /// </summary>
    public static string Fingerprint(IEnumerable<(int E, int N)> tiles, out int unhashed)
    {
        var list = tiles.Select(t => (new TileId(t.E, t.N), Manifest.Hashes.GetValueOrDefault(t))).ToList();
        unhashed = list.Count(t => t.Item2 == null);
        return TileFingerprint.OfSet(list);
    }

    /// <summary>The Godot build, the renderer and the GPU, the OS and the CPU this process runs on.</summary>
    public static JsonObject Machine() => new()
    {
        ["godot"] = Engine.GetVersionInfo()["string"].AsString(),
        ["renderer"] = ProjectSettings.GetSetting("rendering/renderer/rendering_method").AsString(),
        ["adapter"] = RenderingServer.GetVideoAdapterName(),
        ["vendor"] = RenderingServer.GetVideoAdapterVendor(),
        ["api"] = RenderingServer.GetVideoAdapterApiVersion(),
        ["os"] = $"{OS.GetName()} {OS.GetVersion()}",
        ["cpu"] = OS.GetProcessorName(),
    };

    /// <summary>A file's SHA-256 (hex), null when it is not there.</summary>
    public static string? Sha256(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// <summary>The settings this process runs with (the render profile's), as JSON.</summary>
    public static JsonNode? Settings() => JsonSerializer.SerializeToNode(GameSettings.Current);

    /// <summary>
    /// <c>--trailer-commit</c>: the game's commit as <c>tools/trailer.sh</c> read it (<c>abc1234</c>,
    /// <c>abc1234+dirty</c> with local changes under <c>src</c> or <c>tools</c>); "unknown" when run by hand.
    /// </summary>
    public static string Commit => CmdArgs.Value("--trailer-commit") ?? "unknown";

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
}
