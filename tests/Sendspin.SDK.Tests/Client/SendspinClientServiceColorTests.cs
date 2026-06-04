using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Coverage for the color role: server/state color updates merged onto GroupState, the ColorChanged
/// event, the spec's absent/null/value delta semantics, and color@v1 role advertisement.
/// </summary>
public class SendspinClientServiceColorTests
{
    private const string ServerHelloJson = """
        { "type": "server/hello", "payload": { "server_id": "s", "version": 1, "active_roles": ["color@v1"] } }
        """;

    [Fact]
    public void ServerStateColor_PopulatesGroupAndRaisesColorChanged()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        ColorPalette? raised = null;
        client.ColorChanged += (_, p) => raised = p;

        connection.RaiseTextMessageReceived("""
            {
                "type": "server/state",
                "payload": { "color": { "timestamp": 42, "background_dark": [10, 20, 30], "primary": [255, 0, 0] } }
            }
            """);

        Assert.NotNull(client.CurrentGroup);
        var colors = client.CurrentGroup.Colors;
        Assert.Equal(42, colors.Timestamp);
        Assert.Equal(new RgbColor(10, 20, 30), colors.BackgroundDark);
        Assert.Equal(new RgbColor(255, 0, 0), colors.Primary);

        Assert.NotNull(raised);
        Assert.Same(colors, raised);
    }

    [Fact]
    public void ServerStateColor_MergesDeltas_AbsentKeepsNullClearsValueUpdates()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        // First update: set primary and accent.
        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "color": { "primary": [1, 1, 1], "accent": [2, 2, 2] } } }
            """);

        // Second update: clear primary (null), update on_dark (value), leave accent absent (keep).
        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "color": { "primary": null, "on_dark": [3, 3, 3] } } }
            """);

        var colors = client.CurrentGroup!.Colors;
        Assert.Null(colors.Primary);                          // present-null -> cleared
        Assert.Equal(new RgbColor(2, 2, 2), colors.Accent);   // absent -> kept
        Assert.Equal(new RgbColor(3, 3, 3), colors.OnDark);   // present-value -> updated
    }

    [Fact]
    public void MalformedColor_DoesNotDropSiblingControllerUpdate()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        // One bad color channel must not abort the whole server/state and lose the volume update.
        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "controller": { "volume": 55 }, "color": { "primary": [1, 2] } } }
            """);

        Assert.NotNull(client.CurrentGroup);
        Assert.Equal(55, client.CurrentGroup.Volume);     // sibling applied
        Assert.Null(client.CurrentGroup.Colors.Primary);  // malformed color left unset
    }

    [Fact]
    public void ColorTimestamp_KeptWhenLaterUpdateOmitsIt()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "color": { "timestamp": 100, "primary": [1, 1, 1] } } }
            """);
        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "color": { "accent": [2, 2, 2] } } }
            """);

        Assert.Equal(100, client.CurrentGroup!.Colors.Timestamp); // retained when omitted
    }

    [Fact]
    public void ServerState_WithoutColor_DoesNotRaiseColorChanged()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        var fired = false;
        client.ColorChanged += (_, _) => fired = true;

        connection.RaiseTextMessageReceived("""
            { "type": "server/state", "payload": { "controller": { "volume": 50 } } }
            """);

        Assert.False(fired);
    }

    [Fact]
    public async Task ClientHello_AdvertisesColorRole()
    {
        var connection = new FakeSendspinConnection();
        using var client = new SendspinClientService(
            NullLogger<SendspinClientService>.Instance,
            connection);

        var connectTask = client.ConnectAsync(new Uri("ws://test"));
        connection.RaiseTextMessageReceived(ServerHelloJson);
        await connectTask;

        var hello = connection.SentMessages.OfType<ClientHelloMessage>().Single();
        Assert.Contains("color@v1", hello.Payload.SupportedRoles);
    }
}
