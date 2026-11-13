namespace UnitSport.Terrain.Biomes;

/// <summary>
/// What a fill leaf looks like at a point outside the generator's own map (#1809, <c>docs/plans/fill-biomes.md</c>):
/// <see cref="FillBiome.At"/> decides it, <see cref="BiomePass"/> applies it to the generator's leaf. <see cref="Swiss"/>
/// is the generator unchanged; the temperate classes, cropland and urban (land cover only) look Swiss too.
/// </summary>
public enum Biome : byte
{
    Swiss = 0,
    TemperateGrass = 1,
    TemperateForest = 2,
    Cropland = 3,
    Urban = 4,
    Wetland = 5,
    Desert = 6,
    Steppe = 7,
    Savanna = 8,
    TropicalForest = 9,
    Boreal = 10,
    Tundra = 11,
    Ice = 12,
    Sea = 13,
}

/// <summary>How a decided biome was reached (the chain's step), for probes and logs.</summary>
public enum BiomeSource : byte
{
    /// <summary>Inside the generator's own map: Swiss whatever the data says.</summary>
    Guard = 0,
    LandCover = 1,
    PhotoGuess = 2,
    /// <summary>No input knew the point: today's Swiss generator.</summary>
    Generator = 3,
}
