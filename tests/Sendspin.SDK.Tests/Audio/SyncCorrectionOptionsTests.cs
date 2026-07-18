using Sendspin.SDK.Audio;

namespace Sendspin.SDK.Tests.Audio;

public class SyncCorrectionOptionsTests
{
    [Fact]
    public void Default_RoutesModerateErrorsThroughResampling()
    {
        var options = new SyncCorrectionOptions();

        // Errors up to 100ms are corrected inaudibly via playback-rate
        // adjustment; audible frame drop/insert is reserved for errors
        // rate correction can't close (9.0.3 item 3).
        Assert.Equal(100_000, options.ResamplingThresholdMicroseconds);
    }

    [Fact]
    public void Default_StillValidates()
    {
        var options = new SyncCorrectionOptions();
        options.Validate(); // must not throw with the new threshold
    }
}
