using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// pairing code presentation grouping, per spec commit 3f8528a9. Grouping is display-only: the pairing code value
/// is the contiguous digits, and separators never enter derivation, entry or the PRS transcript.
/// </summary>
public class PinPresentationGroupingTests
{
    // The spec's table, transcribed independently of the implementation's copy. Both being wrong
    // the same way is the failure mode this guards against, so these are written from the spec
    // text ("4, 3-2, 3-3, 4-3, 4-4, 3-3-3, 4-3-3, 4-3-4, 4-4-4 for L = 4 through 12") rather
    // than from GroupSizes.
    [Theory]
    [InlineData("1234", "1234")]
    [InlineData("12345", "123|45")]
    [InlineData("123456", "123|456")]
    [InlineData("1234567", "1234|567")]
    [InlineData("12345678", "1234|5678")]
    [InlineData("123456789", "123|456|789")]
    [InlineData("1234567890", "1234|567|890")]
    [InlineData("12345678901", "1234|567|8901")]
    [InlineData("123456789012", "1234|5678|9012")]
    public void Groups_MatchTheSpecTable(string pin, string expected)
    {
        var presentation = new PairingCodePresentation(pin, null);

        Assert.Equal(expected, string.Join("|", presentation.Groups));
    }

    [Fact]
    public void Groups_AlwaysReconstructThePairingCodeExactly()
    {
        // The invariant that matters: grouping partitions the digits and invents nothing. A
        // table typo that still summed to the right length would pass the case above only if it
        // also passed here, and vice versa.
        for (int length = 1; length <= 16; length++)
        {
            string pin = string.Concat(Enumerable.Range(0, length).Select(i => (char)('0' + (i % 10))));

            Assert.Equal(pin, string.Concat(new PairingCodePresentation(pin, null).Groups));
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    public void Groups_NeverContainsAGroupSmallerThanThree_WithinTheSpecRange(int length)
    {
        // Length 5 is the spec's stated exception (3-2) and is excluded deliberately.
        string pin = new string('7', length);

        Assert.All(new PairingCodePresentation(pin, null).Groups, g => Assert.True(g.Length >= 3));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234567890123")]
    [InlineData("")]
    public void Groups_OutsideTheSpecRange_ReturnsTheWholePairingCodeAsOneGroup(string pin)
    {
        // Display formatting must not throw a pairing away. 4-12 is the spec's range, so nothing
        // here is reachable from a negotiated pairing code -- this pins the fallback, not a behaviour.
        Assert.Equal(pin, Assert.Single(new PairingCodePresentation(pin, null).Groups));
    }

    [Fact]
    public void PairingCode_RemainsContiguousDigits()
    {
        // Grouping is a derived view. If PairingCode itself ever grew separators, CPace derivation and
        // the PRS transcript would break while every grouping test above still passed.
        var presentation = new PairingCodePresentation("12345678", null);

        Assert.Equal("12345678", presentation.PairingCode);
    }
}
