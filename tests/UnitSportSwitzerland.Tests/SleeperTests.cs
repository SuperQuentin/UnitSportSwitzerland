using System.IO;
using UnitSport.Net;
using Xunit;

namespace UnitSport.Tests;

/// <summary>Sleepers' identity key and book (src/Net/PlayerKey.cs, src/Net/SleeperBook.cs, linked in, #644).</summary>
public class SleeperTests
{
    [Fact]
    public void A_signed_nonce_verifies_against_its_own_key_only()
    {
        using var a = PlayerKey.CreateEphemeral();
        using var b = PlayerKey.CreateEphemeral();
        var nonce = PlayerKey.NewNonce();
        var sig = a.Sign(nonce);

        Assert.True(PlayerKey.Verify(a.PublicKey, nonce, sig));
        Assert.False(PlayerKey.Verify(b.PublicKey, nonce, sig));
        Assert.False(PlayerKey.Verify(a.PublicKey, PlayerKey.NewNonce(), sig));
        Assert.NotEqual(PlayerKey.Fingerprint(a.PublicKey), PlayerKey.Fingerprint(b.PublicKey));
    }

    [Fact]
    public void Garbage_never_verifies()
    {
        var nonce = PlayerKey.NewNonce();
        Assert.False(PlayerKey.Verify(new byte[91], nonce, new byte[64]));
        Assert.False(PlayerKey.Verify(System.Array.Empty<byte>(), nonce, new byte[64]));
        Assert.False(PlayerKey.Verify(new byte[1000], nonce, new byte[64]));
        using var a = PlayerKey.CreateEphemeral();
        Assert.False(PlayerKey.Verify(a.PublicKey, new byte[3], a.Sign(new byte[3])));
    }

    [Fact]
    public void The_key_file_keeps_the_identity()
    {
        string path = Path.Combine(Path.GetTempPath(), $"us-key-{System.Guid.NewGuid():N}", "identity.key");
        try
        {
            byte[] first;
            using (var k = PlayerKey.LoadOrCreate(path)) first = k.PublicKey;
            using var again = PlayerKey.LoadOrCreate(path);
            Assert.Equal(first, again.PublicKey);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    private static Sleeper S(string key, string name) => new(0, key, name, 2600000.5, 1200000.25, 1234.5, 1.5f, 77, 123456789L, 42, 3);

    [Fact]
    public void One_sleeper_per_key_and_waking_removes_it()
    {
        var book = new SleeperBook();
        var first = book.Lay(S("k1", "Anna"), out var none);
        Assert.Null(none);
        var second = book.Lay(S("k1", "Anna"), out var replaced);
        Assert.Equal(first, replaced);
        Assert.NotEqual(first.Id, second.Id);
        book.Lay(S("k2", "Beat"), out _);
        Assert.Equal(2, book.All.Count);

        Assert.Equal(second, book.Wake("k1"));
        Assert.Null(book.Wake("k1"));
        Assert.Single(book.All);
    }

    [Fact]
    public void Survives_a_round_trip_through_json()
    {
        var book = new SleeperBook();
        var laid = book.Lay(S("k1", "Anna"), out _);
        var back = SleeperBook.FromJson(book.ToJson());
        Assert.Equal(laid, Assert.Single(back.All));
        // ids keep counting up after a restart
        Assert.True(back.Lay(S("k2", "Beat"), out _).Id > laid.Id);
    }
}
