namespace Sendspin.SDK.Tests.Audio;

/// <summary>
/// PairingCodes the fake to the real KalmanClockSynchronizer sign convention:
/// ClientToServerTime(c) = c + offset, ServerToClientTime(s) = s - offset - staticDelayUs,
/// GetStatus().OffsetMicroseconds = offset (no static delay). A fake with an inverted
/// convention makes drift tests pass with the wrong sign against production.
/// </summary>
public class FakeClockSynchronizerTests
{
    [Fact]
    public void Conversions_MatchKalmanConvention()
    {
        var fake = new FakeClockSynchronizer
        {
            OffsetMicroseconds = 5_000_000,
            StaticDelayMs = 100,
        };

        Assert.Equal(1_000_000 + 5_000_000, fake.ClientToServerTime(1_000_000));
        Assert.Equal(9_000_000 - 5_000_000 - 100_000, fake.ServerToClientTime(9_000_000));
        Assert.Equal(5_000_000, fake.GetStatus().OffsetMicroseconds);
    }
}
