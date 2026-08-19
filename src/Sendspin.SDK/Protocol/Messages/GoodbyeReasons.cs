namespace Sendspin.SDK.Protocol.Messages;

/// <summary>
/// The closed set of <c>client/goodbye</c> reasons the spec defines (messaging.md:426).
/// </summary>
/// <remarks>
/// <para>
/// The reason is not free text: a server branches on it, and one it cannot parse is
/// indistinguishable from no goodbye at all — which the spec tells the server to treat as
/// <see cref="Restart"/> and auto-reconnect (messaging.md:442). Sending an undefined string
/// is therefore worse than saying nothing deliberately, because it looks like a client that
/// crashed and wants to be reconnected.
/// </para>
/// <para>
/// Use these constants rather than literals so a call site cannot invent a reason the server
/// will not understand.
/// </para>
/// </remarks>
public static class GoodbyeReasons
{
    /// <summary>Switching to a different Sendspin server. The server SHOULD NOT auto-reconnect.</summary>
    public const string AnotherServer = "another_server";

    /// <summary>
    /// Powering off, or otherwise not coming back, and no more specific reason applies
    /// (messaging.md:436). This is what an application should send as it exits.
    /// </summary>
    public const string Shutdown = "shutdown";

    /// <summary>
    /// Going away but expected back — the server may auto-reconnect. The right reason for a
    /// self-update or a deliberate restart, and the default for an unexplained graceful close.
    /// </summary>
    public const string Restart = "restart";

    /// <summary>The operator asked to disconnect.</summary>
    public const string UserRequest = "user_request";

    /// <summary>The server's activation was not admissible for the matched PSK.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>Unpaired access is disabled and pairing would make the activation admissible.</summary>
    public const string PairingRequired = "pairing_required";

    /// <summary>Another attempt or connection is already in progress with this client.</summary>
    public const string ConcurrentAttempt = "concurrent_attempt";

    /// <summary>The server removed its pairing record via <c>server/unpair</c>.</summary>
    public const string Unpaired = "unpaired";

    /// <summary>Every reason the spec defines, for validating a caller-supplied value.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        AnotherServer, Shutdown, Restart, UserRequest,
        Unauthorized, PairingRequired, ConcurrentAttempt, Unpaired,
    ];

    /// <summary>Whether the spec defines a reason.</summary>
    /// <param name="reason">The reason to check.</param>
    /// <returns><see langword="true"/> if <paramref name="reason"/> is in <see cref="All"/>.</returns>
    public static bool IsDefined(string reason) => All.Contains(reason, StringComparer.Ordinal);
}
