using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Models;

public class RgbColorTests
{
    [Fact]
    public void Equality_ValueSemantics()
    {
        var a = new RgbColor(10, 20, 30);
        var b = new RgbColor(10, 20, 30);
        var c = new RgbColor(99, 20, 30);

        Assert.True(a.Equals(b));
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        Assert.False(a == c);
        Assert.True(a != c);
    }

    [Fact]
    public void ToString_IsUppercaseHex() => Assert.Equal("#FF8000", new RgbColor(255, 128, 0).ToString());

    [Fact]
    public void Deconstruct_YieldsChannels()
    {
        var (r, g, b) = new RgbColor(10, 20, 30);

        Assert.Equal(10, r);
        Assert.Equal(20, g);
        Assert.Equal(30, b);
    }
}
