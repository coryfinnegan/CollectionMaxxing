using System.Net;
using System.Net.Sockets;
using WireMock.Server;
using WireMock.Settings;

namespace CollectionMaxxing.Api.Tests.Support;

/// <summary>
/// Starts WireMock on 127.0.0.1 only. <see cref="WireMockServer.Start()"/> listens on every interface, which makes Windows
/// Firewall ask the owner about each new test executable (TestServerBindingTests keeps it that way).
/// </summary>
public static class LoopbackWireMock
{
    // Finding a free port and starting on it are two steps, and the port is free again in between. Test classes run in parallel,
    // so without this lock two of them could be handed the same port. Another process can still grab it (the other test projects
    // run at the same time), hence the retry.
    private static readonly object StartLock = new();

    public static WireMockServer Start()
    {
        lock (StartLock)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return WireMockServer.Start(new WireMockServerSettings { Urls = [$"http://127.0.0.1:{FreeLoopbackPort()}"] });
                }
                catch (Exception ex) when (attempt < 5 && ex is not OutOfMemoryException)
                {
                    // The port was taken in the meantime: ask for another.
                }
            }
        }
    }

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
