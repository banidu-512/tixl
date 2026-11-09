using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using T3.Core.DataTypes.Volume;
using T3.Core.DataTypes.Volume.IO;
using Xunit;
using Xunit.Abstractions;

namespace Core.Tests.Volume;

/// <summary>
/// Cross-checks the NanoVDB layer against the reference implementation's own tools.
///
/// A round trip through our own reader and writer proves only that the two agree with each other -
/// they can be wrong together. These tests use nanovdb_convert, nanovdb_validate and nanovdb_print
/// from the OpenVDB distribution, so the comparison is between this code and the library the format
/// is defined by.
///
/// Opt-in, because it needs two things a build machine usually lacks:
///   TIXL_NANOVDB_TOOLS - folder containing usr/bin/nanovdb_convert, nanovdb_validate and nanovdb_print
///                        (extracting the Debian libnanovdb-tools package is enough, no install needed)
///   TIXL_VDB_SAMPLES   - folder with real .vdb files (bunny.vdb, smoke.vdb, ...)
/// Silent when either variable is unset.
/// </summary>
public class NanoVdbReferenceToolTests(ITestOutputHelper output)
{
    [Fact]
    public void ReferenceToolsAcceptTheBufferWeWrite()
    {
        if (!TryPrepare(out var sample, out var tools))
            return;

        var grid = LoadFirstFloatGrid(sample);
        Assert.NotNull(grid);

        var path = Path.Combine(Path.GetTempPath(), $"tixl-ref-{Guid.NewGuid():N}.nvdb");
        try
        {
            NanoVdbBuffer.FromGrid(grid!).Save(path);

            var validate = Run(tools, "nanovdb_validate", path);
            output.WriteLine(validate.Output);
            Assert.True(validate.ExitCode == 0, $"nanovdb_validate rejected our buffer:\n{validate.Output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OurReaderAcceptsTheReferenceConvertersOutput()
    {
        if (!TryPrepare(out var sample, out var tools))
            return;

        var path = Path.Combine(Path.GetTempPath(), $"ref-{Guid.NewGuid():N}.nvdb");
        try
        {
            var convert = Run(tools, "nanovdb_convert", sample, path);
            output.WriteLine(convert.Output);
            if (convert.ExitCode != 0)
            {
                output.WriteLine("Skipped: nanovdb_convert could not convert the sample.");
                return;
            }

            var buffer = NanoVdbBuffer.LoadFile(path);

            Assert.True(buffer.TryValidate(out var error), $"our validator rejected the reference file: {error}");
            var info = buffer.ReadInfo();
            output.WriteLine($"{info.Name}: {info.VoxelCount} voxels, {info.LeafNodes} leaves, voxel size {info.VoxelSize}");

            Assert.True(info.VoxelCount > 0, "reference file reported no active voxels");
            Assert.True(info.LeafNodes > 0, "reference file reported no leaves");

            // Same grid, read two ways: the voxel count has to agree with what our .vdb parser saw.
            var ourGrid = LoadFirstFloatGrid(sample);
            if (ourGrid != null)
            {
                var ourCount = NanoVdbBuffer.FromGrid(ourGrid).ReadInfo().VoxelCount;
                Assert.Equal(ourCount, info.VoxelCount);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BothWritersProduceTheSameVoxelCount()
    {
        if (!TryPrepare(out var sample, out var tools))
            return;

        var referencePath = Path.Combine(Path.GetTempPath(), $"ref-{Guid.NewGuid():N}.nvdb");
        var ourPath = Path.Combine(Path.GetTempPath(), $"our-{Guid.NewGuid():N}.nvdb");
        try
        {
            if (Run(tools, "nanovdb_convert", sample, referencePath).ExitCode != 0)
            {
                output.WriteLine("Skipped: nanovdb_convert could not convert the sample.");
                return;
            }

            var grid = LoadFirstFloatGrid(sample);
            Assert.NotNull(grid);
            NanoVdbBuffer.FromGrid(grid!).Save(ourPath);

            var reference = NanoVdbBuffer.LoadFile(referencePath).ReadInfo();
            var ours = NanoVdbBuffer.LoadFile(ourPath).ReadInfo();

            output.WriteLine($"reference: {reference.VoxelCount} voxels, {reference.LeafNodes} leaves");
            output.WriteLine($"ours:      {ours.VoxelCount} voxels, {ours.LeafNodes} leaves");

            Assert.Equal(reference.VoxelCount, ours.VoxelCount);
            Assert.Equal(reference.LeafNodes, ours.LeafNodes);
            Assert.Equal(reference.VoxelSize, ours.VoxelSize, 4);
        }
        finally
        {
            File.Delete(referencePath);
            File.Delete(ourPath);
        }
    }

    private static VdbGrid<float> LoadFirstFloatGrid(string path)
    {
        var volume = VdbFile.Load(path);
        return volume.GetFloatGrid();
    }

    private bool TryPrepare(out string sample, out string tools)
    {
        sample = null;
        tools = null;

        var toolsFolder = Environment.GetEnvironmentVariable("TIXL_NANOVDB_TOOLS");
        var samplesFolder = Environment.GetEnvironmentVariable("TIXL_VDB_SAMPLES");

        if (string.IsNullOrEmpty(toolsFolder) || string.IsNullOrEmpty(samplesFolder))
        {
            output.WriteLine("Skipped: set TIXL_NANOVDB_TOOLS and TIXL_VDB_SAMPLES to run this.");
            return false;
        }

        var binary = Path.Combine(toolsFolder, "nanovdb_validate");
        if (!File.Exists(binary))
        {
            var alt = Path.Combine(toolsFolder, "usr", "bin", "nanovdb_validate");
            if (!File.Exists(alt))
            {
                output.WriteLine($"Skipped: no nanovdb_validate in {toolsFolder}.");
                return false;
            }

            tools = Path.Combine(toolsFolder, "usr", "bin");
        }
        else
        {
            tools = toolsFolder;
        }

        var candidate = Directory.EnumerateFiles(samplesFolder, "*.vdb").OrderBy(x => x).FirstOrDefault();
        if (candidate == null)
        {
            output.WriteLine($"Skipped: no .vdb files in {samplesFolder}.");
            return false;
        }

        sample = candidate;
        return true;
    }

    private static (int ExitCode, string Output) Run(string toolsFolder, string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(toolsFolder, tool))
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                        };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        // The extracted package's libraries are not installed system-wide.
        var libraries = Path.Combine(Path.GetDirectoryName(toolsFolder)!, "lib", "x86_64-linux-gnu");
        if (Directory.Exists(libraries))
            start.Environment["LD_LIBRARY_PATH"] = libraries;

        using var process = Process.Start(start);
        if (process == null)
            return (-1, "could not start " + tool);

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return (process.ExitCode, stdout + stderr);
    }
}
