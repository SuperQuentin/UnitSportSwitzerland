using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;

namespace UnitSport.World.Bodies;

/// <summary>
/// Another body's pyramid for this process (docs/plans/planets.md section 4, #1849): one <see cref="PyramidSource"/> a body,
/// the Earth's chain (<see cref="GlobeData"/>, #1656) for each of them. Finest copy first:
/// <list type="number">
/// <item>the local install, <c>&lt;dir&gt;/&lt;body&gt;/cube</c> under <see cref="TerrainPaths.FindBodiesDir"/> (<c>--bodies</c>,
/// a <c>terrain_location.json</c>'s "bodies", or <c>planets</c> beside the chunk folder);</item>
/// <item>the server's, once its index for that body arrives (<see cref="PyramidSource.UseRemote"/>, the body in the
/// request's high bits: <c>AssetStream.CubeSection</c>);</item>
/// <item>the base the game ships, <c>res://assets/bodies/&lt;body&gt;</c> (levels 0-2, <c>--globebase</c>);</item>
/// <item>generated, where a body has none of these: its sky map (or its tint) on the reference surface, levels 0-1, the
/// profile's craters and relief under it as everywhere (<see cref="Terrain.Bodies.BodyHeights"/>).</item>
/// </list>
/// The Earth is <see cref="GlobeData.Earth"/>; the Sun has none.
/// </summary>
public static class BodyData
{
    private static readonly Dictionary<BodyId, PyramidSource> Chains = new();
    private static readonly object Gate = new();

    /// <summary>The shipped base's folder for a body.</summary>
    public static string ShippedDir(CelestialBody body) => $"res://assets/bodies/{body.Name.ToLowerInvariant()}";

    /// <summary>The local install's folder for a body, or null where this copy has none.</summary>
    public static string? LocalDir(CelestialBody body) =>
        TerrainPaths.FindBodiesDir() is { } root && Path.Combine(root, body.Name.ToLowerInvariant(), "cube") is var dir
        && GlobeFiles.HasManifest(dir) ? dir : null;

    /// <summary>Whether a body gets a pyramid (and so a globe) at all: everything but the Earth (its own chain) and the Sun.</summary>
    public static bool HasGlobe(CelestialBody body) => body.Id is not BodyId.Earth and not BodyId.Sun;

    /// <summary>A body's chain, made on the first ask (any thread); null for the Earth and the Sun.</summary>
    public static PyramidSource? For(CelestialBody body)
    {
        if (!HasGlobe(body)) return null;
        lock (Gate)
        {
            if (Chains.TryGetValue(body.Id, out var chain)) return chain;
            var local = new List<PyramidSource.Layer>();
            if (LocalDir(body) is { } dir) local.Add(new GlobeFiles(dir).Layer(dir, local: true));
            var fallback = new List<PyramidSource.Layer>();
            string shipped = ShippedDir(body);
            if (GlobeFiles.HasManifest(shipped)) fallback.Add(new GlobeFiles(shipped).Layer("the shipped base", local: false));
            else if (local.Count == 0) fallback.Add(Generated(body));
            chain = new PyramidSource(local, fallback);
            Chains[body.Id] = chain;
            GD.Print($"[bodies] {body.Name}'s pyramid: {chain.Describe()}");
            return chain;
        }
    }

    /// <summary>Whether this copy has a body's pyramid on its own disk (what a server serves).</summary>
    public static bool HasLocal(CelestialBody body) => For(body) is { } c && c.Keys.Any(c.IsLocal);

    /// <summary>Levels a generated body has: its sky map's 2k is about four 128² tiles a face.</summary>
    public const int GeneratedLevels = 2;

    /// <summary>Colour a generated tile has, a side.</summary>
    public const int GeneratedColour = 128;

    /// <summary>
    /// A body with no pyramid anywhere (#1849): levels 0-1 on the reference surface (the heights 0; the profile's craters
    /// and relief come under them in <see cref="Terrain.Bodies.BodyHeights"/>), coloured from its sky map, or its sky tint
    /// where it has none. Made lazily, a tile at a time, on whichever thread asks.
    /// </summary>
    public static PyramidSource.Layer Generated(CelestialBody body)
    {
        var map = new Lazy<EquirectMap?>(() => SkyMap(body), LazyThreadSafetyMode.ExecutionAndPublication);
        var tint = SkyBodies.TintOf(body.Sky ?? SkyBody.Moon);
        var cube = body.Cube;
        const int bits = 6;
        var tiles = new Dictionary<CubeKey, PyramidSource.Tile>();
        for (int level = 0; level < GeneratedLevels; level++)
            for (int face = 0; face < CubeSphere.Faces; face++)
                for (int y = 0; y < 1 << level; y++)
                    for (int x = 0; x < 1 << level; x++)
                    {
                        var key = new CubeKey(face, level, x, y);
                        var heights = CubeTileCodec.Encode(new CubeTile { Key = key, Bits = bits, Heights = new float[((1 << bits) + 1) * ((1 << bits) + 1)] });
                        var colour = new Lazy<byte[]?>(() => Colour(key, map.Value, tint, cube), LazyThreadSafetyMode.ExecutionAndPublication);
                        tiles[key] = new PyramidSource.Tile(bits, () => heights, _ => colour.Value, Local: false);
                    }
        return new PyramidSource.Layer("generated from its sky map", null, tiles);
    }

    private static byte[]? Colour(CubeKey key, EquirectMap? map, Color tint, CubeShape cube)
    {
        int size = GeneratedColour;
        byte[] px;
        if (map != null) px = GlobeAlbedoBake.Pixels(map, key, size, cube);
        else
        {
            px = new byte[size * size * 3];
            var srgb = tint.LinearToSrgb();
            for (int i = 0; i < size * size; i++)
            {
                px[i * 3] = (byte)(srgb.R * 255);
                px[i * 3 + 1] = (byte)(srgb.G * 255);
                px[i * 3 + 2] = (byte)(srgb.B * 255);
            }
        }
        var image = Image.CreateFromData(size, size, false, Image.Format.Rgb8, px);
        return CubeAlbedoCodec.Encode(new CubeAlbedo { Key = key, Size = size, Months = [image.SaveWebpToBuffer(true, 0.85f)] });
    }

    /// <summary>A body's 2k sky map as an equirectangular map, or null where it has none.</summary>
    private static EquirectMap? SkyMap(CelestialBody body)
    {
        if (body.Sky is not { } sky || SkyBodies.MapOf(sky) is not { } path || !Godot.FileAccess.FileExists(path)) return null;
        var image = GD.Load<Texture2D>(path)?.GetImage();
        if (image == null) return null;
        if (image.IsCompressed()) image.Decompress();
        if (image.GetFormat() != Image.Format.Rgb8) image.Convert(Image.Format.Rgb8);
        return new EquirectMap(image.GetData(), image.GetWidth(), image.GetHeight());
    }
}
