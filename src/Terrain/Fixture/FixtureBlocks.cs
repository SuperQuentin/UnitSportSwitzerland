using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Fixture;

/// <summary>
/// The buildings of a fixture course (#558): a box with four walls and a flat roof, as the door
/// checks' synthetic solids are, in a tile's own frame (X east of its west edge, Z south of its
/// north edge). The front is the wall facing south (+Z), turned with the block.
/// </summary>
public static class FixtureBlocks
{
    public static Building Solid(FixtureCourse.Block block, float cx, float cz)
    {
        float hw = block.Width / 2, hd = block.Depth / 2, h = block.Height;
        var tris = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 c) => tris.AddRange([a.X, a.Y, a.Z, b.X, b.Y, b.Z, c.X, c.Y, c.Z]);
        float turn = Mathf.DegToRad(block.Turn);
        Vector3 Corner(float x, float z) =>
            new(cx + x * Mathf.Cos(turn) - z * Mathf.Sin(turn), 0, cz + x * Mathf.Sin(turn) + z * Mathf.Cos(turn));
        void Wall(Vector3 p0, Vector3 p1)
        {
            Tri(p0, p1, p1 + Vector3.Up * h);
            Tri(p0, p1 + Vector3.Up * h, p0 + Vector3.Up * h);
        }
        var nw = Corner(-hw, -hd);
        var ne = Corner(hw, -hd);
        var se = Corner(hw, hd);
        var sw = Corner(-hw, hd);
        Wall(nw, ne); Wall(ne, se); Wall(se, sw); Wall(sw, nw);
        Tri(nw + Vector3.Up * h, ne + Vector3.Up * h, se + Vector3.Up * h);
        Tri(nw + Vector3.Up * h, se + Vector3.Up * h, sw + Vector3.Up * h);
        return new Building
        {
            Kind = block.Kind, MinY = (float)FixtureCourse.FlatHeight, MaxY = (float)FixtureCourse.FlatHeight + h,
            Floors = (byte)Math.Max(1, (int)(h / 3f)),
            Triangles = tris.Select((v, i) => i % 3 == 1 ? v + (float)FixtureCourse.FlatHeight : v).ToArray(),
        };
    }
}
