using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
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

    [Fact]
    public void CreateForDial_WiresOneNoiseFramingAsBothFramingAndSession()
    {
        var options = new SendspinClientOptions { Identity = SendspinIdentity.Generate() };

        using var client = SendspinClientService.CreateForDial(
            NullLoggerFactory.Instance,
            options);

        // The invariant CreateForDial exists to guarantee: the connection's wire framing
        // and the client's Noise session must be the exact same object, not merely two
        // framings that happen to agree. Reference equality is the only assertion that
        // can catch a miswiring here (value-based checks on ServerId etc. can pass even
        // when the two are unrelated instances).
        var connection = Assert.IsType<SendspinConnection>(client.ClientConnection);
        Assert.Same(connection.Framing, client.Session);
    }
}
