using System.Buffers.Binary;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// Builds the three artwork binary messages as aiosendspin 10.0.0 packs them (spec
/// roles/artwork/v1.md): an announce, a part, and a cancel.
/// </summary>
internal static class ArtworkWire
{
    /// <summary><c>[type][flags=0x02][timestamp int64 BE][total_size uint32 BE]</c>.</summary>
    public static byte[] Announce(byte type, long timestamp, uint totalSize)
    {
        var buf = new byte[14];
        buf[0] = type;
        buf[1] = 0x02;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(2, 8), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(10, 4), totalSize);
        return buf;
    }

    /// <summary><c>[type][flags=0x00][data]</c>.</summary>
    public static byte[] Part(byte type, params byte[] data)
    {
        var buf = new byte[2 + data.Length];
        buf[0] = type;
        data.CopyTo(buf, 2);
        return buf;
    }

    /// <summary><c>[type][flags=0x01]</c>.</summary>
    public static byte[] Cancel(byte type) => new byte[] { type, 0x01 };

    /// <summary>
    /// Delivers a whole image as the shortest transfer that carries it: an announce, then one
    /// part. An empty image is the announce alone, which clears the channel.
    /// </summary>
    public static void RaiseArtwork(this FakeSendspinConnection connection, byte type, long timestamp, byte[] image)
    {
        connection.RaiseBinaryMessageReceived(Announce(type, timestamp, (uint)image.Length));
        if (image.Length > 0)
        {
            connection.RaiseBinaryMessageReceived(Part(type, image));
        }
    }
}
