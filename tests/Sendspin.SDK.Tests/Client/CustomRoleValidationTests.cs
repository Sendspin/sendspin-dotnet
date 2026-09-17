using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

/// <summary>
/// A custom (application-specific) role is named with a leading <c>_</c> and MUST carry an
/// explicit <c>@v…</c> version when advertised (spec template.md, PR #243).
/// <see cref="ClientCapabilities"/> rejects a versionless one at construction, where the app can
/// still fix it, rather than letting it reach a server that would close the connection over it.
/// </summary>
public class CustomRoleValidationTests
{
    [Fact]
    public void CustomRole_WithoutVersion_ThrowsAtConstruction()
    {
        var ex = Assert.Throws<ArgumentException>(() => TestClient.Create(configure: options => options with
        {
            Capabilities = new ClientCapabilities
            {
                Roles = new List<string> { "player@v1", "_diagnostics" },
            },
        }));

        Assert.Contains("_diagnostics", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomRole_WithVersion_IsAccepted()
    {
        // The positive control: the same role with an explicit version constructs cleanly, so the
        // rejection above is the version rule and not the underscore prefix by itself.
        var (client, _, _) = TestClient.Create(configure: options => options with
        {
            Capabilities = new ClientCapabilities
            {
                Roles = new List<string> { "player@v1", "_diagnostics@v1" },
            },
        });
        using var _c = client;
    }
}
