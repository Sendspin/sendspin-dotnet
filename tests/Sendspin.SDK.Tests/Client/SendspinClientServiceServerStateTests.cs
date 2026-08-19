using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for server/state handling: repeat/shuffle in controller, Optional-field merge
/// semantics (absent = keep, explicit null = clear) for all metadata string/numeric fields,
/// the same three states one level up on the role objects themselves, and the
/// reference-identity contract for the merged progress object.
/// </summary>
public class SendspinClientServiceServerStateTests
{
    [Fact]
    public void RepeatAndShuffle_ReadFromControllerObject()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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

    // --- Role objects: absent / present-null / present-value (#196) ---

    [Fact]
    public void MetadataRoleObject_ExplicitNull_ClearsAllMetadataState()
    {
        // messaging.md: "a whole role object set to null clears all of that role's state".
        // The server sends this when metadata leaves active_roles, and on pairing quiesce, so
        // the client must stop exposing the last track rather than holding it indefinitely.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": {
                        "title": "Track A",
                        "artist": "Artist",
                        "progress": { "track_progress": 5000, "track_duration": 180000 }
                    }
                }
            }
            """);

        Assert.NotNull(client.CurrentGroup?.Metadata);

        GroupState? raised = null;
        client.GroupStateChanged += (_, g) => raised = g;

        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "metadata": null } }
            """);

        Assert.Null(client.CurrentGroup?.Metadata);

        // A UI only drops the stale track if it is told to: the clear must be announced, not
        // just applied.
        Assert.Same(client.CurrentGroup, raised);
    }

    [Fact]
    public void MetadataRoleObject_Absent_LeavesMetadataState()
    {
        // The control the null case turns on: a server/state carrying only another role must
        // not be mistaken for a metadata clear.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "metadata": { "title": "Track A" } } }
            """);

        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "controller": { "volume": 30 } } }
            """);

        Assert.Equal("Track A", client.CurrentGroup?.Metadata?.Title);
        Assert.Equal(30, client.CurrentGroup?.Volume);
    }

    [Fact]
    public void MetadataRoleObject_ExplicitNull_ThenValue_StartsFromEmpty()
    {
        // The clear must drop the merge base too, not just the exposed object: a later partial
        // update has to start from empty rather than resurrecting pre-clear fields.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "metadata": { "title": "Track A", "artist": "Artist" } } }
            """);
        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "metadata": null } }
            """);
        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "metadata": { "title": "Track B" } } }
            """);

        var meta = client.CurrentGroup?.Metadata;
        Assert.NotNull(meta);
        Assert.Equal("Track B", meta.Title);
        Assert.Null(meta.Artist);
    }

    [Fact]
    public void ControllerRoleObject_ExplicitNull_ClearsControllerState()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "controller": {
                        "volume": 40, "muted": true, "repeat": "all", "shuffle": true,
                        "supported_commands": ["play", "pause"]
                    }
                }
            }
            """);

        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "controller": null } }
            """);

        // Everything the controller role owns returns to the value a group carries before the
        // server has reported any of it.
        var group = client.CurrentGroup;
        Assert.NotNull(group);
        var unreported = new GroupState();
        Assert.Equal(unreported.Volume, group.Volume);
        Assert.Equal(unreported.Muted, group.Muted);
        Assert.Null(group.Repeat);
        Assert.False(group.Shuffle);
        Assert.Null(group.SupportedCommands);
    }

    [Fact]
    public void ControllerRoleObject_Absent_LeavesControllerState()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "controller": { "volume": 40, "repeat": "one" } } }
            """);
        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "metadata": { "title": "Track A" } } }
            """);

        Assert.Equal(40, client.CurrentGroup?.Volume);
        Assert.Equal("one", client.CurrentGroup?.Repeat);
    }

    [Fact]
    public void MetadataRoleObject_ExplicitNull_LeavesSiblingRolesAlone()
    {
        // "Clear all of THAT role's state": deactivating metadata must not disturb the
        // controller or color state carried on the same GroupState.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": {
                    "metadata": { "title": "Track A" },
                    "controller": { "volume": 40 },
                    "color": { "primary": [1, 2, 3] }
                }
            }
            """);

        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "metadata": null } }
            """);

        Assert.Null(client.CurrentGroup?.Metadata);
        Assert.Equal(40, client.CurrentGroup?.Volume);
        Assert.Equal(new RgbColor(1, 2, 3), client.CurrentGroup?.Colors.Primary);
    }

    [Fact]
    public void MetadataLeafNull_StillClearsOnlyThatLeaf()
    {
        // Regression guard for the level the role-object fix sits above: wrapping the role
        // objects must not turn a leaf null into a whole-role clear.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "metadata": { "title": "Track A", "artist": "Artist" } } }
            """);
        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "metadata": { "artist": null } } }
            """);

        var meta = client.CurrentGroup?.Metadata;
        Assert.NotNull(meta);
        Assert.Equal("Track A", meta.Title);
        Assert.Null(meta.Artist);
    }

    // --- Optional-field merge: artwork_url ---

    [Fact]
    public void Metadata_ArtworkUrl_WithValue_SetsMergedMetadata()
    {
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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

        // Guard against a vacuous pass: prove the second message was actually processed
        // (a silently dropped message would leave the old instance in place too).
        var meta = client.CurrentGroup?.Metadata;
        Assert.NotNull(meta);
        Assert.Equal("Track A updated", meta.Title);
        Assert.Same(firstProgress, meta.Progress);
    }

    [Fact]
    public void Metadata_Progress_Present_IsFreshInstance()
    {
        // Every server/state that carries the progress field yields a newly deserialized
        // PlaybackProgress instance — even when the values are identical to the previous
        // update. This is the other half of the reference-identity freshness contract.
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
        var (client, connection, _) = TestClient.Create();
        using var _c = client;

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
