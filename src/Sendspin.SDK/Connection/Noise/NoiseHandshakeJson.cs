using System.Text.Json.Serialization;

namespace Sendspin.SDK.Connection.Noise;

/// <summary>
/// Typed shapes for the two handshake frames the framing emits, so they can be serialized
/// through the source-generated context instead of reflection over
/// <c>Dictionary&lt;string, object&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reflection-based <c>System.Text.Json</c> on this path emits IL2026/IL3050 and breaks under
/// <c>PublishAot</c> — on the mandatory transport, which every AOT consumer reaches (#89).
/// </para>
/// <para>
/// **The property order here is load-bearing.** The prologue binds the literal wire bytes of
/// both init messages, so reordering a member, renaming one, or letting the context's naming
/// policy apply would change the hash and break every handshake against a conformant peer —
/// while self-tests kept passing, because both sides would move together. Each member carries
/// an explicit <see cref="JsonPropertyNameAttribute"/> for that reason, and the order matches
/// what the dictionaries produced. <c>HandshakeByteFidelityTests</c> pins the result.
/// </para>
/// </remarks>
internal sealed record ClientInitJson(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] ClientInitPayloadJson Payload);

/// <summary>Payload of <c>client/init</c>. Member order is prologue-critical; see the type above.</summary>
internal sealed record ClientInitPayloadJson(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("suite")] string Suite);

/// <summary>The <c>noise/handshake</c> envelope carrying one Noise message.</summary>
internal sealed record NoiseHandshakeJson(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("payload")] NoiseHandshakePayloadJson Payload);

/// <summary>Payload of <c>noise/handshake</c>: the base64url-encoded Noise message.</summary>
internal sealed record NoiseHandshakePayloadJson(
    [property: JsonPropertyName("data")] string Data);
