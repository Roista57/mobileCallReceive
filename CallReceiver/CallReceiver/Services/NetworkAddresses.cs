using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CallReceiver.Services;

public static class NetworkAddresses
{
    private const string Loopback = "127.0.0.1";

    public static IReadOnlyList<string> List() => new[] { "127.0.0.1" }.Concat(
        NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())).Distinct().ToArray();

    public sealed record Adapter(int Index, NetworkInterfaceType Type, bool IsUp, bool IsHardware,
        bool IsTunnel, bool HasGateway, IReadOnlyList<string> Addresses);

    public static string DefaultAddress() => DefaultAddress(ReadAdapters);

    public static string DefaultAddress(Func<IReadOnlyList<Adapter>> adapterProvider)
    {
        try
        {
            return adapterProvider()
                .Where(a => a.IsUp && a.IsHardware && !a.IsTunnel &&
                    a.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .OrderByDescending(a => a.HasGateway)
                .ThenBy(a => a.Type == NetworkInterfaceType.Ethernet ? 0 : 1)
                .ThenBy(a => a.Index)
                .SelectMany(a => a.Addresses.Select(ParseLanAddress).Where(ip => ip is not null)
                    .OrderBy(ip => AddressNumber(ip!)))
                .FirstOrDefault()?.ToString() ?? Loopback;
        }
        catch { return Loopback; }
    }

    private static IPAddress? ParseLanAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(ip)) return null;
        var bytes = ip.GetAddressBytes();
        return bytes[0] == 0 || bytes[0] >= 224 || (bytes[0] == 169 && bytes[1] == 254) ? null : ip;
    }

    private static uint AddressNumber(IPAddress address) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    public static IReadOnlyList<Adapter> ReadAdapters()
    {
        var result = new List<Adapter>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
                var properties = adapter.GetIPProperties();
                var index = properties.GetIPv4Properties()?.Index;
                if (index is null) continue;
                var row = new InterfaceRow { Index = (uint)index.Value };
                if (GetIfEntry2(ref row) != 0) continue;
                result.Add(new Adapter(index.Value, adapter.NetworkInterfaceType, true,
                    (row.Flags & 1) != 0, row.TunnelType != 0,
                    properties.GatewayAddresses.Any(g => ParseLanAddress(g.Address.ToString()) is not null),
                    properties.UnicastAddresses.Select(a => a.Address.ToString()).ToArray()));
            }
            catch (Exception e) when (e is NetworkInformationException or SocketException or InvalidOperationException)
            {
                // An adapter can disappear during enumeration; keep the other candidates.
            }
        }
        return result;
    }

    // Windows SDK MIB_IF_ROW2 (8-byte alignment), including space for every native field.
    // Only the input index and the hardware/tunnel output fields are needed here.
    [StructLayout(LayoutKind.Explicit, Size = 1352)]
    private struct InterfaceRow
    {
        [FieldOffset(8)] public uint Index;
        [FieldOffset(1132)] public uint TunnelType;
        [FieldOffset(1152)] public byte Flags;
    }
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(ref InterfaceRow row);
}
