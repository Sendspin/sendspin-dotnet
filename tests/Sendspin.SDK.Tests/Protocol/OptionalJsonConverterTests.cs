using System.Text.Json;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

public class OptionalJsonConverterTests
{
    // --- Deserialization ---

    [Fact]
    public void Deserialize_AbsentField_IsAbsent()
    {
        var json = """
        {
            "type": "server/state",
            "payload": {
                "metadata": {
                    "title": "Test Song",
                    "artist": "Test Artist"
                }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Metadata);
        Assert.True(msg.Payload.Metadata.Progress.IsAbsent);
    }

    [Fact]
    public void Deserialize_ExplicitNull_IsPresentWithNullValue()
    {
        var json = """
        {
            "type": "server/state",
            "payload": {
                "metadata": {
                    "title": "Test Song",
                    "artist": "Test Artist",
                    "progress": null
                }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Metadata);
        Assert.True(msg.Payload.Metadata.Progress.IsPresent);
        Assert.Null(msg.Payload.Metadata.Progress.Value);
    }

    [Fact]
    public void Deserialize_PresentWithValue_IsPresentWithData()
    {
        var json = """
        {
            "type": "server/state",
            "payload": {
                "metadata": {
                    "title": "Test Song",
                    "artist": "Test Artist",
                    "progress": {
                        "track_progress": 30000,
                        "track_duration": 180000,
                        "playback_speed": 1000
                    }
                }
            }
        }
        """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(msg);
        Assert.NotNull(msg.Payload.Metadata);
        Assert.True(msg.Payload.Metadata.Progress.IsPresent);
        Assert.NotNull(msg.Payload.Metadata.Progress.Value);
        Assert.Equal(30000, msg.Payload.Metadata.Progress.Value!.TrackProgress);
        Assert.Equal(180000, msg.Payload.Metadata.Progress.Value.TrackDuration);
        Assert.Equal(1000, msg.Payload.Metadata.Progress.Value.PlaybackSpeed);
    }

    // --- Serialization round-trip ---

    [Fact]
    public void RoundTrip_PresentWithValue_PreservesData()
    {
        var original = new ServerStateMessage
        {
            Payload = new ServerStatePayload
            {
                Metadata = new ServerMetadata
                {
                    Title = Optional<string?>.Present("Round Trip"),
                    Progress = Optional<PlaybackProgress?>.Present(new PlaybackProgress
                    {
                        TrackProgress = 5000,
                        TrackDuration = 120000,
                        PlaybackSpeed = 1000,
                    }),
                }
            }
        };

        var json = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize<ServerStateMessage>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Payload.Metadata!.Progress.IsPresent);
        Assert.Equal(5000, deserialized.Payload.Metadata.Progress.Value!.TrackProgress);
    }

    [Fact]
    public void RoundTrip_ExplicitNull_PreservesNull()
    {
        var original = new ServerStateMessage
        {
            Payload = new ServerStatePayload
            {
                Metadata = new ServerMetadata
                {
                    Title = Optional<string?>.Present("Null Progress"),
                    Progress = Optional<PlaybackProgress?>.Present(null),
                }
            }
        };

        var json = MessageSerializer.Serialize(original);
        Assert.Contains("\"progress\":null", json);

        var deserialized = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.Payload.Metadata!.Progress.IsPresent);
        Assert.Null(deserialized.Payload.Metadata.Progress.Value);
    }

    [Fact]
    public void RoundTrip_Absent_OmitsFieldAndRoundTrips()
    {
        var original = new ServerStateMessage
        {
            Payload = new ServerStatePayload
            {
                Metadata = new ServerMetadata
                {
                    Title = Optional<string?>.Present("No Progress"),
                    Progress = Optional<PlaybackProgress?>.Absent(),
                }
            }
        };

        var json = MessageSerializer.Serialize(original);
        Assert.DoesNotContain("progress", json);

        var deserialized = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.Payload.Metadata!.Progress.IsAbsent);
    }

    // --- Unregistered type safety (AOT guardrail) ---

    [Fact]
    public void CreateConverter_UnregisteredType_ThrowsDirectly()
    {
        // Calling the factory directly with an unregistered Optional<T> type.
        // double is not used in any protocol message, so it is not registered.
        var factory = new OptionalJsonConverterFactory();
        var unregisteredType = typeof(Optional<double>);

        Assert.True(factory.CanConvert(unregisteredType));

        var ex = Assert.Throws<NotSupportedException>(
            () => factory.CreateConverter(unregisteredType, new JsonSerializerOptions()));

        Assert.Contains("Optional<Double>", ex.Message);
        Assert.Contains(nameof(OptionalJsonConverterFactory), ex.Message);
    }

    [Fact]
    public void CreateConverter_UnregisteredType_ThrowsDuringSerialization()
    {
        // Simulating what happens when someone adds Optional<T> to a message
        // but forgets to register it in the factory — hit via JsonSerializer
        var options = new JsonSerializerOptions
        {
            Converters = { new OptionalJsonConverterFactory() },
        };

        var value = Optional<double>.Present(3.14);

        Assert.Throws<NotSupportedException>(
            () => JsonSerializer.Serialize(value, options));
    }

}
