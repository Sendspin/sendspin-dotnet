using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// <c>min_buffer_ms</c> is a promise in two directions: the server keeps that much queued, and
/// the player waits for that much before starting. The client reported it to the server but
/// never told its own pipeline, so an app advertising 500 ms still started at the SDK's 150 ms
/// default — before the audio it asked for had arrived.
/// </summary>
public class PlayerMinBufferWiringTests
{
    private static (SendspinClientService Client, FakeAudioPipeline Pipeline) PlayerClient(
        int minBufferMs)
    {
        var pipe = new FakeAudioPipeline();
        var (client, _, _) = TestClient.Create(configure: options => options with
        {
            AudioPipeline = pipe,
            ClockSynchronizer = new FakeClockSynchronizer { IsConverged = true },
            Capabilities = new ClientCapabilities { MinBufferMs = minBufferMs },
        });
        return (client, pipe);
    }

    [Fact]
    public void Construction_ForwardsTheAdvertisedMinBufferToThePipeline()
    {
        var (client, pipe) = PlayerClient(minBufferMs: 500);
        using var _c = client;

        Assert.Equal(new[] { 500 }, pipe.MinBufferMsCalls);
    }

    [Fact]
    public void Construction_ForwardsTheDefaultWhenNothingIsConfigured()
    {
        var (client, pipe) = PlayerClient(PlayerBufferCapacity.DefaultMinBufferMilliseconds);
        using var _c = client;

        Assert.Equal(
            new[] { PlayerBufferCapacity.DefaultMinBufferMilliseconds },
            pipe.MinBufferMsCalls);
    }

    [Fact]
    public async Task UpdateTimingAsync_ForwardsTheNewMinBufferToThePipeline()
    {
        // The spec lets a client update its timing parameters at any time
        // (roles/player/v1.md:68). A gate left on the old value is a gate that no longer
        // matches what the server was told.
        var (client, pipe) = PlayerClient(minBufferMs: 150);
        using var _c = client;

        await client.UpdateTimingAsync(requiredLeadTimeMs: 200, minBufferMs: 400);

        Assert.Equal(new[] { 150, 400 }, pipe.MinBufferMsCalls);
    }

    [Fact]
    public async Task NegativeMinBuffer_IsForwardedClamped()
    {
        // The wire value is clamped at zero; the gate must see the same number the server does.
        var (client, pipe) = PlayerClient(minBufferMs: -50);
        using var _c = client;

        await client.UpdateTimingAsync(requiredLeadTimeMs: 200, minBufferMs: -1);

        Assert.Equal(new[] { 0, 0 }, pipe.MinBufferMsCalls);
    }
}
