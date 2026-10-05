using System.Net.NetworkInformation;
using CallReceiver.Services;
using Xunit;
using Xunit.Abstractions;

namespace CallReceiver.Tests;

public class NetworkAddressTests(ITestOutputHelper output)
{
    private static NetworkAddresses.Adapter Lan(int index, params string[] addresses) =>
        new(index, NetworkInterfaceType.Ethernet, true, true, false, true, addresses);

    [Fact] public void VirtualAndInvalidCandidatesAreExcluded()
    {
        var physical = Lan(23, "192.168.0.50");
        var excluded = new[] {
            Lan(1, "172.23.176.1") with { IsHardware = false },
            Lan(2, "10.0.0.1") with { IsTunnel = true },
            Lan(3, "10.0.0.2") with { IsUp = false },
            Lan(4, "10.0.0.3") with { Type = NetworkInterfaceType.Ppp },
            Lan(5, "169.254.1.2", "127.0.0.1", "::1", "invalid", "0.0.0.0", "255.255.255.255", "224.0.0.1")
        };
        Assert.Equal("192.168.0.50", NetworkAddresses.DefaultAddress(() => [.. excluded, physical]));
        Assert.Equal("127.0.0.1", NetworkAddresses.DefaultAddress(() => excluded));
    }

    [Fact] public void PriorityIsGatewayThenTypeThenIndexThenNumericAddress()
    {
        var ethernet = Lan(10, "192.168.0.100", "192.168.0.9");
        var wifi = Lan(1, "10.0.0.7") with { Type = NetworkInterfaceType.Wireless80211 };
        Assert.Equal("192.168.0.9", NetworkAddresses.DefaultAddress(() => [wifi, ethernet]));
        Assert.Equal("10.0.0.7", NetworkAddresses.DefaultAddress(() => [ethernet with { HasGateway = false }, wifi]));
        var earlier = Lan(9, "192.168.1.50");
        Assert.Equal("192.168.1.50", NetworkAddresses.DefaultAddress(() => [ethernet, earlier]));
        Assert.Equal("192.168.1.50", NetworkAddresses.DefaultAddress(() => [earlier, ethernet]));
        Assert.Equal("192.168.0.9", NetworkAddresses.DefaultAddress(() => [ethernet with { HasGateway = false }]));
    }

    [Fact] public void EmptyOrFailedQueryFallsBack()
    {
        Assert.Equal("127.0.0.1", NetworkAddresses.DefaultAddress(() => []));
        Assert.Equal("127.0.0.1", NetworkAddresses.DefaultAddress(() => throw new NetworkInformationException()));
    }

    [Fact] public void NativeAdapterQueryAndSelectionAreConsistent()
    {
        var adapters = NetworkAddresses.ReadAdapters();
        foreach (var a in adapters)
            output.WriteLine($"index={a.Index} hardware={a.IsHardware} tunnel={a.IsTunnel} gateway={a.HasGateway} addresses={string.Join(",", a.Addresses)}");
        var selected = NetworkAddresses.DefaultAddress(() => adapters);
        output.WriteLine($"Selected LAN IPv4: {selected}");
        Assert.Contains(selected, NetworkAddresses.List());
    }
}
