using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CallReceiver.Services;

public static class NetworkAddresses
{
    private const string Loopback = "127.0.0.1";

    public static IReadOnlyList<string> List() => new[] { "127.0.0.1" }.Concat(
        NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())).Distinct().ToArray();

    public static string DefaultAddress() => DefaultAddress(List);

    public static string DefaultAddress(Func<IReadOnlyList<string>> addressProvider)
    {
        try
        {
            return addressProvider()
                .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
                .FirstOrDefault(address => address is { AddressFamily: AddressFamily.InterNetwork } &&
                    !IPAddress.IsLoopback(address))
                ?.ToString() ?? Loopback;
        }
        catch
        {
            return Loopback;
        }
    }
}
