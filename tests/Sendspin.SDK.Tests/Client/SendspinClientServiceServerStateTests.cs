using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for server/state handling: repeat/shuffle in controller, and Optional-field merge
/// semantics (absent = keep, explicit null = clear) for all metadata string/numeric fields.
/// </summary>
public class SendspinClientServiceServerStateTests
{
    [Fact]
    public void RepeatAndShuffle_ReadFromControllerObject()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "controller": { "volume": 40, "muted": false, "repeat": "all", "shuffle": true }
                }
            }
            """);

        Assert.NotNull(client.CurrentGroup);
        Assert.Equal("all", client.CurrentGroup.Repeat);
        Assert.True(client.CurrentGroup.Shuffle);
    }

    [Fact]
    public void RepeatAndShuffle_InMetadataObject_AreIgnored()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        // Old wire layout: repeat/shuffle under metadata. They moved to the controller object,
        // so the client must not pick them up here.
        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "title": "Song", "repeat": "one", "shuffle": true }
                }
            }
            """);

        Assert.NotNull(client.CurrentGroup);
        Assert.Equal("Song", client.CurrentGroup.Metadata?.Title);
        Assert.Null(client.CurrentGroup.Repeat);
        Assert.False(client.CurrentGroup.Shuffle);
    }

    // --- Optional-field merge: artwork_url ---

    [Fact]
    public void Metadata_ArtworkUrl_WithValue_SetsMergedMetadata()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "title": "Track A", "artwork_url": "https://art.example.com/cover.jpg" }
                }
            }
            """);

        Assert.Equal("https://art.example.com/cover.jpg", client.CurrentGroup?.Metadata?.ArtworkUrl);
    }

    [Fact]
    public void Metadata_ArtworkUrl_ExplicitNull_ClearsMergedMetadata()
    {
        // Regression: artwork_url: null is the spec's "clear" signal (sent by MA on artless tracks).
        // The SDK must not retain the old URL via the ?? merge operator.
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "title": "Track A", "artwork_url": "https://art.example.com/cover.jpg" }
                }
            }
            """);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "title": "Track B", "artwork_url": null }
                }
            }
            """);

        Assert.Null(client.CurrentGroup?.Metadata?.ArtworkUrl);
    }

    [Fact]
    public void Metadata_ArtworkUrl_Absent_RetainsPreviousValue()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "title": "Track A", "artwork_url": "https://art.example.com/cover.jpg" }
                }
            }
            """);

        // Partial update: artwork_url absent means "no change"
        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "title": "Track A updated" }
                }
            }
            """);

        Assert.Equal("https://art.example.com/cover.jpg", client.CurrentGroup?.Metadata?.ArtworkUrl);
    }

    // --- Optional-field merge: all string fields (cleared_update() scenario) ---

    [Fact]
    public void Metadata_AllStringFields_ExplicitNull_ClearMergedMetadata()
    {
        // cleared_update() in aiosendspin nulls every field when playback stops.
        // All fields must forward the null rather than silently retaining old values.
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": {
                        "title": "Track A",
                        "artist": "Artist",
                        "album_artist": "Album Artist",
                        "album": "Album",
                        "artwork_url": "https://art.example.com/cover.jpg"
                    }
                }
            }
            """);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": {
                        "title": null,
                        "artist": null,
                        "album_artist": null,
                        "album": null,
                        "artwork_url": null
                    }
                }
            }
            """);

        var meta = client.CurrentGroup?.Metadata;
        Assert.NotNull(meta);
        Assert.Null(meta.Title);
        Assert.Null(meta.Artist);
        Assert.Null(meta.AlbumArtist);
        Assert.Null(meta.Album);
        Assert.Null(meta.ArtworkUrl);
    }

    [Fact]
    public void Metadata_NumericFields_ExplicitNull_ClearMergedMetadata()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "year": 2023, "track": 5 }
                }
            }
            """);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "year": null, "track": null }
                }
            }
            """);

        var meta = client.CurrentGroup?.Metadata;
        Assert.NotNull(meta);
        Assert.Null(meta.Year);
        Assert.Null(meta.Track);
    }
}
