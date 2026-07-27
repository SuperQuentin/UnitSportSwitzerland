using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Core;

/// <summary>
/// Configurable LV95 anchor mapping geodata coordinates to Godot world space:
/// x = east offset, y = altitude, z = -north offset. All terrain math stays in double
/// LV95; floats appear only here at the render/physics boundary. For all-Switzerland
/// scale this is where a floating-origin shift would plug in later.
/// </summary>
public sealed class WorldOrigin
{
    public double E { get; }
    public double N { get; }

    public WorldOrigin(double e, double n)
    {
        E = e;
        N = n;
    }

    public Vector3 ToWorld(double lv95E, double lv95N, double altitude) =>
        new((float)(lv95E - E), (float)altitude, (float)-(lv95N - N));

    public (double E, double N) ToLv95(Vector3 world) => (E + world.X, N - world.Z);

    public TileId TileAt(Vector3 world)
    {
        var (e, n) = ToLv95(world);
        return TileId.FromLv95(e, n);
    }
}
