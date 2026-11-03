using Godot;

namespace UnitSport.Interiors.Games;

/// <summary>
/// What shows a playable piece's game on a client (#1324, #1326): a node at the piece's frame (back −Z, front +Z,
/// on its floor), built with the venue's interior. What moves (balls, rods, darts, the puck) is its own node or
/// a MultiMesh instance, set from the game; nothing rebuilds a mesh as it plays.
/// </summary>
public abstract partial class TableRig : Node3D
{
    /// <summary>The piece's plan entry, for its size.</summary>
    public FurniturePlan Piece { get; private set; } = null!;

    protected TableRig() { }

    protected void Init(FurniturePlan piece) => Piece = piece;

    /// <summary>The server sent a new state of this piece's game (a shot taken, a turn passed, a score).</summary>
    public virtual void OnState(TableGame game, double now) { }

    /// <summary>Every frame while this peer is in the venue: show the game at shared time <paramref name="now"/>.</summary>
    public abstract void Show(TableGame? game, double now, double delta);
}
