using UnitSport.Terrain.Format;
using UnitSport.Terrain.Leaves;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// Leaves from the server (#1655, G2 C7): the <see cref="LeafIndex"/> a server sends, and <see cref="LeafStore"/>'s source
/// chain (its disk, then the server's leaves through a <see cref="LeafFetch"/>). The transfer itself is
/// <c>tools/leafstreamcheck.sh</c>'s.
/// </summary>
public class LeafStreamTests
{
    private static readonly CubeKey A = LeafFrames.LeafAt(46.2044, 6.1432);
    private static readonly CubeKey B = A.Neighbour(1, 0)!.Value;
    private const int N = 513;

    private static byte[] Entry(CubeKey key, float height, bool trees = true)
    {
        var h = new float[N * N];
        Array.Fill(h, height);
        var content = new CubeLeafContent { Key = key, Heights = new CubeTile { Key = key, Bits = 9, Heights = h } };
        if (trees) content.Trees = CubeLeafSamples.Trees();
        return CubeLeafCodec.Encode(content.ToLeaf());
    }

    [Fact]
    public void The_index_reads_back_and_names_what_changed()
    {
        var index = new LeafIndex(new Dictionary<CubeKey, LeafIndex.Entry> { [A] = new(1234, 7), [B] = new(99, 0xDEADBEEF) });
        var back = LeafIndex.FromBytes(index.ToBytes())!;
        Assert.Equal(index.Leaves.OrderBy(kv => kv.Key.X), back.Leaves.OrderBy(kv => kv.Key.X));

        // a rebuilt bundle (new stamp), a leaf gone, a leaf added
        var c = B.Neighbour(1, 0)!.Value;
        var after = new Dictionary<CubeKey, LeafIndex.Entry> { [A] = new(1234, 8), [c] = new(5, 1) };
        Assert.Equal(new[] { A, B, c }.OrderBy(k => k.X), LeafIndex.Changed(index.Leaves, after).OrderBy(k => k.X));
        Assert.Empty(LeafIndex.Changed(index.Leaves, index.Leaves));

        // not an index: refused, never half read
        Assert.Null(LeafIndex.FromBytes(index.ToBytes().AsSpan(0, 20)));
        Assert.Null(LeafIndex.FromBytes(new byte[30]));
    }

    [Fact]
    public void A_remote_leaf_that_did_not_come_is_asked_again_and_then_kept()
    {
        var bytes = Entry(A, 512f);
        int asked = 0;
        bool up = false;
        var store = LeafStore.None();
        Assert.False(store.Has(A));
        var changed = store.UseRemote(new LeafIndex(new Dictionary<CubeKey, LeafIndex.Entry> { [A] = new(bytes.Length, 1) }),
            (key, entry, section) => { asked++; return up ? bytes : null; });
        Assert.Equal(new[] { A }, changed);
        Assert.True(store.Has(A) && store.IsReal(A) && !store.IsLocal(A));
        Assert.Equal(bytes.Length, store.Bytes(A));

        // the server busy: nothing, and nothing remembered
        Assert.Null(store.Get(A));
        up = true;
        var leaf = store.Get(A);
        Assert.NotNull(leaf);
        Assert.Equal(512f, leaf!.Heights!.Heights[0]);
        // held now: no third transfer
        Assert.Same(leaf, store.Get(A));
        Assert.Equal(2, asked);

        // the same index again changes nothing; a rebuilt bundle drops the held leaf
        Assert.Empty(store.UseRemote(new LeafIndex(new Dictionary<CubeKey, LeafIndex.Entry> { [A] = new(bytes.Length, 1) }), (_, _, _) => bytes));
        Assert.Same(leaf, store.Cached(A));
        Assert.Equal(new[] { A }, store.UseRemote(new LeafIndex(new Dictionary<CubeKey, LeafIndex.Entry> { [A] = new(bytes.Length, 2) }), (_, _, _) => bytes));
        Assert.Null(store.Cached(A));
    }

    [Fact]
    public void Heights_and_roads_alone_ask_for_their_section_only()
    {
        var bytes = Entry(A, 700f);
        var sections = new List<LeafKind?>();
        var store = LeafStore.None();
        store.UseRemote(new LeafIndex(new Dictionary<CubeKey, LeafIndex.Entry> { [A] = new(bytes.Length, 1) }),
            (key, entry, section) =>
            {
                sections.Add(section);
                return section is { } s ? LeafStore.OneSection(bytes, s) : bytes;
            });
        Assert.Equal(700f, store.HeightsOf(A)!.Heights[0]);
        Assert.Null(store.RoadsOf(A));
        Assert.Equal(new LeafKind?[] { LeafKind.Heights, LeafKind.Roads }, sections);

        // the cut entry holds the heights alone, smaller than the whole
        var cut = LeafStore.OneSection(bytes, LeafKind.Heights);
        Assert.Equal(new[] { LeafKind.Heights }, CubeLeafCodec.ReadTable(cut).Table.Select(t => t.Kind));
        Assert.True(cut.Length < bytes.Length);
        Assert.Empty(CubeLeafCodec.ReadTable(LeafStore.OneSection(bytes, LeafKind.Roads)).Table);
    }

    [Fact]
    public void A_server_indexes_its_bundles_and_a_client_keeps_its_own_leaves_first()
    {
        string dir = Path.Combine(Path.GetTempPath(), "leafstream_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var bundle = A.AncestorAt(9);
            string path = Path.Combine(dir, CubeBundle.LeafFileName(bundle));
            CubeBundle.Write(path, bundle, [(A, Entry(A, 100f))], CubeBundle.LeafMagic);
            var server = LeafStore.Open(dir);
            var index = LeafIndex.Of(server);
            Assert.Equal(new[] { A }, index.Leaves.Keys);
            uint stamp = index.Leaves[A].Stamp;
            Assert.NotEqual(0u, stamp);
            Assert.Equal(Entry(A, 100f), server.LocalEntry(A));

            // rebuilt with another leaf in it: the stamp moves
            CubeBundle.Write(path, bundle, [(A, Entry(A, 100f, trees: false)), (B, Entry(B, 1f))], CubeBundle.LeafMagic);
            var rebuilt = LeafIndex.Of(LeafStore.Open(dir));
            Assert.NotEqual(stamp, rebuilt.Leaves[A].Stamp);

            // a client with A on its disk takes only B from the server, and never fetches A
            var client = LeafStore.Open(dir);
            var remote = new LeafIndex(new Dictionary<CubeKey, LeafIndex.Entry> { [A] = new(1, 1), [B.Neighbour(0, 1)!.Value] = new(1, 1) });
            var changed = client.UseRemote(remote, (key, _, _) => key == A ? throw new InvalidOperationException("fetched a local leaf") : null);
            Assert.Equal(new[] { B.Neighbour(0, 1)!.Value }, changed);
            Assert.Equal(100f, client.Get(A)!.Heights!.Heights[0]);
            Assert.Equal(3, client.Count);
            Assert.Equal(1, client.RemoteCount);
            Assert.Equal(2, client.LocalKeys.Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
