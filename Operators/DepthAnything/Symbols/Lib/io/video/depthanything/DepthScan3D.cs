
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Operator.Interfaces;
using T3.Core.Resource;
using T3.Core.Resource.Assets;
using T3.Core.Utils;
using T3.Graphics;
using T3.Graphics.Compat;

#pragma warning disable CS8981

namespace Lib.io.video.depthanything;

[Guid("7e6eb18c-433f-41c0-8443-6a82dae72703")]
public sealed class DepthScan3D : Instance<DepthScan3D>, IStatusProvider, ICustomDropdownHolder
{
    #region Outputs

    [Output(Guid = "9165831a-1b50-44f2-a5e3-c88c5e630423", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<BufferWithViews?> Points = new();

    [Output(Guid = "29fc10f7-e7a9-49a7-abca-62b5105b3914", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> PointCount = new();

    [Output(Guid = "63df221c-b771-430e-bad7-ea252018c1ff", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> Captures = new();

    [Output(Guid = "077366c4-089f-4bfe-ae55-72ec76dbb0be", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> PointsAdded = new();

    [Output(Guid = "9b45f9d2-07d1-4385-b448-9ff857382e8d")]
    public readonly Slot<string> SavedPath = new();

    [Output(Guid = "b19a2b3c-4d5e-4f60-a1b2-c3d4e5f6a7b8", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<bool> Recording = new();

    #endregion

    public DepthScan3D()
    {
        Points.UpdateAction = Update;
        PointCount.UpdateAction = Update;
        Captures.UpdateAction = Update;
        PointsAdded.UpdateAction = Update;
        SavedPath.UpdateAction = Update;
        Recording.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        HandleReset(context);
        HandleRecordTriggers(context);
        HandleSave(context);
        Recording.Value = _recording;

        var depthTexture = DepthMap.GetValue(context);
        if (depthTexture == null || depthTexture.IsDisposed)
        {
            _depthPending = false;
            _confPending = false;
            _colorPending = false;
            if (_points.Count == 0)
                SetStatus("Connect a depth map (e.g. DepthAnything's DepthTexture) and enable Capture.", IStatusProvider.StatusLevel.Notice);
            return;
        }

        var desc = depthTexture.Description;
        if (desc.Format != Format.R32_Float)
        {
            if (!_loggedWrongFormat)
            {
                _loggedWrongFormat = true;
                Log.Warning("[DepthScan3D] Unsupported depth format; expected R32_Float.", this);
            }

            SetStatus("Unsupported depth format; connect a R32_Float depth map (DepthAnything's DepthTexture).", IStatusProvider.StatusLevel.Warning);
            return;
        }

        _uiMode = Mode.GetValue(context);
        _uiSaveFormat = SaveFormatInput.GetValue(context);

        var capture = Capture.GetValue(context);
        var captureOnce = MathUtils.WasTriggered(CaptureOnce.GetValue(context), ref _captureOnceState);
        if (captureOnce)
        {
            CaptureOnce.SetTypedInputValue(false);
            _captureOnceState = false;
        }

        var lifetime = Math.Max(0f, SampleLifetime.GetValue(context));
        if (lifetime > 0f && _points.Count > 0 && ExpirePoints(lifetime))
            return;

        // Complete the previous capture's readback and merge it before starting a new one.
        if (_depthPending)
        {
            if (!TryMapStaging(ref _depthPending, _depthStaging, ref _waitFrames))
                return;

            var confMapped = true;
            if (_confPending && !TryMapStaging(ref _confPending, _confStaging, ref _confWaitFrames))
                confMapped = false;

            var colorMapped = true;
            if (_colorPending && !TryMapStaging(ref _colorPending, _colorStaging, ref _colorWaitFrames))
                colorMapped = false;

            MergeScan(confMapped, colorMapped);
            _confPending = false;
            _colorPending = false;
            _mappedDepth = null;
            _mappedConf = null;
            _mappedColor = null;

            // Capture ended while the copy was in flight: the merge above was valid, now hold.
            if (!capture && !captureOnce && !_recording)
            {
                UpdateStatus();
                return;
            }
        }

        if (!capture && !captureOnce && !_recording)
        {
            UpdateStatus();
            return;
        }

        var confidence = Confidence.GetValue(context);
        var useConfidence = confidence is { IsDisposed: false }
                            && confidence.Description.Format == Format.R32_Float
                            && confidence.Description.Width == desc.Width
                            && confidence.Description.Height == desc.Height;
        if (confidence is { IsDisposed: false } && !useConfidence && !_loggedConfMismatch)
        {
            _loggedConfMismatch = true;
            Log.Warning("[DepthScan3D] Confidence texture ignored; size or format differs from the depth map.", this);
        }

        var colorTexture = ColorMap.GetValue(context);
        var useColor = colorTexture is { IsDisposed: false }
                       && colorTexture.Description.Format == Format.R8G8B8A8_UNorm
                       && colorTexture.Description.Width == desc.Width
                       && colorTexture.Description.Height == desc.Height;
        if (colorTexture is { IsDisposed: false } && !useColor && !_loggedColorMismatch)
        {
            _loggedColorMismatch = true;
            Log.Warning("[DepthScan3D] ColorMap ignored; size or format differs from the depth map.", this);
        }

        GetOrCreateStaging(desc.Width, desc.Height);
        var deviceContext = ResourceManager.Device.ImmediateContext;
        deviceContext.CopyResource(depthTexture, _depthStaging);
        _depthPending = true;
        _confPending = false;
        _colorPending = false;

        if (useConfidence)
        {
            deviceContext.CopyResource(confidence, _confStaging);
            _confPending = true;
        }

        if (useColor)
        {
            deviceContext.CopyResource(colorTexture, _colorStaging);
            _colorPending = true;
        }

        _waitFrames = 0;
        _confWaitFrames = 0;
        _colorWaitFrames = 0;
        _pendingThreshold = Math.Clamp(ConfidenceThreshold.GetValue(context), 0f, 1f);
        _pendingMode = (MergeMode)Math.Clamp(Mode.GetValue(context), 0, 1);
        _pendingScale = Math.Max(1e-6f, PointScale.GetValue(context));
        _pendingVoxelSize = Math.Max(1e-5f, VoxelSize.GetValue(context));
        _pendingStride = Math.Clamp(PixelStride.GetValue(context), 1, 64);
        _pendingMaxPoints = Math.Max(1000, MaxPoints.GetValue(context));
        PrepareCamera(context, desc.Width, desc.Height);
        UpdateStatus();
    }

    /// <summary>
    /// Sets up the camera-to-world transform and frustum half-tangents for the next merge. With a
    /// camera connected (DepthAnything's CamReference implements ICamera) points land in world space;
    /// without one, points use DA3-style camera space with +Z forward and y up.
    /// </summary>
    private void PrepareCamera(EvaluationContext context, int width, int height)
    {
        _pendingCamToWorld = System.Numerics.Matrix4x4.Identity;
        _pendingCamIsPose = false;
        var tanHalfY = MathF.Tan(Math.Clamp(FieldOfView.GetValue(context), 1f, 179f) * MathF.PI / 360f);
        var aspect = Math.Max(width, 1) / (float)Math.Max(height, 1);
        _pendingTanHalfX = tanHalfY * aspect;
        _pendingTanHalfY = tanHalfY;

        var camObj = CameraReference.GetValue(context);
        if (camObj is ICamera camera)
        {
            var worldToCamera = camera.WorldToCamera;
            var clip = camera.CameraToClipSpace;
            if (worldToCamera != default
                && clip.M11 > 1e-6f && clip.M22 > 1e-6f
                && System.Numerics.Matrix4x4.Invert(worldToCamera, out var camToWorld))
            {
                _pendingCamToWorld = camToWorld;
                _pendingTanHalfX = 1f / clip.M11;
                _pendingTanHalfY = 1f / clip.M22;
                _pendingCamIsPose = true;
            }
        }
    }

    /// <summary>
    /// Merges one completed readback into the world-space scan: every valid pixel is unprojected to
    /// world space and inserted into the voxel hash, where overlapping observations are averaged or
    /// replaced. Runs single-threaded; PixelStride limits the per-capture cost.
    /// </summary>
    private unsafe void MergeScan(bool confMapped, bool colorMapped)
    {
        if (_mappedDepth is not { SizeInBytes: > 0 })
            return;

        var width = _stagingWidth;
        var height = _stagingHeight;
        if (width <= 0 || height <= 0)
            return;

        var stride = _pendingStride;
        var threshold = _pendingThreshold;
        var hasConfidence = confMapped && _mappedConf is { SizeInBytes: > 0 };
        var hasColor = colorMapped && _mappedColor is { SizeInBytes: > 0 };
        var mode = _pendingMode;
        var now = (float)_scanClock.Elapsed.TotalSeconds;
        var pointsAdded = 0;
        var skippedFull = 0;

        var mapping = _mappedDepth!.Value;
        MappedMemory mappedConf = hasConfidence ? _mappedConf.Value : default;
        MappedMemory mappedColor = hasColor ? _mappedColor.Value : default;

        for (var y = 0; y < height; y += stride)
        {
            var row = (float*)((byte*)mapping.Data + (long)y * mapping.RowPitch);
            var confRow = hasConfidence ? (float*)((byte*)mappedConf.Data + (long)y * mappedConf.RowPitch) : null;
            var colorRow = hasColor ? (byte*)((byte*)mappedColor.Data + (long)y * mappedColor.RowPitch) : null;
            for (var x = 0; x < width; x += stride)
            {
                var z = row[x];
                if (!float.IsFinite(z) || z <= 0f)
                    continue;

                if (confRow != null && confRow[x] < threshold)
                    continue;

                var r = 1f;
                var g = 1f;
                var b = 1f;
                if (colorRow != null)
                {
                    var c = colorRow + x * 4;
                    r = c[0] / 255f;
                    g = c[1] / 255f;
                    b = c[2] / 255f;
                }

                var u = (x + 0.5f) / width;
                var v = (y + 0.5f) / height;
                var ndcX = u * 2f - 1f;
                var ndcY = 1f - v * 2f;
                var tanX = _pendingTanHalfX;
                var tanY = _pendingTanHalfY;
                var depth = z * _pendingScale;
                System.Numerics.Vector3 position;
                if (_pendingCamIsPose)
                {
                    // RH camera space: the view direction is -Z, so flip the depth.
                    var camPoint = new System.Numerics.Vector3(ndcX * tanX * depth, ndcY * tanY * depth, -depth);
                    position = System.Numerics.Vector3.Transform(camPoint, _pendingCamToWorld);
                }
                else
                {
                    position = new System.Numerics.Vector3(ndcX * tanX * depth, ndcY * tanY * depth, depth);
                }

                var inserted = InsertPoint(position, r, g, b, now);
                if (inserted == 1)
                    pointsAdded++;
                else if (inserted == 2)
                    skippedFull++;
            }
        }

        PointsAdded.Value = pointsAdded;
        _captures++;
        Captures.Value = _captures;
        RebuildOutput();
        if (_bufferFullLogged == false && skippedFull > 0)
        {
            _bufferFullLogged = true;
            SetStatus($"Point budget reached ({_points.Count} points); raise MaxPoints to keep scanning.", IStatusProvider.StatusLevel.Warning);
        }
        UpdateStatus();
    }

    /// <summary>
    /// Inserts one world-space sample: quantized to the voxel hash, averaged or replaced when the
    /// voxel is occupied. Returns 1 when a new voxel was created.
    /// </summary>
    private int InsertPoint(System.Numerics.Vector3 position, float r, float g, float b, float now)
    {
        var vs = _pendingVoxelSize;
        var vx = (int)MathF.Floor(position.X / vs);
        var vy = (int)MathF.Floor(position.Y / vs);
        var vz = (int)MathF.Floor(position.Z / vs);
        var key = PackVoxelKey(vx, vy, vz);
        if (_voxelIndex.TryGetValue(key, out var index))
        {
            var sp = _points[index];
            sp.Count++;
            sp.LastSeen = now;
            if (_pendingMode == MergeMode.Average)
            {
                var k = sp.Count;
                sp.Position += (position - sp.Position) / k;
                sp.Color += (new System.Numerics.Vector3(r, g, b) - sp.Color) / k;
            }
            else
            {
                sp.Position = position;
                sp.Color = new System.Numerics.Vector3(r, g, b);
            }

            _points[index] = sp;
            return 0;
        }

        if (_points.Count >= _pendingMaxPoints)
        {
            return 2;
        }

        _points.Add(new ScanPoint { Position = position, Color = new(r, g, b), Count = 1, LastSeen = now });
        _voxelIndex[key] = _points.Count - 1;
        return 1;
    }

    private static long PackVoxelKey(int vx, int vy, int vz)
    {
        long key = ((long)((vx + 0x100000) & 0x1FFFFF) << 42)
                   | ((long)((vy + 0x100000) & 0x1FFFFF) << 21)
                   | (long)((vz + 0x100000) & 0x1FFFFF);
        return key;
    }

    /// <summary>
    /// Drops points not re-observed within the lifetime, rebuilding the voxel index. Returns true
    /// when anything expired (the output was refreshed).
    /// </summary>
    private bool ExpirePoints(float lifetime)
    {
        var now = (float)_scanClock.Elapsed.TotalSeconds;
        var deadline = now - lifetime;
        var expired = 0;
        for (var i = 0; i < _points.Count; i++)
        {
            if (_points[i].LastSeen < deadline)
                expired++;
        }

        if (expired == 0)
            return false;

        var kept = new List<ScanPoint>(_points.Count - expired);
        var index = new Dictionary<long, int>();
        for (var i = 0; i < _points.Count; i++)
        {
            var sp = _points[i];
            if (sp.LastSeen < deadline)
                continue;

            index[PackVoxelKey((int)MathF.Floor(sp.Position.X / _pendingVoxelSize),
                               (int)MathF.Floor(sp.Position.Y / _pendingVoxelSize),
                               (int)MathF.Floor(sp.Position.Z / _pendingVoxelSize))] = kept.Count;
            kept.Add(sp);
        }

        _points = kept;
        _voxelIndex = index;
        RebuildOutput();
        UpdateStatus();
        return true;
    }

    /// <summary>Rebuilds and uploads the drawable point buffer from the scan store.</summary>
    private void RebuildOutput()
    {
        var count = _points.Count;
        if (count == 0)
        {
            Points.Value = null;
            PointCount.Value = 0;
            return;
        }

        var points = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var sp = _points[i];
            points[i] = new Point
            {
                Position = sp.Position,
                Color = new System.Numerics.Vector4(sp.Color, 1f),
                Orientation = System.Numerics.Quaternion.Identity,
                Scale = System.Numerics.Vector3.One,
                F1 = 1,
                F2 = 1,
            };
        }

        EnsurePointBuffer(points, count);
        Points.Value = _pointBuffer;
        PointCount.Value = count;
    }

    private void EnsurePointBuffer(Point[] points, int count)
    {
        var bytesNeeded = count * Point.Stride;
        var currentCount = _pointBuffer?.Buffer != null
                               ? _pointBuffer.Buffer.Description.SizeInBytes / Point.Stride
                               : 0;
        if (count != currentCount)
        {
            _pointBuffer?.Dispose();
            _pointBuffer = null;
        }

        if (_pointBuffer == null)
        {
            _pointBuffer = new BufferWithViews();
            ResourceManager.SetupStructuredBuffer(points, bytesNeeded, Point.Stride, ref _pointBuffer.Buffer);
            ResourceManager.CreateStructuredBufferSrv(_pointBuffer.Buffer, ref _pointBuffer.Srv);
            ResourceManager.CreateStructuredBufferUav(_pointBuffer.Buffer, UnorderedAccessViewBufferFlags.None, ref _pointBuffer.Uav);
            return;
        }

        ResourceManager.Device.ImmediateContext.UpdateSubresource(points, _pointBuffer.Buffer!);
    }

    private void ResetScan()
    {
        _points.Clear();
        _voxelIndex.Clear();
        _captures = 0;
        _pointsAdded = 0;
        _bufferFullLogged = false;
        Points.Value = null;
        PointCount.Value = 0;
        Captures.Value = 0;
        PointsAdded.Value = 0;
        SavedPath.Value = string.Empty;
    }

    /// <summary>One staging texture per source; replaced on size change so buffers don't accumulate.</summary>
    private void GetOrCreateStaging(int width, int height)
    {
        if (_depthStaging is { IsDisposed: false }
            && _depthStaging.Description.Width == width
            && _depthStaging.Description.Height == height)
            return;

        _depthPending = false;
        _confPending = false;
        _colorPending = false;
        _stagingWidth = width;
        _stagingHeight = height;
        _depthStaging?.Dispose();
        _confStaging?.Dispose();
        _colorStaging?.Dispose();
        _depthStaging = CreateStagingTexture(width, height, Format.R32_Float);
        _confStaging = new T3.Graphics.Compat.Texture2D(ResourceManager.Device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1,
            Format = Format.R32_Float, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read, OptionFlags = ResourceOptionFlags.None
        });
        _colorStaging = new T3.Graphics.Compat.Texture2D(ResourceManager.Device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read, OptionFlags = ResourceOptionFlags.None
        });
    }
    /// <summary>
    /// Tries to map a pending staging copy without blocking. Returns false while the copy is still
    /// in flight. A copy that never retires (device reset) is dropped after a timeout.
    /// </summary>
    private bool TryMapStaging(ref bool pending, T3.Graphics.Compat.Texture2D? staging, ref int waitFrames)
    {
        if (staging is not { IsDisposed: false } || staging.Native == null)
        {
            pending = false;
            return true;
        }

        if (!ResourceManager.Device.Backend.TryMapForRead(staging.Native, 0, out var mapped))
        {
            waitFrames++;
            if (waitFrames <= ReadbackTimeoutFrames)
                return false;

            pending = false;
            SetStatus("A depth copy never completed; the held scan is untouched.", IStatusProvider.StatusLevel.Warning);
            return true;
        }

        if (ReferenceEquals(staging, _depthStaging))
            _mappedDepth = mapped;
        else if (ReferenceEquals(staging, _confStaging))
            _mappedConf = mapped;
        else
            _mappedColor = mapped;
        return true;
    }
    private static T3.Graphics.Compat.Texture2D CreateStagingTexture(int width, int height, Format format)
    {
        var description = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None
        };
        return new T3.Graphics.Compat.Texture2D(ResourceManager.Device, description);
    }
    private void HandleReset(EvaluationContext context)
    {
        if (!MathUtils.WasTriggered(Reset.GetValue(context), ref _resetState))
            return;

        Reset.SetTypedInputValue(false);
        _resetState = false;
        ResetScan();
        SetStatus("The scan was cleared.", IStatusProvider.StatusLevel.Notice);
    }
    private void HandleRecordTriggers(EvaluationContext context)
    {
        if (MathUtils.WasTriggered(StartRecord.GetValue(context), ref _startRecordState))
        {
            StartRecord.SetTypedInputValue(false);
            _startRecordState = false;
            _recording = true;
        }

        if (MathUtils.WasTriggered(StopRecord.GetValue(context), ref _stopRecordState))
        {
            StopRecord.SetTypedInputValue(false);
            _stopRecordState = false;
            _recording = false;
        }
    }

    private void HandleSave(EvaluationContext context)
    {
        if (!MathUtils.WasTriggered(SaveTrigger.GetValue(context), ref _saveState))
            return;

        SaveTrigger.SetTypedInputValue(false);
        _saveState = false;

        if (_points.Count == 0)
        {
            SetStatus("No scan points to save; capture first.", IStatusProvider.StatusLevel.Warning);
            SavedPath.Value = string.Empty;
            return;
        }

        var format = (PlyFormat)Math.Clamp(SaveFormatInput.GetValue(context), 0, 1);
        var relativePath = BuildRelativePath(SaveFolder.GetValue(context) ?? string.Empty,
                                             SaveFileName.GetValue(context) ?? string.Empty,
                                             ".ply");

        if (relativePath.Length == 0)
        {
            SaveStatus(string.Empty, "The file name is empty; the scan was not saved.");
            return;
        }

        if (!AssetRegistry.TryResolveAddressForWriting(relativePath, this, out var absolutePath, out var failureReason))
        {
            SaveStatus(string.Empty, $"The scan could not be written: {failureReason}");
            return;
        }

        try
        {
            WritePly(absolutePath, format == PlyFormat.Ascii);
            SavedPath.Value = relativePath;
            SetStatus($"Saved {_points.Count} points ({_captures} captures) to {relativePath}", IStatusProvider.StatusLevel.Success);
        }
        catch (Exception e)
        {
            SaveStatus(string.Empty, $"Saving the scan failed: {e.Message}");
            Log.Error($"[DepthScan3D] Saving {absolutePath} failed: {e.Message}", this);
        }
    }

    private void SaveStatus(string path, string message)
    {
        SavedPath.Value = path;
        SetStatus(message, IStatusProvider.StatusLevel.Error);
    }
    private static string BuildRelativePath(string folder, string fileName, string extension)
    {
        var name = SanitizeFileName(fileName, extension);
        if (name.Length == 0)
            return string.Empty;

        var dir = folder.Trim();
        if (dir.Length == 0)
            return name;

        dir = dir.Replace('\\', '/').TrimEnd('/');
        return $"{dir}/{name}";
    }
    /// <summary>
    /// Reduces the file name to one safe segment that always ends in the given extension, so a
    /// stray separator can never redirect the write out of the folder.
    /// </summary>
    private static string SanitizeFileName(string fileName, string extension)
    {
        var name = (fileName ?? string.Empty).Trim();

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            name = name.Replace(invalidChar, '_');

        name = name.TrimEnd('.', ' ');
        if (name.Length == 0)
            return string.Empty;

        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0)
            name = name[..lastDot];

        return name + extension;
    }

    /// <summary>
    /// Writes the scan as a PLY point cloud (ASCII or binary little-endian): float32 x/y/z plus
    /// uchar red/green/blue per vertex, openable in MeshLab/CloudCompare/blender.
    /// </summary>
    private void WritePly(string absolutePath, bool ascii)
    {
        var count = _points.Count;
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("ply");
        builder.AppendLine(ascii ? "format ascii 1.0" : "format binary_little_endian 1.0");
        builder.AppendLine("comment TiXL DepthScan3D");
        builder.AppendLine($"element vertex {count}");
        builder.AppendLine("property float x");
        builder.AppendLine("property float y");

        builder.AppendLine("property float z");
        builder.AppendLine("property uchar red");
        builder.AppendLine("property uchar green");
        builder.AppendLine("property uchar blue");
        builder.AppendLine("end_header");
        var header = builder.ToString();
        if (ascii)
        {
            var ci = CultureInfo.InvariantCulture;
            using (var writer = new StreamWriter(absolutePath, false, new UTF8Encoding(false)))
            {
                writer.Write(header);
                foreach (var sp in _points)
                {
                    writer.WriteLine("{0} {1} {2} {3} {4} {5}",
                                     sp.Position.X.ToString("R", ci), sp.Position.Y.ToString("R", ci), sp.Position.Z.ToString("R", ci),
                                     ToByte(sp.Color.X).ToString(ci), ToByte(sp.Color.Y).ToString(ci), ToByte(sp.Color.Z).ToString(ci));
                }
            }

            return;
        }

        var payload = new byte[count * 15];
        var offset = 0;
        foreach (var sp in _points)
        {
            var xb = BitConverter.GetBytes(sp.Position.X);
            var yb = BitConverter.GetBytes(sp.Position.Y);
            var zb = BitConverter.GetBytes(sp.Position.Z);
            Array.Copy(xb, 0, payload, offset, 4);
            Array.Copy(yb, 0, payload, offset + 4, 4);
            Array.Copy(zb, 0, payload, offset + 8, 4);
            payload[offset + 12] = ToByte(sp.Color.X);
            payload[offset + 13] = ToByte(sp.Color.Y);
            payload[offset + 14] = ToByte(sp.Color.Z);
            offset += 15;
        }

        using (var file = File.Create(absolutePath))
        {
            var headerBytes = System.Text.Encoding.UTF8.GetBytes(header);
            file.Write(headerBytes, 0, headerBytes.Length);
            file.Write(payload, 0, payload.Length);
        }
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);
    }

    private void UpdateStatus()
    {
        if (_points.Count == 0)
        {
            SetStatus("Enable Capture or StartRecord to scan points into the cloud.", IStatusProvider.StatusLevel.Notice);
            return;
        }

        var state = _recording ? "Recording" : "Holding";
        SetStatus($"{state} scan: {_points.Count} points, {_captures} captures (+{PointsAdded.Value} last).", IStatusProvider.StatusLevel.Success);
    }

    /// <summary>How overlapping voxel observations merge; values only ever appended.</summary>
    public enum MergeMode
    {
        Average,
        Replace,
    }

    /// <summary>PLY output encoding; values only ever appended.</summary>
    public enum PlyFormat
    {
        Ascii,
        Binary,
    }
    private void SetStatus(string message, IStatusProvider.StatusLevel level)
    {
        _statusMessage = message;
        _statusLevel = level;
        if (level >= IStatusProvider.StatusLevel.Warning && message != _lastLoggedStatus)
        {
            _lastLoggedStatus = message;
            Log.Warning($"[DepthScanBuffer] {message}", this);
        }
    }
    string? ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        if (inputId == Mode.Id)
        {
            var index = Math.Clamp(_uiMode, 0, ModeNames.Length - 1);
            return ModeNames[index];
        }

        if (inputId == SaveFormatInput.Id)
        {
            var index = Math.Clamp(_uiSaveFormat, 0, SaveFormatNames.Length - 1);
            return SaveFormatNames[index];
        }

        return "";
    }
    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        if (inputId == Mode.Id)
        {
            foreach (var name in ModeNames)
                yield return name;
        }

        if (inputId == SaveFormatInput.Id)
        {
            foreach (var name in SaveFormatNames)
                yield return name;
        }

        yield break;
    }
    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        if (inputId == Mode.Id)
        {
            var index = Array.IndexOf(ModeNames, selected);
            if (index >= 0)
                Mode.SetTypedInputValue(index);
        }

        if (inputId == SaveFormatInput.Id)
        {
            var index = Array.IndexOf(SaveFormatNames, selected);
            if (index >= 0)
                SaveFormatInput.SetTypedInputValue(index);
        }
    }

    #region Inputs

    [Input(Guid = "93c8d941-f395-4271-b36a-86aadb010cff")]
    public readonly InputSlot<Texture2D?> DepthMap = new();

    [Input(Guid = "afc191c7-be4d-44eb-938b-701e35f75fae")]
    public readonly InputSlot<Texture2D?> ColorMap = new();

    [Input(Guid = "79ecd596-22d3-44a1-8f25-5fd74549770d")]
    public readonly InputSlot<Texture2D?> Confidence = new();

    [Input(Guid = "a7a2eb05-619f-4363-9b6a-120d3c4d0411")]
    public readonly InputSlot<object> CameraReference = new();

    [Input(Guid = "d31b9eb4-076e-460d-9204-25247df35875")]
    public readonly InputSlot<float> FieldOfView = new(50f);

    [Input(Guid = "c0f6dcfb-f8d9-4030-80da-ca0f7042e936")]
    public readonly InputSlot<float> PointScale = new(1f);

    [Input(Guid = "c84c32af-1f72-4ade-badd-83525ba48332")]
    public readonly InputSlot<float> VoxelSize = new(0.02f);

    [Input(Guid = "9fa379e9-10b0-4010-a344-2bba6a85fcd0", MappedType = typeof(MergeMode))]
    public readonly InputSlot<int> Mode = new((int)MergeMode.Average);

    [Input(Guid = "fc20b574-76b5-4acd-bcec-933960de8f6a")]
    public readonly InputSlot<bool> Capture = new(false);

    [Input(Guid = "34d31350-9754-43bf-8037-aa1f7f1a6eda")]
    public readonly InputSlot<bool> CaptureOnce = new(false);

    [Input(Guid = "79d30636-4d7f-4c46-97b6-806b20d8f674")]
    public readonly InputSlot<bool> StartRecord = new(false);

    [Input(Guid = "b080c2a6-1559-4dcb-837d-24e68f4db439")]
    public readonly InputSlot<bool> StopRecord = new(false);

    [Input(Guid = "e8ba2ceb-4817-47b9-a2e2-f86cf73f2a6a")]
    public readonly InputSlot<bool> Reset = new(false);

    [Input(Guid = "5304c51a-4e3f-4d9c-a34b-4af6f5cbe0d9")]
    public readonly InputSlot<int> MaxPoints = new(500000);

    [Input(Guid = "5a05aca8-d4ed-43b0-82e4-1dcd41849203")]
    public readonly InputSlot<float> SampleLifetime = new(0f);

    [Input(Guid = "0187ea06-b66b-4cc2-9fd1-7d68455ac6b9")]
    public readonly InputSlot<int> PixelStride = new(2);

    [Input(Guid = "e805a254-7980-4bbe-b603-d58e3a010105")]
    public readonly InputSlot<float> ConfidenceThreshold = new(0.5f);

    [Input(Guid = "6c269444-aaec-4359-9708-4eb2cfdd0487")]
    public readonly InputSlot<bool> SaveTrigger = new(false);

    [Input(Guid = "75c48797-6bba-4176-9549-459e9bcac012")]
    public readonly InputSlot<string> SaveFolder = new("Resources/DepthScans");

    [Input(Guid = "0773ed04-80ce-43b1-9104-990d44f9ba33")]
    public readonly InputSlot<string> SaveFileName = new("scan3d");

    [Input(Guid = "13b697b4-c85d-4a15-afb2-37d76a8425e9", MappedType = typeof(PlyFormat))]
    public readonly InputSlot<int> SaveFormatInput = new((int)PlyFormat.Binary);

    #endregion

    #region Private Fields

    private struct ScanPoint
    {
        public System.Numerics.Vector3 Position;
        public System.Numerics.Vector3 Color;
        public int Count;
        public float LastSeen;
    }

    private const int ReadbackTimeoutFrames = 60;

    private List<ScanPoint> _points = [];
    private Dictionary<long, int> _voxelIndex = [];
    private readonly System.Diagnostics.Stopwatch _scanClock = System.Diagnostics.Stopwatch.StartNew();
    private int _captures;
    private int _pointsAdded;
    private bool _bufferFullLogged;
    private BufferWithViews? _pointBuffer;

    private T3.Graphics.Compat.Texture2D? _depthStaging;
    private T3.Graphics.Compat.Texture2D? _confStaging;
    private T3.Graphics.Compat.Texture2D? _colorStaging;
    private MappedMemory? _mappedDepth;
    private MappedMemory? _mappedConf;
    private MappedMemory? _mappedColor;
    private bool _depthPending;
    private bool _confPending;
    private bool _colorPending;
    private int _waitFrames;
    private int _confWaitFrames;
    private int _colorWaitFrames;
    private int _stagingWidth;
    private int _stagingHeight;

    private System.Numerics.Matrix4x4 _pendingCamToWorld;
    private float _pendingTanHalfX;
    private float _pendingTanHalfY;
    private bool _pendingCamIsPose;
    private MergeMode _pendingMode;
    private float _pendingThreshold;
    private float _pendingScale;
    private float _pendingVoxelSize;
    private int _pendingStride;
    private int _pendingMaxPoints;
    private bool _captureOnceState;
    private bool _resetState;
    private bool _saveState;
    private bool _startRecordState;
    private bool _stopRecordState;
    private bool _recording;
    private bool _loggedWrongFormat;
    private bool _loggedConfMismatch;
    private bool _loggedColorMismatch;
    private string? _statusMessage;
    private IStatusProvider.StatusLevel _statusLevel;
    private string? _lastLoggedStatus;
    private int _uiMode;
    private int _uiSaveFormat;

    private static readonly string[] ModeNames = { "Average", "Replace" };
    private static readonly string[] SaveFormatNames = { "PLY ASCII", "PLY binary" };

    #endregion

    public IStatusProvider.StatusLevel GetStatusLevel()
    {
        return _statusLevel;
    }

    public string? GetStatusMessage()
    {
        return _statusMessage;
    }

    protected override void Dispose(bool isDisposing)
    {
        if (!isDisposing)
            return;

        _pointBuffer?.Dispose();
        _pointBuffer = null;
        _depthStaging?.Dispose();
        _depthStaging = null;
        _confStaging?.Dispose();
        _confStaging = null;
        _colorStaging?.Dispose();
        _colorStaging = null;
        base.Dispose(isDisposing);
    }
}
