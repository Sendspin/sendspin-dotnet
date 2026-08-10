using System.Text.Json.Serialization;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// Source-generated serialization for the file-backed pairing store's on-disk shape, so the
/// store does not fall back to reflection under <c>PublishAot</c> (#89).
/// </summary>
/// <remarks>
/// <para>
/// **Deliberately no naming policy.** This context does not set
/// <c>PropertyNamingPolicy</c>, because the previous code called
/// <c>JsonSerializer.Serialize/Deserialize</c> with default options and therefore wrote
/// PascalCase members — <c>Psk</c>, <c>Category</c>, <c>ServerId</c>, <c>Used</c>. Attaching
/// the protocol context (which applies <c>SnakeCaseLower</c>) would rename every field and
/// silently orphan every pairing file already on disk: the store would read a file it could
/// not map, find no records, and the device would appear unpaired with its long-term PSKs
/// still sitting there.
/// </para>
/// <para>
/// This is a storage format, not a wire format, so it is not the protocol context's business
/// either way.
/// </para>
/// </remarks>
[JsonSerializable(typeof(List<FilePairingRecordStore.Entry>))]
internal sealed partial class PairingRecordStoreJsonContext : JsonSerializerContext;
