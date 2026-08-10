// Live interop client: runs the .NET SDK as an encrypted Sendspin host that the
// aiosendspin reference server dials into. Drives one scenario, prints JSON result lines,
// and exits non-zero on failure.
//
// Usage: InteropClient <scenario> <port> [secret]
//   unpaired    connect for playback over unpaired access
//   pairing     full Pairing PSK round-trip; secret = pairing PSK as hex
//   static-pin  full static-PIN round-trip; secret = the 8-digit PIN
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;

string scenario = args.Length > 0 ? args[0] : "unpaired";
int port = args.Length > 1 ? int.Parse(args[1]) : 8930;
string? secret = args.Length > 2 ? args[2] : null;
bool pairs = scenario is "pairing" or "static-pin";

ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole(o => o.SingleLine = true));

var identity = SendspinIdentity.Generate();
var records = new InMemoryPairingRecordStore();
if (scenario == "pairing")
{
    // Stage the shared bootstrap secret so our host resolves the server's dial to the
    // Pairing PSK (category Pairing) during the Noise handshake.
    records.Upsert(new PairingRecord(Convert.FromHexString(secret!), PskCategory.Pairing));
}

var caps = new ClientCapabilities
{
    ClientName = "dotnet-interop",
    UnpairedAccessEnabled = scenario == "unpaired",
};

if (scenario == "static-pin")
{
    caps.PinPairingMethods.Add("static_pin");
    caps.StaticPin = secret;
}

// Every static_pin attempt is gesture-gated, so the window is what lets it proceed at all.
var window = new PairingWindow();

await using var host = new SendspinHostService(
    loggerFactory,
    new SendspinClientOptions
    {
        Identity = identity,
        Capabilities = caps,
        PairingRecordStore = records,
        PairingWindow = window,
        // The failure counter has to persist for the method to be offered at all.
        PinLockoutStore = scenario == "static-pin"
            ? new FilePinLockoutStore(Path.Combine(Path.GetTempPath(), $"interop-lockout-{Guid.NewGuid():N}.json"))
            : null,
    },
    listenerOptions: new ListenerOptions { Port = port },
    advertiserOptions: new AdvertiserOptions { Enabled = false });

var connected = new TaskCompletionSource<ConnectedServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
var paired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
host.ServerConnected += (_, info) => connected.TrySetResult(info);
host.PairingCompleted += (_, serverId) => paired.TrySetResult(serverId);

// Stand in for the physical operator gesture: the SDK reports it is withholding
// client/pair-init until a window opens, and we open one. Emitting the event first makes
// the gating observable — if it stopped happening, this line would stop appearing.
host.PairingGestureRequested += (_, e) =>
{
    Emit(new { @event = "gesture_requested", method = e.Method, pairing_index = e.PairingIndex });
    window.Open();
};

await host.StartAsync();
Emit(new { @event = "host_ready", port = host.ListeningPort, client_id = identity.PeerId });

int exitCode = 0;
try
{
    var timeout = TimeSpan.FromSeconds(30);

    if (pairs)
    {
        string serverId = await paired.Task.WaitAsync(timeout);
        // After pairing the server re-handshakes to the new long-term PSK; the record
        // store must now hold a LongTerm record bound to that server.
        bool persisted = records.List().Any(r => r.Category == PskCategory.LongTerm && r.ServerId == serverId);
        Emit(new { @event = "pairing_completed", server_id = serverId, long_term_record_persisted = persisted });
        if (!persisted)
        {
            exitCode = 1;
        }
    }
    else
    {
        var info = await connected.Task.WaitAsync(timeout);
        Emit(new { @event = "connected", server_id = info.ServerId, trust = "none_unpaired" });
    }

    Emit(new { @event = "success", scenario });

    // Stay connected so the reference server can observe the connection/pairing before
    // teardown. The orchestrator terminates this process once the server confirms.
    await Task.Delay(TimeSpan.FromSeconds(20));
}
catch (TimeoutException)
{
    Emit(new { @event = "timeout", scenario });
    exitCode = 2;
}

await host.StopAsync();
return exitCode;

static void Emit(object o) => Console.WriteLine(JsonSerializer.Serialize(o));
