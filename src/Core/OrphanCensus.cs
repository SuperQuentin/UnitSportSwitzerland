using Godot;

namespace UnitSport.Core;

/// <summary>
/// How many orphan nodes (made, never added to the tree or freed) there are: a node leak's size. A 100-bot
/// Battle Royale client grew 75,000 of them and 128 GB (#837). <see cref="Node.PrintOrphanNodes"/> names them
/// (debug builds); <c>GetOrphanNodeIds</c> gives 32-bit ids, too short to look a node up.
/// </summary>
public static class OrphanCensus
{
    public static string Line() => $"{Node.GetOrphanNodeIds().Count} orphans";
}
