using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CallReceiver.Services;

public static class NetworkAddresses
{
    public static IReadOnlyList<string> List() => new[] { "127.0.0.1" }.Concat(
        NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())).Distinct().ToArray();
}
