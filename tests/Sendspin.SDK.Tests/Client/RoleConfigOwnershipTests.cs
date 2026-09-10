using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// <see cref="ClientCapabilities"/> belongs to the app, and a host hands the same instance to
/// every connection it accepts. The two runtime reconfiguration calls — artwork channels and
/// visualizer configuration — therefore write to per-client state copied at construction, not
/// through the app's object: writing back leaked one connection's configuration into its
/// siblings and raced the client/state builders reading the same mutable list.
/// </summary>
public class RoleConfigOwnershipTests
{
    private static readonly List<string> AllRoles = new() { "artwork@v1", "visualizer@v1" };

    /// <summary>
    /// A client over the given capabilities, deliberately without the player role: a player
    /// defers its initial client/state until the clock converges, which would leave these tests
    /// with no state message to read.
    /// </summary>
    private static (SendspinClientService Client, FakeSendspinConnection Connection) ClientOver(
        ClientCapabilities capabilities, params string[] activeRoles)
    {
        var (client, connection, _) = TestClient.Create(
            configure: options => options with { Capabilities = capabilities });
        TestClient.CompleteHandshake(connection, activeRoles);
        return (client, connection);
    }

    private static ClientCapabilities SharedCapabilities() => new()
    {
        Roles = new List<string>(AllRoles),
        ArtworkChannels = new List<ArtworkChannelState>
        {
            new() { Source = ArtworkSources.Album, Format = "jpeg", Width = 512, Height = 512 },
        },
        VisualizerRoleSupport = new VisualizerRoleSupport
        {
            BufferCapacity = 65536,
            RateMax = 30,
            Types = new List<string> { VisualizerTypes.Loudness },
        },
    };

    private static List<ArtworkChannelState> StateChannels(FakeSendspinConnection connection)
    {
        var state = connection.SnapshotSentMessages().OfType<ClientStateMessage>().Last();
        Assert.NotNull(state.Payload.Artwork);
        return state.Payload.Artwork.Channels;
    }

    private static VisualizerStatePayload StateVisualizer(FakeSendspinConnection connection)
    {
        var state = connection.SnapshotSentMessages().OfType<ClientStateMessage>().Last();
        Assert.NotNull(state.Payload.Visualizer);
        return state.Payload.Visualizer;
    }

    [Fact]
    public async Task SetArtworkChannelAsync_LeavesTheSuppliedCapabilitiesUnchanged()
    {
        var capabilities = SharedCapabilities();
        var (client, connection) = ClientOver(capabilities, "artwork@v1");
        using var _c = client;

        await client.SetArtworkChannelAsync(channel: 2, source: ArtworkSources.Artist, format: "png", width: 64, height: 64);

        // The client reported three channels...
        Assert.Equal(3, StateChannels(connection).Count);

        // ...and the app's object is exactly as it was handed over.
        var untouched = Assert.Single(capabilities.ArtworkChannels);
        Assert.Equal(ArtworkSources.Album, untouched.Source);
        Assert.Equal("jpeg", untouched.Format);
        Assert.Equal(512, untouched.Width);
        Assert.Equal(512, untouched.Height);
    }

    [Fact]
    public async Task SetVisualizerConfigurationAsync_LeavesTheSuppliedCapabilitiesUnchanged()
    {
        var capabilities = SharedCapabilities();
        var (client, connection) = ClientOver(capabilities, "visualizer@v1");
        using var _c = client;

        await client.SetVisualizerConfigurationAsync(
            types: new List<string> { VisualizerTypes.Beat }, rateMax: 15);

        Assert.Equal(new[] { VisualizerTypes.Beat }, StateVisualizer(connection).Types);

        var support = capabilities.VisualizerRoleSupport;
        Assert.NotNull(support);
        Assert.Equal(new[] { VisualizerTypes.Loudness }, support.Types);
        Assert.Equal(30, support.RateMax);
        Assert.Equal(65536, support.BufferCapacity);
    }

    [Fact]
    public async Task ArtworkReconfiguration_DoesNotReachASiblingSharingTheCapabilities()
    {
        // Two connections of one host: the same ClientCapabilities instance, two clients.
        var capabilities = SharedCapabilities();
        var (first, firstConnection) = ClientOver(capabilities, "artwork@v1");
        using var _f = first;
        var (second, secondConnection) = ClientOver(capabilities, "artwork@v1");
        using var _s = second;

        await first.SetArtworkChannelAsync(channel: 1, source: ArtworkSources.Artist, format: "png", width: 64, height: 64);

        Assert.Equal(2, StateChannels(firstConnection).Count);

        // The sibling has said nothing since, so its last reported state is still its own —
        // and re-reporting must not pick up the first client's extra channel either.
        await second.SetArtworkChannelAsync(channel: 0, width: 256, height: 256);

        var sibling = Assert.Single(StateChannels(secondConnection));
        Assert.Equal(ArtworkSources.Album, sibling.Source);
        Assert.Equal(256, sibling.Width);
    }

