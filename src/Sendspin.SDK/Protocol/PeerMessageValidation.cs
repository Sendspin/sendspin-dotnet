using System.Text.Json;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// Rejects a peer-supplied message that carries JSON <c>null</c> where the model declares a
/// non-nullable reference member.
/// </summary>
/// <remarks>
/// <para>
/// System.Text.Json does not enforce nullable reference annotations, and a property initializer
/// does not save them: an <em>absent</em> member keeps its initializer, but an explicit
/// <c>"channels": null</c> writes the null straight over it. Every non-nullable reference member
/// on the inbound surface is therefore nullable in practice, and a hostile authenticated peer
/// can pick any of them. Checking here is what makes those annotations load-bearing rather than
/// decorative (#106).
/// </para>
/// <para>
/// .NET 9 added <c>JsonSerializerOptions.RespectNullableAnnotations</c>, which would do this for
/// free — but it does not exist on <c>net8.0</c>, so switching it on would make the two target
/// frameworks accept different input. That is exactly the divergence #108 was, so this is
/// hand-rolled instead and behaves identically on both.
/// </para>
/// <para>
/// A type switch, not reflection: this runs on the mandatory transport path and the SDK
/// publishes under <c>PublishAot</c>. Adding a peer-supplied message type means adding an arm.
/// </para>
/// </remarks>
internal static class PeerMessageValidation
{
    /// <summary>
    /// Throws if <paramref name="message"/> holds a null where its model promises a value.
    /// </summary>
    /// <exception cref="JsonException">
    /// A non-nullable member arrived as null. Chosen deliberately over a bespoke exception type:
    /// every receive path already routes <see cref="JsonException"/> to "malformed input from an
    /// authenticated peer, close the connection", which is the treatment this warrants.
    /// </exception>
    internal static void ThrowIfNullMembers(IMessage message)
    {
        switch (message)
        {
            case ServerHelloMessage m:
                Require(m.Payload, "server/hello", "payload");
                Require(m.Payload.ServerId, "server/hello", "payload.server_id");
                Require(m.Payload.ActiveRoles, "server/hello", "payload.active_roles");
                break;

            case ServerActivateMessage m:
                Require(m.Payload, "server/activate", "payload");
                Require(m.Payload.ActivitiesList, "server/activate", "payload.activities");
                if (m.Payload.Pairing is { } pairing)
                {
                    Require(pairing.Method, "server/activate", "payload.pairing.method");
                }

                break;

            case ServerPairFinalizeMessage m:
                Require(m.Payload, "server/pair-finalize", "payload");
                break;

            case PairAbortMessage m:
                Require(m.Payload, "pair/abort", "payload");
                Require(m.Payload.Reason, "pair/abort", "payload.reason");
                break;

            case ServerPairInitMessage m:
                Require(m.Payload, "server/pair-init", "payload");
                Require(m.Payload.NonceA, "server/pair-init", "payload.nonce_A");
                break;

            case ServerPairAuthMessage m:
                Require(m.Payload, "server/pair-auth", "payload");
                Require(m.Payload.PakeMsg1, "server/pair-auth", "payload.pake_msg_1");
                break;

            case ServerPairConfirmMessage m:
                Require(m.Payload, "server/pair-confirm", "payload");
                Require(m.Payload.ServerKc, "server/pair-confirm", "payload.server_kc");
                break;

            case ServerTimeMessage m:
                Require(m.Payload, "server/time", "payload");
                break;

            case StreamStartMessage m:
                ValidateStreamStart(m);
                break;

            case StreamEndMessage m:
                Require(m.Payload, "stream/end", "payload");
                break;

            case StreamClearMessage m:
                Require(m.Payload, "stream/clear", "payload");
                break;

            case GroupUpdateMessage m:
                Require(m.Payload, "group/update", "payload");
                Require(m.Payload.GroupId, "group/update", "payload.group_id");
                break;

            case ServerCommandMessage m:
                Require(m.Payload, "server/command", "payload");
                break;

            default:
                // Client-authored messages reach this only from tests round-tripping their own
                // output, where the SDK built every member and there is nothing to police.
                break;
        }
    }

    private static void ValidateStreamStart(StreamStartMessage m)
    {
        Require(m.Payload, "stream/start", "payload");

        if (m.Payload.Format is { } format)
        {
            Require(format.Codec, "stream/start", "payload.player.codec");
        }

        if (m.Payload.Artwork is { } artwork)
        {
            Require(artwork.Channels, "stream/start", "payload.artwork.channels");

            for (int i = 0; i < artwork.Channels.Count; i++)
            {
                var channel = artwork.Channels[i];
                Require(channel, "stream/start", $"payload.artwork.channels[{i}]");
                Require(channel.Source, "stream/start", $"payload.artwork.channels[{i}].source");
                Require(channel.Format, "stream/start", $"payload.artwork.channels[{i}].format");
            }
        }

        if (m.Payload.Visualizer is { } visualizer)
        {
            Require(visualizer.Types, "stream/start", "payload.visualizer.types");
        }
    }

    private static void Require(object? value, string messageType, string member)
    {
        if (value is null)
        {
            throw new JsonException(
                $"{messageType}: '{member}' was null, but the protocol declares it non-nullable.");
        }
    }
}
