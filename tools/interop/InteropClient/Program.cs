// Live interop client: runs the .NET SDK as an encrypted Sendspin host that the
// aiosendspin reference server dials into. Drives one scenario, prints JSON result lines,
// and exits non-zero on failure.
//
// Usage: InteropClient <scenario> <port> [secret]
//   unpaired    connect for playback over unpaired access
//   pairing     full Pairing PSK round-trip; secret = pairing PSK as hex
//   static-pin  full static-pairing code round-trip; secret = the 8-digit pairing code
//   source      stream captured PCM through the source@v1 role
using System.Text.Json;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Models;
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
// 'source' pairs first: the source role only runs at 'user' trust.
bool pairs = scenario is "pairing" or "static-pin" or "source";

// Quiet by default so the JSON result lines stay readable; set INTEROP_LOG=Debug when a
// scenario fails and you need the SDK's own account of what it did.
var logLevel = Enum.TryParse(Environment.GetEnvironmentVariable("INTEROP_LOG"), out LogLevel parsed)
    ? parsed
    : LogLevel.Warning;
ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(logLevel).AddSimpleConsole(o => o.SingleLine = true));

var identity = SendspinIdentity.Generate();
var records = new InMemoryPairingRecordStore();
if (scenario is "pairing" or "source")
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
    caps.PairingCodeMethods.Add("static_pairing_code");
    caps.StaticPairingCode = secret;
}

if (scenario == "source")
{
    caps.Roles.Add("source@v1");
    caps.SourceRoleSupport = new SourceRoleSupport();
}

// Every static_pairing_code attempt is gesture-gated, so the window is what lets it proceed at all.
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
        PairingCodeLockoutStore = scenario == "static-pin"
            ? new FilePairingCodeLockoutStore(Path.Combine(Path.GetTempPath(), $"interop-lockout-{Guid.NewGuid():N}.json"))
            : null,
        CaptureDevice = scenario == "source" ? new ToneCaptureDevice() : null,
        SourceEncoderFactory = scenario == "source" ? new PcmEncoderFactory() : null,
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
    if (scenario == "source")
    {
        // The source role streams in server time, so nothing it sends counts until the
        // clock converges. Report what the filter actually did, so a stalled scenario says
        // why instead of just producing no audio.
        for (int i = 0; i < 90; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            var status = host.ConnectedServers.Count > 0
                ? host.ConnectedServers[0].ClockSyncStatus
                : null;
            if (status is not null && i % 4 == 0)
            {
                Emit(new
                {
                    @event = "clock_sync",
                    converged = status.IsConverged,
                    measurements = status.MeasurementCount,
                    offset_uncertainty_us = Math.Round(status.OffsetUncertaintyMicroseconds, 1),
                    offset_us = status.OffsetMicroseconds,
                    forgetting_triggers = status.AdaptiveForgettingTriggerCount,
                });
            }
        }
    }
    else
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
    }
}
catch (TimeoutException)
{
    Emit(new { @event = "timeout", scenario });
    exitCode = 2;
}

await host.StopAsync();
return exitCode;

static void Emit(object o) => Console.WriteLine(JsonSerializer.Serialize(o));

/// <summary>
/// Stands in for a line-in device: emits 20 ms buffers of a 440 Hz tone at 48 kHz stereo
/// s16. A tone rather than silence so a decode failure on the far side shows up as wrong
/// samples rather than as plausible-looking quiet.
/// </summary>
internal sealed class ToneCaptureDevice : IAudioCaptureDevice
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int SamplesPerBuffer = SampleRate / 50;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _phase;

    public AudioFormat Format { get; } = new()
    {
        Codec = "pcm",
        SampleRate = SampleRate,
        Channels = Channels,
        BitDepth = 16,
    };

    public event EventHandler<CapturedAudio>? AudioCaptured;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => CaptureLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            await _loop!;
        }
        catch (OperationCanceledException)
        {
            // Expected on stop.
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[SamplesPerBuffer * Channels * 2];
        while (!cancellationToken.IsCancellationRequested)
        {
            for (int i = 0; i < SamplesPerBuffer; i++)
            {
                short sample = (short)(Math.Sin(2 * Math.PI * 440 * _phase++ / SampleRate) * 8000);
                for (int ch = 0; ch < Channels; ch++)
                {
                    int offset = ((i * Channels) + ch) * 2;
                    buffer[offset] = (byte)(sample & 0xFF);
                    buffer[offset + 1] = (byte)((sample >> 8) & 0xFF);
                }
            }

            AudioCaptured?.Invoke(
                this,
                new CapturedAudio(buffer.AsMemory(), Environment.TickCount64 * 1000));

            await Task.Delay(20, cancellationToken);
        }
    }
}

/// <summary>Hands out the SDK's passthrough PCM encoder — the capture format is already the wire format.</summary>
internal sealed class PcmEncoderFactory : ISourceAudioEncoderFactory
{
    public ISourceAudioEncoder Create(string codec, AudioFormat format) => new PcmSourceEncoder();
}
