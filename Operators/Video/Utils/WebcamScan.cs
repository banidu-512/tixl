#nullable enable
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using DirectShowLib;
using FormatType = DirectShowLib.FormatType;

namespace Lib.io.video;

/// <summary>
/// Shared webcam device scanning for the video capture operators: sysfs on Linux (the capture node of each
/// V4L2 device — the one whose `index` is 0), DirectShow plus capability enumeration on Windows. The result
/// is cached per session; <see cref="Invalidate"/> lets a reconnect pick up hot-plugged cameras.
/// </summary>
public static class WebcamScan
{
    private static readonly object _capabilitiesLock = new();
    private static readonly Lock _scanLock = new();
    private static bool _scanned;
    private static List<WebcamWithIndex>? _devices;
    private static Dictionary<string, List<(int Width, int Height, double Fps)>>? _capabilities;

    /// <summary>The devices found by the last scan (empty when none were found or scanning hasn't run).</summary>
    public static IReadOnlyList<WebcamWithIndex> Devices
    {
        get
        {
            Scan();
            return _devices ?? [];
        }
    }

    /// <summary>Resolution/fps capabilities per device name — empty lists on Linux (V4L2 exposes none).</summary>
    public static IReadOnlyList<(int Width, int Height, double Fps)> GetCapabilities(string deviceName)
    {
        Scan();
        lock (_capabilitiesLock)
        {
            if (_capabilities != null && _capabilities.TryGetValue(deviceName, out var caps))
                return caps;
        }

        return [];
    }

    public static bool TryGetIndexForName(string? deviceName, out int index)
    {
        index = -1;
        foreach (var device in Devices)
        {
            if (device.Name != deviceName)
                continue;

            index = device.Index;
            return true;
        }

        return false;
    }

    public static void Invalidate()
    {
        lock (_scanLock)
        {
            _scanned = false;
        }
    }

    public static void Scan()
    {
        lock (_scanLock)
        {
            if (_scanned)
                return;

            _devices = [];
            lock (_capabilitiesLock)
            {
                _capabilities = new Dictionary<string, List<(int Width, int Height, double Fps)>>();
            }

            try
            {
                if (OperatingSystem.IsWindows())
                    ScanWindowsCameras(_devices);
                else if (OperatingSystem.IsLinux())
                    ScanLinuxCameras(_devices);
            }
            catch (Exception e)
            {
                Log.Debug("Failed to scan webcams: " + e.Message);
            }

            _scanned = true;
        }
    }

    private static void ScanLinuxCameras(List<WebcamWithIndex> webcams)
    {
        // V4L2 nodes and their friendly names come from sysfs. A physical camera often exposes several
        // nodes (capture, metadata, output); the capture node is the one whose `index` file is 0.
        // Capabilities are not enumerated here — the resolution/fps dropdowns fall back to defaults and
        // the capture open negotiates directly with the driver.
        const string sysfsRoot = "/sys/class/video4linux";
        if (!Directory.Exists(sysfsRoot))
            return;

        foreach (var node in Directory.GetDirectories(sysfsRoot).Order())
        {
            var nodeName = Path.GetFileName(node);
            if (!nodeName.StartsWith("video", StringComparison.Ordinal) ||
                !int.TryParse(nodeName["video".Length..], out var deviceIndex))
            {
                continue;
            }

            try
            {
                var indexFilePath = Path.Combine(node, "index");
                if (File.Exists(indexFilePath) &&
                    int.TryParse(File.ReadAllText(indexFilePath).Trim(), out var nodeIndex) &&
                    nodeIndex != 0)
                {
                    continue;
                }

                var namePath = Path.Combine(node, "name");
                var name = File.Exists(namePath) ? File.ReadAllText(namePath).Trim() : nodeName;
                webcams.Add(new WebcamWithIndex(name, deviceIndex));
            }
            catch (Exception e)
            {
                Log.Debug($"Failed to inspect V4L2 node {nodeName}: {e.Message}");
            }
        }
    }

    private static void ScanWindowsCameras(List<WebcamWithIndex> webcams)
    {
        var videoInputDevices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        for (var i = 0; i < videoInputDevices.Length; i++)
        {
            var device = videoInputDevices[i];
            webcams.Add(new WebcamWithIndex(device.Name, i));

            var capabilities = new List<(int Width, int Height, double Fps)>();
            try
            {
                if (device.Mon is not IMoniker moniker)
                {
                    Log.Debug($"Moniker is null for device {device.Name}");
                    continue;
                }

                var filterGuid = typeof(IBaseFilter).GUID;
                moniker.BindToObject(null!, null!, ref filterGuid, out var filter);
                if (filter is not IBaseFilter captureFilter)
                {
                    Log.Debug($"Failed to create capture filter for {device.Name}");
                    continue;
                }

                captureFilter.EnumPins(out var enumPins);
                var pins = new IPin[1];
                while (enumPins.Next(1, pins, IntPtr.Zero) == 0)
                {
                    pins[0].QueryPinInfo(out var pinInfo);
                    if (pinInfo.dir == PinDirection.Output)
                    {
                        if (pins[0] is IAMStreamConfig streamConfig)
                        {
                            streamConfig.GetNumberOfCapabilities(out int count, out int size);
                            if (size > 0)
                            {
                                var capsPtr = Marshal.AllocCoTaskMem(size);
                                try
                                {
                                    for (int j = 0; j < count; j++)
                                    {
                                        streamConfig.GetStreamCaps(j, out var mediaType, capsPtr);
                                        if (mediaType != null &&
                                            mediaType.formatType == FormatType.VideoInfo &&
                                            mediaType.formatPtr != IntPtr.Zero)
                                        {
                                            var vih = Marshal.PtrToStructure<VideoInfoHeader>(mediaType.formatPtr)!;
                                            int w = vih.BmiHeader.Width;
                                            int h = vih.BmiHeader.Height;
                                            double fps = vih.AvgTimePerFrame > 0
                                                             ? 10_000_000.0 / vih.AvgTimePerFrame
                                                             : 30.0;
                                            capabilities.Add((w, h, fps));
                                        }

                                        if (mediaType != null)
                                            DsUtils.FreeAMMediaType(mediaType);
                                    }
                                }
                                finally
                                {
                                    Marshal.FreeCoTaskMem(capsPtr);
                                }
                            }
                        }
                    }

                    Marshal.ReleaseComObject(pins[0]);
                }

                Marshal.ReleaseComObject(enumPins);
                Marshal.ReleaseComObject(captureFilter);
            }
            catch (Exception e)
            {
                Log.Debug($"Failed to get capabilities for {device.Name}: {e.Message}");
            }

            lock (_capabilitiesLock)
            {
                _capabilities!.Add(device.Name, capabilities);
            }
        }
    }
}

/// <summary>A capture device by list name and platform device index.</summary>
public sealed record WebcamWithIndex(string Name, int Index);
