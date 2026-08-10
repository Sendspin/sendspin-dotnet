using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Discovery;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// SendspinHostService rebuilds SendspinClientOptions per connection by hand-copying fields.
/// An option left out of that mirror never reaches any connection, and the failure is silent:
/// gating degrades to "never gated", a timeout silently reverts to its default, and
/// single-connection tests still pass. Three options have already been lost this way, so this
/// pins the mirror structurally — by reflecting over every property — rather than by listing
/// the ones someone remembered.
/// </summary>
public class PairingWindowOptionsTests
{
    /// <summary>
    /// ClockSynchronizer is the one property BuildClientOptions does not mirror: it is the
    /// reason the rebuild exists at all. Left null on the fixture — a configured synchronizer
    /// makes BuildClientOptions hand back the stored options wholesale, and the hand-copied
    /// mirror this file is about would never run.
    /// </summary>
    private const string SubstitutedProperty = nameof(SendspinClientOptions.ClockSynchronizer);

    [Fact]
    public async Task BuildClientOptions_CopiesEveryOptionToThePerConnectionOptions()
    {
        var hostOptions = FullyPopulatedOptions();
        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            hostOptions,
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await using (host)
        {
            var built = host.BuildClientOptions();

            // The rebuilt instance, not the stored one: otherwise every assertion below would
            // hold trivially and the mirror would be untested.
            Assert.NotSame(hostOptions, built);

            foreach (var property in MirroredProperties())
            {
                Assert.Equal(property.GetValue(hostOptions), property.GetValue(built));
            }

            // Not mirrored: substituted with a fresh per-connection synchronizer.
            Assert.NotNull(built.ClockSynchronizer);
        }
    }

    [Fact]
    public void EveryMirroredOption_IsGivenANonDefaultValue_ByTheFixture()
    {
        // Guards the test above from rotting: a property added to SendspinClientOptions and
        // not added to FullyPopulatedOptions would otherwise be "mirrored" trivially, both
        // sides holding the default, and the mirror check would pass while the option was
        // still being dropped on the floor.
        var populated = FullyPopulatedOptions();
        var bare = new SendspinClientOptions { Identity = SendspinIdentity.Generate() };

        foreach (var property in MirroredProperties())
        {
            Assert.NotEqual(property.GetValue(bare), property.GetValue(populated));
        }

        Assert.Null(populated.ClockSynchronizer);
    }

    [Fact]
    public async Task PairingWindow_SetOnHostOptions_ReachesPerConnectionOptions()
    {
        var window = new PairingWindow();
        var host = new SendspinHostService(
            NullLoggerFactory.Instance,
            new SendspinClientOptions
            {
                Identity = SendspinIdentity.Generate(),
                PairingWindow = window,
            },
            listenerOptions: new ListenerOptions { Port = 0 },
            advertiserOptions: new AdvertiserOptions { Enabled = false });

        await using (host)
        {
            Assert.Same(window, host.BuildClientOptions().PairingWindow);
        }
    }

    private static IEnumerable<PropertyInfo> MirroredProperties() =>
        typeof(SendspinClientOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != SubstitutedProperty);

    /// <summary>
    /// Host options with every mirrored property set to a value distinguishable from the
    /// default, so "the mirror copied it" and "both sides happen to hold the default" cannot
    /// be confused. ClockSynchronizer is deliberately left null — see
    /// <see cref="SubstitutedProperty"/>.
    /// </summary>
    private static SendspinClientOptions FullyPopulatedOptions()
    {
        var identity = SendspinIdentity.Generate();
        var defaultSuite = new SendspinClientOptions { Identity = identity }.Suite;

        return new SendspinClientOptions
        {
            Identity = identity,
            PairingRecordStore = new InMemoryPairingRecordStore(),
            Capabilities = new ClientCapabilities(),
            Suite = defaultSuite == NoiseCipherSuite.ChaChaPoly
                ? NoiseCipherSuite.AesGcm
                : NoiseCipherSuite.ChaChaPoly,
            AudioPipeline = new FakeAudioPipeline(),
            StaticDelayStore = new StubStaticDelayStore(),
            PinLockoutStore = new InMemoryPinLockoutStore(),
            PresentPinAsync = (_, _) => ValueTask.CompletedTask,
            CaptureDevice = new FakeCaptureDevice(),
            SourceEncoderFactory = new StubSourceEncoderFactory(),
            PairingWindow = new PairingWindow(),
            PairingAttemptTimeout = TimeSpan.FromSeconds(7),
        };
    }

    private sealed class StubStaticDelayStore : IStaticDelayStore
    {
        public double? Load() => null;

        public void Save(double staticDelayMs)
        {
        }
    }

    private sealed class StubSourceEncoderFactory : ISourceAudioEncoderFactory
    {
        public ISourceAudioEncoder Create(string codec, Sendspin.SDK.Models.AudioFormat format) =>
            throw new NotSupportedException("Never invoked: this stub only has to be a distinct instance.");
    }
}
