// <copyright file="ITimeProbeTransport.cs" company="Sendspin Windows Client">
// Licensed under the MIT License. See LICENSE file in the project root.
// </copyright>

namespace Sendspin.SDK.Connection;

/// <summary>
/// The transport-boundary seam the clock-synchronization exchange needs: T1 stamped at the
/// socket write, T4 stamped at the socket read.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <em>not</em> part of <see cref="ISendspinConnection"/>. 9.x's published surface
/// is frozen, and the equivalent fix upstream widened that interface and changed the
/// <c>TextMessageReceived</c> event's argument type — both breaking changes for anyone who has
/// implemented the interface. A transport that does not implement this seam still works: the
/// client falls back to stamping T1 at the call site and T4 after parsing, which is what 9.2.0
/// did for every transport.
/// </para>
/// <para>
/// Why the stamps belong to the transport at all: a T1 captured before the call is widened by
/// serialization and by any queueing ahead of it, and a T4 read after parsing charges decrypt
/// and parse time to the round trip. Either lands in the measured round trip and biases the
/// offset by half of any send/receive asymmetry. The reference implementation stamps at the
/// same two points for the same reason.
/// </para>
/// </remarks>
internal interface ITimeProbeTransport
{
    /// <summary>
    /// Sends a <c>client/time</c> probe whose <c>client_transmitted</c> (T1) is stamped by the
    /// transport, as close to the socket write as the implementation can manage.
    /// </summary>
    /// <param name="onTransmitted">
    /// Invoked with T1 the moment it is stamped, before the frame reaches the socket and while
    /// the send is still serialized against other sends. Callers register their pending
    /// exchange from here: a reply cannot exist yet, so there is no window in which a
    /// <c>server/time</c> can arrive for a T1 the caller has not recorded. It runs on the
    /// sending thread holding the send lock — do only bookkeeping in it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendTimeMessageAsync(Action<long> onTransmitted, CancellationToken cancellationToken);

    /// <summary>
    /// When the transport saw the text frame it is currently dispatching, in the
    /// <c>HighPrecisionTimer</c> time base shared with audio scheduling and with the T1 stamp.
    /// </summary>
    /// <remarks>
    /// Read from inside a <see cref="ISendspinConnection.TextMessageReceived"/> handler and
    /// nowhere else. The transport writes it immediately before raising that event and the
    /// event is raised synchronously on the single thread that drains the socket, so a handler
    /// always reads the stamp belonging to the frame it was handed. A property rather than a
    /// richer event argument because the event's public signature cannot change on this line.
    /// </remarks>
    long LastTextReceivedAtMicroseconds { get; }
}
