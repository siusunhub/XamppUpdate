using System.Net;
using System.Net.Sockets;

namespace XamppUpdate.Services.Uat;

public static class UatPortProbe
{
    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>Exclusive bind on IPv4 loopback and any-address; the probe sockets are released before returning.</summary>
    public static bool IsPortFree(int port)
    {
        if (!IsValidPort(port)) return false;

        foreach (var address in new[] { IPAddress.Loopback, IPAddress.Any })
        {
            try
            {
                var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
                listener.Start();
                listener.Stop();
            }
            catch (SocketException)
            {
                return false;
            }
        }
        return true;
    }
}
