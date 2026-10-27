using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A building's real shape in its interior's frame (#577): an L, a U, a ring round an inner
/// courtyard, approximated by a few rectangles ("wings") that keep its general shape, instead of
/// the one box (<see cref="PlanBox"/>) every interior used to fill.
///
/// <para>
/// The shape comes from the roof: every face that is not a wall, laid flat on a 0.5 m grid in the
/// plan frame (x across the front, z from front to back, both from the box's centre). A courtyard
/// has no roof, so it stays empty. The grid is then peeled into rectangles, the biggest fully
/// covered one first, while what is left still holds a wing worth a stairwell and a flat.
/// Corners that fit no wing are left out: the inside may differ a little from the outline, never
/// from its shape.
/// </para>
/// </summary>
public static class PlanOutline
{
    public const float Cell = 0.5f;
    /// <summary>A wing narrower than this is no wing: a stairwell and one flat need it.</summary>
    public const float MinWing = 6f;
    /// <summary>A box whose roof covers this much of it is planned as the box.</summary>
    public const float Boxy = 0.92f;
    public const int MaxWings = 4;

    /// <summary>
    /// The wings of a building in its interior frame, or null when the box is the building
    /// (nearly full, no roof to read, or nothing a wing fits in).
    /// </summary>
    public static List<RectPlan>? Wings(Building b, Vector2 center, Vector2 axisU, float width, float depth) =>
        Grid(b, center, axisU, width, depth, Cell) is { } grid ? Peel(grid, width, depth) : null;

    /// <summary>Cells of the fine grid the outline is traced on (#1290).</summary>
    public const float FineCell = 0.25f;
    /// <summary>How far a traced edge may stray from the cells' staircase: an angled facade is one edge again.</summary>
    public const float Tolerance = 0.3f;
    /// <summary>An outline covering this much of its box is the box: nothing to follow.</summary>
    public const float Boxlike = 0.97f;
    /// <summary>The deepest vestibule a doorway on the box's side gets in to the building.</summary>
    public const float MaxStub = 3.0f;

    /// <summary>
    /// The building's outline in its interior frame (#1290): rings, the outer counter-clockwise and
    /// a courtyard's clockwise, each edge a facade (an angled one straight), or null when it is
    /// its box (no roof to read, or the roof covers nearly all of it).
    /// </summary>
    public static List<Vector2[]>? Rings(Building b, Vector2 center, Vector2 axisU, float width, float depth) =>
        FineGrid(b, center, axisU, width, depth) is { } grid ? Rings(grid, width, depth) : null;

    /// <summary>The roof on the <see cref="FineCell"/> grid, or null when it is nearly all of the box.</summary>
    public static bool[,]? FineGrid(Building b, Vector2 center, Vector2 axisU, float width, float depth)
    {
        if (Grid(b, center, axisU, width, depth, FineCell) is not { } grid) return null;
        int covered = 0;
        foreach (bool g in grid) if (g) covered++;
        return covered >= Boxlike * grid.Length ? null : grid;
    }

    /// <summary>
    /// The rings of a <see cref="FineGrid"/>, with a stub for each doorway: the stretch of the box's
    /// side a way in stands on, from the side in to where the building is (<paramref name="doors"/>:
    /// the side, the middle along it and the width). A plan keeps its doorways on the box's sides,
    /// so a hall cut by a facade at an angle keeps the way in to its door: a short vestibule.
    /// </summary>
    public static List<Vector2[]> Rings(bool[,] fine, float width, float depth, IEnumerable<(Side Side, float At, float Width, bool Main)>? doors = null)
    {
        var grid = fine;
        if (doors != null)
        {
            grid = (bool[,])fine.Clone();
            int nx = grid.GetLength(0), nz = grid.GetLength(1);
            foreach (var (side, at, w, main) in doors)
            {
                bool along = side is Side.Front or Side.Back;
                int n = along ? nx : nz, deep = along ? nz : nx;
                float half = (along ? width : depth) / 2;
                int c0 = Math.Clamp((int)((at - w / 2 - 0.3f + half) / FineCell), 0, n - 1);
                int c1 = Math.Clamp((int)((at + w / 2 + 0.3f + half) / FineCell), 0, n - 1);
                // in from the side, row by row, until the whole band is building; another door than
                // the main one no further than a vestibule goes (a door out in an L's yard gets none)
                var rows = new List<int>();
                bool reached = false;
                for (int k = 0; k < (main ? deep / 2 : Math.Min(deep, (int)(MaxStub / FineCell))); k++)
                {
                    int row = side is Side.Front or Side.Left ? k : deep - 1 - k;
                    bool full = true;
                    for (int c = c0; c <= c1; c++) full &= along ? grid[c, row] : grid[row, c];
                    if (full) { reached = true; break; }
                    rows.Add(row);
                }
                if (!reached) continue;
                foreach (int row in rows)
                    for (int c = c0; c <= c1; c++)
                        if (along) grid[c, row] = true; else grid[row, c] = true;
            }
        }
        return RoomShapes.Trace(grid, FineCell, width, depth, Tolerance);
    }

    /// <summary>
    /// The biggest rectangle of the outline that stands on the front wall and holds the stretch
    /// <paramref name="lo"/>..<paramref name="hi"/> of it (the main door's), each side at least
    /// <paramref name="min"/>; a side within a cell and a half of the box's is put on it. Null if
    /// none: the front is not the building's there, or too little of it is.
    /// </summary>
    public static RectPlan? FrontCore(Building b, Vector2 center, Vector2 axisU, float width, float depth, float lo, float hi, float min)
    {
        if (Grid(b, center, axisU, width, depth, Cell) is not { } g) return null;
        int nx = g.GetLength(0), nz = g.GetLength(1);
        int c0 = Math.Clamp((int)((lo + width / 2) / Cell), 0, nx - 1), c1 = Math.Clamp((int)MathF.Ceiling((hi + width / 2) / Cell) - 1, 0, nx - 1);
        int need = (int)MathF.Ceiling(min / Cell);
        var full = new bool[nx];
        for (int i = 0; i < nx; i++) full[i] = true;
        (int I0, int I1, int H)? best = null;
        int bestArea = 0;
        // where the building starts behind the door's stretch of the front: the first row, or a cell
        // short of it (a cell centre off the roof's edge), or further back where the front is not
        // straight (the notch of a bent bar): the door then opens into a recess
        int start = 0;
        while (start < nz - need && Enumerable.Range(c0, c1 - c0 + 1).Any(i => !g[i, start])) start++;
        if (start >= nz - need) return null;
        for (int h = 1; start + h <= nz; h++)
        {
            int j = start + h - 1;
            for (int i = 0; i < nx; i++) full[i] &= g[i, j];
            if (!full[c0] || !full[c1]) break;
            int i0 = c0, i1 = c1;
            while (i0 > 0 && full[i0 - 1]) i0--;
            while (i1 < nx - 1 && full[i1 + 1]) i1++;
            int w = i1 - i0 + 1;
            if (w >= need && h >= need && w * h > bestArea) { bestArea = w * h; best = (i0, i1, h); }
        }
        if (best is not { } r) return null;
        float hw = width / 2, hd = depth / 2, snap = Cell * 1.5f;
        float x0 = r.I0 * Cell - hw, x1 = Math.Min((r.I1 + 1) * Cell, width) - hw, z1 = Math.Min((start + r.H) * Cell, depth) - hd;
        float z0 = start * Cell - hd;
        return new RectPlan(x0 <= -hw + snap ? -hw : x0, z0 <= -hd + snap ? -hd : z0, x1 >= hw - snap ? hw : x1, z1 >= hd - snap ? hd : z1);
    }

    /// <summary>The roof laid flat on a grid of <paramref name="cell"/> in the plan frame, or null with no roof.</summary>
    public static bool[,]? Grid(Building b, Vector2 center, Vector2 axisU, float width, float depth, float cell)
    {
        var axisV = new Vector2(-axisU.Y, axisU.X);
        int nx = Math.Max(1, (int)MathF.Ceiling(width / cell)), nz = Math.Max(1, (int)MathF.Ceiling(depth / cell));
        var grid = new bool[nx, nz];
        bool any = false;
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (a, c, d) = b.Tri(t);
            var n = (c - a).Cross(d - a);
            float len = n.Length();
            // a wall is no part of the plan; a roof face, flat or pitched, is
            if (len < 1e-6f || Mathf.Abs(n.Y / len) < BuildingTriangles.RoofNormalY) continue;
            Vector2 L(Vector3 p)
            {
                var q = new Vector2(p.X, p.Z) - center;
                return new Vector2(q.Dot(axisU) + width / 2, q.Dot(axisV) + depth / 2);
            }
            var pa = L(a); var pc = L(c); var pd = L(d);
            int i0 = Math.Max(0, (int)(Math.Min(pa.X, Math.Min(pc.X, pd.X)) / cell)), i1 = Math.Min(nx - 1, (int)(Math.Max(pa.X, Math.Max(pc.X, pd.X)) / cell));
            int j0 = Math.Max(0, (int)(Math.Min(pa.Y, Math.Min(pc.Y, pd.Y)) / cell)), j1 = Math.Min(nz - 1, (int)(Math.Max(pa.Y, Math.Max(pc.Y, pd.Y)) / cell));
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                    if (!grid[i, j] && InTriangle(new Vector2((i + 0.5f) * cell, (j + 0.5f) * cell), pa, pc, pd))
                    {
                        grid[i, j] = true;
                        any = true;
                    }
        }
        return any ? grid : null;
    }

    /// <summary>
    /// The rectangles of a covered grid, biggest first (pure; the shaped blocks of `--flatcheck`): null when the grid is
    /// nearly full or no wing fits.
    /// </summary>
    public static List<RectPlan>? Peel(bool[,] grid, float width, float depth)
    {
        int nx = grid.GetLength(0), nz = grid.GetLength(1);
        int covered = 0;
        foreach (bool g in grid) if (g) covered++;
        if (covered >= Boxy * nx * nz) return null;
        var left = (bool[,])grid.Clone();
        int min = (int)MathF.Ceiling(MinWing / Cell);
        var wings = new List<RectPlan>();
        int taken = 0;
        while (wings.Count < MaxWings)
        {
            var r = Largest(left, min);
            if (r is not { } w) break;
            for (int i = w.I0; i < w.I1; i++)
                for (int j = w.J0; j < w.J1; j++)
                    left[i, j] = false;
            taken += (w.I1 - w.I0) * (w.J1 - w.J0);
            wings.Add(new RectPlan(w.I0 * Cell - width / 2, w.J0 * Cell - depth / 2,
                Math.Min(w.I1 * Cell, width) - width / 2, Math.Min(w.J1 * Cell, depth) - depth / 2));
            if (covered - taken < 0.1f * covered) break;
        }
        if (wings.Count == 0) return null;
        // a wing within a cell of the box's edge is on it: the facade, not a sliver short of it
        for (int k = 0; k < wings.Count; k++)
        {
            var w = wings[k];
            float hw = width / 2, hd = depth / 2;
            wings[k] = new RectPlan(w.X0 <= -hw + Cell * 1.5f ? -hw : w.X0, w.Z0 <= -hd + Cell * 1.5f ? -hd : w.Z0,
                w.X1 >= hw - Cell * 1.5f ? hw : w.X1, w.Z1 >= hd - Cell * 1.5f ? hd : w.Z1);
        }
        // one wing that is nearly the box is the box
        if (wings.Count == 1 && (wings[0].X1 - wings[0].X0) * (wings[0].Z1 - wings[0].Z0) >= Boxy * width * depth) return null;
        return wings;
    }

    /// <summary>The largest all-covered rectangle with both sides at least <paramref name="min"/> cells (histogram method).</summary>
    private static (int I0, int J0, int I1, int J1)? Largest(bool[,] g, int min)
    {
        int nx = g.GetLength(0), nz = g.GetLength(1);
        var height = new int[nx];
        (int, int, int, int)? best = null;
        int bestArea = 0;
        for (int j = 0; j < nz; j++)
        {
            for (int i = 0; i < nx; i++) height[i] = g[i, j] ? height[i] + 1 : 0;
            // every run of columns at least as tall as each one, by a stack
            var stack = new Stack<int>();
            for (int i = 0; i <= nx; i++)
            {
                int hgt = i == nx ? 0 : height[i];
                while (stack.Count > 0 && height[stack.Peek()] >= hgt)
                {
                    int top = stack.Pop();
                    int h = height[top];
                    int i0 = stack.Count == 0 ? 0 : stack.Peek() + 1;
                    int w = i - i0;
                    if (h >= min && w >= min && h * w > bestArea)
                    {
                        bestArea = h * w;
                        best = (i0, j - h + 1, i, j + 1);
                    }
                }
                stack.Push(i);
            }
        }
        return best;
    }

    private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Cross(Vector2 o, Vector2 u, Vector2 v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
}
