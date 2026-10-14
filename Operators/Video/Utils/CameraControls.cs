#nullable enable
using System.Runtime.InteropServices;
using DirectShowLib;

namespace Lib.io.video;

/// <summary>
/// The standard capture-device controls a camera may expose — pan/tilt/zoom for PTZ and gimbal cameras,
/// focus/iris/exposure for optics, and the processing controls (brightness, contrast, ...) for regular
/// webcams. One surface, two backends: V4L2 controls on Linux, DirectShow's IAMCameraControl /
/// IAMVideoProcAmp on Windows. This is the cross-platform replacement for the old DirectShow settings
/// dialog that went away with the FFmpeg capture port.
/// </summary>
public enum CameraControl
{
    Pan,
    Tilt,
    Roll,
    Zoom,
    Focus,
    Iris,
    Exposure,
    Brightness,
    Contrast,
    Saturation,
    Hue,
    Gamma,
    Sharpness,
    WhiteBalance,
    BacklightCompensation,
    Gain,
}

/// <summary>The device-reported (or convention-estimated) range of a control, in device units.</summary>
public readonly record struct CameraControlRange(int Minimum, int Maximum, int Step, int Default)
{
    public bool Contains(int value) => value >= Minimum && value <= Maximum;
}

public static class CameraControls
{
    /// <summary>
    /// Whether the device supports the control, and its range. Ranges are device-reported where the driver
    /// implements the query; devices that don't (e.g. the DJI Pocket 3 implements pan/tilt/zoom but not the
    /// query) fall back to the ranges the UVC class conventions define, flagged as estimated.
    /// Results are cached per device — call <see cref="InvalidateCache"/> when devices change.
    /// </summary>
    public static bool IsSupported(int deviceIndex, CameraControl control, out CameraControlRange range)
    {
        var key = (deviceIndex, control);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                range = cached ?? default;
                return cached.HasValue;
            }
        }

        var supported = OperatingSystem.IsWindows()
                            ? IsSupportedWindows(deviceIndex, control, out range)
                            : IsSupportedLinux(deviceIndex, control, out range, out _);

        lock (_cacheLock)
        {
            _cache[key] = supported ? range : null;
        }

        return supported;
    }

    public static bool TryGetValue(int deviceIndex, CameraControl control, out int value)
    {
        value = 0;
        if (OperatingSystem.IsWindows())
            return TryGetValueWindows(deviceIndex, control, out value);

        return TryOperateLinux(deviceIndex, control, query: false, ref value);
    }

    public static bool TrySetValue(int deviceIndex, CameraControl control, int value)
    {
        if (OperatingSystem.IsWindows())
            return TrySetValueWindows(deviceIndex, control, value);

        var dummy = value;
        return TryOperateLinux(deviceIndex, control, query: false, ref dummy, writeValue: value);
    }

    /// <summary>True when the range came from UVC class conventions rather than a device query.</summary>
    public static bool IsRangeEstimated(int deviceIndex, CameraControl control)
    {
        return !OperatingSystem.IsWindows()
               && IsSupportedLinux(deviceIndex, control, out _, out var estimated)
               && estimated;
    }

    public static void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
        }
    }

    public static string GetName(CameraControl control) => control.ToString();

    // ---------------------------------------------------------------- Linux (V4L2)

    // ioctl request numbers via the kernel's _IOWR('V', nr, struct) macro — computed, not hand-hexed (a
    // hand-converted constant cost an afternoon: 0x27 is 39, not 27).
    private static ulong Iowr(byte number, uint structSize)
        => (3UL << 30) | ((ulong)structSize << 16) | (0x56UL << 8) | number;

    private static readonly ulong VidIocQueryCtrl = Iowr(36, 64); // struct v4l2_queryctrl
    private static readonly ulong VidIocGCtrl = Iowr(27, 8);      // struct v4l2_control
    private static readonly ulong VidIocSCtrl = Iowr(28, 8);
    private const int O_RDWR = 2;

    // V4L2_CID_* — user class (0x00980900 = BASE) and camera class (0x009A0900 = CAMERA_CLASS_BASE).
    private static readonly Dictionary<CameraControl, uint> _v4l2Ids = new()
                                                                          {
                                                                              [CameraControl.Pan] = 0x009A0908,
                                                                              [CameraControl.Tilt] = 0x009A0909,
                                                                              [CameraControl.Roll] = 0x009A0912,
                                                                              [CameraControl.Zoom] = 0x009A090D,
                                                                              [CameraControl.Focus] = 0x009A090A,
                                                                              [CameraControl.Iris] = 0x009A0910,
                                                                              [CameraControl.Exposure] = 0x009A0901,
                                                                              [CameraControl.Brightness] = 0x00980900,
                                                                              [CameraControl.Contrast] = 0x00980901,
                                                                              [CameraControl.Saturation] = 0x00980902,
                                                                              [CameraControl.Hue] = 0x00980903,
                                                                              [CameraControl.Gamma] = 0x00980908,
                                                                              [CameraControl.Sharpness] = 0x00980911,
                                                                              [CameraControl.WhiteBalance] = 0x00980910,
                                                                              [CameraControl.BacklightCompensation] = 0x00980912,
                                                                              [CameraControl.Gain] = 0x0098090B,
                                                                          };

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct V4L2QueryCtrl
    {
        public uint Id;
        public uint Type;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;

        public int Minimum;
        public int Maximum;
        public int Step;
        public int DefaultValue;
        public uint Flags;
        public uint Reserved0;
        public uint Reserved1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct V4L2Control
    {
        public uint Id;
        public int Value;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref V4L2QueryCtrl arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, ref V4L2Control arg);

    private static bool IsSupportedLinux(int deviceIndex, CameraControl control, out CameraControlRange range, out bool estimated)
    {
        range = default;
        estimated = false;

        if (!_v4l2Ids.TryGetValue(control, out var id))
            return false;

        var query = new V4L2QueryCtrl { Id = id };
        var fd = OpenDevice(deviceIndex);
        if (fd < 0)
            return false;

        try
        {
            if (ioctl(fd, VidIocQueryCtrl, ref query) == 0 && query.Minimum != query.Maximum)
            {
                range = new CameraControlRange(query.Minimum, query.Maximum, Math.Max(query.Step, 1), query.DefaultValue);
                return true;
            }

            // The device doesn't report ranges (some UVC implementations). Fall back to the UVC class
            // conventions if the control at least reads back — that's what makes the DJI usable.
            var probeValue = 0;
            if (TryOperateLinux(deviceIndex, control, query: false, ref probeValue))
            {
                range = GetConventionalRange(control);
                estimated = true;
                return true;
            }

            return false;
        }
        finally
        {
            close(fd);
        }
    }

    private static bool TryOperateLinux(int deviceIndex, CameraControl control, bool query, ref int value, int writeValue = 0)
    {
        if (!_v4l2Ids.TryGetValue(control, out var id))
            return false;

        var fd = OpenDevice(deviceIndex);
        if (fd < 0)
            return false;

        try
        {
            var controlStruct = new V4L2Control { Id = id, Value = writeValue };
            var request = query ? VidIocGCtrl : VidIocSCtrl;
            if (ioctl(fd, request, ref controlStruct) != 0)
                return false;

            value = controlStruct.Value;
            return true;
        }
        finally
        {
            close(fd);
        }
    }

    private static int OpenDevice(int deviceIndex)
    {
        // The capture stream opens the same node through FFmpeg; V4L2 controls work through any additional
        // file handle and apply to the device as a whole.
        return open($"/dev/video{deviceIndex}", O_RDWR);
    }

    private static CameraControlRange GetConventionalRange(CameraControl control)
    {
        // UVC class conventions for devices that accept a control but don't report its range.
        return control switch
               {
                   CameraControl.Pan => new CameraControlRange(-11520, 11520, 64, 0),    // 1/64 degree
                   CameraControl.Tilt => new CameraControlRange(-11520, 11520, 64, 0),
                   CameraControl.Roll => new CameraControlRange(-11520, 11520, 64, 0),
                   CameraControl.Zoom => new CameraControlRange(100, 400, 1, 100),        // 100 = 1x
                   CameraControl.Focus => new CameraControlRange(0, 255, 1, 80),
                   CameraControl.Iris => new CameraControlRange(0, 10, 1, 5),
                   CameraControl.Exposure => new CameraControlRange(1, 5000, 1, 166),     // 0.1 ms
                   CameraControl.Brightness => new CameraControlRange(0, 255, 1, 128),
                   CameraControl.Contrast => new CameraControlRange(0, 255, 1, 128),
                   CameraControl.Saturation => new CameraControlRange(0, 255, 1, 64),
                   CameraControl.Hue => new CameraControlRange(-180, 180, 1, 0),
                   CameraControl.Gamma => new CameraControlRange(0, 255, 1, 128),
                   CameraControl.Sharpness => new CameraControlRange(0, 255, 1, 128),
                   CameraControl.WhiteBalance => new CameraControlRange(2500, 6500, 1, 4600), // kelvin
                   CameraControl.BacklightCompensation => new CameraControlRange(0, 2, 1, 0),
                   CameraControl.Gain => new CameraControlRange(0, 128, 1, 32),
                   _ => new CameraControlRange(0, 255, 1, 128),
               };
    }

    // ---------------------------------------------------------------- Windows (DirectShow)

    private static bool IsSupportedWindows(int deviceIndex, CameraControl control, out CameraControlRange range)
    {
        range = default;
        var filter = BindToDevice(deviceIndex);
        if (filter == null)
            return false;

        try
        {
            if (TryMapToCameraControlProperty(control, out var cameraProperty))
            {
                var cameraControl = (IAMCameraControl)filter;
                cameraControl.GetRange(cameraProperty, out var min, out var max, out var step, out var defaultValue, out _);
                range = new CameraControlRange(min, max, Math.Max(step, 1), defaultValue);
                return true;
            }

            if (TryMapToVideoProcAmpProperty(control, out var procAmpProperty))
            {
                var procAmp = (IAMVideoProcAmp)filter;
                procAmp.GetRange(procAmpProperty, out var min, out var max, out var step, out var defaultValue, out _);
                range = new CameraControlRange(min, max, Math.Max(step, 1), defaultValue);
                return true;
            }

            return false;
        }
        catch
        {
            return false; // E_PROP_ID_UNSUPPORTED, device gone, etc.
        }
        finally
        {
            Marshal.ReleaseComObject(filter);
        }
    }

    private static bool TryGetValueWindows(int deviceIndex, CameraControl control, out int value)
    {
        value = 0;
        var filter = BindToDevice(deviceIndex);
        if (filter == null)
            return false;

        try
        {
            if (TryMapToCameraControlProperty(control, out var cameraProperty))
                return ((IAMCameraControl)filter).Get(cameraProperty, out value, out _) == 0;

            if (TryMapToVideoProcAmpProperty(control, out var procAmpProperty))
                return ((IAMVideoProcAmp)filter).Get(procAmpProperty, out value, out _) == 0;

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(filter);
        }
    }

    private static bool TrySetValueWindows(int deviceIndex, CameraControl control, int value)
    {
        var filter = BindToDevice(deviceIndex);
        if (filter == null)
            return false;

        try
        {
            if (TryMapToCameraControlProperty(control, out var cameraProperty))
                return ((IAMCameraControl)filter).Set(cameraProperty, value, CameraControlFlags.Manual) == 0;

            if (TryMapToVideoProcAmpProperty(control, out var procAmpProperty))
                return ((IAMVideoProcAmp)filter).Set(procAmpProperty, value, VideoProcAmpFlags.Manual) == 0;

            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(filter);
        }
    }

    private static object? BindToDevice(int deviceIndex)
    {
        var devices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
        if (deviceIndex < 0 || deviceIndex >= devices.Length)
            return null;

        var filterGuid = typeof(IBaseFilter).GUID;
        devices[deviceIndex].Mon.BindToObject(null!, null!, ref filterGuid, out var filter);
        return filter;
    }

    private static bool TryMapToCameraControlProperty(CameraControl control, out CameraControlProperty property)
    {
        switch (control)
        {
            case CameraControl.Pan: property = CameraControlProperty.Pan; return true;
            case CameraControl.Tilt: property = CameraControlProperty.Tilt; return true;
            case CameraControl.Roll: property = CameraControlProperty.Roll; return true;
            case CameraControl.Zoom: property = CameraControlProperty.Zoom; return true;
            case CameraControl.Exposure: property = CameraControlProperty.Exposure; return true;
            case CameraControl.Iris: property = CameraControlProperty.Iris; return true;
            case CameraControl.Focus: property = CameraControlProperty.Focus; return true;
            default: property = default; return false;
        }
    }

    private static bool TryMapToVideoProcAmpProperty(CameraControl control, out VideoProcAmpProperty property)
    {
        switch (control)
        {
            case CameraControl.Brightness: property = VideoProcAmpProperty.Brightness; return true;
            case CameraControl.Contrast: property = VideoProcAmpProperty.Contrast; return true;
            case CameraControl.Hue: property = VideoProcAmpProperty.Hue; return true;
            case CameraControl.Saturation: property = VideoProcAmpProperty.Saturation; return true;
            case CameraControl.Sharpness: property = VideoProcAmpProperty.Sharpness; return true;
            case CameraControl.Gamma: property = VideoProcAmpProperty.Gamma; return true;
            case CameraControl.WhiteBalance: property = VideoProcAmpProperty.WhiteBalance; return true;
            case CameraControl.BacklightCompensation: property = VideoProcAmpProperty.BacklightCompensation; return true;
            case CameraControl.Gain: property = VideoProcAmpProperty.Gain; return true;
            default: property = default; return false;
        }
    }

    private static readonly object _cacheLock = new();
    private static readonly Dictionary<(int DeviceIndex, CameraControl Control), CameraControlRange?> _cache = new();
}
