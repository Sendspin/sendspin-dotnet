using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for server/state handling: repeat/shuffle in controller, Optional-field merge
/// semantics (absent = keep, explicit null = clear) for all metadata string/numeric fields,
/// and the reference-identity contract for the merged progress object.
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

    // --- Optional-field merge: progress (reference identity) ---

    [Fact]
    public void Metadata_Progress_Absent_CarriesForwardSameInstance()
    {
        // Consumers use ReferenceEquals to distinguish fresh progress from progress carried
        // forward by the merge (e.g. the Windows client's seek bar only re-anchors on a fresh
        // instance). A partial update without the progress field must reuse the previous
        // PlaybackProgress instance — not clone it or copy its values.
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
                        "progress": { "track_progress": 5000, "track_duration": 180000, "playback_speed": 1000 }
                    }
                }
            }
            """);

        var firstProgress = client.CurrentGroup?.Metadata?.Progress;
        Assert.NotNull(firstProgress);

        // Partial update: progress absent means "no change"
        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "title": "Track A updated" }
                }
            }
            """);

        Assert.Same(firstProgress, client.CurrentGroup?.Metadata?.Progress);
    }

    [Fact]
    public void Metadata_Progress_Present_IsFreshInstance()
    {
        // Every server/state that carries the progress field yields a newly deserialized
        // PlaybackProgress instance — even when the values are identical to the previous
        // update. This is the other half of the reference-identity freshness contract.
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": {
                        "progress": { "track_progress": 5000, "track_duration": 180000 }
                    }
                }
            }
            """);

        var firstProgress = client.CurrentGroup?.Metadata?.Progress;
        Assert.NotNull(firstProgress);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": {
                        "progress": { "track_progress": 5000, "track_duration": 180000 }
                    }
                }
            }
            """);

        var secondProgress = client.CurrentGroup?.Metadata?.Progress;
        Assert.NotNull(secondProgress);
        Assert.NotSame(firstProgress, secondProgress);
        Assert.Equal(5000, secondProgress.TrackProgress);
    }

    [Fact]
    public void Metadata_Progress_ExplicitNull_ClearsMergedMetadata()
    {
        // progress: null is the spec's "track ended" signal and must clear the merged value,
        // not retain the previous instance.
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": {
                        "progress": { "track_progress": 5000, "track_duration": 180000 }
                    }
                }
            }
            """);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "progress": null }
                }
            }
            """);

        var meta = client.CurrentGroup?.Metadata;
        Assert.NotNull(meta);
        Assert.Null(meta.Progress);
    }

    [Fact]
    public void Metadata_Timestamp_UpdatesWhileAbsentProgressIsCarriedForward()
    {
        // Timestamp merges independently of progress: a partial update carrying a new
        // timestamp but no progress field yields a fresh Timestamp alongside the
        // carried-forward Progress instance. Consumers must not treat a newer timestamp
        // as evidence that the progress object itself is fresh.
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": {
                        "timestamp": 1000000,
                        "progress": { "track_progress": 5000, "track_duration": 180000 }
                    }
                }
            }
            """);

        var firstProgress = client.CurrentGroup?.Metadata?.Progress;
        Assert.NotNull(firstProgress);

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "timestamp": 2000000 }
                }
            }
            """);

        var meta = client.CurrentGroup?.Metadata;
        Assert.NotNull(meta);
        Assert.Equal(2000000, meta.Timestamp);
        Assert.Same(firstProgress, meta.Progress);
    }
}
