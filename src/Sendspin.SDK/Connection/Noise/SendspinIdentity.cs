using System.Security.Cryptography;
using Noise;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// A Sendspin static identity: a long-lived X25519 keypair whose base64url-encoded
/// public key (43 chars, no padding) is the <c>client_id</c>. Persist both key halves;
/// rotating the keypair changes the client's identity.
/// </summary>
public sealed class SendspinIdentity
{
    /// <summary>
    /// Raw 32-byte X25519 private key. Internal by design: persist an identity through
    /// <see cref="ISendspinIdentityStore"/> rather than extracting key bytes.
    /// </summary>
    internal ReadOnlyMemory<byte> PrivateKey { get; }

    /// <summary>Raw 32-byte X25519 public key.</summary>
    public ReadOnlyMemory<byte> PublicKey { get; }

    /// <summary>The base64url-encoded public key serving as this identity's peer id.</summary>
    public string PeerId { get; }

    private SendspinIdentity(byte[] privateKey, byte[] publicKey)
    {
        if (privateKey.Length != NoiseConstants.KeySize)
            throw new ArgumentException($"private key must be {NoiseConstants.KeySize} bytes", nameof(privateKey));
        if (publicKey.Length != NoiseConstants.KeySize)
            throw new ArgumentException($"public key must be {NoiseConstants.KeySize} bytes", nameof(publicKey));

        PrivateKey = privateKey;
        PublicKey = publicKey;
        PeerId = Base64UrlText.Encode(publicKey);
    }

    /// <summary>Generates a new random identity.</summary>
    public static SendspinIdentity Generate()
    {
        using var keyPair = KeyPair.Generate();
        return new SendspinIdentity((byte[])keyPair.PrivateKey.Clone(), (byte[])keyPair.PublicKey.Clone());
    }

    /// <summary>Reconstructs an identity from persisted key material.</summary>
    public static SendspinIdentity FromKeys(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey) =>
        new(privateKey.ToArray(), publicKey.ToArray());

    /// <summary>Format version of the identity blob this SDK writes and understands.</summary>
    private const byte BlobVersion = 1;

    /// <summary>Bytes of truncated SHA-256 appended to the blob's key material.</summary>
    private const int BlobChecksumSize = 4;

    private const int BlobKeyMaterialOffset = 1;
    private const int BlobKeyMaterialSize = NoiseConstants.KeySize * 2;
    private const int BlobSize = BlobKeyMaterialOffset + BlobKeyMaterialSize + BlobChecksumSize;

    /// <summary>
    /// Loads the identity from <paramref name="store"/>, generating and persisting a new one
    /// on first run. The returned identity's <see cref="PeerId"/> is stable across restarts,
    /// which the spec requires of <c>client_id</c>.
    /// </summary>
    /// <remarks>
    /// The blob is <c>[version:1][private key:32][public key:32][truncated SHA-256 of the key
    /// material:4]</c>. The checksum exists because the two key halves are only meaningful
    /// together: a corrupted private half still leaves a decodable public half, so without it
    /// this method would happily return an identity whose <see cref="PeerId"/> looks right and
    /// whose every Noise handshake fails at MAC verification. Verifying the private key against
    /// the public one directly is not available — Noise.NET keeps its Curve25519 primitive
    /// internal, and neither target framework ships a public X25519.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The stored data is not a readable Sendspin identity — <paramref name="store"/> returned
    /// a blob of the wrong length, an unrecognised format version, or key material that does
    /// not match its checksum; or the store itself could not decode what it had persisted
    /// (see e.g. <see cref="FileSendspinIdentityStore.Load"/>).
    /// </exception>
    public static SendspinIdentity FromStore(ISendspinIdentityStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (store.Load() is { } blob)
            return FromBlob(blob, store.GetType().Name);

        var generated = Generate();
        store.Save(generated.ToBlob());
        return generated;
    }

    /// <param name="storeName">
    /// Type name of the store the blob came from. The wrong-length branch is where an empty or
    /// truncated file lands, and a third-party store that returns an empty array instead of null
    /// lands there too — reporting "0 bytes" with no hint of which store produced it sent
    /// implementors looking for corruption rather than at their own return value.
    /// </param>
    private static SendspinIdentity FromBlob(byte[] blob, string storeName)
    {
        if (blob.Length != BlobSize)
        {
            throw new InvalidOperationException(
                $"stored Sendspin identity from {storeName} is {blob.Length} bytes; expected " +
                $"{BlobSize}. The identity store may be corrupt" +
                (blob.Length == 0
                    ? ", or returned an empty array where it meant to report no stored identity — " +
                      "an ISendspinIdentityStore signals that with null."
                    : "."));
        }

        if (blob[0] != BlobVersion)
        {
            // The checksum below covers the key material but not this byte, so a corrupted
            // version byte is caught here rather than there. Harmless while 0x01 is the only
            // defined value; a v2 must either widen the checksum to include the version or
            // accept that a 1<->2 flip selects the wrong parse silently.
            throw new InvalidOperationException(
                $"stored Sendspin identity has format version {blob[0]}, which this SDK does " +
                $"not understand (it writes version {BlobVersion}). The identity store may be " +
                "corrupt, or written by a newer SDK.");
        }

        var keyMaterial = blob.AsSpan(BlobKeyMaterialOffset, BlobKeyMaterialSize);
        if (!blob.AsSpan(BlobKeyMaterialOffset + BlobKeyMaterialSize).SequenceEqual(
                SHA256.HashData(keyMaterial).AsSpan(0, BlobChecksumSize)))
        {
            throw new InvalidOperationException(
                "stored Sendspin identity failed its integrity check: the key material does not " +
                "match its checksum, so the private key no longer corresponds to the public key " +
                "the client_id is derived from. The identity store is corrupt.");
        }

        return FromKeys(
            keyMaterial[..NoiseConstants.KeySize],
            keyMaterial[NoiseConstants.KeySize..]);
    }

    private byte[] ToBlob()
    {
        byte[] blob = new byte[BlobSize];
        blob[0] = BlobVersion;
        PrivateKey.Span.CopyTo(blob.AsSpan(BlobKeyMaterialOffset));
        PublicKey.Span.CopyTo(blob.AsSpan(BlobKeyMaterialOffset + NoiseConstants.KeySize));
        SHA256.HashData(blob.AsSpan(BlobKeyMaterialOffset, BlobKeyMaterialSize))
            .AsSpan(0, BlobChecksumSize)
            .CopyTo(blob.AsSpan(BlobKeyMaterialOffset + BlobKeyMaterialSize));
        return blob;
    }

    /// <summary>Decodes a 43-character base64url peer id into raw public-key bytes.</summary>
    public static byte[] DecodePeerId(string peerId)
    {
        var bytes = Base64UrlText.Decode(peerId);
        if (bytes.Length != NoiseConstants.KeySize)
            throw new FormatException($"peer id must decode to {NoiseConstants.KeySize} bytes");
        return bytes;
    }

    /// <summary>
    /// Decodes a base64url pre-shared key into raw bytes. Distinct from
    /// <see cref="DecodePeerId"/> so a malformed PSK does not report itself as a bad peer id.
    /// </summary>
    internal static byte[] DecodePsk(string encoded)
    {
        byte[] psk = Base64UrlText.Decode(encoded);
        if (psk.Length != NoiseConstants.KeySize)
            throw new FormatException($"PSK must decode to {NoiseConstants.KeySize} bytes");
        return psk;
    }
}
