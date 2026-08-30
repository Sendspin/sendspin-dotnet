using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Extensions;
using Sendspin.SDK.Tests.Audio;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// #253: a server-initiated connection refused from inside its own handshake completion must
/// leave alone the state it shares with the session that is still playing. The application's
/// refusal runs inside <c>MarkConnected</c>'s synchronous state dispatch — the host's handshake
/// waiter completes inline — so by the time <c>HandleServerHello</c> resumes, the connection it
/// is finishing has already been closed.
/// </summary>
/// <remarks>
/// Over a real socket rather than <see cref="FakeSendspinConnection"/>: the bug IS that
/// synchronous dispatch out of <see cref="IncomingConnection.MarkConnected"/>, and only the
/// concrete transports perform the promotion at all.
/// </remarks>
public class HandshakeCompletionTeardownTests : IAsyncDisposable
{
    private const string ServerHello =
        """{"type":"server/hello","payload":{"server_id":"srv-1","version":1,"active_roles":["player@v1"]}}""";

    private readonly FakeAudioPipeline _pipeline = new();

    // Reports converged from the start, so a Reset() shows up as the flip back to false — this
    // fake's Reset does to the sync state exactly what the real one does.
    private readonly FakeClockSynchronizer _clock = new() { IsConverged = true, HasMinimalSync = true };

    private readonly CapturingLogger<SendspinClientService> _logs = new();

    private IncomingLoopback? _loopback;
    private SendspinClientService? _client;

    [Fact]
    public async Task ConnectionClosedInsideItsOwnHandshakeCompletion_LeavesTheSharedClockAndPipelineAlone()
    {
        var client = await AcceptAsync();

        // What an application does when it refuses a server it does not want: close, from the
        // notification that the connection came up. That notification is raised by
        // MarkConnected, while HandleServerHello is still running.
        client.ConnectionStateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Connected)
            {
                client.DisconnectAsync("another_server").SafeFireAndForget();
            }
        };

        await _loopback!.SendFromPeerAsync(ServerHello);

        // Disconnected is published at the end of the close, which is strictly after the
        // handshake tail under test has run or been skipped.
        await WaitUntilAsync(
            () => client.ConnectionState == ConnectionState.Disconnected,
            "the refused connection to finish closing");

        // Each of these is shared with whatever other session the application is running, which
        // is how the teardown of a refused socket stopped playback on one it never touched.
        Assert.True(_clock.IsConverged, "the shared clock synchronizer was reset");
        Assert.Equal(0, _pipeline.NotifyReconnectCount);
        Assert.False(Logged("Sending initial client/state"));
        Assert.False(Logged("Failed to send initial client state"));
    }

    [Fact]
    public async Task ConnectionThatSurvivesItsHandshake_StillResetsTheClockAndNotifiesThePipeline()
    {
        // Positive control. Without it, an implementation that had simply deleted the handshake
        // tail would pass the test above just as well.
        var client = await AcceptAsync();

        await _loopback!.SendFromPeerAsync(ServerHello);

        await WaitUntilAsync(
            () => !_clock.IsConverged
                && _pipeline.NotifyReconnectCount == 1
                && Logged("Sending initial client/state"),
            "the accepted connection's handshake tail");

        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_loopback is not null)
        {
            await _loopback.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {because}");
            await Task.Delay(10);
        }
    }

    private async Task<SendspinClientService> AcceptAsync()
    {
        _loopback = await IncomingLoopback.StartAsync();
        _client = new SendspinClientService(
            _logs,
            _loopback.Incoming,
            clockSynchronizer: _clock,
            audioPipeline: _pipeline);

        return _client;
    }

    private bool Logged(string fragment) =>
        _logs.Entries.Any(e => e.Message.Contains(fragment, StringComparison.Ordinal));
}
