using Godot;
using UnitSport.Player;

namespace UnitSport.Interiors;

/// <summary>
/// Chairs, stools, sofas, benches and pews to sit on (#1385): E (pad Y; VR: Y, or reach out and grip) in front of one
/// sits there facing its way, as on a stadium's stand or a cinema seat (<see cref="FootPlayer.TrySit"/>); nothing new on
/// the wire. Indoors, read from the interior's furniture where its mesh draws the seats (<see cref="InteriorMeshBuilder"/>:
/// a lone chair, an armchair, a sofa, a pew, a bench, a table set's chairs, a banquette and its chairs, a high table's
/// stools, a bar stool, a settee); outdoors, a venue terrace's bistro chairs and beer benches (<see cref="VenueTerraces"/>).
/// </summary>
public static class ChairSeats
{
    /// <summary>How near the player's feet a seat's middle must be to sit on it, m.</summary>
    public const float Reach = 1.1f;

    /// <summary>A bench's places, m apart.</summary>
    private const float Place = 0.6f;

    /// <summary>A seat in its piece's frame: its middle (x, z), its top (y), the way the one sitting faces (fx, fz); 0, 0 any way.</summary>
    public readonly record struct Local(float X, float Z, float Y, float Fx, float Fz);

    /// <summary>The seats of a piece, in its frame (authored with its back to -Z): where its mesh draws them.</summary>
    public static IEnumerable<Local> Of(FurniturePlan p)
    {
        float w = p.W / 2, d = p.D / 2;
        IEnumerable<Local> Along(float x0, float x1, float z, float y, float fx, float fz)
        {
            int n = Math.Max(1, (int)((x1 - x0) / Place));
            for (int i = 0; i < n; i++) yield return new Local(x0 + (x1 - x0) * (i + 0.5f) / n, z, y, fx, fz);
        }
        switch (p.Type)
        {
            case FurnitureType.Chair:
                return [new Local(0, 0, 0.46f, 0, 1)];
            case FurnitureType.Armchair:
                return [new Local(0, 0.05f, 0.42f, 0, 1)];
            case FurnitureType.Sofa:
                return Along(-w + 0.18f, w - 0.18f, 0.08f, 0.42f, 0, 1);
            case FurnitureType.Pew:
            case FurnitureType.FrontPew:
                return Along(-w + 0.06f, w - 0.06f, 0.05f, 0.47f, 0, 1);
            case FurnitureType.Bench:
                return Along(-w, w, 0, p.H, 0, 1);
            case FurnitureType.TableSet:
            {
                // as InteriorMeshBuilder.Dining draws them: a chair's back away from the table, so it faces it
                const float chair = 0.42f;
                bool ends = p.W >= 1.6f;
                float hw = ends ? w - chair : w - 0.17f;
                int per = p.W >= 1.85f ? 2 : 1;
                var seats = new List<Local>();
                for (int i = 0; i < per; i++)
                {
                    float off = per == 1 ? 0 : (i - 0.5f) * hw;
                    seats.Add(new Local(off, d - 0.2f, 0.45f, 0, -1));
                    seats.Add(new Local(off, -d + 0.2f, 0.45f, 0, 1));
                    if (!ends) continue;
                    seats.Add(new Local(w - 0.2f, off, 0.45f, -1, 0));
                    seats.Add(new Local(-w + 0.2f, off, 0.45f, 1, 0));
                }
                return seats;
            }
            case FurnitureType.BanquetteSet:
            {
                // the bench along the wall facing the table, the chairs across facing back
                var seats = Along(-w + 0.1f, w - 0.1f, -d + 0.33f, 0.47f, 0, 1).ToList();
                int n = p.W >= 1.4f ? 2 : 1;
                for (int i = 0; i < n; i++) seats.Add(new Local(n == 1 ? 0 : (i - 0.5f) * (p.W - 0.6f), d - 0.2f, 0.45f, 0, -1));
                return seats;
            }
            case FurnitureType.HighTable:
                return [new Local(0, d - 0.18f, 0.77f, 0, -1), new Local(0, -d + 0.18f, 0.77f, 0, 1),
                    new Local(w - 0.18f, 0, 0.77f, -1, 0), new Local(-w + 0.18f, 0, 0.77f, 1, 0)];
            case FurnitureType.BarStool:
                return [new Local(0, 0, 0.78f, 0, 0)];
            case FurnitureType.Settee:
                return Along(-w + 0.1f, w - 0.1f, -d + 0.33f, 0.48f, 0, 1);
            default:
                return [];
        }
    }

