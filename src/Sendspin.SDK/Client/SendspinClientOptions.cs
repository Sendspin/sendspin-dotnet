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
/// </remarks>
public sealed class SendspinClientOptions
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
    /// Failure counter persistence for the PIN pairing methods. <b>Required</b> if
    /// <see cref="ClientCapabilities.PinPairingMethods"/> is non-empty: without a store the
    /// counter cannot survive a restart, so a method could never escalate to gesture-gating —
    /// the SDK declines to offer the PIN methods at all (it sends <c>pair/abort</c> and logs a
    /// warning) rather than granting unlimited, ungated attempts.
    /// <see cref="Connection.Noise.Pairing.FilePinLockoutStore"/> is provided.
    /// </summary>
    public IPinLockoutStore? PinLockoutStore { get; init; }

    /// <summary>
    /// Presents a derived dynamic PIN to the operator through the app's out-channel (display,
    /// speaker) so it can be entered into the server. Required when <c>"dynamic_pin"</c> is
    /// offered in <see cref="ClientCapabilities.PinPairingMethods"/>; pairing fails closed
    /// without it. Awaited before the client proceeds, so a slow presenter delays pairing
    /// rather than racing it.
    /// </summary>
    public Func<PinPresentation, CancellationToken, ValueTask>? PresentPinAsync { get; init; }

    /// <summary>Capture device for the <c>source@v1</c> role.</summary>
    public IAudioCaptureDevice? CaptureDevice { get; init; }

    /// <summary>Encoder factory for the <c>source@v1</c> role.</summary>
    public ISourceAudioEncoderFactory? SourceEncoderFactory { get; init; }

    /// <summary>
    /// The device's pairing window, shared by every connection this application runs.
    /// <b>Required</b> to complete any gesture-gated pairing attempt: static PIN always, and
    /// dynamic PIN once the method is escalated or the session's PIN is shorter than 6 digits.
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
