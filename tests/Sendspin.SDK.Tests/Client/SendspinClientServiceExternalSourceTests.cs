using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the external_source enter/exit API: state-only client/state notifications, the
/// IsExternalSource flag, and rollback when the server notification fails.
/// </summary>
public class SendspinClientServiceExternalSourceTests
{
    private static SendspinClientService NewClient(FakeSendspinConnection connection) =>
        new(NullLogger<SendspinClientService>.Instance, connection);

    private static string? LastState(FakeSendspinConnection connection) =>
        connection.SentMessages.OfType<ClientStateMessage>().Last().Payload.State;

    [Fact]
    public async Task EnterExternalSource_SendsStateAndSetsFlag()
    {
        var connection = new FakeSendspinConnection();
        using var client = NewClient(connection);

        await client.EnterExternalSourceAsync();

        Assert.Equal("external_source", LastState(connection));
        Assert.True(client.IsExternalSource);
    }

    [Fact]
    public async Task ExitExternalSource_ReportsSynchronizedAndClearsFlag()
    {
        var connection = new FakeSendspinConnection();
        using var client = NewClient(connection);

        await client.EnterExternalSourceAsync();
        await client.ExitExternalSourceAsync();

        Assert.Equal("synchronized", LastState(connection));
        Assert.False(client.IsExternalSource);
    }

    [Fact]
    public async Task EnterExternalSource_RollsBackWhenNotificationFails()
    {
        var connection = new FakeSendspinConnection { ThrowOnSend = true };
        using var client = NewClient(connection);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EnterExternalSourceAsync());

        // Notification failed, so the local state must not have flipped.
        Assert.False(client.IsExternalSource);
    }
}
