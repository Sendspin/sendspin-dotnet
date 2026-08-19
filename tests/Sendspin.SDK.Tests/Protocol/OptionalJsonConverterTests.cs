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
        Assert.NotNull(msg.Payload.Metadata.Value);
        Assert.True(msg.Payload.Metadata.Value.Progress.IsAbsent);
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
        Assert.NotNull(msg.Payload.Metadata.Value);
        Assert.True(msg.Payload.Metadata.Value.Progress.IsPresent);
        Assert.Null(msg.Payload.Metadata.Value.Progress.Value);
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
        var meta = msg.Payload.Metadata.Value;
        Assert.NotNull(meta);
        Assert.True(meta.Progress.IsPresent);
        Assert.NotNull(meta.Progress.Value);
        Assert.Equal(30000, meta.Progress.Value!.TrackProgress);
        Assert.Equal(180000, meta.Progress.Value.TrackDuration);
        Assert.Equal(1000, meta.Progress.Value.PlaybackSpeed);
    }

    // --- Serialization round-trip ---

    [Fact]
    public void RoundTrip_PresentWithValue_PreservesData()
    {
        var original = new ServerStateMessage
        {
            Payload = new ServerStatePayload
            {
                Metadata = Optional<ServerMetadata?>.Present(new ServerMetadata
                {
                    Title = Optional<string?>.Present("Round Trip"),
                    Progress = Optional<PlaybackProgress?>.Present(new PlaybackProgress
                    {
                        TrackProgress = 5000,
                        TrackDuration = 120000,
                        PlaybackSpeed = 1000,
                    }),
                }),
            }
        };

        var json = MessageSerializer.Serialize(original);
        var deserialized = MessageSerializer.Deserialize<ServerStateMessage>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Payload.Metadata.Value!.Progress.IsPresent);
        Assert.Equal(5000, deserialized.Payload.Metadata.Value.Progress.Value!.TrackProgress);
    }

    [Fact]
    public void RoundTrip_ExplicitNull_PreservesNull()
    {
        var original = new ServerStateMessage
        {
            Payload = new ServerStatePayload
            {
                Metadata = Optional<ServerMetadata?>.Present(new ServerMetadata
                {
                    Title = Optional<string?>.Present("Null Progress"),
                    Progress = Optional<PlaybackProgress?>.Present(null),
                }),
            }
        };

        var json = MessageSerializer.Serialize(original);
        Assert.Contains("\"progress\":null", json);

        var deserialized = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.Payload.Metadata.Value!.Progress.IsPresent);
        Assert.Null(deserialized.Payload.Metadata.Value.Progress.Value);
    }

    [Fact]
    public void RoundTrip_Absent_OmitsFieldAndRoundTrips()
    {
        var original = new ServerStateMessage
        {
            Payload = new ServerStatePayload
            {
                Metadata = Optional<ServerMetadata?>.Present(new ServerMetadata
                {
                    Title = Optional<string?>.Present("No Progress"),
                    Progress = Optional<PlaybackProgress?>.Absent(),
                }),
            }
        };

        var json = MessageSerializer.Serialize(original);
        Assert.DoesNotContain("progress", json);

        var deserialized = MessageSerializer.Deserialize<ServerStateMessage>(json);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.Payload.Metadata.Value!.Progress.IsAbsent);
    }

    // --- Role objects: the same three states one level up (#196) ---

    [Theory]
    [InlineData("""{"type":"server/state","payload":{}}""", false, false, false)]
    [InlineData("""{"type":"server/state","payload":{"metadata":null}}""", true, false, false)]
    [InlineData("""{"type":"server/state","payload":{"controller":null}}""", false, true, false)]
    [InlineData("""{"type":"server/state","payload":{"color":null}}""", false, false, true)]
    public void RoleObject_ExplicitNull_IsDistinguishableFromAbsent(
        string json, bool metadata, bool controller, bool color)
    {
        // The distinction the whole issue turns on: plain nullables made these four documents
        // deserialize identically, so the spec's "clear this whole role" signal was a no-op.
        var msg = MessageSerializer.Deserialize<ServerStateMessage>(json);
        var payload = msg!.Payload;

        Assert.Equal(metadata, payload.Metadata.IsPresent);
        Assert.Equal(controller, payload.Controller.IsPresent);
        Assert.Equal(color, payload.Color.IsPresent);
    }

    [Fact]
    public void RoleObject_RoundTrips_AbsentOmitsAndPresentNullWritesNull()
    {
        var absent = MessageSerializer.Serialize(new ServerStateMessage
        {
            Payload = new ServerStatePayload(),
        });
        Assert.DoesNotContain("metadata", absent);

        var cleared = MessageSerializer.Serialize(new ServerStateMessage
        {
            Payload = new ServerStatePayload { Metadata = Optional<ServerMetadata?>.Present(null) },
        });
        Assert.Contains("\"metadata\":null", cleared);

        var reparsed = MessageSerializer.Deserialize<ServerStateMessage>(cleared);
        Assert.True(reparsed!.Payload.Metadata.IsPresent);
        Assert.Null(reparsed.Payload.Metadata.Value);
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
