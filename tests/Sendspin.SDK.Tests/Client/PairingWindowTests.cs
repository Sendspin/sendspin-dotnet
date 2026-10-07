using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// The pairing window is device-level, not connection-level: the spec calls it a state in
/// which the client has decided to accept pairing attempts, and admits them "only on the
/// connection that carries its first". A host running several servers therefore shares one
/// window, and exactly one connection is admitted by any given opening.
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
    public void TryAdmit_LeavesTheWindowOpen_AndAdmitsTheSameConnectionAgain()
    {
        // An attempt that ends timed out or cancelled "does not itself close the window".
        var window = new PairingWindow();
        var connection = new object();
        window.Open();

        Assert.True(window.TryAdmit(connection));
        Assert.True(window.TryAdmit(connection));
        Assert.True(window.IsOpen);
    }

    [Fact]
    public void TryAdmit_OnAnotherConnection_Fails()
    {
        // "The window admits attempts only on the connection that carries its first."
        var window = new PairingWindow();
        window.Open();

        Assert.True(window.TryAdmit(new object()));
        Assert.False(window.TryAdmit(new object()));
    }

    [Fact]
    public void TryAdmit_OnAClosedWindow_Fails()
    {
        Assert.False(new PairingWindow().TryAdmit(new object()));
    }

    [Fact]
    public void RecordFailedAttempt_ClosesTheWindowOnTheFifth()
    {
        var window = new PairingWindow();
        var connection = new object();
        window.Open();
        window.TryAdmit(connection);

        for (int i = 0; i < 4; i++)
        {
            window.RecordFailedAttempt(connection);
        }

        Assert.True(window.IsOpen);

        window.RecordFailedAttempt(connection);

        Assert.False(window.IsOpen);
    }

    [Fact]
    public void RecordFailedAttempt_OnAnotherConnection_DoesNotCount()
    {
        // An ungated attempt failing elsewhere is not one of this window's attempts.
        var window = new PairingWindow();
        window.Open();
        window.TryAdmit(new object());

        var other = new object();
        for (int i = 0; i < 5; i++)
        {
            window.RecordFailedAttempt(other);
        }

        Assert.True(window.IsOpen);
    }

    [Fact]
    public void CloseFor_TheAdmittedConnection_ClosesTheWindow()
    {
        var window = new PairingWindow();
        var connection = new object();
        window.Open();
        window.TryAdmit(connection);

        window.CloseFor(connection);

        Assert.False(window.IsOpen);
    }

    [Fact]
    public void CloseFor_AnotherConnection_LeavesTheWindowOpen()
    {
        // Only the drop of the connection carrying the attempts closes the window; any other
        // connection going away must not spend the operator's gesture.
        var window = new PairingWindow();
        window.Open();
        window.TryAdmit(new object());

        window.CloseFor(new object());

        Assert.True(window.IsOpen);
    }

    [Fact]
    public void Reopening_AdmitsADifferentConnection_WithAFreshFailureCount()
    {
        var window = new PairingWindow();
        var first = new object();
        window.Open();
        window.TryAdmit(first);
        for (int i = 0; i < 4; i++)
        {
            window.RecordFailedAttempt(first);
        }

        window.Open();
        var second = new object();

        Assert.True(window.TryAdmit(second));
        window.RecordFailedAttempt(second);
        Assert.True(window.IsOpen);
    }

    [Fact]
    public void Window_ExpiresAfterItsLifetime()
    {
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.False(window.IsOpen);
        Assert.False(window.TryAdmit(new object()));
    }

    [Fact]
    public void Window_DoesNotExpireEarly()
    {
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();

        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));

        Assert.True(window.IsOpen);
        Assert.True(window.TryAdmit(new object()));
    }

    [Fact]
    public void Window_AtExactlyItsLifetime_IsStillOpen()
    {
        // The comparison is strictly greater-than, so a window at exactly its lifetime has
        // not yet expired. PairingCodes the boundary the 4:59/5:01 tests leave open.
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(window.IsOpen);
        Assert.True(window.TryAdmit(new object()));
    }

    [Fact]
    public void WallClockSteppingForward_DoesNotCloseTheWindow()
    {
        // A device that boots without a valid clock: the operator presses the pairing button,
        // then NTP steps the clock forward by years. A minute has passed, not the lifetime.
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();

        clock.StepWallClock(TimeSpan.FromDays(365 * 50));
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(window.IsOpen);
        Assert.True(window.TryAdmit(new object()));
    }

    [Fact]
    public void WallClockSteppingBack_DoesNotExtendTheWindow()
    {
        var clock = new FakeClock();
        var window = new PairingWindow(TimeSpan.FromMinutes(5), clock);
        window.Open();

        clock.StepWallClock(TimeSpan.FromHours(-1));
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.False(window.IsOpen);
        Assert.False(window.TryAdmit(new object()));
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
    public void ConcurrentConnections_ProduceExactlyOneWinner()
    {
        // The multi-server case: two connections race for one opening.
        var window = new PairingWindow();
        window.Open();

        int winners = 0;
        Parallel.For(0, 64, _ =>
        {
            if (window.TryAdmit(new object()))
            {
                Interlocked.Increment(ref winners);
            }
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

    [Fact]
    public void StateChanged_ContainsAThrowingSubscriber()
    {
        // Every connection sharing the window subscribes, and the raise is synchronous on the
        // caller's thread -- for a local open, whatever thread the app opened it on.
        // A throwing handler must not
        // reach the caller or stop the other subscribers from seeing the opening.
        var window = new PairingWindow();
        int reached = 0;
        window.StateChanged += (_, _) => throw new InvalidOperationException("subscriber fault");
        window.StateChanged += (_, _) => Interlocked.Increment(ref reached);

        window.Open();
        window.Close();

        Assert.Equal(2, reached);
    }

    /// <summary>
    /// Clock stub with the two clocks a device has: elapsed time, which only moves forward, and
    /// the wall clock, which can also be stepped. The window expires lazily, so nothing fires.
    /// </summary>
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private long _elapsedTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _elapsedTicks;

        public void Advance(TimeSpan by)
        {
            _now += by;
            _elapsedTicks += by.Ticks;
        }

        public void StepWallClock(TimeSpan by) => _now += by;
    }
}
