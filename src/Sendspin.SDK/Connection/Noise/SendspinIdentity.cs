using Noise;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// A Sendspin static identity: a long-lived X25519 keypair whose base64url-encoded
/// public key (43 chars, no padding) is the <c>client_id</c>. Persist both key halves;
/// rotating the keypair changes the client's identity.
/// </summary>
public sealed class SendspinIdentity
{
    /// <summary>Raw 32-byte X25519 private key.</summary>
    public ReadOnlyMemory<byte> PrivateKey { get; }

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

    /// <summary>Decodes a 43-character base64url peer id into raw public-key bytes.</summary>
    public static byte[] DecodePeerId(string peerId)
    {
        var bytes = Base64UrlText.Decode(peerId);
        if (bytes.Length != NoiseConstants.KeySize)
            throw new FormatException($"peer id must decode to {NoiseConstants.KeySize} bytes");
        return bytes;
    }
}
