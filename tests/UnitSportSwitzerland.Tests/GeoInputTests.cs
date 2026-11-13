using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>The LV95 build's way in for latitudes and longitudes (#1507): rigorous, on the built tiles' ground.</summary>
public class GeoInputTests
{
    [Fact]
    public void Without_tiles_it_is_the_rigorous_conversion_at_the_plateaus_height()
    {
        var (e, n) = GeoInput.NoGround.ToLv95(46.5, 7.8);
        var (re, rn) = SwissGeodesy.ToLv95(46.5, 7.8);
        Assert.Equal(re, e, 9);
        Assert.Equal(rn, n, 9);
    }

    [Fact]
    public void A_known_height_puts_the_point_back_where_it_came_from()
    {
        // a point on the Jungfraujoch's height: the Plateau's height would put it ~10 cm off
        double e0 = 2641000, n0 = 1155000, h = 3460;
        var (lat, lon) = SwissGeodesy.ToLatLon(e0, n0, h);
        var (e, n) = GeoInput.At(lat, lon, h);
        Assert.True(Math.Abs(e - e0) < 1e-4 && Math.Abs(n - n0) < 1e-4, $"{e - e0:F6} {n - n0:F6}");
        var (pe, pn) = SwissGeodesy.ToLv95(lat, lon);
        Assert.True(Math.Sqrt((pe - e0) * (pe - e0) + (pn - n0) * (pn - n0)) > 0.05);
    }

    [Fact]
    public void Over_built_tiles_a_point_takes_their_ground()
    {
        string dir = Path.Combine(Path.GetTempPath(), "geoinput_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // one coarse companion (stride 10) of a tile on a 3,000 m plateau
            var id = new TileId(2641, 1155);
            var enc = HeightEncoding.Legacy;
            var heights = Enumerable.Repeat(enc.Quantize(3000), 101 * 101).ToArray();
            using (var fs = File.Create(Path.Combine(dir, ChunkFormat.CoarseFileName(id))))
                ChunkCodec.Encode(new ChunkGrid(id, heights, 3000, 3000, 10), fs, enc);
            var geo = GeoInput.Over(dir);
            double e0 = 2641400, n0 = 1155600, h = enc.Dequantize(enc.Quantize(3000));
            Assert.Equal(h, geo.Ground(e0, n0), 3);
            Assert.True(double.IsNaN(geo.Ground(2600000, 1200000)));
            var (lat, lon) = SwissGeodesy.ToLatLon(e0, n0, h);
            var (e, n) = geo.ToLv95(lat, lon);
            Assert.True(Math.Abs(e - e0) < 1e-4 && Math.Abs(n - n0) < 1e-4, $"{e - e0:F6} {n - n0:F6}");
            // a height the source gives does not override the ground
            var (e2, n2) = geo.ToLv95(lat, lon, 500);
            Assert.True(Math.Abs(e2 - e0) < 1e-4 && Math.Abs(n2 - n0) < 1e-4);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
