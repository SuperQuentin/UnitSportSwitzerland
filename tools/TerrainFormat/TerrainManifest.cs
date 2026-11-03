using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Terrain.Format;

/// <summary>
/// Index of available chunks, written by the preprocessor next to the .terr files.
/// The game reads it to know coverage without listing directories; a future HTTP
/// chunk source serves the same document as its index endpoint.
/// </summary>
public sealed class TerrainManifest
{
    public int FormatVersion { get; set; } = ChunkFormat.Version;
    public int GridSize { get; set; } = ChunkFormat.GridSize;
    public double SpacingM { get; set; } = ChunkFormat.SpacingM;
    public double HeightScale { get; set; } = ChunkFormat.HeightScale;

    /// <summary>Metres at quantized height 0: 0 in version 2 (absent in old files), -50 in version 3.</summary>
    public double HeightOffsetM { get; set; }
    public Lv95Point SuggestedOriginLv95 { get; set; } = new();
    public Lv95Bounds BoundsLv95 { get; set; } = new();
    public List<ManifestTile> Tiles { get; set; } = new();

    /// <summary>
    /// The manifest's own layout (not the tiles' <see cref="FormatVersion"/>): 1 before #924 (absent
    /// in the file), 2 once the tiles carry a content <see cref="ManifestTile.Hash"/>. Old readers skip
    /// the new fields, and a version-1 file reads with no hashes.
    /// </summary>
    public int ManifestVersion { get; set; } = 1;

    /// <summary>How <see cref="ManifestTile.Hash"/> was computed (<see cref="TileFingerprint.Algorithm"/>); null: no hashes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HashAlgorithm { get; set; }

    /// <summary>The region-wide files' hashes (<see cref="TileFingerprint.SharedFiles"/>, those present); null: none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SortedDictionary<string, string>? Shared { get; set; }

    /// <summary>The version a manifest with tile hashes has.</summary>
    public const int HashedVersion = 2;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Makes this region's height encoding the process's (<see cref="ChunkFormat.Use"/>); the game's
    /// manifest loads call it, tools that merely read a manifest do not.
    /// </summary>
    public TerrainManifest UseFormat()
    {
        ChunkFormat.Use(FormatVersion);
        return this;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static TerrainManifest FromJson(string json) =>
        JsonSerializer.Deserialize<TerrainManifest>(json, JsonOptions)
        ?? throw new InvalidDataException("Empty terrain manifest");
}

public sealed class ManifestTile
{
    public int E { get; set; }
    public int N { get; set; }
    public float Min { get; set; }
    public float Max { get; set; }

    /// <summary>
    /// The tile's content hash (<see cref="TileFingerprint.OfTile"/> over all its files), null in a
    /// manifest from before #924 or one written by a build that does not hash.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; set; }

    [JsonIgnore]
    public TileId Id => new(E, N);
}

public sealed class Lv95Point
{
    public double E { get; set; }
    public double N { get; set; }
}

public sealed class Lv95Bounds
{
    public double MinE { get; set; }
    public double MinN { get; set; }
    public double MaxE { get; set; }
    public double MaxN { get; set; }
}
