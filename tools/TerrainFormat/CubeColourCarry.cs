namespace UnitSport.Terrain.Format;

/// <summary>
/// A pyramid level's colour across a rebuild of its heights (#1659). A tile's colour (<c>.cuba</c>, Blue Marble over
/// its footprint, <c>World/Globe/GlobeAlbedoBake</c>) is a function of its key alone, never of its heights, so when
/// <c>--cube-swiss</c> builds a level's heights again its colour bundles still hold for every tile they have. Dropping
/// the level's <see cref="CubeManifestLevel.Albedo"/> instead left the bundles on disk unread and the globe drew the
/// level uncoloured: B4's pyramid was white from level 5 down, a rectangle west of Switzerland seen from space.
/// </summary>
public static class CubeColourCarry
{
    /// <summary>
    /// Gives <paramref name="rebuilt"/> the colour of <paramref name="before"/> (the same level before the rebuild) when
    /// it had one and its bundles in <paramref name="dir"/> are there, and returns the rebuilt tiles that have no colour
    /// in them (to bake: <c>--globealbedo</c>). A level that had no colour is left without, every tile returned.
    /// </summary>
    public static List<CubeKey> Carry(string dir, CubeManifestLevel? before, CubeManifestLevel rebuilt, IEnumerable<CubeKey> tiles)
    {
        var all = tiles.ToList();
        if (before is not { Albedo: > 0 }) return all;
        var coloured = new HashSet<CubeKey>();
        bool any = false;
        foreach (var bundle in all.Select(k => k.AncestorAt(before.AlbedoBundleLevel)).Distinct())
        {
            string path = Path.Combine(dir, CubeBundle.AlbedoFileName(bundle));
            if (!File.Exists(path)) continue;
            using var s = File.OpenRead(path);
            foreach (var key in CubeBundle.ReadIndex(s, CubeBundle.AlbedoMagic).Keys) coloured.Add(key);
            any = true;
        }
        if (!any) return all;
        rebuilt.Albedo = before.Albedo;
        rebuilt.AlbedoBundleLevel = before.AlbedoBundleLevel;
        return all.Where(k => !coloured.Contains(k)).ToList();
    }
}
