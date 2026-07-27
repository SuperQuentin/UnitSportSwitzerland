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
    public Lv95Point SuggestedOriginLv95 { get; set; } = new();
    public Lv95Bounds BoundsLv95 { get; set; } = new();
    public List<ManifestTile> Tiles { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

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
