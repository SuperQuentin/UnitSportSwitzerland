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
    public static List<RectPlan>? Wings(Building b, Vector2 center, Vector2 axisU, float width, float depth)
    {
        var axisV = new Vector2(-axisU.Y, axisU.X);
        int nx = Math.Max(1, (int)MathF.Ceiling(width / Cell)), nz = Math.Max(1, (int)MathF.Ceiling(depth / Cell));
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
            int i0 = Math.Max(0, (int)(Math.Min(pa.X, Math.Min(pc.X, pd.X)) / Cell)), i1 = Math.Min(nx - 1, (int)(Math.Max(pa.X, Math.Max(pc.X, pd.X)) / Cell));
            int j0 = Math.Max(0, (int)(Math.Min(pa.Y, Math.Min(pc.Y, pd.Y)) / Cell)), j1 = Math.Min(nz - 1, (int)(Math.Max(pa.Y, Math.Max(pc.Y, pd.Y)) / Cell));
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                    if (!grid[i, j] && InTriangle(new Vector2((i + 0.5f) * Cell, (j + 0.5f) * Cell), pa, pc, pd))
                    {
                        grid[i, j] = true;
                        any = true;
                    }
        }
        if (!any) return null;
        return Peel(grid, width, depth);
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
