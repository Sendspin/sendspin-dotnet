using System.Text.Json.Serialization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Message sent by client when disconnecting gracefully.
/// Uses the envelope format: { "type": "client/goodbye", "payload": { ... } }
/// </summary>
public sealed class ClientGoodbyeMessage : IMessageWithPayload<ClientGoodbyePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientGoodbye;

    [JsonPropertyName("payload")]
    required public ClientGoodbyePayload Payload { get; init; }

    /// <summary>
    /// Creates a ClientGoodbyeMessage with the specified reason, substituting
    /// <see cref="GoodbyeReasons.Restart"/> for a reason the spec does not define.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every <c>client/goodbye</c> on either connection path is built here, which makes this the
    /// one place that can keep an out-of-set reason off the wire — and three callers in a row
    /// invented one (<c>"disposing"</c>, <c>"handshake_timeout"</c>,
    /// <c>"switching_connection_mode"</c>), so the guard belongs at the sink rather than at each
    /// call site.
    /// </para>
    /// <para>
    /// Substituted rather than thrown: both callers reach this from a teardown path, and
    /// <c>IncomingConnection.DisconnectAsync</c> builds the message before it closes anything,
    /// so throwing would leave the connection open and still holding its arbitration slot — a
    /// worse failure than the wrong string. <c>restart</c> is the substitute because it is what
    /// a conformant server already assumes when it cannot parse the reason (messaging.md:442),
    /// so this makes the frame conformant without changing what the server does with it.
    /// </para>
    /// </remarks>
    public static ClientGoodbyeMessage Create(string reason = GoodbyeReasons.Restart)
    {
        return new ClientGoodbyeMessage
        {
            Payload = new ClientGoodbyePayload
            {
                Reason = GoodbyeReasons.IsDefined(reason) ? reason : GoodbyeReasons.Restart,
            }
        };
    }
}

/// <summary>
/// Payload for the client/goodbye message.
/// </summary>
public sealed class ClientGoodbyePayload
{
    /// <summary>
    /// Reason for disconnection.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "restart";
}
