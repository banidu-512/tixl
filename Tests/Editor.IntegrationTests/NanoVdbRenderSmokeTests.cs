using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TiXL.DebugClient;
using Xunit;
using Xunit.Abstractions;

namespace Editor.IntegrationTests;

/// <summary>
/// Renders a .vdb through the field pipeline in a real editor and screenshots it:
/// LoadVdb reads the shipped bunny, VolumeToNanoVdb flattens it, VdbToSdfField exposes it as a
/// signed-distance field and RaymarchField draws it. The raymarcher itself is pinned, because
/// pinning a compound does not route it to the output window.
///
/// This is the only check that shows the traversal producing pixels. It needs a machine where the
/// fragment shaders compile, which a Windows box without spirv-opt is not.
/// </summary>
[Collection("Editor")]
public sealed class NanoVdbRenderSmokeTests(EditorFixture editor, ITestOutputHelper output)
{
    [Fact]
    public void TheBunnyRendersThroughTheFieldPipeline()
    {
        var client = editor.Client;
        EnsureAGraphIsOpen(client);
        var since = client.GetLatestLogSeq();

        var load = client.AddOp("LoadVdb", 0, 0);
        client.SetInput(load, "Path", JToken.FromObject("Examples:volumes/bunny.vdb"));

        var toNano = client.AddOp("VolumeToNanoVdb", 300, 0);
        client.ConnectOps(load, "Volume", toNano, "Volume");

        var sdf = client.AddOp("VdbToSdfField", 600, 0);

        // The field reports "distance to the box + value" outside the grid, so a generous clamp turns
        // the grid's bounding box into a visible shell that hides the surface inside it. Keep it just
        // above the band so rays stop at the volume instead of at the box.
        client.SetInput(sdf, "OutsideValue", JToken.FromObject(1.5f));
        client.ConnectOps(toNano, "Buffer", sdf, "Grid");
        client.ConnectOps(toNano, "WorldMin", sdf, "BoundsMin");
        client.ConnectOps(toNano, "WorldMax", sdf, "BoundsMax");

        var march = client.AddOp("RaymarchField", 900, 0);
        client.ConnectOps(sdf, "Result", march, "SdfField");

        // Frame the volume: the raymarcher draws into a camera, which renders into a target. Pinning
        // RaymarchField alone renders with the editor's own camera, which sits inside the bunny.
        var camera = client.AddOp("Camera", 0, 300);
        client.SetInput(camera, "Position", JToken.FromObject(new { X = 2.0f, Y = 1.0f, Z = 3.0f }));
        client.SetInput(camera, "Target", JToken.FromObject(new { X = 0f, Y = 0f, Z = 0f }));
        client.ConnectOps(march, "DrawCommand", camera, "Command");

        var target = client.AddOp("RenderTarget", 400, 300);
        client.ConnectOps(camera, "Output", target, "Command");
        client.Pin(target);

        client.PumpFrames(90);

        var shotDir = Path.Combine(Path.GetTempPath(), "tixl-debug");
        Directory.CreateDirectory(shotDir);
        var shot = Path.Combine(shotDir, "nanovdb-bunny.png");
        client.Screenshot(shot);
        client.PumpFrames(10);
        output.WriteLine($"screenshot: {shot} ({new FileInfo(shot).Length} bytes)");

        // Does the frame come from this graph at all? Recolour the raymarcher and compare. A picture
        // that does not change is the project's own scene, not our volume.
        var control = Path.Combine(shotDir, "nanovdb-bunny-red.png");
        client.SetVector4Input(march, "Color", 1f, 0.1f, 0.05f, 1f);
        client.PumpFrames(20);
        client.Screenshot(control);
        client.PumpFrames(10);

        var before = File.ReadAllBytes(shot);
        var after = File.ReadAllBytes(control);
        var changed = !before.AsSpan().SequenceEqual(after);
        output.WriteLine($"frame changes when the raymarcher is recoloured: {changed}");

        foreach (var entry in client.GetLogTail(since, "warning"))
            output.WriteLine($"log {entry["level"]}: {entry["message"]}");

        var errors = client.GetLogTail(since, "error");
        Assert.True(errors.Count == 0, "errors: " + string.Join(" | ", errors.Select(e => e["message"])));
        Assert.True(new FileInfo(shot).Length > 2000, "the screenshot should have content");
    }

    /// <summary>The playground exists on the dev machine; anywhere else make one scratch project, once.</summary>
    private static void EnsureAGraphIsOpen(DebugProtocolClient client)
    {
        // Prefer a project that already exists: a project created by the protocol can leave the
        // editor's playback settings pointing at a composition symbol that is not registered yet,
        // which crashes the render loop. That is an editor bug, unrelated to volumes.
        foreach (var name in new[] { EditorFixture.PlaygroundProject, "TestOPS" })
        {
            try
            {
                client.OpenProject(name).Require(name);
                client.PumpFrames(10);
                return;
            }
            catch (DebugProtocolException)
            {
                // try the next one
            }
        }

        client.Call("newProject", new { name = "artonurban.vdbRender" });
        client.PumpFrames(15);
        var opened = client.OpenProject("artonurban.vdbRender");
        opened.Require("scratch project");
        client.PumpFrames(15);
    }
}
