namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// Drives a connection into the state a suspended peer leaves it in (#354): the peer has
/// stopped reading, the socket buffers on both sides are full, and a send is parked inside
/// the socket holding the connection's send lock.
/// </summary>
internal static class StalledPeer
{
    /// <summary>
    /// Sends until one send stays pending, and returns it. Against a peer that is not reading,
    /// that send cannot complete on its own.
    /// </summary>
    public static async Task<Task> SendUntilStalledAsync(Func<byte[], Task> send)
    {
        var chunk = new byte[64 * 1024];

        for (var i = 0; i < 4096; i++)
        {
            var pending = send(chunk);
            if (await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(1))) != pending)
            {
                return pending;
            }

            await pending;
        }

        throw new InvalidOperationException("256 MiB was sent to a peer that is not reading and no send stalled");
    }
}
