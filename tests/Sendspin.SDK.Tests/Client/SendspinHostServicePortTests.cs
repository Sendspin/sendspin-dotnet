using Makaretu.Dns;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;

namespace Sendspin.SDK.Tests.Client;

[Collection("RealSockets")]
public class SendspinHostServicePortTests
{
    [Fact]
    public async Task ListeningPort_ReflectsOsAssignedPort_WhenBindingZero()
    {
        await using var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions { Identity = SendspinIdentity.Generate() },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await host.StartAsync();
        try
        {
            Assert.True(host.ListeningPort > 0, $"expected an OS-assigned port, got {host.ListeningPort}");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>
    /// #342: the SRV record must carry the port the listener bound, and the TXT record the path
    /// it serves — connection.md, "Port: The port the Sendspin client is listening on". The
    /// advertiser options here disagree with the listener on both, as the default-built ones
    /// did on the port whenever it was 0.
    /// </summary>
    /// <remarks>
    /// Listens on the real multicast transport without querying, the way
    /// <see cref="Sendspin.SDK.Tests.Discovery.MdnsAnnouncementTests"/> does; the GUID instance
    /// name keeps other Sendspin traffic on the LAN out of it.
    /// </remarks>
    [Fact]
    public async Task Advertises_TheBoundPortAndListenerPath_WhenBindingZero()
    {
        var instanceName = $"port-test-{Guid.NewGuid():N}";
        var announced = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var mdns = new MulticastService();
        mdns.AnswerReceived += (s, e) =>
        {
            if (e.Message.Answers.Concat(e.Message.AdditionalRecords)
                .Any(r => r is SRVRecord && r.CanonicalName.Contains(instanceName, StringComparison.OrdinalIgnoreCase)))
            {
                announced.TrySetResult(e.Message);
            }
        };
        mdns.Start();

        await using var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions { Identity = SendspinIdentity.Generate() },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { InstanceName = instanceName, Port = 0, Path = "/elsewhere" });

        await host.StartAsync();
        try
        {
            var message = await announced.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var records = message.Answers.Concat(message.AdditionalRecords).ToList();

            Assert.Equal(host.ListeningPort, records.OfType<SRVRecord>().Single().Port);
            Assert.Contains("path=/sendspin", records.OfType<TXTRecord>().Single().Strings);
        }
        finally
        {
            await host.StopAsync();
            mdns.Stop();
        }
    }
}
