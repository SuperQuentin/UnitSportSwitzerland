using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Farming;

/// <summary>
/// The farmed ground on this peer (#494): field tiles near the players, the worked cells, their
/// drawing, and the link to the server. STUB: the farming core fills it in.
/// </summary>
public partial class FarmField : Node
{
    public static FarmField? Instance { get; private set; }

    public override void _EnterTree() => Instance = this;
    public override void _ExitTree() { if (Instance == this) Instance = null; }

    public FarmStroke Sweep(FarmTool tool, Vector3 a, Vector3 b, float width, CropKind seed) => default;

    public FieldCellView? CellAt(Vector3 world) => null;
}
