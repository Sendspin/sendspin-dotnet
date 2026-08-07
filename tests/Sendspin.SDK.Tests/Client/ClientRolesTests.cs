using System.Reflection;
using Sendspin.SDK.Client;

namespace Sendspin.SDK.Tests.Client;

public class ClientRolesTests
{
    [Theory]
    [InlineData("player@v1")]
    [InlineData("controller@v1")]
    [InlineData("metadata@v1")]
    [InlineData("artwork@v1")]
    [InlineData("visualizer@v1")]
    [InlineData("color@v1")]
    [InlineData("source@v1")]
    public void EveryRoleConstant_IsTheWireValue(string expected)
    {
        var all = typeof(ClientRoles)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Contains(expected, all);
    }

    [Fact]
    public void NoRoleConstant_IsMissingItsVersionSuffix()
    {
        var bare = typeof(ClientRoles)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (Name: f.Name, Value: (string)f.GetRawConstantValue()!))
            .Where(x => !x.Value.EndsWith("@v1", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(bare);
    }
}
