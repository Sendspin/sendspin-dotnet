using System.Text.Json.Serialization;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// Clock synchronization request sent by client.
/// Uses the envelope format: { "type": "client/time", "payload": { ... } }
/// Contains the client's current timestamp for round-trip calculation.
/// </summary>
public sealed class ClientTimeMessage : IMessageWithPayload<ClientTimePayload>
{
    [JsonPropertyName("type")]
    public string Type => MessageTypes.ClientTime;

    [JsonPropertyName("payload")]
    public ClientTimePayload Payload { get; set; } = new();

    // Convenience accessor (excluded from serialization)
    [JsonIgnore]
    public long ClientTransmitted => Payload.ClientTransmitted;

    /// <summary>
    /// Creates a new time message with the current timestamp.
    /// </summary>
    /// <remarks>
    /// For a caller assembling a probe itself. The SDK's own probes come from
    /// <c>ISendspinConnection.SendTimeMessageAsync</c>, which stamps T1 at the send point
    /// instead, so serialization and send-queue latency stay out of the measured round trip.
    /// </remarks>
    public static ClientTimeMessage CreateNow() => Create(GetCurrentTimestampMicroseconds());

    /// <summary>
    /// Creates a time message carrying an already-captured T1.
    /// </summary>
    /// <param name="clientTransmitted">T1, in the <see cref="HighPrecisionTimer"/> time base.</param>
    /// <remarks>
    /// For the transport, which stamps T1 at the send point and then builds the message
    /// around it, so the value on the wire is not widened by serialization or send-queue
    /// latency the way a call-site stamp is.
    /// </remarks>
    public static ClientTimeMessage Create(long clientTransmitted)
    {
        return new ClientTimeMessage
        {
            Payload = new ClientTimePayload
            {
                ClientTransmitted = clientTransmitted
            }
        };
    }

    /// <summary>
    /// Gets the current timestamp in microseconds using the shared high-precision timer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// CRITICAL: This MUST use the same timer as audio playback timing (HighPrecisionTimer).
    /// Previously this used raw Stopwatch ticks while audio used Unix-based time from
    /// HighPrecisionTimer, causing a time base mismatch of billions of seconds!
    /// </para>
    /// <para>
    /// The clock offset calculation works correctly as long as T1/T4 (client times)
    /// are in the same time base as the audio playback timer.
    /// </para>
    /// </remarks>
    public static long GetCurrentTimestampMicroseconds()
    {
        return HighPrecisionTimer.Shared.GetCurrentTimeMicroseconds();
    }
}

/// <summary>
/// Payload for the client/time message.
/// </summary>
public sealed class ClientTimePayload
{
    /// <summary>
    /// Client's current monotonic clock timestamp in microseconds.
    /// This is T1 in the NTP-style 4-timestamp exchange.
    /// </summary>
    [JsonPropertyName("client_transmitted")]
    public long ClientTransmitted { get; set; }
}
