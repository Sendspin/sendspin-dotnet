namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A FakeServer that never answers the host's close handshake — the non-conformant peer
/// behaviour that exposed #143's disposal hang. Used only by the regression test for that
/// issue; every other scenario uses the conformant base <see cref="FakeServer"/>.
/// </summary>
internal sealed class SilentCloseFakeServer : FakeServer
{
    internal SilentCloseFakeServer(byte[] psk, IReadOnlyList<string> activities)
        : base(psk, activities)
    {
    }

    protected override Task OnCloseReceivedAsync() => Task.CompletedTask;
}
