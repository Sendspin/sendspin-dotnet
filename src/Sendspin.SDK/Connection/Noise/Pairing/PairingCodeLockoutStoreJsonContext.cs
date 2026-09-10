using System.Text.Json.Serialization;

namespace Sendspin.SDK.Connection.Noise.Pairing;

/// <summary>
/// Source-generated serialization for the pairing code failure-counter file, so the store does not
/// fall back to reflection under <c>PublishAot</c> (#89).
/// </summary>
/// <remarks>
/// No naming policy, for the same reason as <c>PairingRecordStoreJsonContext</c>: the previous
/// code used default options, and the keys here are method names supplied at runtime
/// (<c>dynamic_pairing_code</c>, <c>static_pairing_code</c>) rather than member names, so a policy would be both
/// wrong and silently destructive to files already on disk.
/// </remarks>
[JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class PairingCodeLockoutStoreJsonContext : JsonSerializerContext;
