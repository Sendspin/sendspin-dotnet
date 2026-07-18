namespace Sendspin.SDK.Tests;

/// <summary>
/// xUnit collection for tests that open real localhost WebSocket connections
/// (host/listener, loopback client/server, reconnect). Marked
/// <see cref="CollectionDefinitionAttribute"/> with parallelization disabled so these
/// classes run serially rather than concurrently.
/// </summary>
/// <remarks>
/// Running several real-socket test classes in parallel on a resource-constrained CI
/// runner starved individual connections past their timeouts (intermittent
/// TimeoutException in the host arbitration tests). Serializing them removes the
/// contention; they are fast enough that the serial cost is negligible. Pure/in-memory
/// tests are unaffected and still run in parallel.
/// </remarks>
[CollectionDefinition("RealSockets", DisableParallelization = true)]
public sealed class RealSocketsCollection
{
}
