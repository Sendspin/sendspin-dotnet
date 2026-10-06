// Live interop client: runs the .NET SDK as an encrypted Sendspin host that the
// aiosendspin reference server dials into. Drives one scenario, prints JSON result lines,
// and exits non-zero on failure.
//
// Usage: InteropClient <scenario> <port> [secret]
//   unpaired    connect for playback over unpaired access
//   pairing     full Pairing PSK round-trip; secret = pairing PSK as hex
//   static-pin  full static-pairing code round-trip; secret = the 8-digit pairing code
//   dynamic-pin full dynamic-pairing code round-trip; the code is derived per attempt and
//               printed as a pairing_code event for the harness to relay
//   source      stream captured PCM through the source@v1 role
//   player      receive a PCM stream through the player@v1 role and decode it
using System.Text.Json;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Protocol;

string scenario = args.Length > 0 ? args[0] : "unpaired";
int port = args.Length > 1 ? int.Parse(args[1]) : 8930;
string? secret = args.Length > 2 ? args[2] : null;
// 'source' and 'player' pair first: the source role only runs at 'user' trust, and 'player'
// follows it so both streaming scenarios reach their role the same way.
bool pairs = scenario is "pairing" or "static-pin" or "dynamic-pin" or "source" or "player";

// Quiet by default so the JSON result lines stay readable; set INTEROP_LOG=Debug when a
// scenario fails and you need the SDK's own account of what it did.
var logLevel = Enum.TryParse(Environment.GetEnvironmentVariable("INTEROP_LOG"), out LogLevel parsed)
    ? parsed
    : LogLevel.Warning;
ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(logLevel).AddSimpleConsole(o => o.SingleLine = true));

var identity = SendspinIdentity.Generate();
var records = new InMemoryPairingRecordStore();
if (scenario is "pairing" or "source" or "player")
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

// aiosendspin 9.1.1 still requires the retired artwork@v1_support object whenever
// artwork@v1 is advertised. Artwork configuration is now state-based in the SDK, so
// leave this role out of the interop harness until the reference server accepts that wire shape.
caps.Roles.Remove("artwork@v1");

if (scenario == "static-pin")
{
    caps.PairingCodeMethods.Add("static_pairing_code");
    caps.StaticPairingCode = secret;
}

if (scenario == "dynamic-pin")
{
    caps.PairingCodeMethods.Add("dynamic_pairing_code");
}

if (scenario == "source")
{
    caps.Roles.Add("source@v1");
    caps.SourceRoleSupport = new SourceRoleSupport();
}

var pipeline = new CountingPipeline();
if (scenario == "player")
{
    // PCM only, so the server passes its samples through unre-encoded and the decoded count
    // can be compared exactly with what it sent.
    caps.AudioFormats = [new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 16 }];
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
        PairingCodeLockoutStore = scenario is "static-pin" or "dynamic-pin"
            ? new FilePairingCodeLockoutStore(Path.Combine(Path.GetTempPath(), $"interop-lockout-{Guid.NewGuid():N}.json"))
            : null,
        // Stand in for the device's display: the harness reads the code from this line and
        // types it into the server, as an operator would.
        PresentPairingCodeAsync = scenario == "dynamic-pin"
            ? (presentation, _) =>
            {
                Emit(new { @event = "pairing_code", code = presentation.PairingCode, format = presentation.Format });
                return ValueTask.CompletedTask;
            }
            : null,
        AudioPipeline = scenario == "player" ? pipeline : null,
        CaptureDevice = scenario == "source" ? new ToneCaptureDevice() : null,
        SourceEncoderFactory = scenario == "source" ? new PcmEncoderFactory() : null,
    },
    listenerOptions: new ListenerOptions { Port = port },
    advertiserOptions: new AdvertiserOptions { Enabled = false });

var connected = new TaskCompletionSource<ConnectedServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
var paired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
host.ServerConnected += (_, info) => connected.TrySetResult(info);
host.PairingCompleted += (_, serverId) => paired.TrySetResult(serverId);

// Taken from the message itself rather than from the pipeline being stopped, which the SDK
// also does when it tears a client down.
var streamEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
host.StreamEndReceived += (_, _) => streamEnded.TrySetResult();

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

    if (scenario == "player")
    {
        // The stream only starts once the server has seen our initial client/state, which
        // waits on clock sync, so this allows for that as well as the audio itself.
        await streamEnded.Task.WaitAsync(TimeSpan.FromSeconds(120));
        Emit(new
        {
            @event = "player_audio_decoded",
            chunks = pipeline.Chunks,
            samples = pipeline.Samples,
            max_send_ahead_us = pipeline.MaxSendAhead,
        });

        // send_ahead is the lead the server transmitted with, so a stream sent ahead of its
        // play time has to show one; zero throughout means the header was not read.
        if (pipeline.Chunks == 0 || pipeline.MaxSendAhead == 0)
        {
            exitCode = 1;
        }
    }

    if (exitCode == 0)
    {
        Emit(new { @event = "success", scenario });
    }

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

/// <summary>
/// Stands in for the playback chain: decodes each chunk with the SDK's own decoder and counts
/// what comes out, with no buffer or output device behind it. Enough to prove
/// <c>stream/start</c>, the binary chunk header and <c>stream/end</c> against a real server
/// without CI needing an audio device.
/// </summary>
internal sealed class CountingPipeline : IAudioPipeline
{
    private readonly AudioDecoderFactory _decoders = new();
    private IAudioDecoder? _decoder;
    private float[] _samples = [];

    public event EventHandler<AudioPipelineState>? StateChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<AudioPipelineError>? ErrorOccurred
    {
        add { }
        remove { }
    }

    public event EventHandler<int>? OutputLatencyChanged
    {
        add { }
        remove { }
    }

    public int Chunks { get; private set; }

    public long Samples { get; private set; }

    public uint MaxSendAhead { get; private set; }

    public AudioPipelineState State => _decoder is null ? AudioPipelineState.Idle : AudioPipelineState.Playing;

    public bool IsReady => _decoder is not null;

    public AudioBufferStats? BufferStats => null;

    public AudioFormat? CurrentFormat => _decoder?.Format;

    public int DetectedOutputLatencyMs => 0;

    public Task<AudioPipelineStartOutcome> StartAsync(
        AudioFormat format, long? targetTimestamp = null, CancellationToken cancellationToken = default)
    {
        _decoder = _decoders.Create(format);
        _samples = new float[_decoder.MaxSamplesPerFrame];
        return Task.FromResult(AudioPipelineStartOutcome.Restarted);
    }

    public Task StopAsync()
    {
        _decoder?.Dispose();
        _decoder = null;
        return Task.CompletedTask;
    }

    public void ProcessAudioChunk(AudioChunk chunk)
    {
        Chunks++;
        Samples += _decoder!.Decode(chunk.EncodedData, _samples);
        MaxSendAhead = Math.Max(MaxSendAhead, chunk.SendAhead);
    }

    public void NotifyReconnect()
    {
    }

    public void Clear(long? newTargetTimestamp = null)
    {
    }

    public void ReanchorTiming()
    {
    }

    public void SetVolume(int volume)
    {
    }

    public void SetMuted(bool muted)
    {
    }

    public void SetMinBufferMilliseconds(int minBufferMs)
    {
    }

    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Hands out the SDK's passthrough PCM encoder — the capture format is already the wire format.</summary>
internal sealed class PcmEncoderFactory : ISourceAudioEncoderFactory
{
    public ISourceAudioEncoder Create(string codec, AudioFormat format) => new PcmSourceEncoder();
}
