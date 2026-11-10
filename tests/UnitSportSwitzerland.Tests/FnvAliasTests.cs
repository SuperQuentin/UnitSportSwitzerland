using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// #1658: a building on a leaf seeds every choice (plan, shop, bank, loot, locks) from its Earth identity, not from the
/// stand-in tile's key; the flat world's keys hash as they always did.
/// </summary>
public class FnvAliasTests
{
    [Fact]
    public void A_stand_in_key_seeds_as_its_building_id_with_every_suffix()
    {
        Fnv.Alias("100417_101234_7", "b4620412_614871");
        Assert.Equal(Plain("b4620412_614871"), Fnv.Hash("100417_101234_7"));
        Assert.Equal(Plain("b4620412_614871|cellar"), Fnv.Hash("100417_101234_7|cellar"));
        Assert.Equal(Plain("b4620412_614871#3#film"), Fnv.Hash("100417_101234_7#3#film"));
        // a door's slot rides along
        Assert.Equal(Plain("b4620412_614871_2"), Fnv.Hash("100417_101234_7_2"));
        Assert.Equal(Plain("b4620412_614871_2|lock|1"), Fnv.Hash("100417_101234_7_2|lock|1"));
        Assert.Equal(Fnv.Unit("b4620412_614871|shop"), Fnv.Unit("100417_101234_7|shop"));
    }

    [Fact]
    public void Other_keys_hash_as_they_did()
    {
        Fnv.Alias("100417_101234_8", "b4620500_614900");
        // another building of the same leaf, an index that only starts the same, a flat LV95 key, any text
        Assert.Equal(Plain("100417_101234_9|x"), Fnv.Hash("100417_101234_9|x"));
        Assert.Equal(Plain("100417_101234_80"), Fnv.Hash("100417_101234_80"));
        Assert.Equal(Plain("2583_1113_8"), Fnv.Hash("2583_1113_8"));
        Assert.Equal(Plain("2583_1113_8|cellar"), Fnv.Hash("2583_1113_8|cellar"));
        Assert.Equal(Plain("hello"), Fnv.Hash("hello"));
        Assert.Equal(Plain(""), Fnv.Hash(""));
    }

    private static int Plain(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return (int)h;
    }
}
