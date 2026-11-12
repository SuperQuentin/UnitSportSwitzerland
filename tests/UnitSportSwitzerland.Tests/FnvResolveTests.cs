using UnitSport.Core;
using Xunit;

namespace UnitSport.Tests;

/// <summary>
/// A stand-in building's seed is the same whenever it is asked (#1704): asked before its leaf registered its aliases,
/// <see cref="Fnv.Resolve"/> registers them first, so it hashes as from the building's Earth identity, as after.
/// </summary>
public class FnvResolveTests
{
    [Fact]
    public void A_seed_asked_before_its_alias_hashes_as_after()
    {
        const string key = "104321_108765_7", name = "B:9f3a-test";
        int asNamed = Fnv.Hash($"{name}#hall#1");
        var was = Fnv.Resolve;
        int asked = 0;
        try
        {
            Fnv.Resolve = k =>
            {
                asked++;
                if (k != key) return false;
                Fnv.Alias(key, name);
                return true;
            };
            Assert.Equal(asNamed, Fnv.Hash($"{key}#hall#1"));
            Assert.Equal(asNamed, Fnv.Hash($"{key}#hall#1"));
            Assert.Equal(1, asked);
            // an LV95 key is never a stand-in: never resolved, hashed as it is
            Assert.Equal(Fnv.Hash("2600_1200_7#hall#1"), Fnv.Hash("2600_1200_7#hall#1"));
            Assert.Equal(1, asked);
        }
        finally { Fnv.Resolve = was; }
    }
}
