
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
        if (_meshDone)
        {
            _meshDone = false;
            SavedPath.Value = _meshResultPath;
            SetStatus(_meshResultMessage, _meshResultPath.Length > 0 ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Warning);
        }

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

        // Complete the previous capture's readback and merge it before starting a new one. All copies of a capture
        // must be mapped before merging: the larger color frame often lands a frame after the depth, and merging
        // without it left the points white. Each staging texture is mapped once and unmapped after the merge, since
        // copying into a still-mapped texture fails and later captures read stale memory.
        if (_depthPending)
        {
            if (_mappedDepth == null && !TryMapStaging(ref _depthPending, _depthStaging, ref _waitFrames))
                return;

            if (_confPending && _mappedConf == null && !TryMapStaging(ref _confPending, _confStaging, ref _confWaitFrames))
                return;

            if (_colorPending && _mappedColor == null && !TryMapStaging(ref _colorPending, _colorStaging, ref _colorWaitFrames))
                return;

            MergeScan(_mappedConf != null, _mappedColor != null);
            UnmapStaging();
            _depthPending = false;
            _confPending = false;
            _colorPending = false;

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

        // The color map may be larger than the depth (the camera frame vs. the model's output) and BGRA (video
        // decoders): it is sampled at each depth pixel's position, so only the channel layout has to be known.
        var colorTexture = ColorMap.GetValue(context);
        var colorFormat = colorTexture is { IsDisposed: false } ? colorTexture.Description.Format : Format.Unknown;
        var colorIsBgra = colorFormat is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb;
        var useColor = colorIsBgra || colorFormat is Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb;
        if (colorTexture is { IsDisposed: false } && !useColor && !_loggedColorMismatch)
        {
            _loggedColorMismatch = true;
            Log.Warning($"[DepthScan3D] ColorMap ignored; {colorFormat} is not an 8-bit RGBA or BGRA format.", this);
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
            GetOrCreateColorStaging(colorTexture!.Description.Width, colorTexture.Description.Height, colorFormat);
            _pendingColorIsBgra = colorIsBgra;
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
        _pendingAutoScale = AutoScale.GetValue(context);
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
        _pendingSpherical = false;
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

                // A camera that only rotates about the origin (a pan/tilt head) sees one surface per direction
                _pendingSpherical = camToWorld.Translation.LengthSquared() < 1e-6f;
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

        // Relative depth models wobble their scale from capture to capture; on a rotation-only scan that
        // stacks repeated copies of the same surface at slightly increasing range. Pull each capture back
        // onto the points already merged before inserting it.
        var anchor = 1f;
        var shift = 0f;
        if (_pendingAutoScale && _points.Count >= MinAnchorPoints)
        {
            var estimate = EstimateAnchorScale(mapping, width, height, stride,
                                               hasConfidence ? mappedConf : (MappedMemory?)null, threshold, out shift);
            anchor = float.IsFinite(estimate) ? Math.Clamp(estimate, 0.25f, 4f) : _anchorScale;
            if (!float.IsFinite(estimate))
                shift = 0f;

            _anchorScale = anchor;
        }

        for (var y = 0; y < height; y += stride)
        {
            var row = (float*)((byte*)mapping.Data + (long)y * mapping.RowPitch);
            var belowRow = y + stride < height ? (float*)((byte*)mapping.Data + (long)(y + stride) * mapping.RowPitch) : null;
            var confRow = hasConfidence ? (float*)((byte*)mappedConf.Data + (long)y * mappedConf.RowPitch) : null;
            var colorY = hasColor ? Math.Min(_colorStagingHeight - 1, (int)((y + 0.5f) * _colorStagingHeight / height)) : 0;
            var colorRow = hasColor ? (byte*)((byte*)mappedColor.Data + (long)colorY * mappedColor.RowPitch) : null;
            for (var x = 0; x < width; x += stride)
            {
                var z = row[x];
                if (!float.IsFinite(z) || z <= 0f)
                    continue;

                if (confRow != null && confRow[x] < threshold)
                    continue;

                // Depth edges smear into flying pixels between the near and far surface
                if (IsDepthEdge(z, x + stride < width ? row[x + stride] : z, belowRow != null ? belowRow[x] : z))
                    continue;

                var r = 1f;
                var g = 1f;
                var b = 1f;
                if (colorRow != null)
                {
                    var colorX = Math.Min(_colorStagingWidth - 1, (int)((x + 0.5f) * _colorStagingWidth / width));
                    var c = colorRow + colorX * 4;
                    r = c[_pendingColorIsBgra ? 2 : 0] / 255f;
                    g = c[1] / 255f;
                    b = c[_pendingColorIsBgra ? 0 : 2] / 255f;
                }

                var depth = MathF.Max(z * _pendingScale * anchor + shift, 1e-3f);
                var position = UnprojectPoint(x, y, width, height, depth);

                // Frame centers are sharper and less distorted than the borders, so they count for more
                var ndcX = (x + 0.5f) / width * 2f - 1f;
                var ndcY = (y + 0.5f) / height * 2f - 1f;
                var weight = MathF.Max(0.05f, (1f - 0.7f * ndcX * ndcX) * (1f - 0.7f * ndcY * ndcY));
                var inserted = _pendingSpherical ? InsertSpherical(position, r, g, b, now, weight) : InsertPoint(position, r, g, b, now);
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
    /// Unprojects one depth pixel to scan space: with a camera pose, through the camera into world
    /// space, otherwise into DA3-style camera space (+Z forward, y up).
    /// </summary>
    private System.Numerics.Vector3 UnprojectPoint(float x, float y, float width, float height, float depth)
    {
        var ndcX = (x + 0.5f) / width * 2f - 1f;
        var ndcY = 1f - (y + 0.5f) / height * 2f;
        if (_pendingCamIsPose)
        {
            // RH camera space: the view direction is -Z, so flip the depth.
            var camPoint = new System.Numerics.Vector3(ndcX * _pendingTanHalfX * depth, ndcY * _pendingTanHalfY * depth, -depth);
            return System.Numerics.Vector3.Transform(camPoint, _pendingCamToWorld);
        }

        return new System.Numerics.Vector3(ndcX * _pendingTanHalfX * depth, ndcY * _pendingTanHalfY * depth, depth);
    }

    /// <summary>
    /// DA3's relative depth has no absolute scale and wobbles from capture to capture; on a rotation-only
    /// scan each capture then stacks another slightly-more-distant copy of the same surfaces. This samples
    /// the incoming capture sparsely, matches samples against the nearest already-merged points, and returns
    /// the median range ratio from the camera: the factor that pulls the capture onto the existing cloud.
    /// Returns NaN when there is not enough overlap to trust an estimate.
    /// </summary>
    private unsafe float EstimateAnchorScale(MappedMemory mapping, int width, int height, int stride,
                                             MappedMemory? conf, float threshold, out float shift)
    {
        shift = 0f;
        Span<float> sampleNew = stackalloc float[AnchorSampleCap];
        Span<float> sampleExisting = stackalloc float[AnchorSampleCap];
        var cameraOrigin = _pendingCamIsPose ? _pendingCamToWorld.Translation : default;
        Span<float> ratios = stackalloc float[AnchorSampleCap];
        var count = 0;
        var sampleStride = stride * 3;
        for (var y = 0; y < height && count < AnchorSampleCap; y += sampleStride)
        {
            var row = (float*)((byte*)mapping.Data + (long)y * mapping.RowPitch);
            var confRow = conf != null ? (float*)((byte*)conf.Value.Data + (long)y * conf.Value.RowPitch) : null;
            for (var x = 0; x < width && count < AnchorSampleCap; x += sampleStride)
            {
                var z = row[x];
                if (!float.IsFinite(z) || z <= 0f || (confRow != null && confRow[x] < threshold))
                    continue;

                var position = UnprojectPoint(x, y, width, height, z * _pendingScale);
                var found = _pendingSpherical ? TryGetSphericalPoint(position, out var nearest) : TryGetNearestPoint(position, out nearest);
                if (!found)
                    continue;

                var range = System.Numerics.Vector3.Distance(position, cameraOrigin);
                if (range < _pendingVoxelSize)
                    continue;

                var existingRange = System.Numerics.Vector3.Distance(nearest, cameraOrigin);
                sampleNew[count] = z * _pendingScale;
                sampleExisting[count] = existingRange * z * _pendingScale / range;
                ratios[count++] = Math.Clamp(existingRange / range, 0.25f, 4f);
            }
        }

        if (count < MinAnchorMatches)
            return float.NaN;

        for (var i = 1; i < count; i++) // insertion sort: tiny fixed buffer
        {
            var value = ratios[i];
            var j = i - 1;
            while (j >= 0 && ratios[j] > value)
            {
                ratios[j + 1] = ratios[j];
                j--;
            }

            ratios[j + 1] = value;
        }

        var scale = count % 2 == 1 ? ratios[count / 2] : 0.5f * (ratios[count / 2 - 1] + ratios[count / 2]);

        // Relative depth is off by a shift as well as a scale: refit existing = a * new + b on the samples that already
        // agree with the median scale, but only when the overlap spans enough depth to pin both down
        double n = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var i = 0; i < count; i++)
        {
            if (MathF.Abs(sampleExisting[i] - scale * sampleNew[i]) > 0.2f * sampleExisting[i])
                continue;

            n++;
            sx += sampleNew[i];
            sy += sampleExisting[i];
            sxx += sampleNew[i] * sampleNew[i];
            sxy += sampleNew[i] * sampleExisting[i];
        }

        if (n >= MinAnchorMatches * 2)
        {
            var meanX = sx / n;
            var meanY = sy / n;
            var variance = sxx / n - meanX * meanX;
            if (variance > 0.01 * meanX * meanX)
            {
                var slope = (sxy / n - meanX * meanY) / variance;
                var offset = meanY - slope * meanX;
                if (slope is > 0.25 and < 4.0 && Math.Abs(offset) < 0.4 * meanY)
                {
                    shift = (float)offset;
                    return (float)slope;
                }
            }
        }

        return scale;
    }

    private static bool IsDepthEdge(float z, float right, float below)
    {
        return (float.IsFinite(right) && right > 0f && MathF.Abs(right - z) > FlyingPixelRatio * z)
               || (float.IsFinite(below) && below > 0f && MathF.Abs(below - z) > FlyingPixelRatio * z);
    }

    private static long SphericalKey(System.Numerics.Vector3 position, out float range)
    {
        range = position.Length();
        if (range < 1e-5f)
            return long.MinValue;

        var elevation = MathF.Asin(Math.Clamp(position.Y / range, -1f, 1f));
        var azimuth = MathF.Atan2(position.X, -position.Z);
        var ix = (int)MathF.Floor((azimuth + MathF.PI) / SphericalCellRadians);
        var iy = (int)MathF.Floor((elevation + 0.5f * MathF.PI) / SphericalCellRadians);
        return PackVoxelKey(ix, iy, 0);
    }

    private bool TryGetSphericalPoint(System.Numerics.Vector3 position, out System.Numerics.Vector3 existing)
    {
        existing = default;
        var key = SphericalKey(position, out _);
        if (key == long.MinValue || !_voxelIndex.TryGetValue(key, out var index))
            return false;

        existing = _points[index].Position;
        return true;
    }

    /// <summary>
    /// Merges a sample into a direction-binned range map: one surface per viewing direction, so repeated and
    /// slightly mis-scaled captures of a wall average into one sheet instead of stacking copies.
    /// </summary>
    private int InsertSpherical(System.Numerics.Vector3 position, float r, float g, float b, float now, float weight)
    {
        var key = SphericalKey(position, out var range);
        if (key == long.MinValue)
            return 0;

        if (_voxelIndex.TryGetValue(key, out var index))
        {
            var sp = _points[index];
            var ratio = range / MathF.Max(sp.Position.Length(), 1e-5f);
            if (sp.Count >= 2 && (ratio < SphericalAgreeLow || ratio > SphericalAgreeHigh))
                return 0;

            sp.Count++;
            sp.LastSeen = now;
            if (_pendingMode == MergeMode.Average)
            {
                sp.Weight += weight;
                var k = weight / sp.Weight;
                sp.Position += (position - sp.Position) * k;
                sp.Color += (new System.Numerics.Vector3(r, g, b) - sp.Color) * k;
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
            return 2;

        _points.Add(new ScanPoint { Position = position, Color = new(r, g, b), Count = 1, Weight = weight, LastSeen = now });
        _voxelIndex[key] = _points.Count - 1;
        return 1;
    }

    /// <summary>Nearest merged point within the 3x3x3 voxel neighborhood of a position.</summary>
    private bool TryGetNearestPoint(System.Numerics.Vector3 position, out System.Numerics.Vector3 nearest)
    {
        var vs = _pendingVoxelSize;
        var vx = (int)MathF.Floor(position.X / vs);
        var vy = (int)MathF.Floor(position.Y / vs);
        var vz = (int)MathF.Floor(position.Z / vs);
        var bestDistance = float.MaxValue;
        nearest = default;
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!_voxelIndex.TryGetValue(PackVoxelKey(vx + dx, vy + dy, vz + dz), out var index))
                        continue;

                    var distance = System.Numerics.Vector3.DistanceSquared(_points[index].Position, position);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        nearest = _points[index].Position;
                    }
                }
            }
        }

        return bestDistance != float.MaxValue;
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

            index[_pendingSpherical
                      ? SphericalKey(sp.Position, out _)
                      : PackVoxelKey((int)MathF.Floor(sp.Position.X / _pendingVoxelSize),
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
        var kept = 0;
        for (var i = 0; i < count; i++)
        {
            var sp = _points[i];

            // A direction seen once is as likely noise as surface
            if (_pendingSpherical && sp.Count < SphericalMinObservations)
                continue;

            points[kept++] = new Point
            {
                Position = sp.Position,
                Color = new System.Numerics.Vector4(sp.Color, 1f),
                Orientation = System.Numerics.Quaternion.Identity,
                Scale = System.Numerics.Vector3.One,
                F1 = 1,
                F2 = 1,
            };
        }

        count = kept;
        if (count == 0)
        {
            Points.Value = null;
            PointCount.Value = 0;
            return;
        }

        if (kept < points.Length)
            Array.Resize(ref points, kept);

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
        _anchorScale = 1f;
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
        _colorStaging = null;
    }

    /** The color map keeps its own size and format, so its staging copy is sized independently of the depth. */
    private void GetOrCreateColorStaging(int width, int height, Format format)
    {
        if (_colorStaging is { IsDisposed: false }
            && _colorStaging.Description.Width == width
            && _colorStaging.Description.Height == height
            && _colorStaging.Description.Format == format)
            return;

        _colorPending = false;
        _colorStaging?.Dispose();
        _colorStagingWidth = width;
        _colorStagingHeight = height;
        _colorStaging = new T3.Graphics.Compat.Texture2D(ResourceManager.Device, new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1,
            Format = format, SampleDescription = new SampleDescription(1, 0),
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
    private void UnmapStaging()
    {
        var context = ResourceManager.Device.ImmediateContext;
        if (_mappedDepth != null && _depthStaging is { IsDisposed: false })
            context.UnmapSubresource(_depthStaging, 0);
        if (_mappedConf != null && _confStaging is { IsDisposed: false })
            context.UnmapSubresource(_confStaging, 0);
        if (_mappedColor != null && _colorStaging is { IsDisposed: false })
            context.UnmapSubresource(_colorStaging, 0);

        _mappedDepth = null;
        _mappedConf = null;
        _mappedColor = null;
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

        var format = (PlyFormat)Math.Clamp(SaveFormatInput.GetValue(context), 0, 2);
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
            var directory = Path.GetDirectoryName(absolutePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (format == PlyFormat.Mesh)
            {
                if (!_pendingSpherical)
                {
                    SaveStatus(string.Empty, "Mesh export needs a camera that rotates about the origin (a PTZ scan).");
                    return;
                }

                if (_meshBusy)
                {
                    SetStatus("A mesh is still being built.", IStatusProvider.StatusLevel.Warning);
                    return;
                }

                // The meshing walks millions of cells: it runs on a snapshot off the editor thread
                var snapshot = _points.ToArray();
                _meshBusy = true;
                SetStatus($"Building a mesh from {snapshot.Length} points...", IStatusProvider.StatusLevel.Notice);
                System.Threading.Tasks.Task.Run(() =>
                                                {
                                                    try
                                                    {
                                                        var watch = System.Diagnostics.Stopwatch.StartNew();
                                                        var (vertices, triangles) = WriteSphericalMesh(absolutePath, snapshot);
                                                        _meshResultPath = relativePath;
                                                        _meshResultMessage = $"Saved a mesh of {vertices} vertices and {triangles} triangles to {relativePath} ({watch.Elapsed.TotalSeconds:F1} s)";
                                                    }
                                                    catch (Exception e)
                                                    {
                                                        _meshResultPath = string.Empty;
                                                        _meshResultMessage = $"Building the mesh failed: {e.Message}";
                                                    }
                                                    finally
                                                    {
                                                        _meshBusy = false;
                                                        _meshDone = true;
                                                    }
                                                });
                return;
            }
            else
            {
                WritePly(absolutePath, format == PlyFormat.Ascii);
                SavedPath.Value = relativePath;
                SetStatus($"Saved {_points.Count} points ({_captures} captures) to {relativePath}", IStatusProvider.StatusLevel.Success);
            }
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
    /**
     * Turns the spherical range map into a closed triangle mesh: sparse cells are dropped, the range grid is median
     * filtered and small holes are filled, neighbouring cells become triangles unless a depth jump separates them
     * (so surfaces never stretch across an edge), and the open rim at the lowest seen elevation is closed with a
     * floor fan so the shell is solid. Faces point toward the camera.
     */
    private (int Vertices, int Triangles) WriteSphericalMesh(string absolutePath, ScanPoint[] scanPoints)
    {
        var width = (int)MathF.Ceiling(2f * MathF.PI / SphericalCellRadians);
        var height = (int)MathF.Ceiling(MathF.PI / SphericalCellRadians) + 1;
        var range = new float[width * height];
        var color = new System.Numerics.Vector3[width * height];
        Array.Fill(range, float.NaN);
        foreach (var sp in scanPoints)
        {
            if (sp.Count < SphericalMinObservations)
                continue;

            var r = sp.Position.Length();
            var e = MathF.Asin(Math.Clamp(sp.Position.Y / r, -1f, 1f));
            var a = MathF.Atan2(sp.Position.X, -sp.Position.Z);
            var ix = Math.Clamp((int)MathF.Floor((a + MathF.PI) / SphericalCellRadians), 0, width - 1);
            var iy = Math.Clamp((int)MathF.Floor((0.5f * MathF.PI - e) / SphericalCellRadians), 0, height - 1);
            range[iy * width + ix] = r;
            color[iy * width + ix] = sp.Color;
        }

        MedianFilterRange(range, width, height);
        FillSmallHoles(range, color, width, height, 6);

        var vertexIndex = new int[width * height];
        Array.Fill(vertexIndex, -1);
        var positions = new List<System.Numerics.Vector3>();
        var colors = new List<System.Numerics.Vector3>();
        for (var y = 0; y < height; y++)
        {
            var elevation = 0.5f * MathF.PI - (y + 0.5f) * SphericalCellRadians;
            for (var x = 0; x < width; x++)
            {
                var r = range[y * width + x];
                if (float.IsNaN(r))
                    continue;

                var azimuth = (x + 0.5f) * SphericalCellRadians - MathF.PI;
                var cosE = MathF.Cos(elevation);
                vertexIndex[y * width + x] = positions.Count;
                positions.Add(new System.Numerics.Vector3(r * cosE * MathF.Sin(azimuth), r * MathF.Sin(elevation), -r * cosE * MathF.Cos(azimuth)));
                colors.Add(color[y * width + x]);
            }
        }

        var faces = new List<int>();
        void AddFace(int i0, int i1, int i2)
        {
            var normal = System.Numerics.Vector3.Cross(positions[i1] - positions[i0], positions[i2] - positions[i0]);
            var centroid = positions[i0] + positions[i1] + positions[i2];
            if (System.Numerics.Vector3.Dot(normal, centroid) > 0f)
                (i1, i2) = (i2, i1);

            faces.Add(i0);
            faces.Add(i1);
            faces.Add(i2);
        }

        for (var y = 0; y < height - 1; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var x1 = (x + 1) % width;
                var i00 = vertexIndex[y * width + x];
                var i10 = vertexIndex[y * width + x1];
                var i01 = vertexIndex[(y + 1) * width + x];
                var i11 = vertexIndex[(y + 1) * width + x1];
                if (i00 < 0 || i10 < 0 || i01 < 0 || i11 < 0)
                    continue;

                var r00 = range[y * width + x];
                var r10 = range[y * width + x1];
                var r01 = range[(y + 1) * width + x];
                var r11 = range[(y + 1) * width + x1];
                var lo = MathF.Min(MathF.Min(r00, r10), MathF.Min(r01, r11));
                var hi = MathF.Max(MathF.Max(r00, r10), MathF.Max(r01, r11));
                if (hi > lo * MeshDepthJumpRatio)
                    continue;

                AddFace(i00, i10, i01);
                AddFace(i10, i11, i01);
            }
        }

        CloseHoles(positions, colors, faces, AddFace);

        using (var file = File.Create(absolutePath))
        {
            var header = "ply\nformat binary_little_endian 1.0\ncomment TiXL DepthScan3D mesh\n"
                         + $"element vertex {positions.Count}\nproperty float x\nproperty float y\nproperty float z\n"
                         + "property uchar red\nproperty uchar green\nproperty uchar blue\n"
                         + $"element face {faces.Count / 3}\nproperty list uchar int vertex_indices\nend_header\n";
            var headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
            file.Write(headerBytes, 0, headerBytes.Length);
            using var writer = new BinaryWriter(file);
            for (var i = 0; i < positions.Count; i++)
            {
                writer.Write(positions[i].X);
                writer.Write(positions[i].Y);
                writer.Write(positions[i].Z);
                writer.Write(ToByte(colors[i].X));
                writer.Write(ToByte(colors[i].Y));
                writer.Write(ToByte(colors[i].Z));
            }

            for (var i = 0; i < faces.Count; i += 3)
            {
                writer.Write((byte)3);
                writer.Write(faces[i]);
                writer.Write(faces[i + 1]);
                writer.Write(faces[i + 2]);
            }
        }

        return (positions.Count, faces.Count / 3);
    }

    /**
     * Fans over every boundary loop of the mesh so the shell is solid: gaps left by depth jumps and missing cells close on
     * their centroid, and the large outer rim (the lowest elevation the camera could see) closes on a point at floor level.
     */
    private static void CloseHoles(List<System.Numerics.Vector3> positions, List<System.Numerics.Vector3> colors, List<int> faces,
                                   Action<int, int, int> addFace)
    {
        // Tuple keys: a packed 64-bit key hashes to a ^ b, which piles neighbouring edges into a few buckets
        var directed = new HashSet<(int, int)>();
        for (var i = 0; i < faces.Count; i += 3)
        {
            for (var k = 0; k < 3; k++)
                directed.Add((faces[i + k], faces[i + (k + 1) % 3]));
        }

        // Boundary edges, reversed so that walking them follows the open side
        var outgoing = new Dictionary<int, List<int>>();
        foreach (var (from, to) in directed)
        {
            if (directed.Contains((to, from)))
                continue;

            if (!outgoing.TryGetValue(to, out var list))
                outgoing[to] = list = new List<int>(2);

            list.Add(from);
        }

        var loop = new List<int>();
        foreach (var start in new List<int>(outgoing.Keys))
        {
            while (outgoing[start].Count > 0)
            {
                loop.Clear();
                var current = start;
                var closed = false;
                while (true)
                {
                    loop.Add(current);
                    if (!outgoing.TryGetValue(current, out var options) || options.Count == 0)
                        break;

                    var following = options[^1];
                    options.RemoveAt(options.Count - 1);
                    current = following;
                    if (current == start)
                    {
                        closed = true;
                        break;
                    }
                }

                if (!closed || loop.Count < 3)
                    continue;

                var center = System.Numerics.Vector3.Zero;
                var color = System.Numerics.Vector3.Zero;
                var lowest = float.MaxValue;
                foreach (var vertex in loop)
                {
                    center += positions[vertex];
                    color += colors[vertex];
                    lowest = MathF.Min(lowest, positions[vertex].Y);
                }

                center /= loop.Count;
                if (loop.Count > MeshRimLoopEdges)
                {
                    center.X = 0f;
                    center.Z = 0f;
                    center.Y = lowest;
                    color = new System.Numerics.Vector3(0.5f, 0.5f, 0.5f) * loop.Count;
                }

                var centerIndex = positions.Count;
                positions.Add(center);
                colors.Add(color / loop.Count);
                for (var i = 0; i < loop.Count; i++)
                    addFace(loop[i], loop[(i + 1) % loop.Count], centerIndex);
            }
        }
    }

    /** 3x3 median of the range grid (azimuth wraps); cells with fewer than four filled neighbours are removed as specks. */
    private static void MedianFilterRange(float[] range, int width, int height)
    {
        var result = new float[range.Length];
        Array.Fill(result, float.NaN);
        var window = new float[9];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (float.IsNaN(range[y * width + x]))
                    continue;

                var n = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var yy = y + dy;
                    if (yy < 0 || yy >= height)
                        continue;

                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var value = range[yy * width + (x + dx + width) % width];
                        if (!float.IsNaN(value))
                            window[n++] = value;
                    }
                }

                if (n < 4)
                    continue;

                Array.Sort(window, 0, n);
                result[y * width + x] = window[n / 2];
            }
        }

        Array.Copy(result, range, range.Length);
    }

    /** Fills empty cells that have at least three filled 8-neighbours with their mean, a few passes deep. */
    private static void FillSmallHoles(float[] range, System.Numerics.Vector3[] color, int width, int height, int passes)
    {
        var add = new List<(int Index, float Range, System.Numerics.Vector3 Color)>();
        for (var pass = 0; pass < passes; pass++)
        {
            add.Clear();
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (!float.IsNaN(range[y * width + x]))
                        continue;

                    var n = 0;
                    var sum = 0f;
                    var colorSum = System.Numerics.Vector3.Zero;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var index = (y + dy) * width + (x + dx + width) % width;
                            if (float.IsNaN(range[index]))
                                continue;

                            n++;
                            sum += range[index];
                            colorSum += color[index];
                        }
                    }

                    if (n >= 3)
                        add.Add((y * width + x, sum / n, colorSum / n));
                }
            }

            if (add.Count == 0)
                break;

            foreach (var item in add)
            {
                range[item.Index] = item.Range;
                color[item.Index] = item.Color;
            }
        }
    }

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
        var camMode = _pendingCamIsPose ? "world space (DA3 camera pose)" : "camera space - connect CamReference for world space";
        var anchorText = _pendingAutoScale && _anchorScale != 1f ? $", scale x{_anchorScale:0.###}" : string.Empty;
        SetStatus($"{state} scan: {_points.Count} points, {_captures} captures{anchorText}, {camMode}.", IStatusProvider.StatusLevel.Success);
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
        Mesh,
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

    [Input(Guid = "8a4f0d63-2e91-4b57-a3c8-6f5d1e2b4c70")]
    public readonly InputSlot<bool> AutoScale = new(true);

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
        public float Weight;
        public float LastSeen;
    }

    private const int ReadbackTimeoutFrames = 60;
    private const int AnchorSampleCap = 400;
    private const int MinAnchorMatches = 12;
    private const int MinAnchorPoints = 300;
    private const float SphericalCellRadians = 0.25f * MathF.PI / 180f;
    private const float SphericalAgreeLow = 0.92f;
    private const float SphericalAgreeHigh = 1.08f;
    private const int SphericalMinObservations = 2;
    private const float FlyingPixelRatio = 0.08f;
    private const float MeshDepthJumpRatio = 1.25f;
    private const int MeshRimLoopEdges = 2000;

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
    private int _colorStagingWidth;
    private int _colorStagingHeight;
    private bool _pendingColorIsBgra;

    private System.Numerics.Matrix4x4 _pendingCamToWorld;
    private float _pendingTanHalfX;
    private float _pendingTanHalfY;
    private bool _pendingCamIsPose;
    private bool _pendingSpherical;
    private volatile bool _meshBusy;
    private volatile bool _meshDone;
    private volatile string _meshResultPath = string.Empty;
    private volatile string _meshResultMessage = string.Empty;
    private MergeMode _pendingMode;
    private float _pendingThreshold;
    private float _pendingScale;
    private float _pendingVoxelSize;
    private int _pendingStride;
    private int _pendingMaxPoints;
    private bool _pendingAutoScale;
    private float _anchorScale = 1f;
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
    private static readonly string[] SaveFormatNames = { "PLY ASCII", "PLY binary", "PLY mesh (closed)" };

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
