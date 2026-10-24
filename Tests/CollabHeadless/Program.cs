// Headless tests for the collab/boygroup/failover network layer. Runs anywhere .NET 10 runs.
//
//   CollabHeadless                 → runs every in-process test, prints PASS/FAIL per test, exit 0/1
//   CollabHeadless failover-node <showId> <priority> <heartbeatMs>
//                                  → failover election participant used by TestFailoverElection
//
// These exercise exactly the code that runs inside the editor: the same sources are compile-included.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json.Linq;
using T3.Core.Show;
using T3.Editor.Collab;
using T3.Editor.Show;

namespace T3.CollabHeadless;

internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length >= 4 && args[0] == "failover-node")
            return RunFailoverNode(args[1], int.Parse(args[2]), int.Parse(args[3]));

        Run("framing.roundtrip", TestFramingRoundTrip);
        Run("framing.maxsize", TestFrameSizeLimit);
        Run("json.decode", TestJsonDecode);
        Run("presence.roundtrip", TestPresencePacket);
        Run("auth.mac", TestAuth);
        Run("deploy.inventory", TestDeployInventoryDiff);
        Run("deploy.transfer", TestDeployFileTransfer);
        Run("deploy.pathsafety", TestDeployPathSafety);
        Run("failover.election", TestFailoverElection);

        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} TEST(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception e)
        {
            _failures++;
            Console.WriteLine($"FAIL {name}: {e.Message}");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    // ---------------------------------------------------------------------------------------
    // Framing
    // ---------------------------------------------------------------------------------------

    private static void TestFramingRoundTrip()
    {
        // Real sockets: forces multi-read paths (a 100 KB payload never arrives in one Read).
        var (a, b) = LoopbackPair();
        try
        {
            var streamA = a.GetStream();
            var streamB = b.GetStream();

            var payloads = new[]
                               {
                                   Array.Empty<byte>(),
                                   Encoding.UTF8.GetBytes("hello"),
                                   new byte[100_000],
                               };
            payloads[2].AsSpan().Fill(0xAB);

            foreach (var (payload, i) in payloads.Select((p, i) => (p, i)))
            {
                var type = i % 2 == 0 ? CollabProtocol.FrameType.Json : CollabProtocol.FrameType.Presence;
                CollabProtocol.WriteFrame(streamA, type, payload);
            }

            for (var i = 0; i < payloads.Length; i++)
            {
                var expectedType = i % 2 == 0 ? CollabProtocol.FrameType.Json : CollabProtocol.FrameType.Presence;
                Check(CollabProtocol.TryReadFrameLength(streamB, out var length), $"frame {i}: stream ended early");
                Check(length == payloads[i].Length + 1, $"frame {i}: length {length} != {payloads[i].Length + 1}");

                var header = new byte[1];
                Check(CollabProtocol.TryReadExact(streamB, header), $"frame {i}: type read failed");
                Check((CollabProtocol.FrameType)header[0] == expectedType, $"frame {i}: wrong type {header[0]}");

                var body = new byte[length - 1];
                Check(CollabProtocol.TryReadExact(streamB, body), $"frame {i}: payload read failed");
                Check(body.AsSpan().SequenceEqual(payloads[i]), $"frame {i}: payload mismatch");
            }
        }
        finally
        {
            a.Dispose();
            b.Dispose();
        }
    }

    private static void TestFrameSizeLimit()
    {
        var tooBig = new byte[CollabProtocol.MaxFrameSize]; // +1 type byte exceeds the cap
        try
        {
            CollabProtocol.WriteFrame(System.IO.Stream.Null, CollabProtocol.FrameType.Json, tooBig);
            throw new Exception("oversized frame was accepted");
        }
        catch (System.IO.IOException)
        {
            // expected
        }
    }

    private static void TestJsonDecode()
    {
        var encoded = CollabProtocol.EncodeJson(new { t = "hello", n = 42, g = Guid.Empty.ToString() });
        Check(CollabProtocol.TryDecodeJson(encoded, out var json), "valid message rejected");
        Check(CollabProtocol.GetString(json, "t") == "hello", "GetString failed");
        Check(CollabProtocol.TryGetInt(json, "n", out var n) && n == 42, "TryGetInt failed");
        Check(CollabProtocol.TryGetGuid(json, "g", out var g) && g == Guid.Empty, "TryGetGuid failed");

        Check(!CollabProtocol.TryDecodeJson("[1,2,3]"u8.ToArray(), out _), "array accepted as message");
        Check(!CollabProtocol.TryDecodeJson("not json"u8.ToArray(), out _), "garbage accepted");
        Check(!CollabProtocol.TryDecodeJson("{\"noType\":1}"u8.ToArray(), out _), "message without t accepted");

        // JSON never starts with '{' collides with relay control lines — assert the discriminator.
        var objStart = Encoding.UTF8.GetBytes("{\"t\":\"x\"}");
        Check(objStart[0] == (byte)'{', "JSON frames must start with '{' (relay control-line convention)");
    }

    // ---------------------------------------------------------------------------------------
    // Presence packet (binary, little-endian)
    // ---------------------------------------------------------------------------------------

    private static void TestPresencePacket()
    {
        var packet = new PresencePacket
                         {
                             PeerId = 7,
                             Flags = PresencePacket.FlagHoveringCanvas,
                             CompositionId = Guid.NewGuid(),
                             CursorOnCanvas = new Vector2(-123.5f, 88.25f),
                             SelectedChildIds = [Guid.NewGuid(), Guid.NewGuid()],
                         };

        var buffer = new byte[128];
        var size = packet.Encode(buffer);
        Check(size == 2 + 16 + 8 + 1 + 2 * 16, $"encoded size {size} unexpected");
        Check(PresencePacket.TryDecode(buffer.AsSpan(0, size), out var decoded), "decode failed");
        Check(decoded.PeerId == 7 && decoded.Flags == packet.Flags, "header fields mismatch");
        Check(decoded.CompositionId == packet.CompositionId, "composition id mismatch");
        Check(MathF.Abs(decoded.CursorOnCanvas.X - packet.CursorOnCanvas.X) < 0.001f, "cursor x drifted");
        Check(decoded.SelectedChildIds.SequenceEqual(packet.SelectedChildIds), "selection mismatch");

        Check(!PresencePacket.TryDecode(buffer.AsSpan(0, 26), out _), "truncated packet accepted");
    }

    // ---------------------------------------------------------------------------------------
    // Auth (PBKDF2 + HMAC challenge/response)
    // ---------------------------------------------------------------------------------------

    private static void TestAuth()
    {
        var salt = CollabAuth.CreateSalt();
        var nonce = CollabAuth.CreateNonce();
        var keyA = CollabAuth.DeriveKey("show-password", salt);
        var keyB = CollabAuth.DeriveKey("show-password", salt);
        Check(keyA.AsSpan().SequenceEqual(keyB), "derivation not deterministic for same salt");

        var other = CollabAuth.DeriveKey("wrong-password", salt);
        Check(!keyA.AsSpan().SequenceEqual(other), "different passwords produced equal keys");

        var mac = CollabAuth.ComputeMac(keyA, nonce);
        Check(CollabAuth.MacsEqual(mac, CollabAuth.ComputeMac(keyB, nonce)), "valid mac rejected");
        Check(!CollabAuth.MacsEqual(mac, CollabAuth.ComputeMac(other, nonce)), "wrong-password mac accepted");
        Check(!CollabAuth.MacsEqual(mac, CollabAuth.ComputeMac(keyA, CollabAuth.CreateNonce())), "mac bound to nonce");

        Check(CollabAuth.TryFromHexString(CollabAuth.ToHexString(mac), out var roundTrip)
              && roundTrip.AsSpan().SequenceEqual(mac), "hex round-trip failed");
    }

    // ---------------------------------------------------------------------------------------
    // Deployer: inventory diff + full chunked transfer
    // ---------------------------------------------------------------------------------------

    private static void TestDeployInventoryDiff()
    {
        var server = new Dictionary<string, BoygroupDeployer.InventoryEntry>(StringComparer.Ordinal)
                         {
                             ["a.txt"] = new() { RelativePath = "a.txt", Size = 10, LastWriteTicks = 1000 },
                             ["b.bin"] = new() { RelativePath = "b.bin", Size = 500_000, LastWriteTicks = 2000 },
                             ["sub/c.txt"] = new() { RelativePath = "sub/c.txt", Size = 3, LastWriteTicks = 3000 },
                             ["d.txt"] = new() { RelativePath = "d.txt", Size = 100, LastWriteTicks = 4000 },
                         };
        var client = new Dictionary<string, BoygroupDeployer.InventoryEntry>(StringComparer.Ordinal)
                         {
                             // a.txt: identical. b.bin: size differs. c.txt: mtime differs. d.txt: server-only.
                             ["a.txt"] = new() { RelativePath = "a.txt", Size = 10, LastWriteTicks = 1000 },
                             ["b.bin"] = new() { RelativePath = "b.bin", Size = 400_000, LastWriteTicks = 2000 },
                             ["sub/c.txt"] = new() { RelativePath = "sub/c.txt", Size = 3, LastWriteTicks = 9999 },
                             ["stale.txt"] = new() { RelativePath = "stale.txt", Size = 1, LastWriteTicks = 1 },
                         };

        var message = BoygroupDeployer.BuildInventoryMessage(server);
        var parsed = BoygroupDeployer.ParseInventory(message);
        Check(parsed.Count == 4 && parsed["b.bin"].Size == 500_000, "inventory message round-trip failed");

        var missing = BoygroupDeployer.ComputeMissingFiles(server, client);
        // b.bin: size differs. sub/c.txt: mtime differs. d.txt: exists only on the server.
        Check(missing.Count == 3, $"expected 3 missing, got {string.Join(',', missing)}");
        Check(missing[0] == "sub/c.txt" && missing[1] == "d.txt" && missing[2] == "b.bin",
              $"smallest-first sort wrong: {string.Join(',', missing)}");

        var extra = BoygroupDeployer.ComputeExtraFiles(server, client);
        Check(extra.Count == 1 && extra[0] == "stale.txt", "extra-file detection failed");
    }

    private static void TestDeployFileTransfer()
    {
        var root = Directory.CreateTempSubdirectory("tixl-deploy-");
        try
        {
            // Empty file (edge case), a text file, and a 500 KB binary blob spanning several chunks.
            var mtime = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            var cases = new (string Name, byte[] Content)[]
                            {
                                ("empty.dat", []),
                                ("notes.txt", "höllo ünICODE ✓"u8.ToArray()),
                                ("blob.bin", MakeBlob(500_000)),
                            };
            foreach (var (name, content) in cases)
            {
                var path = Path.Combine(root.FullName, name);
                File.WriteAllBytes(path, content);
                File.SetLastWriteTimeUtc(path, mtime);
            }

            var inventory = BoygroupDeployer.BuildInventory(root.FullName);
            Check(inventory.Count == 3, $"inventory found {inventory.Count} files");

            foreach (var (name, content) in cases)
            {
                var receiver = new BoygroupDeployer.FileReceiver();
                receiver.Begin(root.FullName, new JObject { ["t"] = "fileBegin", ["path"] = name });

                var sender = new BoygroupDeployer.FileSender(root.FullName, name);
                try
                {
                JObject? message;
                while ((message = sender.NextMessage()) != null)
                {
                    var type = message["t"]?.Value<string>();
                    if (type == "fileChunk")
                        receiver.Chunk(message);
                    else if (type == "fileEnd")
                    {
                        var error = receiver.Finish(message);
                        Check(error == null, $"receiver error for {name}: {error}");
                    }
                }

                Check(sender.Completed, $"sender never completed for {name}");
                }
                finally
                {
                    sender.Dispose();
                }

                Check(receiver.Error == null, $"receiver error for {name}: {receiver.Error}");
                var deployed = File.ReadAllBytes(Path.Combine(root.FullName, name));
                Check(deployed.AsSpan().SequenceEqual(content), $"content mismatch for {name}");
                Check(File.GetLastWriteTimeUtc(Path.Combine(root.FullName, name)) == mtime,
                      $"mtime not restored for {name}");
            }
        }
        finally
        {
            try { root.Delete(recursive: true); }
            catch { /* temp cleanup is best-effort in tests */ }
        }
    }

    private static void TestDeployPathSafety()
    {
        var root = Directory.CreateTempSubdirectory("tixl-path-");
        try
        {
            foreach (var evil in new[] { "../escape.txt", "sub/../../escape.txt", "a/../../b" })
            {
                try
                {
                    BoygroupDeployer.SafeProjectPath(root.FullName, evil);
                    throw new Exception($"path escape accepted: {evil}");
                }
                catch (System.IO.IOException)
                {
                    // expected
                }
            }

            var ok = BoygroupDeployer.SafeProjectPath(root.FullName, "sub/dir/file.txt");
            Check(ok.Replace('\\', '/').EndsWith("sub/dir/file.txt", StringComparison.Ordinal), "safe path mangled");
        }
        finally
        {
            try { root.Delete(recursive: true); }
            catch { /* best effort */ }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Failover election — needs separate processes (the arbiter is a static singleton).
    // Spawns two `failover-node` children on the same host: multicast loopback makes this work
    // on Windows, Linux and macOS alike. ShowId isolates this run from any other on the machine.
    // ---------------------------------------------------------------------------------------

    private static void TestFailoverElection()
    {
        var showId = $"headless-{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var heartbeatMs = 150;
        using var master = StartNode(showId, priority: 200, heartbeatMs);
        using var backup = StartNode(showId, priority: 100, heartbeatMs);

        // Master becomes ACTIVE, backup settles on Standby (output suppressed).
        var masterActive = WaitForRole(master, "ACTIVE", TimeSpan.FromSeconds(8));
        Check(masterActive, "master never became ACTIVE; events: " + backup.AllEvents);
        var backupStandby = WaitForRole(backup, "Standby", TimeSpan.FromSeconds(8));
        Check(backupStandby, "backup never settled on Standby; events: " + backup.AllEvents);

        // Kill the master → backup promotes within TimeoutFactor × Heartbeat (+ margin).
        var killAt = Stopwatch.StartNew();
        master.Kill();

        var promoted = WaitForRole(backup, "ACTIVE", TimeSpan.FromSeconds(8));
        Check(promoted, $"backup never promoted after master death; events: {backup.AllEvents}");
        var takeoverMs = killAt.ElapsedMilliseconds;

        // Restart the higher-priority master → preemption reclaims Active, backup demotes.
        using var master2 = StartNode(showId, priority: 200, heartbeatMs);
        Check(WaitForRole(master2, "ACTIVE", TimeSpan.FromSeconds(10)), "restarted master never reclaimed; events: " + master2.AllEvents);
        Check(WaitForRole(backup, "Standby", TimeSpan.FromSeconds(10)), "backup never demoted after preempt; events: " + backup.AllEvents);

        Console.WriteLine($"      failover: takeover after {takeoverMs} ms (timeout budget {3 * heartbeatMs} ms)");
    }

    /// <summary>One failover participant: applies the config, streams role changes to stdout.</summary>
    private static int RunFailoverNode(string showId, int priority, int heartbeatMs)
    {
        FailoverArbiter.ApplyConfig(new FailoverConfig
                                        {
                                            Enabled = true,
                                            ShowId = showId,
                                            Priority = priority,
                                            HeartbeatMs = heartbeatMs,
                                            TimeoutFactor = 3,
                                            Preempt = true,
                                        });

        var reportedRole = "";
        var reportedSuppressed = -1;
        while (true)
        {
            Thread.Sleep(100);
            var role = FailoverArbiter.RoleText;
            var suppressed = FailoverArbiter.IsOutputSuppressed ? 1 : 0;
            if (role != reportedRole || suppressed != reportedSuppressed)
            {
                reportedRole = role;
                reportedSuppressed = suppressed;
                Console.WriteLine($"ROLE {role} suppressed={suppressed}");
            }
        }
    }

    private static FailoverNode StartNode(string showId, int priority, int heartbeatMs)
    {
        var self = Environment.ProcessPath
                   ?? throw new Exception("cannot resolve own executable for child spawn");
        var start = new ProcessStartInfo
                        {
                            FileName = self,
                            Arguments = $"failover-node {showId} {priority} {heartbeatMs}",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                        };
        var process = Process.Start(start)
                      ?? throw new Exception("failed to spawn failover node");
        return new FailoverNode(process);
    }

    private static bool WaitForRole(FailoverNode node, string roleSubstring, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            node.Poll();
            if (node.CurrentRole.Contains(roleSubstring, StringComparison.Ordinal))
                return true;
            Thread.Sleep(50);
        }
        return false;
    }

    /// <summary>Async stdout pump for one child node (reading synchronously would deadlock).</summary>
    private sealed class FailoverNode : IDisposable
    {
        public string CurrentRole = "starting";
        public string AllEvents = "";

        private readonly Process _process;
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _events = new();

        public FailoverNode(Process process)
        {
            _process = process;
            process.OutputDataReceived += (_, e) =>
                                              {
                                                  if (e.Data == null)
                                                      return;
                                                  lock (_stdout)
                                                      _stdout.AppendLine(e.Data);
                                              };
            process.ErrorDataReceived += (_, e) =>
                                             {
                                                 if (e.Data == null)
                                                     return;
                                                 lock (_events)
                                                     _events.AppendLine(e.Data);
                                             };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public void Poll()
        {
            string snapshot;
            lock (_stdout)
                snapshot = _stdout.ToString();
            var lastRoleLine = snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                       .LastOrDefault(l => l.StartsWith("ROLE ", StringComparison.Ordinal));
            if (lastRoleLine != null)
                CurrentRole = lastRoleLine["ROLE ".Length..];

            lock (_events)
                AllEvents = _events.ToString();
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill();
                _process.WaitForExit(2000);
            }
            catch
            {
                // already gone
            }
        }

        public void Dispose()
        {
            Kill();
            _process.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static byte[] MakeBlob(int length)
    {
        // Deterministic pseudo-random content: exercise compression-hostile bytes without RNG setup.
        var blob = new byte[length];
        var x = 0x9e3779b9;
        for (var i = 0; i < length; i++)
        {
            x = x * 1664525 + 1013904223;
            blob[i] = (byte)(x >> 24);
        }
        return blob;
    }

    private static (TcpClient, TcpClient) LoopbackPair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var listenPort = ((IPEndPoint)listener.LocalEndpoint!).Port;
        client.Connect(IPAddress.Loopback, listenPort);
        var server = listener.AcceptTcpClient();
        listener.Stop();
        client.NoDelay = true;
        server.NoDelay = true;
        return (client, server);
    }
}
