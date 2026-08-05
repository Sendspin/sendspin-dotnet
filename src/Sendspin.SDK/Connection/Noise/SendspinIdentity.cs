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

    /// <summary>
    /// Loads the identity from <paramref name="store"/>, generating and persisting a new one
    /// on first run. The returned identity's <see cref="PeerId"/> is stable across restarts,
    /// which the spec requires of <c>client_id</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The store returned a blob this SDK cannot read.
    /// </exception>
    public static SendspinIdentity FromStore(ISendspinIdentityStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (store.Load() is { } blob)
        {
            // A blob is private + public key concatenated, in that order.
            if (blob.Length != NoiseConstants.KeySize * 2)
            {
                throw new InvalidOperationException(
                    $"stored Sendspin identity is {blob.Length} bytes; expected " +
                    $"{NoiseConstants.KeySize * 2}. The identity store may be corrupt.");
            }

            return FromKeys(
                blob.AsSpan(0, NoiseConstants.KeySize),
                blob.AsSpan(NoiseConstants.KeySize, NoiseConstants.KeySize));
        }

        var generated = Generate();
        byte[] fresh = new byte[NoiseConstants.KeySize * 2];
        generated.PrivateKey.Span.CopyTo(fresh);
        generated.PublicKey.Span.CopyTo(fresh.AsSpan(NoiseConstants.KeySize));
        store.Save(fresh);
        return generated;
    }

    /// <summary>Decodes a 43-character base64url peer id into raw public-key bytes.</summary>
    public static byte[] DecodePeerId(string peerId)
    {
        var bytes = Base64UrlText.Decode(peerId);
        if (bytes.Length != NoiseConstants.KeySize)
            throw new FormatException($"peer id must decode to {NoiseConstants.KeySize} bytes");
        return bytes;
    }
}
