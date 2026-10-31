using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The tables a fondue or a raclette pot can stand on (#1384, the owner's call: a pot is a table interaction, not
/// anywhere near a venue): a dining venue's tables inside its guest room (a <see cref="FurnitureType.TableSet"/>, a
/// banquette, a high table) and its terrace's bistro sets and beer benches (<see cref="VenueTerraces"/>). Client only:
/// it finds the table the player stands at and the request carries it; the server checks it is within reach and
/// belongs to a venue (<c>TogetherService.ServeStartAt</c>).
/// </summary>
public static class PotTables
{
    /// <summary>How near the player's feet a table's middle must be for E to set a pot on it, m.</summary>
    public const float Reach = 1.8f;

    /// <summary>A terrace table's top over its foot, m.</summary>
    private const float TerraceTop = 0.75f;

    private static readonly List<(Node3D Node, Vector3 Local)> Terrace = new();

    /// <summary>A terrace table (<see cref="VenueTerraces"/>): its foot in <paramref name="node"/>'s frame. Dropped when the node goes.</summary>
    public static void AddTerrace(Node3D node, Vector3 local) => Terrace.Add((node, local + Vector3.Up * TerraceTop));

    /// <summary>The top of the table nearest <paramref name="feet"/> (world), within <see cref="Reach"/>, or null.</summary>
    public static Vector3? Near(Vector3 feet)
    {
        Vector3? best = null;
        float bestD = Reach * Reach;
        void Consider(Vector3 top)
        {
            float d = new Vector2(top.X - feet.X, top.Z - feet.Z).LengthSquared();
            if (d <= bestD && Mathf.Abs(top.Y - feet.Y) < 1.6f) (best, bestD) = (top, d);
        }
        // indoors: the tables standing in a venue's guest room
        if (InteriorManager.Instance is { Current: { } l, CurrentNode: { } node })
            foreach (var p in l.Furniture)
            {
                if (p.Type is not (FurnitureType.TableSet or FurnitureType.BanquetteSet or FurnitureType.HighTable)) continue;
                if (p.Floor < 0 || p.Floor >= l.Floors.Count) continue;
                if (!l.Floors[p.Floor].Rooms.Any(r => InteriorGenerator.DiningRoom(r.Type) && r.Holds(p.X, p.Z))) continue;
                Consider(node.ToGlobal(new Vector3(p.X, l.FloorY(p.Floor) + Math.Min(p.H, 1.1f), p.Z)));
            }
        Terrace.RemoveAll(t => !GodotObject.IsInstanceValid(t.Node) || !t.Node.IsInsideTree());
        foreach (var (n, local) in Terrace) Consider(n.ToGlobal(local));
        return best;
    }
}
