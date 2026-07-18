using System.Text.Json;
using System.Text.Json.Serialization;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Protocol;

/// <summary>
/// Source-generated JSON serializer context for all Sendspin protocol messages.
/// Enables NativeAOT-compatible serialization without runtime reflection.
/// </summary>
/// <remarks>
/// When adding a new message type, add a [JsonSerializable(typeof(NewMessageType))]
/// attribute here to include it in source generation.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(OptionalJsonConverterFactory)])]
[JsonSerializable(typeof(ClientHelloMessage))]
[JsonSerializable(typeof(ClientGoodbyeMessage))]
[JsonSerializable(typeof(ClientTimeMessage))]
[JsonSerializable(typeof(ClientCommandMessage))]
[JsonSerializable(typeof(ClientStateMessage))]
[JsonSerializable(typeof(StreamRequestFormatMessage))]
[JsonSerializable(typeof(ServerHelloMessage))]
[JsonSerializable(typeof(ServerActivateMessage))]
[JsonSerializable(typeof(ServerTimeMessage))]
[JsonSerializable(typeof(StreamStartMessage))]
[JsonSerializable(typeof(StreamEndMessage))]
[JsonSerializable(typeof(StreamClearMessage))]
[JsonSerializable(typeof(GroupUpdateMessage))]
[JsonSerializable(typeof(ServerCommandMessage))]
[JsonSerializable(typeof(ServerStateMessage))]
[JsonSerializable(typeof(RgbColor))]
[JsonSerializable(typeof(RgbColor?))]
internal partial class MessageSerializerContext : JsonSerializerContext
{
}
