using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Models;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Spec #257: a player's <c>supported_formats</c> must list at least one entry. The SDK builds
/// that list from <see cref="ClientCapabilities.AudioFormats"/>, so an empty list would emit a
/// player@v1_support with no formats. The check is construction-time — like the visualizer and
/// pairing-code validations — and gated on the player role, since a non-player client never emits
/// the object.
/// </summary>
public class PlayerSupportedFormatsValidationTests
{
    [Theory]
    [InlineData("player@v1")]
    [InlineData("player@v2")] // matched by family, so a future player version is caught too
    public void EmptyAudioFormats_WithPlayerRole_ThrowsAtConstruction(string playerRole)
    {
        Assert.Throws<ArgumentException>(() => TestClient.Create(configure: options => options with
        {
            Capabilities = new ClientCapabilities
            {
                Roles = new List<string> { playerRole },
                AudioFormats = new List<AudioFormat>(),
            },
        }));
    }

    [Fact]
    public void EmptyAudioFormats_WithoutPlayerRole_IsAllowed()
    {
        // supported_formats is only put in client/hello for player@v1, so a client that does not
        // advertise the player role has nothing to violate by clearing its formats.
        var (client, _, _) = TestClient.Create(configure: options => options with
        {
            Capabilities = new ClientCapabilities
            {
                Roles = new List<string> { ClientRoles.Controller },
                AudioFormats = new List<AudioFormat>(),
            },
        });

        client.Dispose();
    }

    [Fact]
    public void HostConstruction_WithPlayerRoleAndEmptyAudioFormats_Throws()
    {
        // The host validates the same capabilities up front, so an empty player supported_formats
        // is rejected at construction on the listen path too — before any server connects.
        Assert.Throws<ArgumentException>(() => new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                Capabilities = new ClientCapabilities
                {
                    Roles = new List<string> { ClientRoles.Player },
                    AudioFormats = new List<AudioFormat>(),
                },
            },
            advertiserOptions: new AdvertiserOptions { Enabled = false }));
    }
}
