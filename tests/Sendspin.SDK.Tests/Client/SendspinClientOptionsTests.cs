using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

public class SendspinClientOptionsTests
{
    private sealed class StubSession : INoiseSessionInfo
    {
        public string? ServerId => "GFsV9tLaSQm9HcFWpKsgYQOr7wFTvNUtkmFwuVz3zoo";
        public NoisePsk? MatchedPsk => new(NoiseConstants.SentinelPsk.ToArray(), PskCategory.Sentinel);
        public ReadOnlyMemory<byte>? HandshakeHash => new byte[32];
    }

    [Fact]
    public void OptionsConstructor_BuildsAClientOverTheGivenSession()
    {
        var connection = new FakeSendspinConnection();

        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection,
            new StubSession(),
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                Capabilities = new ClientCapabilities { ClientName = "opts-test" },
            });

        // ServerId stays null until server/init lands; construction succeeding over an
        // explicit session is the behavior under test.
        Assert.Null(client.ServerId);
    }

    [Fact]
    public void Options_DefaultsToChaChaPolySuite()
    {
        var options = new SendspinClientOptions { Identity = SendspinIdentity.Generate() };
        Assert.Equal(NoiseCipherSuite.ChaChaPoly, options.Suite);
    }
}
