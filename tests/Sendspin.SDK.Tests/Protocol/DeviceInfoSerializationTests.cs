using Sendspin.SDK.Protocol;
using Sendspin.SDK.Protocol.Messages;

namespace Sendspin.SDK.Tests.Protocol;

/// <summary>
/// Wire-format coverage for the <c>mac_address</c> field added to the client/hello device_info
/// object (per the Sendspin spec).
/// </summary>
public class DeviceInfoSerializationTests
{
    [Fact]
    public void DeviceInfo_SerializesMacAddress()
    {
        var msg = ClientHelloMessage.Create(
            clientId: "c1",
            name: "Player",
            supportedRoles: new List<string> { "player@v1" },
            deviceInfo: new DeviceInfo { MacAddress = "aa:bb:cc:dd:ee:ff" });

        var json = MessageSerializer.Serialize(msg);

        Assert.Contains("\"mac_address\":\"aa:bb:cc:dd:ee:ff\"", json);
    }

    [Fact]
    public void DeviceInfo_OmitsMacAddressWhenNull()
    {
        var msg = ClientHelloMessage.Create(
            clientId: "c1",
            name: "Player",
            supportedRoles: new List<string> { "player@v1" },
            deviceInfo: new DeviceInfo { ProductName = "Test" });

        var json = MessageSerializer.Serialize(msg);

        Assert.DoesNotContain("mac_address", json);
    }
}
