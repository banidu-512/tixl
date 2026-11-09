# VideoStreamInput

*in [Lib.io.video](README.md)*

Receives a video stream from a network source.

This operator connects to a network video stream (e.g., RTSP, HTTP, RTMP) and provides the video as a texture. It is useful for bringing in live feeds from IP cameras, streaming servers, or other network video sources.

It can also host a small RTMP server: enable `RtmpServer` and push to the URL shown in `Status` from OBS, a phone streaming app, or ffmpeg. Any stream key is accepted while the server is listening.

Tips:
- Use the `Connect` toggle to start and stop pulling from `Url`.
- Enable `RtmpServer` to receive a pushed RTMP stream instead; this takes precedence over `Connect`/`Url`.
- If the stream is interrupted, use the `Reconnect` trigger to try and establish the connection again.
- The `Status` output provides valuable information for debugging connection issues and shows the RTMP server URL.
- Windows may ask for firewall permission the first time the RTMP server listens.

AKA: video stream, rtsp, rtmp, rtmp server, obs, http, ip camera, network video

## Input Parameters
| Name (Relevancy & Type) | Description |
|---|---|
| **Connect** (Boolean) | Starts or stops pulling the stream from `Url`. |
| **Reconnect** (Boolean) | A trigger to force a reconnection to the video stream. |
| **Url** (String) | The URL of the network video stream (e.g., rtsp://192.168.1.100/stream1 or rtmp://server/app/stream). Ignored while `RtmpServer` is enabled. |
| **RtmpServer** (Boolean) | Enables the built-in RTMP server. It listens on `RtmpPort` and waits for a publisher (e.g. OBS); the listening URL is shown in `Status`. Disable it to stop the server. |
| **RtmpPort** (Int32) | The TCP port the built-in RTMP server listens on. Default is 1935, the standard RTMP port. |

## Outputs
| Name | Type |
|---|---|
| **Texture** | T3.Core.DataTypes.Texture2D |
| **Resolution** | T3.Core.DataTypes.Vector.Int2 |
| **Status** | System.String |

