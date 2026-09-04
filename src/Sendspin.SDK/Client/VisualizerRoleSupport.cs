using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Client;

/// <summary>
/// How this client's <c>visualizer@v1</c> role behaves. Only meaningful when the role is
/// advertised in <see cref="ClientCapabilities.Roles"/>.
/// </summary>
/// <remarks>
/// One configuration object spanning two wire objects, because spec PR #195 split the role's
/// declaration in two: <see cref="BufferCapacity"/> is a constant of the device and stays in
/// <c>client/hello</c>'s <c>visualizer@v1_support</c>, while <see cref="Types"/>,
/// <see cref="RateMax"/> and <see cref="Spectrum"/> may change during a connection and travel in
/// the <c>client/state</c> visualizer object. Keeping them together here means an app configures
/// the role once and the client puts each field where the spec wants it; named for the role
/// rather than for either wire object, as <see cref="SourceRoleSupport"/> already is.
/// </remarks>
public sealed class VisualizerRoleSupport
{
    /// <summary>
    /// Max total size in bytes of buffered visualizer binary messages, counting each message's
    /// full wire size (message-type byte + timestamp + data). Advertised in <c>client/hello</c>.
    /// </summary>
    public int BufferCapacity { get; init; }

    /// <summary>
    /// Feature types the client wants, a subset of <see cref="VisualizerTypes"/> (loudness,
    /// f_peak, spectrum, beat, peak). Reported in <c>client/state</c>.
    /// </summary>
    required public List<string> Types { get; init; }

    /// <summary>
    /// Maximum periodic frames per second. Clients should set this to their display refresh rate.
    /// Reported in <c>client/state</c>.
    /// </summary>
    public int RateMax { get; init; }

    /// <summary>
    /// Spectrum configuration. Required when <see cref="VisualizerTypes.Spectrum"/> is among
    /// <see cref="Types"/> — a visualizer state object that lists spectrum without one is a
    /// protocol error the server closes the connection over. Reported in <c>client/state</c>.
    /// </summary>
    public VisualizerSpectrum? Spectrum { get; init; }
}
