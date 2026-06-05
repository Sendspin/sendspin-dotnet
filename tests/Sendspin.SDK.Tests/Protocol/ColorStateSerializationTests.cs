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

    [Fact]
    public void Color_AllSixColorsParse()
    {
        var json = """
            {
                "type": "server/state",
                "payload": { "color": {
                    "background_dark": [1, 1, 1], "background_light": [2, 2, 2],
                    "primary": [3, 3, 3], "accent": [4, 4, 4],
                    "on_dark": [5, 5, 5], "on_light": [6, 6, 6]
                } }
            }
            """;

        var c = MessageSerializer.Deserialize<ServerStateMessage>(json)!.Payload.Color!;

        Assert.Equal(new RgbColor(1, 1, 1), c.BackgroundDark.Value);
        Assert.Equal(new RgbColor(2, 2, 2), c.BackgroundLight.Value);
        Assert.Equal(new RgbColor(3, 3, 3), c.Primary.Value);
        Assert.Equal(new RgbColor(4, 4, 4), c.Accent.Value);
        Assert.Equal(new RgbColor(5, 5, 5), c.OnDark.Value);
        Assert.Equal(new RgbColor(6, 6, 6), c.OnLight.Value);
    }

    [Theory]
    [InlineData("[1, 2]")]          // too few
    [InlineData("[1, 2, 3, 4]")]    // too many
    [InlineData("[]")]              // empty
    [InlineData("5")]               // not an array
    [InlineData("\"#fff\"")]        // string
    [InlineData("[1, \"x\", 3]")]   // non-numeric channel
    public void Color_MalformedValue_DegradesToAbsentAndKeepsSiblings(string malformedPrimary)
    {
        // A malformed color must NOT abort parsing of the rest of the color object (or message).
        var json = $$"""
            { "type": "server/state", "payload": { "color": { "primary": {{malformedPrimary}}, "accent": [9, 9, 9] } } }
            """;

        var color = MessageSerializer.Deserialize<ServerStateMessage>(json)!.Payload.Color!;

        Assert.True(color.Primary.IsAbsent);                     // malformed -> no change
        Assert.Equal(new RgbColor(9, 9, 9), color.Accent.Value); // sibling still parsed
    }
}
