using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnitSport.Terrain.Format;
using UnitSport.Tools.Preprocessor;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// Another body's pyramid from a whole-body DEM (#1849, tools/TerrainPreprocessor/CylRaster.cs and
/// CubeBodyHeights.cs): the reader on a small BigTIFF laid out as LRO LOLA's (int16 rows, a SCALE, a NoData), and
/// heights that are a function of their lattice point, so faces share their edges to the bit.
/// </summary>
public class CubeBodyTests : IDisposable
{
    private const int W = 256, H = 128;
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cylraster-{Guid.NewGuid():N}.tif");

    public CubeBodyTests() => File.WriteAllBytes(_path, BigTiff(W, H, Value));

    public void Dispose() => File.Delete(_path);

    // a raw sample: smooth over the body, a hole at one pixel, wrapping at the antimeridian
    private static short Value(int x, int y) => x == 10 && y == 20 ? short.MinValue : (short)(y * 100 + Math.Abs(x - W / 2));

    [Fact]
    public void Reads_rows_of_int16_with_their_scale()
    {
        using var r = CylRaster.Open(_path);
        Assert.Equal(W, r.Width);
        Assert.Equal(H, r.Height);
        Assert.Equal(0.5, r.Scale);
        var all = r.Read(0, H - 1, 0, W);
        // a pixel centre: column 100, row 30
        double lon = -180 + (100 + 0.5) * r.Step, lat = 90 - (30 + 0.5) * r.Step;
        Assert.Equal(Value(100, 30) * 0.5, all.At(lat, lon), 9);
        // the hole: the mean of the valid samples round it (rows 19-22, columns 9-12: ~0.5 × (2,050 + 117.5))
        double hl = -180 + 10.5 * r.Step, ht = 90 - 20.5 * r.Step;
        Assert.InRange(all.At(ht, hl), 1078, 1090);
        // half a pixel off it, the valid neighbours carry the bilinear weight
        Assert.InRange(all.At(ht, hl + 0.5 * r.Step), 0.5 * (20 * 100 + 117) - 1, 0.5 * (20 * 100 + 118) + 1);
    }

    [Fact]
    public void A_window_wraps_round_the_antimeridian_and_matches_the_whole_body()
    {
        using var r = CylRaster.Open(_path);
        var all = r.Read(0, H - 1, 0, W);
        var wrap = r.Read(40, 80, W - 6, 12);
        foreach (var lon in new[] { 179.9, -179.9, 179.0, -179.0, 180.0 })
            Assert.Equal(all.At(0, lon), wrap.At(0, lon), 12);
        Assert.Throws<ArgumentOutOfRangeException>(() => wrap.At(0, 0));
    }

    [Fact]
    public void Faces_share_their_edges_to_the_bit_and_heights_follow_the_dem()
    {
        using var r = CylRaster.Open(_path);
        var cube = CubeShape.Moon;
        const int level = 1, bits = 4;
        var tiles = new Dictionary<CubeKey, float[]>();
        for (int face = 0; face < 6; face++)
        {
            var (y0, y1, x0, w) = CubeBodyHeights.WindowOf(cube, face, r, level);
            var window = r.Read(y0, y1, x0, w);
            for (int y = 0; y < 2; y++)
                for (int x = 0; x < 2; x++)
                {
                    var key = new CubeKey(face, level, x, y);
                    tiles[key] = CubeBodyHeights.Heights(cube, window, key, bits);
                }
        }
        // every edge vertex: the same bits from every tile that holds it
        var seen = new Dictionary<EcefPos, float>();
        int n = (1 << bits) + 1, shared = 0;
        foreach (var (key, h) in tiles)
            for (int k = 0; k < n; k++)
                foreach (var (i, j) in new[] { (k, 0), (k, n - 1), (0, k), (n - 1, k) })
                {
                    var p = key.VertexOn(cube, bits, i, j, 0);
                    float v = h[j * n + i];
                    if (seen.TryGetValue(p, out var other)) { Assert.Equal(other, v); shared++; }
                    else seen[p] = v;
                }
        Assert.True(shared > 100);
        // and a face centre carries the DEM's value there (0° N 0° E: row 63.5, column 128: |x − 128| small)
        float centre = tiles[new CubeKey(0, level, 1, 1)][0];
        Assert.InRange(centre, 0.5 * (63 * 100), 0.5 * (64 * 100 + 2));
    }

    /// <summary>A BigTIFF as LOLA's: one band of int16, one row a strip, uncompressed, GDAL's SCALE 0.5, NoData −32768.</summary>
    private static byte[] BigTiff(int w, int h, Func<int, int, short> value)
    {
        var tags = new List<(ushort Tag, ushort Type, long Count, byte[] Value)>();
        byte[] U16(ushort v) => BitConverter.GetBytes(v);
        byte[] U32(uint v) => BitConverter.GetBytes(v);
        string meta = "<GDALMetadata><Item name=\"OFFSET\" sample=\"0\" role=\"offset\">0</Item><Item name=\"SCALE\" sample=\"0\" role=\"scale\">0.5</Item></GDALMetadata>\0";
        const int ifdAt = 16;
        int entries = 11;
        long dataAt = ifdAt + 8 + entries * 20 + 8;
        long offsetsAt = dataAt, countsAt = offsetsAt + 8L * h, metaAt = countsAt + 8L * h, pixelsAt = metaAt + meta.Length;
        var offsets = new byte[8 * h];
        var counts = new byte[8 * h];
        for (int y = 0; y < h; y++)
        {
            BitConverter.GetBytes(pixelsAt + (long)y * w * 2).CopyTo(offsets, 8 * y);
            BitConverter.GetBytes((long)w * 2).CopyTo(counts, 8 * y);
        }
        tags.Add((256, 4, 1, U32((uint)w)));
        tags.Add((257, 4, 1, U32((uint)h)));
        tags.Add((258, 3, 1, U16(16)));
        tags.Add((259, 3, 1, U16(1)));
        tags.Add((273, 16, h, BitConverter.GetBytes(offsetsAt)));
        tags.Add((277, 3, 1, U16(1)));
        tags.Add((278, 3, 1, U16(1)));
        tags.Add((279, 16, h, BitConverter.GetBytes(countsAt)));
        tags.Add((339, 3, 1, U16(2)));
        tags.Add((42112, 2, meta.Length, BitConverter.GetBytes(metaAt)));
        tags.Add((42113, 2, 7, Encoding.ASCII.GetBytes("-32768\0")));
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((byte)'I'); bw.Write((byte)'I'); bw.Write((ushort)43); bw.Write((ushort)8); bw.Write((ushort)0); bw.Write((long)ifdAt);
        bw.Write((long)entries);
        foreach (var (tag, type, count, v) in tags)
        {
            bw.Write(tag); bw.Write(type); bw.Write(count);
            var slot = new byte[8];
            v.CopyTo(slot, 0);
            bw.Write(slot);
        }
        bw.Write(0L);
        bw.Write(offsets);
        bw.Write(counts);
        bw.Write(Encoding.ASCII.GetBytes(meta));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bw.Write(value(x, y));
        bw.Flush();
        return ms.ToArray();
    }
}
