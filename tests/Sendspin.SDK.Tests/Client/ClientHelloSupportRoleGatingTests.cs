using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A support object belongs in <c>client/hello</c> exactly when its role version appears in
/// <c>supported_roles</c>. aiosendspin flags an unlisted one as a client deviation and rejects
/// the client outright when run with <c>allow_noncompliant_clients=False</c>.
/// </summary>
/// <remarks>
/// Each case pairs the negative with a positive control on the same capability. Asserting only
/// that an object is absent would still pass if the object had simply stopped being built, which
/// is a different bug wearing the same green.
/// </remarks>
public class ClientHelloSupportRoleGatingTests
{
    private static ClientHelloPayload HelloFor(params string[] roles)
    {
        var (client, connection, _) = TestClient.Create(configure: options =>
            options with
            {
                Capabilities = new ClientCapabilities
                {
                    Roles = roles.ToList(),
                    VisualizerRoleSupport = new VisualizerRoleSupport
                    {
                        BufferCapacity = 65536,
                        Types = new List<string> { VisualizerTypes.Beat },
                    },
                },
            });
        using var _c = client;

        TestClient.CompleteHandshake(connection, roles);

        return connection.SentMessages.OfType<ClientHelloMessage>().Single().Payload;
    }

    [Fact]
    public void PlayerSupport_OmittedWhenPlayerRoleIsNotAdvertised()
    {
        Assert.Null(HelloFor("source@v1").PlayerV1Support);
        Assert.NotNull(HelloFor("source@v1", "player@v1").PlayerV1Support);
    }

    [Fact]
    public void ArtworkSupport_NoLongerExists()
    {
        // Spec PR #195 deleted artwork@v1_support outright: the channel declaration is dynamic
        // configuration and lives in the client/state artwork object instead.
        var json = Sendspin.SDK.Protocol.MessageSerializer.Serialize(
            new ClientHelloMessage { Payload = HelloFor("player@v1", "artwork@v1") });

        Assert.DoesNotContain("artwork@v1_support", json);
    }

    [Fact]
    public void VisualizerSupport_OmittedWhenVisualizerRoleIsNotAdvertised()
    {
        // Configured but unlisted: previously the configuration alone put it on the wire.
        Assert.Null(HelloFor("player@v1").VisualizerV1Support);
        Assert.NotNull(HelloFor("player@v1", "visualizer@v1").VisualizerV1Support);
    }

    [Fact]
    public void SourceSupport_OmittedWhenSourceRoleIsNotAdvertised()
    {
        Assert.Null(HelloFor("player@v1").SourceV1Support);
        Assert.NotNull(HelloFor("player@v1", "source@v1").SourceV1Support);
    }

    [Theory]
    [InlineData("player@v1")]
    [InlineData("source@v1")]
    [InlineData("artwork@v1")]
    [InlineData("visualizer@v1")]
    [InlineData("controller@v1")]
    [InlineData("player@v1", "artwork@v1", "visualizer@v1", "source@v1")]
    public void NoSupportObjectAppearsForAnUnlistedRole(params string[] roles)
    {
        // The whole rule in one assertion, so a support object added later without gating fails
        // here even if nobody adds a case above. Driven from single-role sets rather than the
        // shipped defaults: the defaults list player and artwork, so they would satisfy this
        // whether or not the gate exists.
        var hello = HelloFor(roles);

        Assert.All(
            new (string Role, object? Support)[]
            {
                ("player@v1", hello.PlayerV1Support),
                ("visualizer@v1", hello.VisualizerV1Support),
                ("source@v1", hello.SourceV1Support),
            },
            pair =>
            {
                if (pair.Support is not null)
                    Assert.Contains(pair.Role, hello.SupportedRoles);
            });
    }
}
