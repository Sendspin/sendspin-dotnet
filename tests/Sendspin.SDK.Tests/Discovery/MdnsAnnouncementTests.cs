using Makaretu.Dns;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Discovery;

namespace Sendspin.SDK.Tests.Discovery;

/// <summary>
/// The advertiser must announce its presence, not merely answer queries. The spec's discovery
/// section opens with "Clients announce their presence via mDNS", and RFC 6762 §8.3 requires a
/// responder registering new records to multicast them unsolicited at least twice — because a
/// browser that has finished its startup queries (python-zeroconf switches to refresh-only
/// scheduling after four) will never ask again, so a client that only answers queries is never
/// discovered by a server that started before it.
/// </summary>
/// <remarks>
/// These tests listen on the real multicast transport and never send a query, so anything they
/// receive is unsolicited. The instance name is a fresh GUID per test: real Sendspin traffic on
/// the developer's LAN cannot produce a record bearing it.
/// </remarks>
public sealed class MdnsAnnouncementTests : IAsyncLifetime
{
    private const int AdvertisedPort = 18928;

    private static readonly TimeSpan AnnounceTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan BurstQuietPeriod = TimeSpan.FromSeconds(2);

    private readonly string _instanceName = $"announce-test-{Guid.NewGuid():N}";
    private readonly MulticastService _listener = new MulticastService();
    private readonly List<Message> _matchingAnswers = new List<Message>();
    private readonly SemaphoreSlim _answerReceived = new SemaphoreSlim(0);
    private MdnsServiceAdvertiser? _advertiser;

    public Task InitializeAsync()
    {
        // The RFC 6762 announcement repeat is byte-identical to the first send, one second
        // later — exactly the shape MulticastService's duplicate filter exists to drop, and
        // whether it lands inside the filter's window is a timing coin flip. This listener is
        // measurement equipment: it must see every packet the advertiser puts on the wire.
        _listener.IgnoreDuplicateMessages = false;
        _listener.AnswerReceived += (s, e) =>
        {
            if (MentionsInstance(e.Message))
            {
                lock (_matchingAnswers)
                {
                    _matchingAnswers.Add(e.Message);
                }

                _answerReceived.Release();
            }
        };
        _listener.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_advertiser is not null)
        {
            await _advertiser.DisposeAsync();
        }

        _listener.Stop();
        _listener.Dispose();
    }

    [Fact]
    public async Task StartAsync_AnnouncesPresenceWithoutBeingQueried()
    {
        await StartAdvertiserAsync();

        var received = await _answerReceived.WaitAsync(AnnounceTimeout);

        // Without the unsolicited announcement, a server whose browser has finished its
        // startup queries never discovers this client.
        Assert.True(received, "The advertiser never multicast an unsolicited announcement.");

        // The announcement must carry everything a server needs to connect from cache alone:
        // the instance PTR, the SRV with the port, and the TXT with the required path key.
        Message announcement;
        lock (_matchingAnswers)
        {
            announcement = _matchingAnswers[0];
        }

        var records = announcement.Answers.Concat(announcement.AdditionalRecords).ToList();
        Assert.Contains(records, IsInstancePtr);
        Assert.Contains(records, IsAdvertisedSrv);
        Assert.Contains(records, IsPathTxt);
    }

    [Fact]
    public async Task NetworkInterfaceDiscovered_ReannouncesPresence()
    {
        var advertiser = await StartAdvertiserAsync();

        // Drain the start-up burst before triggering, or one of its packets would satisfy the
        // assertion below without any re-announce happening. The burst is two sends one second
        // apart, each possibly delivered once per interface on a multi-homed machine, so
        // counting packets is wrong on both axes; instead the burst is over once nothing
        // matching has arrived for comfortably longer than its own internal gap.
        Assert.True(await _answerReceived.WaitAsync(AnnounceTimeout), "no initial announcement");
        while (await _answerReceived.WaitAsync(BurstQuietPeriod))
        {
        }

        // A network interface appearing after start — Wi-Fi associating late, resume from
        // sleep — must re-announce, because the announcement sent before that interface
        // existed never reached it.
        advertiser.HandleNetworkInterfaceDiscovered();

        Assert.True(await _answerReceived.WaitAsync(AnnounceTimeout), "a late network interface did not trigger a re-announcement");
    }

    private static bool IsAdvertisedSrv(ResourceRecord record) =>
        record is SRVRecord srv && srv.Port == AdvertisedPort;

    private static bool IsPathTxt(ResourceRecord record) =>
        record is TXTRecord txt && txt.Strings.Any(s => s.StartsWith("path=", StringComparison.Ordinal));

    private bool MentionsInstance(Message message) =>
        message.Answers.Concat(message.AdditionalRecords).Any(ContainsInstanceName);

    private bool ContainsInstanceName(ResourceRecord record) =>
        record.CanonicalName.Contains(_instanceName, StringComparison.OrdinalIgnoreCase)
        || (record is PTRRecord ptr && ptr.DomainName.ToString().Contains(_instanceName, StringComparison.OrdinalIgnoreCase));

    private bool IsInstancePtr(ResourceRecord record) =>
        record is PTRRecord ptr && ptr.DomainName.ToString().Contains(_instanceName, StringComparison.OrdinalIgnoreCase);

    private async Task<MdnsServiceAdvertiser> StartAdvertiserAsync()
    {
        var options = new AdvertiserOptions
        {
            InstanceName = _instanceName,
            PlayerName = "Announce Test",
            Port = AdvertisedPort,
        };
        var advertiser = new MdnsServiceAdvertiser(NullLogger<MdnsServiceAdvertiser>.Instance, options);
        _advertiser = advertiser;
        await advertiser.StartAsync();
        return advertiser;
    }
}
