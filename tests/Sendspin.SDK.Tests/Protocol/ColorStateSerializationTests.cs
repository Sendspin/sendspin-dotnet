using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// Wire-format coverage for the color role: RGB array parsing and the three Optional delta states
/// (absent / present-null / present-value) on the server/state color object.
/// </summary>
public class ColorStateSerializationTests
{
    [Fact]
    public void Color_ParsesRgbArraysAndTimestamp()
    {
        var json = """
            {
                "type": "server/state",
                "payload": {
                    "color": {
                        "timestamp": 1234567,
                        "background_dark": [10, 20, 30],
                        "primary": [255, 128, 0]
                    }
                }
            }
            """;

        var msg = MessageSerializer.Deserialize<ServerStateMessage>(json);

        Assert.NotNull(msg);
        var color = msg.Payload.Color;
        Assert.NotNull(color);
        Assert.Equal(1234567, color.Timestamp);

        Assert.True(color.BackgroundDark.IsPresent);
        Assert.Equal(new RgbColor(10, 20, 30), color.BackgroundDark.Value);

        Assert.True(color.Primary.IsPresent);
        Assert.Equal(new RgbColor(255, 128, 0), color.Primary.Value);
    }

    [Fact]
    public void Color_DistinguishesAbsentNullAndValue()
    {
        // primary present-with-value, accent present-null (clear), on_dark absent.
        var json = """
            {
                "type": "server/state",
                "payload": {
                    "color": { "timestamp": 1, "primary": [1, 2, 3], "accent": null }
                }
            }
            """;

        var color = MessageSerializer.Deserialize<ServerStateMessage>(json)!.Payload.Color!;

        // present-with-value
        Assert.True(color.Primary.IsPresent);
        Assert.NotNull(color.Primary.Value);

        // present-null (clear)
        Assert.True(color.Accent.IsPresent);
        Assert.Null(color.Accent.Value);

        // absent (no change)
        Assert.True(color.OnDark.IsAbsent);
    }

    [Theory]
    [InlineData("[0, 0, 0]", 0, 0, 0)]
    [InlineData("[255, 255, 255]", 255, 255, 255)]
    [InlineData("[300, -5, 128]", 255, 0, 128)] // out-of-range clamps to 0-255
    public void RgbColor_ParsesAndClamps(string array, byte r, byte g, byte b)
    {
        var json = $$"""
            { "type": "server/state", "payload": { "color": { "primary": {{array}} } } }
            """;

        var color = MessageSerializer.Deserialize<ServerStateMessage>(json)!.Payload.Color!;

        Assert.Equal(new RgbColor(r, g, b), color.Primary.Value);
    }
}
