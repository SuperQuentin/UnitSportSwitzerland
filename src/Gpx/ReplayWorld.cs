using Godot;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Gpx;

/// <summary>
/// The world a GPX replay and its video export run on (#1752), the same in both frames: the ground the runners, the
/// ribbon and the cameras stand on (<see cref="IWorldGround"/>: the flat tiles, or the earth world's leaves), what its
/// tiles hold by name for the corridor survey and the road snap (<see cref="Source"/>: the flat streamer's source, or
/// the leaves' stand-ins), and whether the view is complete for the exporter's frame.
///
/// <para>
/// <see cref="Flat"/> is the flat streamer, null under <c>--frame earth</c>. What only it has stays optional: the
/// sightline cut (its terrain shader), the manifest's tile list, the export's offline budgets, prefetch and stats.
/// </para>
/// </summary>
public sealed class ReplayWorld
{
    public required IWorldGround Ground { get; init; }

    /// <summary>The flat streamer when the world is flat; null on the earth world's leaves.</summary>
    public ChunkManager? Flat { get; init; }

    /// <summary>
    /// The earth world's "nothing more to draw for the view" (its near leaves and the globe's far ones, as the shots
    /// wait for, #1679); null on the flat world, which settles by rings round the eye.
    /// </summary>
    public Func<bool>? Idle { get; init; }

    /// <summary>The earth world's loading readout, beside <see cref="Idle"/>.</summary>
    public Func<string?>? IdleStatus { get; init; }

    /// <summary>The flat world's replay world: its streamer and its ground.</summary>
    public static ReplayWorld OnFlat(ChunkManager chunks) => new() { Ground = chunks.Ground, Flat = chunks };

    /// <summary>What the world's tiles hold, loaded by the ground's names: LV95 tiles, or the leaves' stand-ins.</summary>
    public IChunkSource? Source => Ground.Content ?? Flat?.Source;

    /// <summary>
    /// Whether a flat tile exists, so a survey never asks for a missing one (seconds each over the network): the
    /// manifest's, or the generated fill's, made here. Null on the leaves, whose stand-ins are asked of the leaf source,
    /// which answers a missing leaf at once.
    /// </summary>
    public Func<TileId, bool>? KnownTiles => Flat is { } flat ? id => flat.AvailableTiles.Contains(id) || flat.IsGenerated(id) : null;

    /// <summary>Whether the visible world round <paramref name="eye"/> is complete for a frame of video.</summary>
    public bool SettledNear(Vector3 eye, int rings) =>
        Idle != null ? Idle() : Flat?.SettledNear(eye, rings) ?? true;

    public string SettleReport(Vector3 eye) =>
        Idle != null ? IdleStatus?.Invoke() ?? (Idle() ? "settled" : "loading") : Flat?.SettleReport(eye) ?? "";

    /// <summary>The camera's sightline cut through the flat terrain's shader; nothing on the leaves (not built there yet).</summary>
    public void SetSightlineCut(Vector3 from, Vector3 to, float radius) => Flat?.SetSightlineCut(from, to, radius);
}
