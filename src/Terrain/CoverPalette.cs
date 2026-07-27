using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain;

/// <summary>
/// Ground colour per terrain vertex. Real swisstopo cover classes decide the surface;
/// altitude only fills in where TLM maps nothing (open farmland, meadow, alpine turf),
/// which is where the old altitude-band guesswork used to be wrong.
/// </summary>
public static class CoverPalette
{
    public static Color ColorFor(CoverClass cover, float altitude, float hash)
    {
        // hash in [0,1) gives stable per-vertex variation so large areas are not flat
        float v = (hash - 0.5f) * 0.06f;

        Color c = cover switch
        {
            CoverClass.Forest => new Color(0.16f, 0.29f, 0.15f),
            CoverClass.OpenForest => new Color(0.22f, 0.35f, 0.18f),
            CoverClass.Woodland => new Color(0.24f, 0.36f, 0.19f),
            CoverClass.Shrub => new Color(0.31f, 0.38f, 0.22f),
            CoverClass.Rock => new Color(0.46f, 0.43f, 0.40f),
            CoverClass.LooseRock => new Color(0.50f, 0.47f, 0.44f),
            CoverClass.Scree => new Color(0.55f, 0.52f, 0.47f),
            CoverClass.LooseScree => new Color(0.58f, 0.55f, 0.50f),
            CoverClass.Water => new Color(0.20f, 0.32f, 0.42f),
            CoverClass.Wetland => new Color(0.34f, 0.40f, 0.28f),
            CoverClass.Glacier => new Color(0.86f, 0.90f, 0.94f),
            CoverClass.Snowfield => new Color(0.80f, 0.84f, 0.88f),
            CoverClass.Boulders => new Color(0.49f, 0.46f, 0.42f),

            // cultivated ground: warmer and drier than pasture, because it is bare soil
            // between the plants for most of the year
            CoverClass.Vineyard => new Color(0.42f, 0.45f, 0.24f),
            CoverClass.Orchard => new Color(0.38f, 0.48f, 0.25f),
            CoverClass.Nursery => new Color(0.36f, 0.46f, 0.26f),
            CoverClass.Allotment => new Color(0.44f, 0.45f, 0.30f),
            CoverClass.Clearcut => new Color(0.40f, 0.42f, 0.26f),   // stumps and slash

            // managed green space: mown, so greener and more even than anything natural
            CoverClass.Park or CoverClass.Cemetery => new Color(0.30f, 0.47f, 0.24f),
            CoverClass.SportsField or CoverClass.Golf => new Color(0.26f, 0.50f, 0.22f),
            CoverClass.GrassStrip => new Color(0.34f, 0.49f, 0.25f),
            CoverClass.Campsite => new Color(0.35f, 0.46f, 0.27f),
            CoverClass.Leisure => new Color(0.38f, 0.44f, 0.30f),

            CoverClass.Pool => new Color(0.28f, 0.52f, 0.62f),
            CoverClass.Institution => new Color(0.46f, 0.46f, 0.42f),
            CoverClass.Industrial => new Color(0.42f, 0.42f, 0.41f),
            CoverClass.Quarry => new Color(0.60f, 0.56f, 0.48f),     // fresh cut rock
            CoverClass.Landfill => new Color(0.45f, 0.42f, 0.35f),
            CoverClass.Military => new Color(0.42f, 0.43f, 0.33f),
            CoverClass.Runway or CoverClass.Platform => new Color(0.33f, 0.33f, 0.33f),

            CoverClass.ParkingPublic or CoverClass.RestArea => new Color(0.34f, 0.34f, 0.33f),
            CoverClass.ParkingPrivate => new Color(0.37f, 0.36f, 0.34f),
            CoverClass.PavedArea => new Color(0.36f, 0.36f, 0.35f),
            _ => OpenGround(altitude),
        };

        // snow caps everything above the permanent line except open water
        if (altitude > 2900f && cover != CoverClass.Water)
        {
            float t = Mathf.Clamp((altitude - 2900f) / 250f, 0f, 1f);
            c = c.Lerp(new Color(0.92f, 0.94f, 0.96f), t);
        }

        // Alpha carries the surface pattern the shader should draw (bays, vine rows, mown
        // stripes), in quarter steps. Piggybacking on the vertex colour avoids a second
        // attribute stream; the cost is that alpha interpolates across a class boundary,
        // so a pattern can bleed one 2 m cell into its neighbour.
        float pattern = (byte)CoverFormat.PatternFor(cover) / 4f;
        return new Color(
            Mathf.Clamp(c.R + v, 0f, 1f),
            Mathf.Clamp(c.G + v, 0f, 1f),
            Mathf.Clamp(c.B + v, 0f, 1f),
            pattern);
    }

    /// <summary>Unmapped ground: pasture low down, alpine turf then bare ground higher up.</summary>
    private static Color OpenGround(float altitude)
    {
        if (altitude < 900f) return new Color(0.33f, 0.47f, 0.22f);   // valley farmland
        if (altitude < 1500f) return new Color(0.36f, 0.47f, 0.24f);  // pasture
        if (altitude < 2100f) return new Color(0.43f, 0.47f, 0.27f);  // alpine meadow
        if (altitude < 2600f) return new Color(0.48f, 0.47f, 0.36f);  // sparse turf
        return new Color(0.52f, 0.50f, 0.45f);                        // bare ground
    }

    /// <summary>Cheap stable hash of a grid position, in [0,1).</summary>
    public static float Hash(int col, int row)
    {
        uint h = (uint)(col * 73856093) ^ (uint)(row * 19349663);
        h ^= h >> 13;
        h *= 0x85EBCA6B;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000;
    }
}
