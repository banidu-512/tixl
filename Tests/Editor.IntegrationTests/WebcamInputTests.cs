using Newtonsoft.Json.Linq;
using TiXL.DebugClient;
using Xunit;

namespace Editor.IntegrationTests;

/// <summary>
/// Drives a real camera through [WebcamInput] for one operator's whole lifecycle: frames arrive, stopping
/// releases the capture thread and keeps the frame counter quiet, repeated start/stop cycles stay clean,
/// and a reconnect resumes capture. Runs in the _agentTests playground and leaves it at its baseline.
/// <para>
/// One operator instance on purpose: after roughly five rapid activation cycles this camera stops being
/// listed by SDL and capture never recovers (see the known-issue note in .tests-manual/webcam-input.md),
/// which is a separate question from what this test asserts.
/// </para>
/// <para>
/// Needs a connected camera. On a machine without one the status reports that no camera was found, and
/// the test returns without failing so build agents without hardware still pass — but any other failure
/// mode (denied access, a crashed capture thread, a stalled counter, an error status) still fails.
/// </para>
/// </summary>
[Collection("Editor")]
public sealed class WebcamInputTests(EditorFixture editor)
{
    [Fact]
    public void CapturesStopsCyclesAndReconnects()
    {
        var client = editor.Client;
        var baseline = editor.OpenPlayground();
        var baselineLogSeq = client.GetLatestLogSeq();

        var camera = client.AddOp("WebcamInput", posX: -600);
        try
        {
            client.Pin(camera);

            // Opening: frames must arrive and the status must settle on the size they arrive in.
            client.SetInput(camera, "Active", JToken.FromObject(true));
            var status = WaitForStatus(client, camera, IsRunning, out var frameCount);
            if (IsMissingCamera(status))
                return;

            Assert.True(frameCount > 0, $"no frames arrived; Status='{status}'");

            // Stopping must report a clean stop. The capture thread's exit path used to overwrite this with
            // "Error: Camera disconnected." because closing the stream looks like a device loss to it.
            StopAndExpectQuietCounter(client, camera);

            // Reconnect is edge-triggered and resets itself; capture must settle back to running and keep
            // counting without a second activation.
            client.SetInput(camera, "Active", JToken.FromObject(true));
            WaitForStatus(client, camera, IsRunning, out var beforeReconnect);
            client.SetInput(camera, "Reconnect", JToken.FromObject(true));
            client.PumpFrames(5);

            var resumed = WaitForStatus(client, camera, IsRunning, out var afterReconnect);
            Assert.True(afterReconnect > beforeReconnect,
                        $"capture did not resume after reconnect (stuck at {beforeReconnect}); Status='{resumed}'");
            StopAndExpectQuietCounter(client, camera);

            // One more cycle: a capture thread that has not exited yet must not be joined by a second one
            // for the same camera.
            client.SetInput(camera, "Active", JToken.FromObject(true));
            WaitForStatus(client, camera, IsRunning, out _);
            StopAndExpectQuietCounter(client, camera);

            var errors = client.GetLogTail(sinceSeq: baselineLogSeq, minLevel: "error");
            Assert.True(errors.Count == 0, "errors during run: " + string.Join(" | ", errors.Select(e => e["message"])));
        }
        finally
        {
            client.SetInput(camera, "Active", JToken.FromObject(false));
            RestoreBaseline(client, baseline);
        }
    }

    private static void StopAndExpectQuietCounter(DebugProtocolClient client, Guid camera)
    {
        client.SetInput(camera, "Active", JToken.FromObject(false));
        var stopped = WaitForStatus(client, camera, s => s == "Stopped", out _);
        Assert.Equal("Stopped", stopped);

        // A capture thread that ignored the cancellation would keep publishing frames here.
        var stoppedCount = ReadFrameCount(client, camera);
        client.PumpFrames(30);
        Assert.Equal(stoppedCount, ReadFrameCount(client, camera));
    }

    private static bool IsRunning(string status) => status.StartsWith("Running at ", StringComparison.Ordinal);

    private static bool IsMissingCamera(string status)
        => status.Contains("No camera found", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pumps frames until the status is accepted, the camera turns out to be missing, or time runs out.</summary>
    private static string WaitForStatus(DebugProtocolClient client, Guid camera, Func<string, bool> accept, out int frameCount,
                                        int attempts = 250)
    {
        var status = string.Empty;
        frameCount = 0;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            client.PumpFrames(2);
            status = ReadText(client, camera, "Status") ?? string.Empty;
            frameCount = ReadFrameCount(client, camera);
            if (accept(status) || IsMissingCamera(status))
                break;
        }

        return status;
    }

    private static string? ReadText(DebugProtocolClient client, Guid camera, string outputName)
    {
        var response = client.Call("getOutput", new { childId = camera, outputName });
        return response.Ok ? response.Result?["value"]?.ToString() : null;
    }

    private static int ReadFrameCount(DebugProtocolClient client, Guid camera)
        => int.TryParse(ReadText(client, camera, "UpdateCount"), out var count) ? count : 0;

    private static void RestoreBaseline(DebugProtocolClient client, (int Children, int Connections) baseline)
    {
        for (var i = 0; i < 10; i++)
        {
            var state = client.GetGraphState();
            if (state["children"]!.Count() == baseline.Children && state["connections"]!.Count() == baseline.Connections)
                return;

            client.Undo();
            client.PumpFrames(3);
        }
    }
}
