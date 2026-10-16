namespace Sendspin.SDK.Client;

/// <summary>
/// Whether the admitted server has activated this client's <c>source</c> role, and how far the
/// session is trusted. Lets an app say why it cannot send: an unpaired session never gets the
/// role, and a paired one only gets it from a server that accepts inputs.
/// </summary>
/// <param name="ServerId">The admitted server.</param>
/// <param name="IsActive">True when a <c>source@</c> role is in the latest <c>active_roles</c>.</param>
/// <param name="TrustLevel">The session's trust level when the state was read.</param>
public sealed record SourceRoleState(string ServerId, bool IsActive, SendspinTrustLevel TrustLevel);
