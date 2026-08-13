using Sendspin.SDK.Connection.Noise.Pairing;

namespace Sendspin.SDK.Tests.Connection;

/// <summary>
/// <see cref="CPace"/> holds the derived secrets — the ISK and the confirmation MAC key — from
/// <see cref="CPace.Derive"/> until the pairing attempt ends, which spans operator interaction.
/// Disposing must clear them rather than leaving them for the GC (#102).
/// </summary>
/// <remarks>
/// Best-effort by nature: the CLR may have moved or paged these buffers, so this pins the code's
/// intent rather than proving the process holds no copy. That is still worth pinning — the
/// failure mode this guards is someone removing the clear, not the runtime relocating an array.
/// </remarks>
public class CPaceZeroizationTests
{
    private static (CPace Client, CPace Server) DerivedPair()
    {
        byte[] prs = "1234"u8.ToArray();
        byte[] sid = Enumerable.Repeat((byte)3, 16).ToArray();

        var server = CPace.Start(CPaceRole.Initiator, prs, sid);
        var client = CPace.Start(CPaceRole.Responder, prs, sid);

        client.Derive(server.PublicShare);
        server.Derive(client.PublicShare);

        return (client, server);
    }

    [Fact]
    public void Dispose_ZeroesTheIsk()
    {
        var (client, server) = DerivedPair();
        using (server)
        {
            // The reference has to be taken before disposal: afterwards Isk throws, and asking
            // for it then would test the guard instead of the clearing.
            byte[] isk = client.Isk;

            Assert.Contains(isk, b => b != 0);

            client.Dispose();

            Assert.All(isk, b => Assert.Equal(0, b));
        }
    }

    [Fact]
    public void DerivedSecretsAreRealBeforeDisposal()
    {
        // Positive control. Every assertion above is satisfied by a CPace that derived nothing
        // at all, so this pins that the run actually produced agreeing key material first.
        var (client, server) = DerivedPair();
        using (client)
        using (server)
        {
            Assert.Equal(client.Isk, server.Isk);
            Assert.True(server.Verify(client.Tag()));
            Assert.True(client.Verify(server.Tag()));
        }
    }

    [Fact]
    public void UseAfterDispose_Throws_RatherThanReturningZeroedMaterial()
    {
        // A zeroed _isk is still 64 bytes and a zeroed _macKey still produces a tag, so without
        // an explicit guard a use-after-dispose would hand back plausible-looking key material
        // instead of failing.
        var (client, server) = DerivedPair();
        using (server)
        {
            client.Dispose();

            Assert.Throws<ObjectDisposedException>(() => client.Isk);
            Assert.Throws<ObjectDisposedException>(() => client.Tag());
            Assert.Throws<ObjectDisposedException>(() => client.Verify(server.Tag()));
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        // ClearPinState runs on every ending, and more than one can reach the same attempt
        // (an abort followed by a disconnect), so a second Dispose must not throw.
        var (client, server) = DerivedPair();
        using (server)
        {
            client.Dispose();
            client.Dispose();
        }
    }

    [Fact]
    public void DisposeBeforeDerive_IsSafe()
    {
        // The common ending, not a rare one: an operator who walks away, an aborted attempt, a
        // dropped connection. The scalar is still live at that point.
        var cpace = CPace.Start(CPaceRole.Responder, "1234"u8.ToArray(), new byte[16]);

        cpace.Dispose();

        Assert.Throws<ObjectDisposedException>(() => cpace.Derive(new byte[32]));
    }

    [Fact]
    public void DeriveWithABadPeerShare_StillClearsTheScalar()
    {
        // A hostile peer can reach the throwing paths inside Derive, so the clear sits in a
        // finally. Observable only through Derive refusing to run twice — the scalar is
        // consumed either way, which is what stops a retry reusing it.
        var cpace = CPace.Start(CPaceRole.Responder, "1234"u8.ToArray(), new byte[16]);
        using (cpace)
        {
            Assert.Throws<CPaceException>(() => cpace.Derive(new byte[31]));

            var second = Assert.Throws<CPaceException>(() => cpace.Derive(new byte[32]));
            Assert.Contains("only be called once", second.Message);
        }
    }
}
