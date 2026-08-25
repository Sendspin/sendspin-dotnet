// Exercises the paths that would break under NativeAOT, through the SAME public surface a
// consumer uses. The trim/AOT analyzers only see this repo's IL and say nothing about whether
// a dependency survives AOT — Noise.NET sits on the mandatory transport, so until something
// actually published and ran, "PublishAot compatible" was an untested claim (#89).
//
// Not a correctness test; the suite covers that. This exists to be *published* with
// PublishAot, so a reflection or dynamic-code path that only fails after trimming surfaces
// here instead of in a consumer's app.

using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Models;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;
using Sendspin.SDK.Synchronization;

int failures = 0;

void Check(string what, Func<bool> probe)
{
    try
    {
        bool ok = probe();
        Console.WriteLine(ok ? $"  ok    {what}" : $"  FAIL  {what}");
        if (!ok) failures++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL  {what}: {ex.GetType().Name}: {ex.Message}");
        failures++;
    }
}

Console.WriteLine("Sendspin.SDK NativeAOT smoke test");

// 1. Source-generated protocol serialization.
Check("serialize a protocol message", () =>
    MessageSerializer.Serialize(ClientGoodbyeMessage.Create(GoodbyeReasons.Shutdown))
        .Contains("client/goodbye", StringComparison.Ordinal));

// 2. Identity generation — Curve25519 key material.
SendspinIdentity? identity = null;
Check("generate an identity", () =>
{
    identity = SendspinIdentity.Generate();
    return identity.PeerId.Length > 0;
});

// 3. Cipher-suite availability probing. SelectDefault runs a real KKpsk2 message-1 write, so
//    this exercises Curve25519 and the suite's AEAD through libsodium rather than querying the
//    BCL — which is what makes the linux-arm64 leg of this job meaningful (#144).
Check("select a supported cipher suite", () =>
    NoiseCipherSuiteExtensions.SelectDefault().IsSupported());

// 4. The real consumer entry point. CreateForDial builds the Noise framing internally, so
//    this is what drags Noise.NET in — the dependency the analyzers cannot vouch for.
Check("build a client via CreateForDial (pulls in Noise.NET)", () =>
{
    // NullLoggerFactory keeps this to Logging.Abstractions, which the SDK already pulls in.
    using var client = SendspinClientService.CreateForDial(
        NullLoggerFactory.Instance,
        new SendspinClientOptions { Identity = identity! });
    return client is not null;
});

// 5. The pairing store's source-generated file format, written and read back.
Check("pairing store round-trip", () =>
{
    string path = Path.Combine(Path.GetTempPath(), $"aot-{Guid.NewGuid():N}.json");
    try
    {
        new FilePairingRecordStore(path).Upsert(new PairingRecord(
            Enumerable.Repeat((byte)7, 32).ToArray(), PskCategory.LongTerm, ServerId: null));
        return new FilePairingRecordStore(path).List().Count == 1;
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
});

// 6. The smooth correction chain, which carries the vendored WDL resampler. It is pure managed
//    math, so the analyzers ought to be enough — but that is what was said about the transport
//    before #89. Publishing and running it costs nothing and turns the claim into an observation.
Check("pull audio through the smooth correction chain", () =>
{
    var format = new AudioFormat { Codec = "pcm", SampleRate = 48_000, Channels = 2 };
    using var buffer = new TimedAudioBuffer(format, new KalmanClockSynchronizer());
    buffer.Write(new float[48_000 * 2 / 10], serverTimestamp: 0); // 100 ms

    using var source = new SyncCorrectedSampleSource(buffer, () => 0);
    var block = new float[960]; // one 10 ms callback at 48 kHz stereo
    for (int i = 0; i < 5; i++)
    {
        source.Read(block, 0, block.Length);
    }

    return source.PlaybackRate >= buffer.SyncOptions.MinRate
        && source.PlaybackRate <= buffer.SyncOptions.MaxRate;
});

Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures})");
return failures == 0 ? 0 : 1;
