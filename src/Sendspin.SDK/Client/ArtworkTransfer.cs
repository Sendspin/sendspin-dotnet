using Sendspin.SDK.Protocol;

namespace Sendspin.SDK.Client;

/// <summary>
/// Reassembles the one artwork image transfer in flight: an announce, then the parts of the
/// encoded image, until the received data reaches the announced size.
/// </summary>
/// <remarks>
/// <para>
/// Spec roles/artwork/v1.md: "At most one image transfer is in flight at a time across all of
/// the role's channels: a transfer is in flight from its announce until it completes or its
/// partly received image is discarded." So this holds a single transfer, not one per channel.
/// </para>
/// <para>
/// The methods that can meet one of the spec's "malformed sequences" return false for it, and
/// the caller closes the connection: "an announce received while a transfer is in flight, a part
/// received with no transfer in flight or on a channel other than the in-flight transfer's, and
/// a part whose <c>data</c> would extend past <c>total_size</c>".
/// </para>
/// <para>
/// Fed from the receive loop and reset from the connection's state change, which are different
/// threads, hence the lock.
/// </para>
/// </remarks>
internal sealed class ArtworkTransfer
{
    private readonly object _lock = new();

    /// <summary>The image so far; null when no transfer is in flight. Guarded by <see cref="_lock"/>.</summary>
    private MemoryStream? _image;

    private byte _channel;
    private long _timestamp;
    private uint _totalSize;
    private uint _received;

    /// <summary>
    /// Set once any of the transfer's image data has been discarded, after which the image can
    /// no longer be completed and only its bytes are counted.
    /// </summary>
    private bool _discarded;

    /// <summary>
    /// Opens a transfer. An announce of size zero "completes immediately, with no parts".
    /// </summary>
    /// <param name="announce">The parsed announce.</param>
    /// <param name="discard">
    /// True while the client is unavailable and so discarding image data. An empty image has
    /// no data to discard, so a clear completes regardless.
    /// </param>
    /// <param name="complete">The empty image of a zero-size announce; otherwise null.</param>
    /// <returns>False when a transfer is already in flight.</returns>
    internal bool TryBegin(ArtworkMessage announce, bool discard, out ArtworkChunk? complete)
    {
        complete = null;

        lock (_lock)
        {
            if (_image is not null)
            {
                return false;
            }

            if (announce.TotalSize == 0)
            {
                complete = new ArtworkChunk
                {
                    Channel = announce.Channel,
                    Timestamp = announce.Timestamp,
                    ImageData = Array.Empty<byte>(),
                };
                return true;
            }

            _image = new MemoryStream();
            _channel = announce.Channel;
            _timestamp = announce.Timestamp;
            _totalSize = announce.TotalSize;
            _received = 0;
            _discarded = discard;
            return true;
        }
    }

    /// <summary>
    /// Adds a part's data to the transfer in flight, completing it when the received data
    /// reaches the announced size.
    /// </summary>
    /// <param name="channel">The channel the part arrived on.</param>
    /// <param name="data">The part's image bytes.</param>
    /// <param name="discard">
    /// True while the client is unavailable and so discarding image data. The bytes still count:
    /// "clients discarding image data MUST still process announces and cancels and count each
    /// part's <c>data</c> bytes toward <c>total_size</c>".
    /// </param>
    /// <param name="complete">
    /// The image when this part completed it; null while more parts are due, and null when the
    /// transfer completed with data discarded along the way.
    /// </param>
    /// <returns>
    /// False when no transfer is in flight on <paramref name="channel"/>, or the part would
    /// extend past the announced size.
    /// </returns>
    internal bool TryAppend(byte channel, ReadOnlySpan<byte> data, bool discard, out ArtworkChunk? complete)
    {
        complete = null;

        lock (_lock)
        {
            if (_image is null || _channel != channel || data.Length > _totalSize - _received)
            {
                return false;
            }

            _received += (uint)data.Length;

            if (discard || _discarded)
            {
                _discarded = true;
                _image.SetLength(0);
            }
            else
            {
                _image.Write(data);
            }

            if (_received < _totalSize)
            {
                return true;
            }

            if (!_discarded)
            {
                complete = new ArtworkChunk
                {
                    Channel = _channel,
                    Timestamp = _timestamp,
                    ImageData = _image.ToArray(),
                };
            }

            _image = null;
            return true;
        }
    }

    /// <summary>
    /// Ends the transfer in flight on <paramref name="channel"/>, if there is one: its partly
    /// received image is that channel's pending image, which a cancel discards, as does a
    /// <c>stream/start</c> that changes the channel's configuration.
    /// </summary>
    internal void Cancel(byte channel)
    {
        lock (_lock)
        {
            if (_image is not null && _channel == channel)
            {
                _image = null;
            }
        }
    }

    /// <summary>
    /// Forgets the transfer in flight, whichever channel it is on. For an artwork
    /// <c>stream/end</c>, which discards every pending image, and for the loss of the connection
    /// the transfer was arriving on.
    /// </summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _image = null;
        }
    }
}
