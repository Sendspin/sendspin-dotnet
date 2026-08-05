using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Data payload of <c>management/result</c> for <c>management/list-records</c>.
/// </summary>
internal sealed record ManagementRecordsData(
    [property: JsonPropertyName("records")] IReadOnlyList<ManagementRecordEntry> Records);

/// <summary>One pairing record as reported by <c>management/list-records</c>.</summary>
internal sealed record ManagementRecordEntry(
    [property: JsonPropertyName("psk_id")] string PskId,
    [property: JsonPropertyName("server_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServerId,
    [property: JsonPropertyName("used")] bool Used);

/// <summary>
/// Data payload of <c>management/result</c> for <c>management/get-pairing-config</c>.
/// PIN methods are not implemented, so their objects are absent per spec.
/// </summary>
internal sealed record PairingConfigData(
    [property: JsonPropertyName("pairing_psk")] PairingMethodState PairingPsk,
    [property: JsonPropertyName("unpaired_access")] PairingMethodState UnpairedAccess);

/// <summary>Enablement state of one pairing method.</summary>
internal sealed record PairingMethodState(
    [property: JsonPropertyName("enabled")] bool Enabled);
