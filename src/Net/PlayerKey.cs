using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnitSport.Net;

/// <summary>
/// This install's identity (#644): an ECDSA P-256 key made once and kept in a file. A display name
/// is a request anyone can make; this key is what proves a reconnecting player is the one who left
/// (<see cref="Sleepers"/>). The server sends a random nonce, the client signs it, the server checks
/// the signature against the public key it came with, and from then on knows that peer by
/// <see cref="Fingerprint"/>. No Godot here, so it is unit tested.
/// </summary>
public sealed class PlayerKey : IDisposable
{
    /// <summary>A P-256 SubjectPublicKeyInfo is 91 bytes; anything much longer is not one.</summary>
    public const int MaxPublicKeyBytes = 160;

    /// <summary>A P-256 signature is 64 bytes (IEEE P1363).</summary>
    public const int MaxSignatureBytes = 72;

    public const int NonceBytes = 32;

    /// <summary>Signed before the nonce, so a signature made here is good for nothing else.</summary>
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("unitsport-identity-v1:");

    private readonly ECDsa _key;

    private PlayerKey(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
    }

    public byte[] PublicKey { get; }

    /// <summary>Reads the key at <paramref name="path"/>, or makes one and writes it there.</summary>
    public static PlayerKey LoadOrCreate(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            try
            {
                key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
                return new PlayerKey(key);
            }
            catch (CryptographicException)
            {
                // unreadable: a new identity is better than none (the old sleeper is lost)
                key.Dispose();
                key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, key.ExportPkcs8PrivateKey());
        return new PlayerKey(key);
    }

    /// <summary>A key held only in memory (tests).</summary>
    public static PlayerKey CreateEphemeral() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceBytes);

    public byte[] Sign(byte[] nonce) => _key.SignData(Message(nonce), HashAlgorithmName.SHA256);

    /// <summary>True when <paramref name="signature"/> is <paramref name="publicKey"/>'s over <paramref name="nonce"/>.</summary>
    public static bool Verify(byte[] publicKey, byte[] nonce, byte[] signature)
    {
        if (publicKey.Length is 0 or > MaxPublicKeyBytes || signature.Length is 0 or > MaxSignatureBytes
            || nonce.Length != NonceBytes) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out int read);
            if (read != publicKey.Length || key.KeySize != 256) return false;
            return key.VerifyData(Message(nonce), signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>The name of a public key: the first 128 bits of its SHA-256, in hex.</summary>
    public static string Fingerprint(byte[] publicKey) => Convert.ToHexString(SHA256.HashData(publicKey))[..32];

    private static byte[] Message(byte[] nonce)
    {
        var m = new byte[Domain.Length + nonce.Length];
        Domain.CopyTo(m, 0);
        nonce.CopyTo(m, Domain.Length);
        return m;
    }

    public void Dispose() => _key.Dispose();
}
