using System.Collections.Generic;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Bodies;

namespace UnitSport.World.Bodies;

/// <summary>
/// The other bodies' heights on the dedicated server (#1852, C3b): one <see cref="BodyHeights"/> per landable body, read
/// from <c>--bodies</c> (each body's pyramid in <c>DIR/&lt;body&gt;/cube</c>, as the clients' <see cref="BodyViews"/>) on
/// first use; a body without data stands on its generated relief over its datum (the clients that have no data draw no
/// ground there at all). Its regions' grounds (<see cref="Ground"/>) and the interest's height above ground ask it.
/// </summary>
public static class ServerBodies
{
    private static readonly Dictionary<BodyId, BodyHeights?> Cache = new();
    private static readonly object Gate = new();

    /// <summary>A landable body's heights (null for the Earth and for a body nobody stands on).</summary>
    public static BodyHeights? Heights(BodyId id)
    {
        if (id == BodyId.Earth) return null;
        lock (Gate)
        {
            if (Cache.TryGetValue(id, out var known)) return known;
            var body = CelestialBody.Of(id);
            BodyHeights? made = null;
            if (body.Landable)
            {
                Terrain.Leaves.PyramidSource? pyramid = null;
                if (BodyViews.DataRoot is { } root)
                {
                    string dir = System.IO.Path.Combine(root, body.Name.ToLowerInvariant(), "cube");
                    if (GlobeFiles.HasManifest(dir)) pyramid = new Terrain.Leaves.PyramidSource([new GlobeFiles(dir).Layer(dir, local: true)]);
                }
                made = BodyGround.HeightsOf(body, pyramid, pyramid != null ? System.Math.Max(pyramid.MaxLevel, 0) : 0);
                GD.Print($"[bodies] the server's {body.Name}: {(pyramid != null ? $"its pyramid to level {pyramid.MaxLevel}" : "no data, the generated relief on its datum")}");
            }
            Cache[id] = made;
            return made;
        }
    }

    /// <summary>A server region's ground on another body (<c>ServerRegions.MakeBodyGround</c>): null for a body nobody stands on.</summary>
    public static (Node Terrain, Terrain.IWorldGround Ground)? Ground(WorldOrigin frame)
    {
        if (Heights(frame.Body) is not { } heights) return null;
        var g = new ServerBodyGround(CelestialBody.Of(frame.Body), heights, frame);
        return (g, g);
    }

    /// <summary>The ground's height over the body's reference surface under a place on it, metres; null off a landable body.</summary>
    public static double? HeightUnder(GlobalPos at)
    {
        if (Heights(at.Body) is not { } heights) return null;
        return heights.At(heights.NormalUnder(at.Ecef));
    }
}
