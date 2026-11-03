using UnitSport.Avatar;
using Xunit;

namespace UnitSportSwitzerland.Tests;

/// <summary>
/// The appearance's one int (src/Avatar/AppearanceCode.cs, linked in): saved in the settings and
/// replicated, so a value packed before #880 (the aliens' high bits) must read the same, and the
/// new faces and skin tones must round-trip.
/// </summary>
public class AppearanceCodeTests
{
    [Fact]
    public void Nothing_set_is_null()
    {
        Assert.Null(AppearanceCode.Unpack(0));
        Assert.Null(AppearanceCode.Unpack(12345));
    }

    // the #394 layout as it was packed before #880: build 3 bits, face 4, eyes 3, skin 3, hair 4, colour 4
    private static int OldPack(int build, int face, int eyes, int skin, int hair, int colour) =>
        1 << 30 | build | (face & 15) << 3 | (eyes & 7) << 7 | (skin & 7) << 10 | (hair & 15) << 13 | (colour & 15) << 17;

    [Fact]
    public void Every_old_value_reads_the_same_and_packs_back_to_the_same_bits()
    {
        for (int build = 0; build < 5; build++)
            for (int face = 0; face < 16; face++)
                for (int skin = 0; skin < 8; skin++)
                {
                    int eyes = (face + skin) % 8, hair = (build * 3 + face) % 13, colour = (skin + face) % 12;
                    int old = OldPack(build, face, eyes, skin, hair, colour);
                    var c = AppearanceCode.Unpack(old);
                    Assert.Equal(new AppearanceCode(build, face, eyes, skin, hair, colour), c);
                    Assert.Equal(old, c!.Value.Pack());
                }
    }

    [Theory]
    [InlineData(5, 16, 3, 8, 0, 0)]    // the alien build, the visitor face, a green alien
    [InlineData(5, 18, 2, 11, 0, 0)]   // teal, the giggler
    [InlineData(1, 31, 7, 31, 12, 15)] // every new bit at its top
    [InlineData(0, 17, 0, 9, 2, 3)]    // an alien face on a human build
    public void Alien_faces_and_skins_round_trip(int build, int face, int eyes, int skin, int hair, int colour)
    {
        var c = new AppearanceCode(build, face, eyes, skin, hair, colour);
        int bits = c.Pack();
        Assert.Equal(c, AppearanceCode.Unpack(bits));
        // bit 30 stays the only flag; nothing spills past it
        Assert.Equal(AppearanceCode.Set, bits & ~((1 << 24) - 1));
    }

    [Fact]
    public void An_older_peer_reads_a_new_value_as_a_figure_of_its_own_tables()
    {
        // an old build masks face & 15 and skin & 7: an alien face 16 reads as face 0, a green skin 8 as tone 0
        int bits = new AppearanceCode(5, 16, 3, 8, 0, 0).Pack();
        Assert.Equal(0, bits >> 3 & 15);
        Assert.Equal(0, bits >> 10 & 7);
        Assert.Equal(3, bits >> 7 & 7);
    }
}
