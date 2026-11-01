# VideoDeviceInput

*in [Lib.io.video](README.md)*

Captures live video from a connected device like a webcam or capture card, with the basic camera hardware
configs built in: Pan, Tilt, Zoom, Focus, Exposure and Brightness.

Devices are opened through FFmpeg — DirectShow on Windows, V4L2 on Linux — so the operator works on both
platforms. You can select the device, resolution, and frame rate; the capture runs on its own thread and
automatically retries when a device is unplugged and returns. Basic image manipulation like flipping,
rotation, scaling and repositioning is done in software.

Tips:
- To save system resources, enable `DeactivateWhenNotShowing`. This will pause the capture when the operator is not being rendered.
- The config inputs apply to the camera automatically whenever their value changes — several settings at once, each patchable/animatable. Values are in device units (Zoom 100 = 1x, Pan/Tilt in 1/64°); inputs the camera doesn't implement are ignored, and `Status` shows the supported range after an apply.
- On Linux the resolution and frame-rate dropdowns only list defaults; with `ResolutionFpsType` set to `Custom`, type any value — the driver negotiates the closest match and the image is resized in software when the device can't deliver the requested size.
- A `Reconnect` trigger also rescans the device list, so cameras plugged in after startup become selectable.
- The `Status` output provides useful information for troubleshooting connection issues.

AKA: webcam, camera, capture card, ptz, pan, tilt, zoom, video input

## Input Parameters
| Name (Relevancy & Type) | Description |
|---|---|
| **Active** (Boolean) | Enables or disables the video capture. |
| **ApplyRotationData** (Single) | Applies a rotation to the video image in degrees. This is used for correcting orientation. |
| **Brightness** (Single) | Camera brightness in device units. Applied when the value changes; ignored if the camera has no such control. |
| **Exposure** (Single) | Camera exposure in device units (UVC absolute exposure: 0.1 ms). Applied when the value changes. |
| **Focus** (Single) | Camera focus in device units (absolute). Applied when the value changes. |
| **Pan** (Single) | Camera pan in device units (UVC: 1/64°). Applied when the value changes. |
| **Tilt** (Single) | Camera tilt in device units (UVC: 1/64°). Applied when the value changes. |
| **Zoom** (Single) | Camera zoom in device units (UVC: 100 = 1x). Applied when the value changes. |
| **CustomFps** (Int32) | Manually specify a frame rate. This will only work if the device and its driver support the requested FPS. Default is 0 (device default). |
| **CustomResolution** (Int2) | Manually specify a resolution (width and height). This will only work if the device and its driver support the requested resolution. Default is 0,0 (device default). |
| **DeactivateWhenNotShowing** (Boolean) | If enabled, the device is automatically deactivated when its output is not being rendered, which can save significant system resources. |
| **FlipHorizontally** (Boolean) | Flips the incoming video image horizontally. |
| **FlipVertically** (Boolean) | Flips the incoming video image vertically. |
| **InputDeviceName** (String) | The name of the video capture device to use. The dropdown lists all devices detected on the system. |
| **OpenSettings** (Boolean) | Reports a warning — the native driver-settings dialog is not supported by the FFmpeg capture backend. Configure the camera with its vendor tool instead. |
| **Reconnect** (Boolean) | A trigger to force a reconnection to the video device, which can be useful if the device becomes unresponsive or if settings have been changed externally. |
| **Reposition** (Vector2) | Applies a 2D translation (X,Y) to the video image. Part of the transformation. |
| **ResolutionFpsType** (Int32) | Selects how resolution and FPS are determined. 'Custom' allows manual input, otherwise device defaults are used. |
| **Scale** (Vector2) | Scales the video image. Part of the `Reposition` transform. Default is 1,1 (no scaling). |

## Outputs
| Name | Type |
|---|---|
| **Texture** | T3.Core.DataTypes.Texture2D |
| **Resolution** | T3.Core.DataTypes.Vector.Int2 |
| **Status** | System.String |
| **UpdateCount** | System.Int32 |

