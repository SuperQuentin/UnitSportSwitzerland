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
    public double E { get; private set; }
    public double N { get; private set; }

    public WorldOrigin(double e, double n)
    {
        E = e;
        N = n;
    }

    /// <summary>
    /// Fallback anchor for a copy of the game with no terrain data at all — roughly the
    /// centre of Switzerland. Anything is better than LV95 0/0, which is 2.6 million metres
    /// away and would destroy float precision the moment real data arrived.
    /// </summary>
    public static WorldOrigin SwissDefault() => new(2660000, 1190000);

    /// <summary>
    /// Moves the anchor.
    ///
    /// <para>
    /// Only legal while nothing has been placed in the world, which in practice means "a
    /// client with no local terrain has just been told where the server's world is". Every
    /// existing world-space coordinate is an offset from the old anchor, so rebasing with
    /// chunks or players already positioned would silently teleport all of them; the caller
    /// is responsible for having nothing to invalidate.
    /// </para>
    /// </summary>
    public void Rebase(double e, double n)
    {
        if (Math.Abs(E - e) < 0.5 && Math.Abs(N - n) < 0.5) return;

        GD.Print($"[world] origin rebased from LV95 {E:F0}/{N:F0} to {e:F0}/{n:F0}");
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
