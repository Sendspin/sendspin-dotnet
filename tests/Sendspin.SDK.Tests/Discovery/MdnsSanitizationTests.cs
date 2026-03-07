using Sendspin.SDK.Discovery;

namespace Sendspin.SDK.Tests.Discovery;

public class MdnsSanitizationTests
{
    // --- SanitizeServerId ---

    [Fact]
    public void SanitizeServerId_NormalId_PassesThrough()
    {
        var result = MdnsServerDiscovery.SanitizeServerId("my-server-123");
        Assert.Equal("my-server-123", result);
    }

    [Fact]
    public void SanitizeServerId_StripsControlCharacters()
    {
        // Newlines and tabs could enable log injection
        var result = MdnsServerDiscovery.SanitizeServerId("server\n\rinjected-log-line\t");
        Assert.Equal("serverinjected-log-line", result);
    }

    [Fact]
    public void SanitizeServerId_StripsNullBytes()
    {
        var result = MdnsServerDiscovery.SanitizeServerId("server\0id");
        Assert.Equal("serverid", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeServerId_EmptyOrWhitespace_ReturnsEmpty(string? input)
    {
        var result = MdnsServerDiscovery.SanitizeServerId(input!);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SanitizeServerId_TruncatesOverlyLongId()
    {
        var longId = new string('a', 1000);
        var result = MdnsServerDiscovery.SanitizeServerId(longId);
        Assert.Equal(MdnsServerDiscovery.MaxServerIdLength, result.Length);
    }

    [Fact]
    public void SanitizeServerId_UnicodePreserved()
    {
        var result = MdnsServerDiscovery.SanitizeServerId("サーバー-日本語");
        Assert.Equal("サーバー-日本語", result);
    }

    // --- SanitizePath ---

    [Fact]
    public void SanitizePath_NormalPath_PassesThrough()
    {
        var result = MdnsServerDiscovery.SanitizePath("/sendspin");
        Assert.Equal("/sendspin", result);
    }

    [Fact]
    public void SanitizePath_AddsLeadingSlash()
    {
        var result = MdnsServerDiscovery.SanitizePath("sendspin");
        Assert.Equal("/sendspin", result);
    }

    [Fact]
    public void SanitizePath_StripsQueryString()
    {
        // An attacker could inject ?token=steal to append query parameters to WebSocket URI
        var result = MdnsServerDiscovery.SanitizePath("/sendspin?token=steal&redirect=evil.com");
        Assert.Equal("/sendspin", result);
    }

    [Fact]
    public void SanitizePath_StripsFragment()
    {
        var result = MdnsServerDiscovery.SanitizePath("/sendspin#fragment");
        Assert.Equal("/sendspin", result);
    }

    [Fact]
    public void SanitizePath_RemovesPathTraversal()
    {
        var result = MdnsServerDiscovery.SanitizePath("/sendspin/../../../etc/passwd");
        Assert.Equal("/sendspin/etc/passwd", result);
    }

    [Fact]
    public void SanitizePath_RemovesAuthorityPrefix()
    {
        // "//evil.com/path" in a URI makes evil.com the authority
        var result = MdnsServerDiscovery.SanitizePath("//evil.com/path");
        Assert.Equal("/evil.com/path", result);
    }

    [Fact]
    public void SanitizePath_StripsControlCharacters()
    {
        var result = MdnsServerDiscovery.SanitizePath("/send\nspin\r\n");
        Assert.Equal("/sendspin", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizePath_EmptyOrNull_ReturnsDefault(string? input)
    {
        var result = MdnsServerDiscovery.SanitizePath(input!);
        Assert.Equal("/sendspin", result);
    }

    [Fact]
    public void SanitizePath_SlashOnly_ReturnsDefault()
    {
        var result = MdnsServerDiscovery.SanitizePath("/");
        Assert.Equal("/sendspin", result);
    }

    [Fact]
    public void SanitizePath_TruncatesOverlyLongPath()
    {
        var longPath = "/" + new string('a', 1000);
        var result = MdnsServerDiscovery.SanitizePath(longPath);
        Assert.Equal(MdnsServerDiscovery.MaxPathLength, result.Length);
    }

    [Fact]
    public void SanitizePath_CombinedAttack_QueryAndTraversal()
    {
        var result = MdnsServerDiscovery.SanitizePath("/../../../secret?token=x#frag");
        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain("?", result);
        Assert.DoesNotContain("#", result);
    }
}