    /// <summary>A terrace piece's seats, in its frame (as <see cref="TerraceProps"/> draws them).</summary>
    public static IEnumerable<Local> Of(TerraceKind kind) => kind switch
    {
        // a rattan chair each side of the table, its back on the outer side, facing in
        TerraceKind.BistroSet => [new Local(-0.55f, 0, 0.465f, 1, 0), new Local(0.55f, 0, 0.465f, -1, 0)],
        // the two benches along the long table, three places each, facing it
        TerraceKind.BeerBench => [
            new Local(-0.7f, -0.6f, 0.47f, 0, 1), new Local(0, -0.6f, 0.47f, 0, 1), new Local(0.7f, -0.6f, 0.47f, 0, 1),
            new Local(-0.7f, 0.6f, 0.47f, 0, -1), new Local(0, 0.6f, 0.47f, 0, -1), new Local(0.7f, 0.6f, 0.47f, 0, -1)],
        _ => [],
    };

    private static readonly List<(Node3D Node, Vector3 At, float Yaw, TerraceKind Kind)> Terrace = new();

    /// <summary>A terrace piece with seats (<see cref="VenueTerraces"/>): its foot and turn in <paramref name="node"/>'s frame.</summary>
    public static void AddTerrace(Node3D node, Vector3 at, float yaw, TerraceKind kind)
    {
        if (kind is TerraceKind.BistroSet or TerraceKind.BeerBench) Terrace.Add((node, at, yaw, kind));
    }

    /// <summary>Every terrace seat in the world now: its hip and the way it faces (world). For <c>--chaircheck</c>.</summary>
    public static IEnumerable<(Vector3 Hip, Vector3 Facing)> TerraceSeats()
    {
        Terrace.RemoveAll(t => !GodotObject.IsInstanceValid(t.Node) || !t.Node.IsInsideTree());
        foreach (var (node, at, yaw, kind) in Terrace)
        {
            var basis = new Basis(Vector3.Up, yaw);
            foreach (var s in Of(kind))
                yield return (node.ToGlobal(at + basis * new Vector3(s.X, s.Y, s.Z)), node.GlobalBasis * (basis * new Vector3(s.Fx, 0, s.Fz)));
        }
    }

    /// <summary>The seat nearest <paramref name="me"/>'s feet within <see cref="Reach"/>, as a seat to sit on; null when there is none.</summary>
    public static FootPlayer.SitSpot? At(FootPlayer me)
    {
        var feet = me.GlobalPosition;
        FootPlayer.SitSpot? best = null;
        float bestD = Reach * Reach;
        void Consider(Vector3 hip, Vector3 facing, float height)
        {
            float dist = new Vector2(hip.X - feet.X, hip.Z - feet.Z).LengthSquared();
            if (dist > bestD || Mathf.Abs(hip.Y - height - feet.Y) > 0.6f) return;
            // a stool faces where the player looks; anything else the way its back sends you
            var f = facing.LengthSquared() > 1e-4f ? facing : -me.GlobalBasis.Z;
            float yaw = Mathf.Atan2(-f.X, -f.Z);
            bestD = dist;
            // standing up puts you back where you stood to sit: the floor you came from, never into the table
            best = new FootPlayer.SitSpot(hip, height, yaw, feet + Vector3.Up * 0.05f);
        }
        if (me.Indoors)
        {
            if (InteriorManager.Instance is not { Current: { } l, CurrentNode: { } node }) return null;
            foreach (var p in l.Furniture)
            {
                var basis = new Basis(Vector3.Up, p.Turns * Mathf.Pi / 2);
                var at = new Vector3(p.X, l.FloorY(p.Floor) + p.Lift, p.Z);
                foreach (var s in Of(p))
                    Consider(node.ToGlobal(at + basis * new Vector3(s.X, s.Y, s.Z)), node.GlobalBasis * (basis * new Vector3(s.Fx, 0, s.Fz)), s.Y);
            }
            return best;
        }
        Terrace.RemoveAll(t => !GodotObject.IsInstanceValid(t.Node) || !t.Node.IsInsideTree());
        foreach (var (node, at, yaw, kind) in Terrace)
        {
            var basis = new Basis(Vector3.Up, yaw);
            foreach (var s in Of(kind))
                Consider(node.ToGlobal(at + basis * new Vector3(s.X, s.Y, s.Z)), node.GlobalBasis * (basis * new Vector3(s.Fx, 0, s.Fz)), s.Y);
        }
        return best;
    }
}
