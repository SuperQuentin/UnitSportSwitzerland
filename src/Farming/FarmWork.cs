using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>What a stroke of work did: how many cells it worked, of which crop, and the items it gave or used.</summary>
/// <param name="Units">Harvest/mow: items gained (fractions add up, <see cref="FarmTables.YieldPerCell"/>). Sow: seed items used (cells / <see cref="FarmTables.CellsPerSeed"/>).</param>
public readonly record struct FarmStroke(int Cells, CropKind Crop, float Units);

/// <summary>A field cell as seen now: its field's real crop, the crop on it, its stage and growth 0..1.</summary>
public readonly record struct FieldCellView(uint FieldId, CropKind FieldCrop, CropKind Crop, FieldStage Stage, float Growth);

/// <summary>
/// The one door into farming for everything that works the ground (#494): implements behind a
/// tractor, the combine, hand tools. Called by the <b>local</b> player only; the result is applied
/// at once on this peer (predicted) and sent to the server, which owns the field state.
///
/// <para>PINNED INTERFACE: the vehicles and items code calls these; the farming core implements them.</para>
/// </summary>
public static class FarmWork
{
    /// <summary>
    /// Work the strip swept from <paramref name="a"/> to <paramref name="b"/> (world positions; height
    /// ignored), <paramref name="width"/> m wide and centred on the line, with <paramref name="tool"/>:
    /// every field cell whose centre lies in the strip and that the tool can work at its stage now
    /// (<see cref="FarmTables.CanWork"/>). <paramref name="seed"/> is the crop sown (Sow only).
    /// Cheap enough to call every physics tick from a moving machine.
    /// </summary>
    public static FarmStroke Sweep(FarmTool tool, Vector3 a, Vector3 b, float width, CropKind seed = CropKind.None)
        => FarmField.Instance?.Sweep(tool, a, b, width, seed) ?? default;

    /// <summary>The cell under a world point now, or null off every field (or before its tile is loaded).</summary>
    public static FieldCellView? CellAt(Vector3 world) => FarmField.Instance?.CellAt(world);
}
