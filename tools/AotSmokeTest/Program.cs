// Exercises the paths that would break under NativeAOT, through the SAME public surface a
// consumer uses. The trim/AOT analyzers only see this repo's IL and say nothing about whether
// a dependency survives AOT — Noise.NET sits on the mandatory transport, so until something
// actually published and ran, "PublishAot compatible" was an untested claim (#89).
//
// Not a correctness test; the suite covers that. This exists to be *published* with
// PublishAot, so a reflection or dynamic-code path that only fails after trimming surfaces
// here instead of in a consumer's app.

using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

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

// 3. Cipher-suite availability probing (the BCL AEADs).
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

Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures})");
return failures == 0 ? 0 : 1;
