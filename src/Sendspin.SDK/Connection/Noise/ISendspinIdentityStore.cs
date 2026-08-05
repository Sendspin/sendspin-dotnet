namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// Persistence seam for the client's long-lived Curve25519 identity. Mirrors
/// <see cref="Sendspin.SDK.Client.IStaticDelayStore"/> and
/// <see cref="Sendspin.SDK.Client.ILastPlayedServerStore"/>.
/// </summary>
/// <remarks>
/// The spec requires <c>client_id</c> — which IS the base64url public key — to survive
/// reboots, so an identity that is not persisted changes the client's identity on every
/// restart. Because the SDK is a library and cannot choose a storage location, the embedder
/// supplies this (file, DPAPI, Keychain, Android keystore) and passes the result to
/// <see cref="SendspinIdentity.FromStore"/>.
/// <para>
/// The blob is opaque: the SDK owns its format, and an implementation only stores and
/// returns bytes. That is what platform key stores want, and it is what allows the raw
/// private key to stay internal to the SDK.
/// </para>
/// <para><b>The blob contains a private key. Protect it as a secret.</b></para>
/// </remarks>
public interface ISendspinIdentityStore
{
    /// <summary>The persisted identity blob, or <c>null</c> on first run.</summary>
    byte[]? Load();

    /// <summary>Persists the identity blob. Called once, when a new identity is generated.</summary>
    void Save(byte[] identityBlob);
}

/// <summary>
/// File-backed identity store, written atomically and restricted to owner-only access
/// where the platform supports it.
/// </summary>
/// <remarks>
/// On Windows the file inherits its parent directory's ACL, so place it somewhere already
/// user-scoped such as <c>%LOCALAPPDATA%</c>. For hardware-backed protection, supply a
/// platform implementation of <see cref="ISendspinIdentityStore"/> instead.
/// </remarks>
public sealed class FileSendspinIdentityStore : ISendspinIdentityStore
{
    private readonly string _path;

    /// <summary>Creates a store backed by the given file path.</summary>
    public FileSendspinIdentityStore(string path) => _path = path;

    /// <inheritdoc/>
    public byte[]? Load()
    {
        string? text = SecureFile.ReadAllTextOrNull(_path);
        return text is null ? null : Convert.FromBase64String(text);
    }

    /// <inheritdoc/>
    public void Save(byte[] identityBlob) =>
        SecureFile.WriteAllTextAtomic(_path, Convert.ToBase64String(identityBlob));
}
