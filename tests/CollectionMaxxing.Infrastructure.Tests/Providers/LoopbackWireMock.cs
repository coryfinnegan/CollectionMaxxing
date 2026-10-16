using System.Net;
using System.Net.Sockets;
using WireMock.Server;
using WireMock.Settings;

namespace CollectionMaxxing.Infrastructure.Tests.Providers;

/// <summary>
/// Starts WireMock on 127.0.0.1 only. <see cref="WireMockServer.Start()"/> listens on every interface, which makes Windows
/// Firewall ask the owner about each new test executable (TestServerBindingTests keeps it that way).
/// </summary>
public static class LoopbackWireMock
{
    public static WireMockServer Start() =>
        WireMockServer.Start(new WireMockServerSettings { Urls = [$"http://127.0.0.1:{FreeLoopbackPort()}"] });

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
