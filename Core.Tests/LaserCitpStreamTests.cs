using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using T3.Core.Output;
using Xunit;

namespace Core.Tests;

/// <summary>
/// Lifecycle of the CITP laser stream's TCP control session against a loopback listener standing
/// in for Capture: a direct-connect plug (host and port set) must bring the session back after the
/// receiver drops it and comes back, without the plug being rebuilt.
/// </summary>
public class LaserCitpStreamTests
{
    [Fact]
    public void DirectConnect_ReconnectsAfterReceiverRestart()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var plug = new LaserPlug
                       {
                           Kind = LaserPlug.Kinds.Citp,
                           Name = "CITP test",
                           Address = "127.0.0.1",
                           Port = port,
                       };

        var stream = LaserOutputStream.Create(plug);
        Assert.NotNull(stream);
        try
        {
            using (var first = AcceptWithin(listener, TimeSpan.FromSeconds(5)))
            {
                Assert.NotNull(first);
                Assert.True(WaitUntil(() => stream.IsConnected, TimeSpan.FromSeconds(5)), "first session never came up");
            }

            // Receiver goes away: the stream notices the closed session.
            listener.Stop();
            Assert.True(WaitUntil(() => !stream.IsConnected, TimeSpan.FromSeconds(5)), "closed session still reported connected");

            // Receiver restarts on the same port: the stream must reconnect on its own.
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            using var second = AcceptWithin(listener, TimeSpan.FromSeconds(10));
            Assert.NotNull(second);
        }
        finally
        {
            stream.Dispose();
            listener.Stop();
        }
    }

    [Fact]
    public void ControlMessages_AreFramedByMessageSize()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var plug = new LaserPlug
                       {
                           Kind = LaserPlug.Kinds.Citp,
                           Name = "CITP test",
                           Address = "127.0.0.1",
                           Port = port,
                       };

        var stream = LaserOutputStream.Create(plug);
        Assert.NotNull(stream);
        try
        {
            using var session = AcceptWithin(listener, TimeSpan.FromSeconds(5));
            Assert.NotNull(session);
            Assert.True(WaitUntil(() => stream.IsConnected, TimeSpan.FromSeconds(5)), "session never came up");
            var network = session.GetStream();

            // Two messages in one segment: the feed-list request must not swallow the feed start.
            var both = CaexMessage(GetLaserFeedList, []).Concat(CaexMessage(LaserFeedControl, [0, 30])).ToArray();
            network.Write(both, 0, both.Length);
            Assert.True(WaitUntil(() => stream.StatusMessage.Contains("active at 30 fps"), TimeSpan.FromSeconds(5)),
                        $"coalesced feed control ignored - status '{stream.StatusMessage}'");

            // One message split across two segments.
            var control = CaexMessage(LaserFeedControl, [0, 25]);
            network.Write(control, 0, 10);
            network.Flush();
            Thread.Sleep(100);
            network.Write(control, 10, control.Length - 10);
            Assert.True(WaitUntil(() => stream.StatusMessage.Contains("active at 25 fps"), TimeSpan.FromSeconds(5)),
                        $"split feed control ignored - status '{stream.StatusMessage}'");
        }
        finally
        {
            stream.Dispose();
            listener.Stop();
        }
    }

    private const uint GetLaserFeedList = 0x00030100;
    private const uint LaserFeedControl = 0x00030102;

    /// <summary>A CITP/CAEX message: 20-byte CITP header (MessageSize = whole message), ContentCode, body.</summary>
    private static byte[] CaexMessage(uint contentCode, byte[] body)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write("CITP"u8.ToArray());
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write((ushort)0);
        writer.Write((uint)(20 + 4 + body.Length));
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write("CAEX"u8.ToArray());
        writer.Write(contentCode);
        writer.Write(body);
        writer.Flush();
        return ms.ToArray();
    }

    private static TcpClient? AcceptWithin(TcpListener listener, TimeSpan timeout)
    {
        var accept = listener.AcceptTcpClientAsync();
        return accept.Wait(timeout) ? accept.Result : null;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
                return true;

            Thread.Sleep(20);
        }

        return condition();
    }
}
