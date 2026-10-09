namespace UnitSport.Interiors;

/// <summary>
/// Furnishing that leaves a way through (#680): a piece may not seal off part of a room or
/// shut one doorway from another, and a TV stands on the wall its sofa faces.
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>Half the width of a person plus a margin: a gap narrower than twice this is not a way through.</summary>
    private const float WalkerRadius = 0.24f;
    private const float WalkCell = 0.1f;
    /// <summary>A piece may seal off this many cells of floor (0.3 m²) beyond what was already sealed.</summary>
    private const int PocketAllowance = 30;
    /// <summary>Rooms bigger than this are halls, laid out by their own rules and not worth a grid.</summary>
    private const float WalkableMaxArea = 100f;

    /// <summary>A room's floor as a grid of cells a walker's centre may stand on, and where its doorways let in.</summary>
    private sealed class WalkMap
    {
        private readonly RoomPlan _r;
        private readonly float _x0, _z0;
        private readonly int _nx, _nz;
        public readonly List<int> Seeds = new();

        public WalkMap(RoomPlan r)
        {
            _r = r;
            _x0 = r.X0 + WallInset + WalkerRadius;
            _z0 = r.Z0 + WallInset + WalkerRadius;
            _nx = (int)((r.X1 - WallInset - WalkerRadius - _x0) / WalkCell) + 1;
            _nz = (int)((r.Z1 - WallInset - WalkerRadius - _z0) / WalkCell) + 1;
            if (!Usable) return;
            foreach (var o in r.Openings.Where(o => o.Kind != OpeningKind.Window))
            {
                // a step inside the doorway, on the room's walkable floor
                float x = o.Side switch { Side.Left => _x0, Side.Right => r.X1 - WallInset - WalkerRadius, _ => o.Center };
                float z = o.Side switch { Side.Front => _z0, Side.Back => r.Z1 - WallInset - WalkerRadius, _ => o.Center };
                int i = Math.Clamp((int)MathF.Round((x - _x0) / WalkCell), 0, _nx - 1);
                int j = Math.Clamp((int)MathF.Round((z - _z0) / WalkCell), 0, _nz - 1);
                Seeds.Add(j * _nx + i);
            }
        }

        public bool Usable => _r.Area <= WalkableMaxArea && _nx >= 2 && _nz >= 2;

        /// <summary>Cells a walker cannot stand on: within <see cref="WalkerRadius"/> of any piece.</summary>
        public bool[] Shut(IEnumerable<RectPlan> pieces, RectPlan? extra = null)
        {
            var shut = new bool[_nx * _nz];
            void Mark(RectPlan q)
            {
                int i0 = Math.Max(0, (int)MathF.Ceiling((q.X0 - WalkerRadius + 1e-3f - _x0) / WalkCell));
                int i1 = Math.Min(_nx - 1, (int)MathF.Floor((q.X1 + WalkerRadius - 1e-3f - _x0) / WalkCell));
                int j0 = Math.Max(0, (int)MathF.Ceiling((q.Z0 - WalkerRadius + 1e-3f - _z0) / WalkCell));
                int j1 = Math.Min(_nz - 1, (int)MathF.Floor((q.Z1 + WalkerRadius - 1e-3f - _z0) / WalkCell));
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++) shut[j * _nx + i] = true;
            }
            foreach (var q in pieces) Mark(q);
            if (extra != null) Mark(extra);
            return shut;
        }

        /// <summary>The cells reachable from the first doorway, and how many free cells are not.</summary>
        public (bool[] Reached, int Pockets) Flood(bool[] shut)
        {
            var reached = new bool[shut.Length];
            var stack = new Stack<int>();
            stack.Push(Seeds[0]);
            reached[Seeds[0]] = true;
            while (stack.Count > 0)
            {
                int c = stack.Pop();
                int i = c % _nx, j = c / _nx;
                if (i > 0) Visit(c - 1);
                if (i < _nx - 1) Visit(c + 1);
                if (j > 0) Visit(c - _nx);
                if (j < _nz - 1) Visit(c + _nx);
            }
            void Visit(int c)
            {
                if (shut[c] || reached[c]) return;
                reached[c] = true;
                stack.Push(c);
            }
            int pockets = 0;
            for (int k = 0; k < shut.Length; k++)
                if (!shut[k] && !reached[k]) pockets++;
            return (reached, pockets);
        }
    }

    /// <summary>
    /// Whether standing <paramref name="add"/> among <paramref name="placed"/> keeps the room
    /// walkable: from one doorway a person can still reach every other doorway and nearly all
    /// the floor they could reach before. Walkers are a <see cref="WalkerRadius"/> wide, so a
    /// 0.4 m gap between a table and a wall is shut.
    /// </summary>
    private static bool KeepsWay(RoomPlan r, List<RectPlan> placed, RectPlan add)
    {
        var map = new WalkMap(r);
        if (!map.Usable || map.Seeds.Count == 0) return true;
        var before = map.Shut(placed);
        if (before[map.Seeds[0]]) return true; // already shut by what stands there: nothing to protect
        var after = map.Shut(placed, add);
        var (reached, pockets) = map.Flood(after);
        if (map.Seeds.Any(s => !before[s] && (after[s] || !reached[s]))) return false;
        return pockets <= map.Flood(before).Pockets + PocketAllowance;
    }

    /// <summary>
    /// The doorways of <paramref name="r"/> that a person coming in by its first one could not
    /// reach among <paramref name="pieces"/> (what <c>--flatcheck</c> asks of every finished room).
    /// </summary>
    internal static int ShutDoorways(RoomPlan r, List<RectPlan> pieces)
    {
        var map = new WalkMap(r);
        if (!map.Usable || map.Seeds.Count == 0) return 0;
        var shut = map.Shut(pieces);
        if (shut[map.Seeds[0]]) return 0;
        var (reached, _) = map.Flood(shut);
        return map.Seeds.Count(s => !shut[s] && !reached[s]);
    }

    /// <summary>Floor, in m², that a person coming in by the first doorway could not walk to among <paramref name="pieces"/>.</summary>
    internal static float SealedFloor(RoomPlan r, List<RectPlan> pieces)
    {
        var map = new WalkMap(r);
        if (!map.Usable || map.Seeds.Count == 0) return 0;
        var shut = map.Shut(pieces);
        return shut[map.Seeds[0]] ? 0 : map.Flood(shut).Pockets * WalkCell * WalkCell;
    }

    /// <summary>
    /// A TV goes on the wall its sofa faces, as near the sofa's middle as it fits, so the two
    /// look at each other across the room; where that wall will not take it, it is placed as
    /// any piece is.
    /// </summary>
    private static void PlaceTv(InteriorLayout l, int f, RoomPlan r, Piece p,
        List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        var sofa = l.Furniture.LastOrDefault(x => x.Floor == f && x.Type == FurnitureType.Sofa
            && x.X > r.X0 && x.X < r.X1 && x.Z > r.Z0 && x.Z < r.Z1);
        if (sofa != null)
        {
            // the way a piece faces: 0 turns toward +Z, 1 toward +X, 2 toward -Z, 3 toward -X
            var (side, at) = (sofa.Turns & 3) switch
            {
                0 => (Side.Back, sofa.X),
                1 => (Side.Right, sofa.Z),
                2 => (Side.Front, sofa.X),
                _ => (Side.Left, sofa.Z),
            };
            int before = l.Furniture.Count;
            TryPlace(l, f, r, p, placed, blocked, rng, new[] { side }, at);
            if (l.Furniture.Count > before) return;
            // else on a side wall, looking across the room at it; never on the sofa's own wall, facing the way it does
            TryPlace(l, f, r, p, placed, blocked, rng, side is Side.Front or Side.Back ? new[] { Side.Left, Side.Right } : new[] { Side.Front, Side.Back });
            return; // no TV at all, rather than one turned away from the sofa
        }
        TryPlace(l, f, r, p, placed, blocked, rng);
    }
}
