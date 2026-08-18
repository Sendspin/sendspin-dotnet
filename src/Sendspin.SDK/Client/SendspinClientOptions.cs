using Sendspin.SDK.Audio;
using Sendspin.SDK.Audio.Source;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Connection.Noise.Pairing;
using Sendspin.SDK.Synchronization;

namespace Sendspin.SDK.Client;

/// <summary>
/// Everything a Sendspin client needs that is not the connection itself.
/// </summary>
/// <remarks>
/// The identity is required: under the encrypted protocol a client's <c>client_id</c>
/// IS its Curve25519 public key, so there is no such thing as a client without one.
/// This type is the single construction seam for both the dial path
/// (<see cref="SendspinClientService.CreateForDial"/>) and the listen path
/// (<see cref="SendspinHostService"/>).
/// <para>
/// A record, not a class, so per-connection variants are taken with <c>with</c>. Two
/// hand-copied property mirrors previously existed for want of it, and each silently
/// dropped any property added and forgotten (#95).
/// </para>
/// </remarks>
public sealed record SendspinClientOptions
{
    /// <summary>
    /// Persistent Curve25519 identity. <c>client_id</c> is derived from it, and the spec
    /// requires it to survive reboots — persist and reuse the same keypair.
    /// </summary>
    required public SendspinIdentity Identity { get; init; }

    /// <summary>Pairing record store holding the PSKs this client has been paired with.</summary>
    public IPairingRecordStore? PairingRecordStore { get; init; }

    /// <summary>Roles, features and names advertised in <c>client/hello</c>.</summary>
    public ClientCapabilities Capabilities { get; init; } = new();

    /// <summary>
    /// Noise cipher suite announced in <c>client/init</c>. Defaults to whichever suite this
    /// platform can actually perform — ChaCha20-Poly1305 where available, otherwise AES-GCM.
    /// Both are spec-defined and servers support both, so overriding is a preference, not a
    /// compatibility requirement; an override the platform cannot perform fails at connect
    /// with <see cref="PlatformNotSupportedException"/> rather than inside the handshake.
    /// </summary>
    public NoiseCipherSuite Suite { get; init; } = NoiseCipherSuiteExtensions.SelectDefault();

    /// <summary>Clock synchronizer. A <see cref="KalmanClockSynchronizer"/> is created when null.</summary>
    public IClockSynchronizer? ClockSynchronizer { get; init; }

    /// <summary>Audio pipeline for the player role.</summary>
    public IAudioPipeline? AudioPipeline { get; init; }

    /// <summary>Persistence for the player's static delay calibration.</summary>
    public IStaticDelayStore? StaticDelayStore { get; init; }

    /// <summary>
    /// Failure counter persistence for the pairing code methods. <b>Required</b> if
    /// <see cref="ClientCapabilities.PairingCodeMethods"/> is non-empty: without a store the
    /// counter cannot survive a restart, so a method could never escalate to gesture-gating.
    /// Rather than grant unlimited, ungated attempts, the SDK withholds the method — it is
    /// absent from <c>supported_pair_methods</c> in <c>client/hello</c>, reported
    /// <c>enabled: false</c> by <c>management/get-pairing-config</c>, and any activation naming
    /// it is answered with <c>pair/abort</c> <c>method_not_supported</c>.
    /// <see cref="Connection.Noise.Pairing.FilePairingCodeLockoutStore"/> is provided, and takes an
    /// optional <c>ILogger</c> so a corrupt counter file is not discarded silently.
    /// </summary>
    public IPairingCodeLockoutStore? PairingCodeLockoutStore { get; init; }

    /// <summary>
    /// Presents a derived dynamic pairing code to the operator through the app's out-channel (display,
    /// speaker) so it can be entered into the server. Required when <c>"dynamic_pin"</c> is
    /// offered in <see cref="ClientCapabilities.PairingCodeMethods"/>; pairing fails closed
    /// without it. Awaited before the client proceeds, so a slow presenter delays pairing
    /// rather than racing it.
    /// </summary>
    public Func<PairingCodePresentation, CancellationToken, ValueTask>? PresentPairingCodeAsync { get; init; }

    /// <summary>Capture device for the <c>source@v1</c> role.</summary>
    public IAudioCaptureDevice? CaptureDevice { get; init; }

    /// <summary>Encoder factory for the <c>source@v1</c> role.</summary>
    public ISourceAudioEncoderFactory? SourceEncoderFactory { get; init; }

    /// <summary>
    /// The device's pairing window, shared by every connection this application runs.
    /// <b>Required</b> to complete any gesture-gated pairing attempt: static pairing code always, and
    /// dynamic pairing code once the method is escalated or the session's pairing code is shorter than 6 digits.
    /// A null window is treated as permanently closed, so gated attempts stay pending — the
    /// fail-closed direction. Open it from the application's operator gesture.
    /// </summary>
    public PairingWindow? PairingWindow { get; init; }

    /// <summary>
    /// How long a pairing attempt may run before the client aborts it with
    /// <c>attempt_timeout</c>, measured from the attempt's first message. The spec recommends
    /// 2 minutes. Does not apply while a gesture-gated attempt waits on a pairing window:
    /// <c>client/pair-pending</c> precedes an attempt without starting one.
    /// </summary>
    public TimeSpan PairingAttemptTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
