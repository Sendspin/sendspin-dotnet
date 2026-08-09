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
/// A PIN-method object is absent only when the client does not implement that method;
/// an implemented method that is merely disabled still reports itself with
/// <c>enabled: false</c>.
/// </summary>
internal sealed record PairingConfigData(
    [property: JsonPropertyName("pairing_psk")] PairingMethodState PairingPsk,
    [property: JsonPropertyName("static_pin")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PairingMethodState? StaticPin,
    [property: JsonPropertyName("dynamic_pin")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DynamicPinConfigState? DynamicPin,
    [property: JsonPropertyName("record_mode")] RecordModeState RecordMode,
    [property: JsonPropertyName("unpaired_access")] PairingMethodState UnpairedAccess);

/// <summary>Enablement state of one pairing method.</summary>
internal sealed record PairingMethodState(
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>
/// Dynamic-PIN state. <c>escalated</c> is the spec's name for the failure counter having
/// reached 10 (pairing.md:182) — the method stays offered but every attempt is
/// gesture-gated.
/// </summary>
internal sealed record DynamicPinConfigState(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("min_pin_length")] int MinPinLength,
    [property: JsonPropertyName("escalated")] bool Escalated);

/// <summary>The shared-PSK record used as the storage-exhaustion fallback.</summary>
internal sealed record RecordModeState(
    [property: JsonPropertyName("psk_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PskId);
