using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// The default client/goodbye reason is "restart": an unexplained graceful disconnect (e.g. closing
/// a lid) should be reconnectable, matching the server's no-goodbye assumption. Callers that mean
/// "do not reconnect" pass an explicit reason ("user_request"/"shutdown"/"another_server").
/// </summary>
public class ClientGoodbyeDefaultTests
{
    [Fact]
    public void Create_DefaultsToRestart()
    {
        Assert.Equal("restart", ClientGoodbyeMessage.Create().Payload.Reason);
    }

    [Fact]
    public void Create_HonorsExplicitReason()
    {
        Assert.Equal("user_request", ClientGoodbyeMessage.Create("user_request").Payload.Reason);
    }
}