    [Fact]
    public async Task VisualizerReconfiguration_DoesNotReachASiblingSharingTheCapabilities()
    {
        var capabilities = SharedCapabilities();
        var (first, firstConnection) = ClientOver(capabilities, "visualizer@v1");
        using var _f = first;
        var (second, secondConnection) = ClientOver(capabilities, "visualizer@v1");
        using var _s = second;

        await first.SetVisualizerConfigurationAsync(
            types: new List<string> { VisualizerTypes.Beat }, rateMax: 15);

        Assert.Equal(new[] { VisualizerTypes.Beat }, StateVisualizer(firstConnection).Types);

        // The sibling still reports what it was constructed with, buffer capacity included.
        await second.SetVisualizerConfigurationAsync(
            types: new List<string> { VisualizerTypes.Peak }, rateMax: 60);

        Assert.Equal(new[] { VisualizerTypes.Peak }, StateVisualizer(secondConnection).Types);
        Assert.Equal(60, StateVisualizer(secondConnection).RateMax);

        var hello = secondConnection.SnapshotSentMessages().OfType<ClientHelloMessage>().Last();
        Assert.Equal(65536, hello.Payload.VisualizerV1Support!.BufferCapacity);
    }

    [Fact]
    public async Task MutatingTheCapabilitiesAfterConstruction_DoesNotReachAConnectedClient()
    {
        // The other half of the ownership boundary: the client copies the channel list at
        // construction, so an app writing to its own object mid-connection cannot change what a
        // connection reports behind the SDK's back. SetArtworkChannelAsync is the supported way.
        var capabilities = SharedCapabilities();
        var (client, connection) = ClientOver(capabilities, "artwork@v1");
        using var _c = client;

        capabilities.ArtworkChannels.Add(
            new ArtworkChannelState { Source = ArtworkSources.Artist, Format = "png", Width = 8, Height = 8 });

        await client.SetArtworkChannelAsync(channel: 0, height: 400);

        var only = Assert.Single(StateChannels(connection));
        Assert.Equal(400, only.Height);
    }

    [Fact]
    public async Task ConcurrentReconfiguration_AndStateSends_ProduceAConsistentDeclaration()
    {
        // The setters run on app threads while the state builders run on the send path. With the
        // channel list read straight off the shared capabilities, a build enumerating it while a
        // setter appended a gap filler threw "Collection was modified"; the per-client list is
        // guarded so both sides see whole declarations.
        var capabilities = SharedCapabilities();
        var (client, connection) = ClientOver(capabilities, "artwork@v1", "visualizer@v1");
        using var _c = client;

        using var start = new Barrier(8);

        var workers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            start.SignalAndWait();
            for (int n = 0; n < 40; n++)
            {
                if (i % 2 == 0)
                {
                    await client.SetArtworkChannelAsync(
                        channel: (i + n) % 4, source: ArtworkSources.Album, format: "jpeg", width: 64, height: 64);
                }
                else
                {
                    await client.SetVisualizerConfigurationAsync(
                        types: new List<string> { VisualizerTypes.Loudness }, rateMax: 30 + n);
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        // One more send after the storm, so the last recorded message is provably built from the
        // settled configuration rather than from whichever concurrent build finished last.
        await client.SetArtworkChannelAsync(
            channel: 3, source: ArtworkSources.Album, format: "jpeg", width: 64, height: 64);

        // Every client/state that went out carries a whole, in-bounds channel declaration.
        foreach (var state in connection.SnapshotSentMessages().OfType<ClientStateMessage>())
        {
            var channels = state.Payload.Artwork?.Channels;
            Assert.NotNull(channels);
            Assert.InRange(channels.Count, 1, 4);
            Assert.All(channels, c => Assert.False(string.IsNullOrEmpty(c.Source)));

            // An enabled channel always carries the three fields the spec requires of one.
            Assert.All(
                channels.Where(c => c.Source != ArtworkSources.None),
                c =>
                {
                    Assert.NotNull(c.Format);
                    Assert.NotNull(c.Width);
                    Assert.NotNull(c.Height);
                });
        }

        // All four channels ended up configured, and the app's object never saw any of it.
        Assert.Equal(4, StateChannels(connection).Count);
        Assert.Single(capabilities.ArtworkChannels);
        Assert.Equal(new[] { VisualizerTypes.Loudness }, capabilities.VisualizerRoleSupport!.Types);
        Assert.Equal(30, capabilities.VisualizerRoleSupport.RateMax);
    }
}
