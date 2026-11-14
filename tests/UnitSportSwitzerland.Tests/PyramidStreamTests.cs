using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// The globe's pyramid from every place the game has it (#1656): the <see cref="PyramidIndex"/> a server sends, and
/// <see cref="PyramidSource"/>'s chain (this copy's disk, the server's, the shipped base; the finest copy wins), and the fill's
/// base as the server stands it. The transfer itself is <c>tools/pyramidstreamcheck.sh</c>'s.
/// </summary>
public class PyramidStreamTests
{
    private static readonly CubeKey Bern = CubeKey.FromLatLon(46.95, 7.44, 9);

    private static byte[] Heights(CubeKey key, int bits, float h)
    {
        int n = (1 << bits) + 1;
        var a = new float[n * n];
        Array.Fill(a, h);
        return CubeTileCodec.Encode(new CubeTile { Key = key, Bits = bits, Heights = a, Quantum = CubeKey.QuantumFor(key.Level) });
    }

    private static PyramidSource.Layer Layer(string name, bool local, params (CubeKey Key, int Bits, float H)[] tiles) =>
        new(name, null, tiles.ToDictionary(t => t.Key, t =>
        {
            var bytes = Heights(t.Key, t.Bits, t.H);
            return new PyramidSource.Tile(t.Bits, () => bytes, null, local);
        }));

    [Fact]
    public void The_index_reads_back()
    {
        var index = new PyramidIndex(new Dictionary<CubeKey, PyramidIndex.Entry>
        {
            [Bern] = new(9, 1234, 1, 7),
            [Bern.Parent] = new(6, 99, 0, 0xDEADBEEF),
        }, [422.2f, 372.5f]);
        var back = PyramidIndex.FromBytes(index.ToBytes())!;
        Assert.Equal(index.Tiles, back.Tiles);
        Assert.Equal(index.Lakes, back.Lakes);
        Assert.Null(PyramidIndex.FromBytes(index.ToBytes()[..^1]));
    }

    [Fact]
    public void The_finest_copy_wins_and_ties_go_to_the_local_data()
    {
        var shipped = Layer("shipped", false, (Bern, 6, 100));
        var local = Layer("local", true, (Bern.Parent, 9, 50));
        var chain = new PyramidSource([local], [shipped]);
        Assert.Equal(6, chain.BitsOf(Bern));
        Assert.False(chain.IsLocal(Bern));
        Assert.True(chain.IsLocal(Bern.Parent));

        // the server's 513² copy of the tile beats the shipped 65² one
        var serverBytes = Heights(Bern, 9, 200);
        var index = new PyramidIndex(new Dictionary<CubeKey, PyramidIndex.Entry> { [Bern] = new(9, serverBytes.Length, 0, PyramidIndex.StampOf(Bern, serverBytes)) });
        int fetched = 0;
        var changed = chain.UseRemote(index, (k, e, s) => { fetched++; return serverBytes; });
        Assert.Equal([Bern], changed);
        Assert.True(chain.IsStreamed(Bern));
        Assert.Equal(200, chain.Heights(Bern)!.HeightAt(0, 0), 2);
        Assert.Equal(1, fetched);

        // the same index again changes nothing
        Assert.Empty(chain.UseRemote(index, (k, e, s) => serverBytes));
        chain.DropRemote();
        Assert.Equal(6, chain.BitsOf(Bern));
    }

    [Fact]
    public void The_server_base_reads_this_copys_tile_only_where_its_stamp_is_the_servers()
    {
        // a level deep enough for the fill (PyramidBase.MinFinestLevel)
        var key = CubeKey.FromLatLon(46.95, 7.44, PyramidBase.MinFinestLevel);
        var same = Heights(key, 9, 600);
        var other = CubeKey.FromLatLon(46.2, 6.1, PyramidBase.MinFinestLevel);
        var mine = Heights(other, 9, 300);
        var theirs = Heights(other, 9, 310);
        var chain = new PyramidSource([new PyramidSource.Layer("local", null, new Dictionary<CubeKey, PyramidSource.Tile>
        {
            [key] = new(9, () => same, null, true),
            [other] = new(9, () => mine, null, true),
        })]);
        var index = new PyramidIndex(new Dictionary<CubeKey, PyramidIndex.Entry>
        {
            [key] = new(9, same.Length, 0, PyramidIndex.StampOf(key, same)),
            [other] = new(9, theirs.Length, 0, PyramidIndex.StampOf(other, theirs)),
        });
        var asked = new List<CubeKey>();
        chain.UseRemote(index, (k, e, s) => { asked.Add(k); return k == other ? theirs : null; });
        var base_ = chain.ServerBase()!;
        int lattice = PyramidBase.MinFinestLevel + 9;
        long Gi(CubeKey k) => ((long)k.X << 9) + 3;
        long Gj(CubeKey k) => ((long)k.Y << 9) + 3;
        Assert.Equal(600, base_.HeightAt(key.Face, Gi(key), Gj(key), lattice), 1);
        Assert.Equal(310, base_.HeightAt(other.Face, Gi(other), Gj(other), lattice), 1);
        Assert.Equal([other], asked);
        // this copy's own base keeps its own tile
        Assert.Equal(300, chain.Base()!.HeightAt(other.Face, Gi(other), Gj(other), lattice), 1);
    }
}
