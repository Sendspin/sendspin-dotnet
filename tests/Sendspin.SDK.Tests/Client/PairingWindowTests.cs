using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The pairing window is device-level, not connection-level: the spec closes it on "drop of
/// the connection carrying its attempt" and calls it a state in which the client has decided
/// to accept ONE attempt. A host running several servers therefore shares one window, and
/// exactly one connection may consume any given opening.
/// </summary>
public class PairingWindowTests
{
    [Fact]
    public void NewWindow_IsClosed()
    {
        Assert.False(new PairingWindow().IsOpen);
    }

    [Fact]
    public void Open_MakesItOpen_AndCloseShutsIt()
    {
        var window = new PairingWindow();

        window.Open();
        Assert.True(window.IsOpen);

        window.Close();
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void TryConsume_SucceedsOnceThenCloses()
    {
        // "The window admits exactly one attempt."
        var window = new PairingWindow();
        window.Open();

        Assert.True(window.TryConsume());
        Assert.False(window.TryConsume());
        Assert.False(window.IsOpen);
    }

    [Fact]
    public void TryConsume_OnAClosedWindow_Fails()
    {
        Assert.False(new PairingWindow().TryConsume());
    }

    [Fact]
    public void Window_ExpiresAfterItsLifetime()
    {
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.False(window.IsOpen);
        Assert.False(window.TryConsume());
    }

    [Fact]
    public void Window_DoesNotExpireEarly()
    {
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();

        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));

        Assert.True(window.IsOpen);
        Assert.True(window.TryConsume());
    }

    [Fact]
    public void Reopening_RestartsTheLifetime()
    {
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();
        clock.Advance(TimeSpan.FromMinutes(4));

        window.Open();
        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.True(window.IsOpen);
    }

    [Fact]
    public void ConcurrentConsumers_ProduceExactlyOneWinner()
    {
        // The multi-server case: two connections race for one opening.
        var window = new PairingWindow();
        window.Open();

        int winners = 0;
        Parallel.For(0, 64, _ =>
        {
            if (window.TryConsume())
                Interlocked.Increment(ref winners);
        });

        Assert.Equal(1, winners);
    }

    [Fact]
    public void StateChanged_FiresOnOpenAndClose()
    {
        var window = new PairingWindow();
        int fired = 0;
        window.StateChanged += (_, _) => Interlocked.Increment(ref fired);

        window.Open();
        window.Close();

        Assert.Equal(2, fired);
    }

    /// <summary>Clock stub: only GetUtcNow matters, since the window expires lazily.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
