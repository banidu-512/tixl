using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lib.io.video.depthanything.Utils;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using T3.Core.Logging;
using T3.Core.Utils;
using T3.Graphics.Compat;
using T3.Core.Resource;
using T3.Core.Resource.Assets;
using T3.Core.Operator.Interfaces;
using T3.Core.Rendering;

#pragma warning disable CS8981 // Disable warning about type name 'depthanything' being lowercase only

namespace Lib.io.video.depthanything;

internal sealed class DepthEstimationRequest
{
    public float[]? InputData;
    public byte[]? InputBytes;
    public int Width;
    public int Height;
    public int SourceWidth;
    public int SourceHeight;
    public long CaptureTicks;
    public int Generation;
}

internal sealed class DepthEstimationResult
{
    public float[]? DepthData;
    public float[]? ConfidenceData;
    public float[]? SkyData;
    public byte[]? ColorData;
    public int Width;
    public int Height;
    public float Min;
    public float Max;
    public float VerticalFovDegrees;
    public bool HasCameraPose;
    public Vector3 CameraPosition;
    public Vector3 CameraForward;
    public Vector3 CameraUp;
    public float InferenceMs;
    public long CaptureTicks;
    public DepthAnything.DepthOutputFormat ColorFormat;
    public bool ColorEnhance;
    public bool ColorInvert;
    public bool FarIsLarge;
    public int Generation;
    public string? Error;
}

[Guid("c8f7d6e5-4b3a-2c1d-9f8e-7a6b5c4d3e2f")]
[ExportDependencies("Microsoft.ML.OnnxRuntime.dll", "onnxruntime.dll", "DirectML.dll", "libonnxruntime*")]
public class DepthAnything : Instance<DepthAnything>, ICustomDropdownHolder, ICamera, ICameraPropertiesProvider
{
    #region Outputs

    [Output(Guid = "d9e8f7a6-5c4b-3d2e-0f9e-8b7a6c5d4e3f", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> OutputTexture = new();

    [Output(Guid = "e0f9e8b7-6d5c-4e3f-1a0f-9c8b7a6d5e4f", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> DepthTexture = new();

    [Output(Guid = "f1a0f9c8-7e6d-5f4e-2b1a-0d9c8b7a6e5f", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> NormalizedDepthTexture = new();

    [Output(Guid = "3b7e1c42-8a5d-4f60-9d21-6c0e5a7b8f13", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> ConfidenceTexture = new();

    [Output(Guid = "5d9a2e74-1b3c-4a86-b7f0-2e8c4d6a9b35", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D?> SkyTexture = new();

    [Output(Guid = "a2b1c0d9-8f7e-6d5c-3c2b-1e0d9c8b7f6a", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> UpdateCount = new();

    [Output(Guid = "b3c2d1e0-0f9f-8e7d-4d3c-2f1e0d9c8a7b", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<float> MinDepth = new();

    [Output(Guid = "c4d3e2f1-1a0b-9f8e-5e4d-3f2e1d0b9a8c", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<float> MaxDepth = new();

    [Output(Guid = "7f1c4b96-3d5e-4c08-a9b2-4a0e6c8d1f57", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<float> VerticalFov = new();

    [Output(Guid = "91e3d6b8-5f7a-4e2a-8c14-6c2a8e0f3b79", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<float> InferenceMs = new();

    [Output(Guid = "b3a5f8da-7c9b-4a4c-9e36-8e4c0a2b5d9b", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<float> LatencyMs = new();

    [Output(Guid = "d5e6f7a2-1b2c-0d9e-8f7a-6b5c4d3e2f1a", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<string> Status = new();

    /// <summary>
    /// The camera estimated by the model as an <see cref="ICamera"/>. The model outputs
    /// world-to-camera extrinsics and a 3x3 intrinsics matrix; the pose is published here
    /// in DA3's image coordinate frame (camera +Z forward, camera -Y up). Connect it to
    /// camera-reference inputs such as ApplyCamTransform or SortPoints.
    /// </summary>
    [Output(Guid = "5a3334ec-78c3-4001-8018-3d8aae7f870f", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Object> CamReference = new();

    #endregion

    /** What the render thread needs to know about the loaded model to prepare input for it. */
    private sealed class ModelInfo
    {
        public int DeclaredWidth;
        public int DeclaredHeight;
        public bool IsV3;
        public bool IsMetric;

        /** uint8 NHWC input that the graph normalizes itself; otherwise float32 NCHW. */
        public bool InputIsBytes;
    }

    private readonly record struct ModelFile(string Name, bool IsV3, bool IsMetric);

    // Everything a worker generation touches exclusively; isolates an exiting
    // worker from its replacement during model/provider switches
    private sealed class WorkerContext
    {
        public InferenceSession? Session;
        public RunOptions? RunOptions;
        public string[] InputNames = ["pixel_values"];
        public string[] OutputNames = ["predicted_depth"];
        public readonly OrtValue?[] InputValues = new OrtValue?[1];
        public int DepthIndex;
        public int ConfidenceIndex = -1;
        public int SkyIndex = -1;
        public int IntrinsicsIndex = -1;
        public int ExtrinsicsIndex = -1;
        public bool LoggedFirstInference;
        public float[]? PreviousDepth;
        public int PreviousWidth;
        public int PreviousHeight;
        public bool HasRange;
        public float SmoothedMin;
        public float SmoothedMax;
    }

    public DepthAnything()
    {
        OutputTexture.UpdateAction = Update;
        DepthTexture.UpdateAction = Update;
        NormalizedDepthTexture.UpdateAction = Update;
        ConfidenceTexture.UpdateAction = Update;
        SkyTexture.UpdateAction = Update;
        UpdateCount.UpdateAction = Update;
        MinDepth.UpdateAction = Update;
        MaxDepth.UpdateAction = Update;
        VerticalFov.UpdateAction = Update;
        InferenceMs.UpdateAction = Update;
        LatencyMs.UpdateAction = Update;
        Status.UpdateAction = Update;
        CamReference.UpdateAction = Update;
        CamReference.Value = this;
    }

    private void Update(EvaluationContext context)
    {
        var pendingStatus = _pendingStatus;
        if (pendingStatus != null)
        {
            Status.Value = pendingStatus;
            _pendingStatus = null;
        }

        var inputDirty = InputTexture.DirtyFlag.IsDirty;
        var inputTexture = InputTexture.GetValue(context);
        var enabled = Enabled.GetValue(context);
        var debug = Debug.GetValue(context);
        var modelSize = (ModelSize)ModelSizeParam.GetValue(context);
        var provider = (ExecutionProvider)ExecutionProviderParam.GetValue(context);
        _requestedInputSize = ((Resolution)InputResolution.GetValue(context)) switch
        {
            Resolution.R196 => 196,
            Resolution.R280 => 280,
            Resolution.R392 => 392,
            Resolution.R504 => 504,
            Resolution.R518 => 518,
            _ => 0
        };
        var outputFormat = (DepthOutputFormat)OutputFormat.GetValue(context);
        var enhanceContrast = EnhanceContrast.GetValue(context);
        var invertDepth = InvertDepth.GetValue(context);
        var matchRes = MatchInputResolution.GetValue(context);
        _matchInputResolution = matchRes;
        _workerOutputFormat = outputFormat;
        _workerEnhanceContrast = enhanceContrast;
        _workerInvertDepth = invertDepth;
        _workerSmoothing = Math.Clamp(Smoothing.GetValue(context), 0f, MaxSmoothing);
        _workerFieldOfViewDegrees = Math.Clamp(FieldOfView.GetValue(context), 1f, 179f);

        // Store values for UI dropdowns
        _uiOutputFormat = (int)outputFormat;
        _uiModelSize = (int)modelSize;
        _uiInputResolution = (int)InputResolution.GetValue(context);
        _uiExecutionProvider = (int)provider;

        if (!enabled || inputTexture == null || inputTexture.IsDisposed)
        {
            OutputTexture.Value = inputTexture;
            Status.Value = "Disabled";
            PauseProcessing();
            ClearOutputs();
            return;
        }

        OutputTexture.Value = inputTexture;

        if (_initFailed && (modelSize != _activeModelSize || provider != _activeProvider))
        {
            _initFailed = false;
        }

        if (!_initFailed && (_processingTask == null || _processingTask.IsCompleted || modelSize != _activeModelSize || provider != _activeProvider))
        {
            InitializeWorker(debug, modelSize, provider);
        }

        var modelInfo = _modelInfo;
        if (modelInfo == null)
        {
            if (!_isInitializing)
            {
                Status.Value = _initFailureMessage ?? "Failed to initialize ONNX Runtime";
                ClearOutputs();
            }

            return;
        }

        // Only infer when the input texture actually changed; changes that arrive
        // while the worker is busy are latched and picked up when it frees
        if (inputDirty || !ReferenceEquals(inputTexture, _lastInputTexture))
        {
            _hasPendingInput = true;
        }

        if (_hasPendingInput && !_requestInFlight && !_preprocessor.IsPending && _inputQueue.IsEmpty)
        {
            BeginCapture(inputTexture, modelInfo, modelSize);
        }

        if (_preprocessor.IsPending)
        {
            CompleteCapture(inputTexture);
        }

        var generation = Volatile.Read(ref _workerGeneration);
        while (_outputQueue.TryDequeue(out var result))
        {
            if (result == null)
                continue;

            // Results from a replaced worker may have a different size and belong to a disposed model
            if (result.Generation != generation)
            {
                ReturnResultBuffers(result);
                continue;
            }

            if (result.Error != null)
            {
                Status.Value = "Error: " + result.Error;
                ReturnResultBuffers(result);
            }
            else if (result.DepthData != null)
            {
                ReturnResultBuffers(_currentResult);
                _currentResult = result;
                _updateCount++;
                InferenceMs.Value = result.InferenceMs;
                LatencyMs.Value = (float)((Stopwatch.GetTimestamp() - result.CaptureTicks) * 1000.0 / Stopwatch.Frequency);
                VerticalFov.Value = result.VerticalFovDegrees;
                Status.Value = $"Processing {_updateCount}";
            }
            else
            {
                ReturnResultBuffers(result);
            }
        }

        if (_currentResult != null && _currentResult.DepthData != null)
        {
            // Regenerating the textures is a full-res upload -
            // skip it while neither the data nor the formatting inputs changed
            if (_currentResult != _appliedResult || outputFormat != _appliedFormat || enhanceContrast != _appliedEnhance || invertDepth != _appliedInvert || matchRes != _appliedMatchRes)
            {
                if (_currentResult == _appliedResult && debug)
                {
                    Log.Debug($"[DepthAnything] Reapplying outputs - format={outputFormat}, enhance={enhanceContrast}, invert={invertDepth}", this);
                }

                ApplyResult(_currentResult, outputFormat, enhanceContrast, invertDepth);
                _appliedResult = _currentResult;
                _appliedFormat = outputFormat;
                _appliedEnhance = enhanceContrast;
                _appliedInvert = invertDepth;
                _appliedMatchRes = matchRes;
            }

            UpdateCount.Value = _updateCount;
            UpdateCameraReference(context, _currentResult, debug);
        }
        else
        {
            ClearOutputs();
        }
    }

    private void ClearOutputs()
    {
        ReturnResultBuffers(_currentResult);
        _currentResult = null;
        _appliedResult = null;
        DepthTexture.Value = null;
        NormalizedDepthTexture.Value = null;
        ConfidenceTexture.Value = null;
        SkyTexture.Value = null;
        MinDepth.Value = 0.0f;
        MaxDepth.Value = 0.0f;
        VerticalFov.Value = 0.0f;
        ResetCamera();
        if (string.IsNullOrEmpty(Status.Value))
            Status.Value = "No data";
    }

    /// <summary>
    /// Publishes the camera estimated by the model. The model outputs world-to-camera
    /// extrinsics (3x4 or 4x4); the camera position is -R^T t, its forward axis is R's
    /// third row and its image-up axis is -R's second row (DA3 uses an OpenCV-style
    /// y-down camera). Without an extrinsics output we fall back to the reference pose
    /// at the origin looking along +Z, which is what a single image predicts anyway.
    /// </summary>
    private void UpdateCameraReference(EvaluationContext context, DepthEstimationResult? result, bool debug)
    {
        var hasPose = result is { HasCameraPose: true };
        var position = hasPose ? result!.CameraPosition : Vector3.Zero;
        var forward = hasPose ? result!.CameraForward : Vector3.UnitZ;
        var up = hasPose ? result!.CameraUp : Vector3.UnitY;

        var fov = result?.VerticalFovDegrees ?? 0;
        if (fov <= 0)
            fov = _workerFieldOfViewDegrees;
        if (fov <= 0)
            fov = 50;

        var aspect = result is { Height: > 0 } ? (float)result.Width / result.Height : 1f;

        LastObjectToWorld = context.ObjectToWorld;
        SetCameraPose(position, forward, up, fov, aspect);

        if (debug && !_loggedCameraPose)
        {
            _loggedCameraPose = true;
            Log.Debug(hasPose
                          ? $"[DepthAnything] CamReference pose pos={position} target={position + forward} up={up} fov={fov:F1}"
                          : "[DepthAnything] CamReference: model has no extrinsics output; using the reference pose", this);
        }
    }

    private void ResetCamera()
    {
        SetCameraPose(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, _workerFieldOfViewDegrees > 0 ? _workerFieldOfViewDegrees : 50, 1f);
    }

    private void SetCameraPose(Vector3 position, Vector3 forward, Vector3 up, float fovDegrees, float aspect)
    {
        if (forward.LengthSquared() < 1e-8f)
            forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);

        up -= forward * Vector3.Dot(up, forward);
        if (up.LengthSquared() < 1e-8f)
            up = MathF.Abs(forward.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        up = Vector3.Normalize(up);

        _cameraDefinition = new CameraDefinition
                                {
                                    NearFarClip = new Vector2(0.01f, 1000f),
                                    LensShift = Vector2.Zero,
                                    PositionOffset = Vector3.Zero,
                                    Position = position,
                                    Target = position + forward,
                                    Up = up,
                                    AspectRatio = MathF.Max(aspect, 0.0001f),
                                    FieldOfView = Math.Clamp(fovDegrees, 1f, 179f) * MathF.PI / 180f,
                                    Roll = 0,
                                    RotationOffset = Vector3.Zero,
                                    OffsetAffectsTarget = false
                                };
        _cameraDefinition.BuildProjectionMatrices(out var camToClipSpace, out var worldToCamera);

        CameraPosition = position;
        CameraTarget = position + forward;
        CameraRoll = 0;
        CameraToClipSpace = camToClipSpace;
        WorldToCamera = worldToCamera;
        CamReference.Value = this;
    }

    private static CameraPose ReadCameraPose(WorkerContext workerContext, IReadOnlyList<OrtValue> outputs)
    {
        if (workerContext.ExtrinsicsIndex < 0 || workerContext.ExtrinsicsIndex >= outputs.Count)
            return default;

        var value = outputs[workerContext.ExtrinsicsIndex];
        var info = value.GetTensorTypeAndShape();
        if (info.ElementDataType != TensorElementType.Float || info.Shape.Length < 2)
            return default;

        var rows = (int)info.Shape[^2];
        var columns = (int)info.Shape[^1];
        if (rows < 3 || columns < 4)
            return default;

        var data = value.GetTensorDataAsSpan<float>();
        if (data.Length < rows * columns)
            return default;

        // The first view's 3x4 block is the top of the tensor; a 4x4 export has the
        // same top three rows, so both layouts read the same values.
        float r11 = data[0], r12 = data[1], r13 = data[2], tx = data[3];
        float r21 = data[columns], r22 = data[columns + 1], r23 = data[columns + 2], ty = data[columns + 3];
        float r31 = data[2 * columns], r32 = data[2 * columns + 1], r33 = data[2 * columns + 2], tz = data[2 * columns + 3];

        var position = new Vector3(
            -(r11 * tx + r21 * ty + r31 * tz),
            -(r12 * tx + r22 * ty + r32 * tz),
            -(r13 * tx + r23 * ty + r33 * tz));
        var forward = new Vector3(r31, r32, r33);
        var up = new Vector3(-r21, -r22, -r23);

        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || !float.IsFinite(forward.X) || !float.IsFinite(forward.Y) || !float.IsFinite(forward.Z)
            || !float.IsFinite(up.X) || !float.IsFinite(up.Y) || !float.IsFinite(up.Z))
            return default;

        return new CameraPose(true, position, forward, up);
    }

    private readonly record struct CameraPose(bool HasPose, Vector3 Position, Vector3 Forward, Vector3 Up);

    private void ReturnResultBuffers(DepthEstimationResult? result)
    {
        if (result == null)
            return;

        ReturnDepthBuffer(result.DepthData);
        ReturnDepthBuffer(result.ConfidenceData);
        ReturnDepthBuffer(result.SkyData);
        ReturnBufferToPool(result.ColorData);
        result.DepthData = null;
        result.ConfidenceData = null;
        result.SkyData = null;
        result.ColorData = null;
    }

    #region Capture

    private void BeginCapture(Texture2D inputTexture, ModelInfo modelInfo, ModelSize modelSize)
    {
        var description = inputTexture.Description;
        GetInputDimensions(modelInfo, description.Width, description.Height, modelSize, _requestedInputSize, out var width, out var height);

        if (modelInfo.InputIsBytes)
        {
            if (_inputBytes == null || _inputBytes.Length != 3 * width * height)
            {
                _inputBytes = new byte[3 * width * height];
            }
        }
        else if (_inputData == null || _inputData.Length != 3 * width * height)
        {
            _inputData = new float[3 * width * height];
        }

        var layout = modelInfo.InputIsBytes ? DepthInputPreprocessor.InputLayout.PackedBytes : DepthInputPreprocessor.InputLayout.PlanarFloat;
        if (!_preprocessor.TryBegin(inputTexture, width, height, layout))
        {
            if (!_loggedCaptureFailure)
            {
                _loggedCaptureFailure = true;
                Log.Warning($"[DepthAnything] Can't prepare the input texture ({description.Format}): {_preprocessor.Error}", this);
            }

            _hasPendingInput = false;
            return;
        }

        _loggedCaptureFailure = false;
        _captureWidth = width;
        _captureHeight = height;
        _captureSourceWidth = description.Width;
        _captureSourceHeight = description.Height;
        _captureTicks = Stopwatch.GetTimestamp();
        _captureIsBytes = modelInfo.InputIsBytes;
    }

    private void CompleteCapture(Texture2D inputTexture)
    {
        var inputData = _captureIsBytes ? null : _inputData;
        var inputBytes = _captureIsBytes ? _inputBytes : null;
        if (inputData == null && inputBytes == null)
        {
            _preprocessor.Cancel();
            return;
        }

        var state = inputBytes != null ? _preprocessor.TryComplete(inputBytes) : _preprocessor.TryComplete(inputData!);
        switch (state)
        {
            case DepthInputPreprocessor.ReadbackState.Ready:
                _requestInFlight = true;
                _hasPendingInput = false;
                _lastInputTexture = inputTexture;
                _inputQueue.Enqueue(new DepthEstimationRequest
                                        {
                                            InputData = inputData,
                                            InputBytes = inputBytes,
                                            Width = _captureWidth,
                                            Height = _captureHeight,
                                            SourceWidth = _captureSourceWidth,
                                            SourceHeight = _captureSourceHeight,
                                            CaptureTicks = _captureTicks,
                                            Generation = Volatile.Read(ref _workerGeneration)
                                        });
                _workerWake.Set();
                break;

            case DepthInputPreprocessor.ReadbackState.TimedOut:
                Log.Warning("[DepthAnything] Readback never completed - restarting readback", this);
                _hasPendingInput = true;
                break;
        }
    }

    /** Dynamic-size models take the image's aspect with its long side at the requested size; fixed-size exports dictate their own. */
    private static void GetInputDimensions(ModelInfo modelInfo, int sourceWidth, int sourceHeight, ModelSize modelSize, int requestedSize,
                                           out int width, out int height)
    {
        if (modelInfo.DeclaredWidth > 0 && modelInfo.DeclaredHeight > 0)
        {
            width = modelInfo.DeclaredWidth;
            height = modelInfo.DeclaredHeight;
            return;
        }

        var targetSize = GetEffectiveInputSize(requestedSize, modelSize);
        var aspectRatio = (float)sourceWidth / sourceHeight;
        if (aspectRatio > 1.0f)
        {
            width = targetSize;
            height = Math.Max(PatchSize, (int)(targetSize / aspectRatio) / PatchSize * PatchSize);
        }
        else
        {
            height = targetSize;
            width = Math.Max(PatchSize, (int)(targetSize * aspectRatio) / PatchSize * PatchSize);
        }
    }

    private static int GetEffectiveInputSize(int requestedSize, ModelSize modelSize)
    {
        if (requestedSize > 0)
        {
            return requestedSize;
        }

        return modelSize switch
        {
            ModelSize.Small or ModelSize.SmallFp32 or ModelSize.SmallInt8 => 392,
            ModelSize.Da3Small or ModelSize.Da3Base or ModelSize.Da3MonoLarge or ModelSize.Da3MetricLarge => 504,
            _ => DefaultInputSize
        };
    }

    #endregion

    #region ONNX Runtime Integration

    // The ViT backbones need a size divisible by the patch size
    private const int PatchSize = 14;
    private const int DefaultInputSize = 518;

    /** Reference camera focal length, in pixels, the metric model predicts depth for. */
    private const float MetricReferenceFocalPx = 300f;
    private const float MaxSmoothing = 0.95f;

    // Published by the worker once its session is warm; null while loading or after a failure
    private volatile ModelInfo? _modelInfo;

    private Task? _processingTask;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ConcurrentQueue<DepthEstimationRequest> _inputQueue = new();
    private readonly ConcurrentQueue<DepthEstimationResult?> _outputQueue = new();
    private DepthEstimationResult? _currentResult;
    private readonly object _workerLock = new object();
    private ModelSize _activeModelSize;
    private int _updateCount;

    private volatile bool _isInitializing;
    private volatile bool _initFailed;
    private volatile string? _initFailureMessage;
    private volatile bool _requestInFlight;
    private volatile string _activeProviderLabel = "CPU";

    // Render-thread only
    private bool _hasPendingInput;
    private Texture2D? _lastInputTexture;
    private ExecutionProvider _activeProvider;
    private bool _appliedMatchRes;

    // Volatile settings the worker reads when producing a result
    private volatile bool _matchInputResolution;
    private volatile DepthOutputFormat _workerOutputFormat;
    private volatile bool _workerEnhanceContrast;
    private volatile bool _workerInvertDepth;
    private volatile float _workerSmoothing;
    private volatile float _workerFieldOfViewDegrees;

    // Requested inference size from the Resolution input, 0 = auto
    private volatile int _requestedInputSize;

    // Wakes the worker instead of polling; never disposed - an exiting worker may still wait on it
    private readonly AutoResetEvent _workerWake = new(false);
    private int _workerGeneration;
    private volatile string? _pendingStatus;

    // Render-thread side of the GPU input preparation. Only one capture is ever in flight, so the tensor
    // array is free to be rewritten whenever no request is.
    private readonly DepthInputPreprocessor _preprocessor = new();
    private float[]? _inputData;
    private byte[]? _inputBytes;
    private int _captureWidth;
    private int _captureHeight;
    private int _captureSourceWidth;
    private int _captureSourceHeight;
    private long _captureTicks;
    private bool _captureIsBytes;
    private bool _loggedCaptureFailure;

    // Owned by the render thread - outputs only regenerate when these change
    private DepthEstimationResult? _appliedResult;
    private DepthOutputFormat _appliedFormat;
    private bool _appliedEnhance;
    private bool _appliedInvert;
    private byte[]? _normalizedBuffer;

    private SizeClassBufferPool? _bufferPool;

    // Stored values for UI dropdowns (updated each frame, read by UI thread)
    private int _uiOutputFormat;
    private int _uiModelSize;
    private int _uiInputResolution;
    private int _uiExecutionProvider;

    private void InitializeWorker(bool debug, ModelSize modelSize, ExecutionProvider provider)
    {
        var previousTask = _processingTask;
        StopWorker(debug);
        _activeModelSize = modelSize;
        _activeProvider = provider;
        _initFailed = false;
        _initFailureMessage = null;
        _requestInFlight = false;
        _hasPendingInput = true;
        _loggedCameraPose = false;

        var modelFile = ResolveModelFile(modelSize, debug, out var modelPath);
        if (modelPath == null)
        {
            _initFailed = true;
            return;
        }

        _isInitializing = true;
        Status.Value = "Loading model...";

        var generation = Interlocked.Increment(ref _workerGeneration);
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;
        _bufferPool = new SizeClassBufferPool();
        _processingTask = Task.Run(async () =>
        {
            // The previous worker releases its session only when its loop exits. Waiting here keeps the render
            // thread free while preventing two models from holding GPU memory at the same time.
            if (previousTask != null)
            {
                try
                {
                    await previousTask.ConfigureAwait(false);
                }
                catch
                {
                    // A failing predecessor must not prevent this generation from starting
                }
            }

            if (token.IsCancellationRequested)
                return;

            WorkerLoop(modelPath, modelFile, modelSize, provider, token, generation, debug);
        }, CancellationToken.None);
    }

    private void PublishStatus(string status)
    {
        _pendingStatus = status;
    }

    /** Candidate file names per model, in order of preference. A DA3 selection falls back to the V2 model of similar size. */
    private static string[] GetModelCandidates(ModelSize modelSize)
    {
        return modelSize switch
        {
            ModelSize.Small => ["depth-anything-v2-small-fp16.onnx"],
            ModelSize.SmallFp32 => ["depth-anything-v2-small-fp32.onnx"],
            ModelSize.SmallInt8 => ["depth-anything-v2-small-int8.onnx"],
            ModelSize.Base => ["depth-anything-v2-base-fp16.onnx"],
            ModelSize.Large => ["depth-anything-v2-large-fp16.onnx"],
            ModelSize.Da3Small => ["da3-small-fp16.onnx", "da3-small.onnx", "da3-small-int8.onnx", "depth-anything-v2-small-fp16.onnx"],
            ModelSize.Da3Base => ["da3-base-fp16.onnx", "da3-base.onnx", "depth-anything-v2-base-fp16.onnx"],
            ModelSize.Da3MonoLarge => ["da3-mono-large-fp16.onnx", "da3-mono-large-int8.onnx", "depth-anything-v2-large-fp16.onnx"],
            ModelSize.Da3MetricLarge => ["da3-metric-large-fp16.onnx"],
            _ => ["depth-anything-v2-small-fp16.onnx"]
        };
    }

    private ModelFile ResolveModelFile(ModelSize modelSize, bool debug, out string? modelPath)
    {
        var candidates = GetModelCandidates(modelSize);
        foreach (var candidate in candidates)
        {
            var path = FindModelPath(candidate, debug);
            if (path == null)
                continue;

            var isV3 = candidate.StartsWith("da3-", StringComparison.Ordinal);
            if (!isV3 && modelSize >= ModelSize.Da3Small)
            {
                // The enum is persisted in project files. A value from a newer build must not turn
                // into an IndexOutOfRangeException on the evaluation thread.
                var requestedName = Enum.IsDefined(typeof(ModelSize), modelSize) ? modelSize.ToString() : ((int)modelSize).ToString();
                Log.Warning($"[DepthAnything] No Depth Anything 3 model installed for {requestedName} - using {candidate}. " +
                            "Run Scripts/download_models.py in the DepthAnything package to install one.", this);
            }

            modelPath = path;
            return new ModelFile(candidate, isV3, candidate.Contains("metric", StringComparison.Ordinal));
        }

        var status = $"Model not found: {candidates[0]}";
        if (debug) Log.Error($"[DepthAnything] {status}. Place the model in Assets/ folder.", this);
        Status.Value = status;
        // Update would otherwise overwrite this with a generic init-failure message on the same frame
        _initFailureMessage = status;
        modelPath = null;
        return default;
    }

    private string? FindModelPath(string modelName, bool debug)
    {
        // Try AssetRegistry first
        try
        {
            if (AssetRegistry.TryResolveAddress($"DepthAnything:{modelName}", this, out var resolvedPath, out _, logWarnings: false))
            {
                return resolvedPath;
            }
        }
        catch { }

        var asmDir = System.IO.Path.GetDirectoryName(typeof(DepthAnything).Assembly.Location) ?? "";
        var possiblePaths = new[]
        {
            System.IO.Path.Combine(asmDir, "Assets", modelName),
            System.IO.Path.Combine(asmDir, "..", "Assets", modelName),
            System.IO.Path.Combine(asmDir, "..", "..", "Assets", modelName),
            System.IO.Path.Combine(asmDir, "..", "..", "..", "Assets", modelName),
            System.IO.Path.Combine(asmDir, modelName),
            $"Assets/{modelName}",
            $"./Assets/{modelName}",
            $"../Assets/{modelName}"
        };

        foreach (var path in possiblePaths)
        {
            if (System.IO.File.Exists(path))
            {
                var fullPath = System.IO.Path.GetFullPath(path);
                if (debug) Log.Debug($"[DepthAnything] Found model at: {fullPath}", this);
                return fullPath;
            }
        }

        return null;
    }

    private void WorkerLoop(string modelPath, ModelFile modelFile, ModelSize modelSize, ExecutionProvider provider, CancellationToken token, int generation, bool debug)
    {
        if (debug) Log.Debug("[DepthAnything] Worker loop started", this);

        var workerContext = new WorkerContext();
        var failedToInit = false;
        try
        {
            var modelInfo = InitializeSession(workerContext, modelFile, modelPath, provider, token, debug);
            if (modelInfo == null || workerContext.Session == null)
            {
                failedToInit = true;
            }
            else
            {
                WarmUpSession(workerContext, modelInfo, modelSize, token, debug);

                // Published only after warm-up so request acceptance coincides with a usable session
                _modelInfo = modelInfo;

                var size = modelInfo.DeclaredWidth > 0 ? $"{modelInfo.DeclaredWidth}x{modelInfo.DeclaredHeight}" : "dynamic size";
                PublishStatus($"Ready ({_activeProviderLabel}, {modelFile.Name}, {size})");
                Log.Info($"[DepthAnything] Ready ({_activeProviderLabel}) - {modelFile.Name}, input '{workerContext.InputNames[0]}' " +
                         $"[{string.Join(", ", workerContext.Session.InputMetadata[workerContext.InputNames[0]].Dimensions)}], outputs {string.Join(", ", workerContext.OutputNames)}", this);
            }

            while (workerContext.Session != null && !token.IsCancellationRequested)
            {
                if (_inputQueue.TryDequeue(out var request))
                {
                    // A request captured for a replaced model carries the old input layout; it must not reach
                    // the new session (the render thread's in-flight flag belongs to the new generation).
                    if (request.Generation != generation)
                    {
                        if (debug)
                            Log.Debug($"[DepthAnything] Dropping stale request (generation {request.Generation}, worker {generation})", this);
                        continue;
                    }

                    if ((request.InputData != null || request.InputBytes != null) && workerContext.Session != null)
                    {
                        var result = ProcessFrame(workerContext, request, modelInfo ?? new ModelInfo(), debug);
                        if (result != null)
                            result.Generation = generation;

                        if (token.IsCancellationRequested)
                        {
                            // A cancelled generation must not feed results into its successor
                            ReturnResultBuffers(result);
                        }
                        else
                        {
                            _outputQueue.Enqueue(result);
                        }
                    }

                    _requestInFlight = false;
                }
                else
                {
                    _workerWake.WaitOne(15);
                }
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                if (debug) Log.Error($"[DepthAnything] Worker loop error: {ex.Message}", this);
                _outputQueue.Enqueue(new DepthEstimationResult { Error = ex.Message, Generation = generation });
            }

            _requestInFlight = false;
        }
        finally
        {
            // Owns its session - dispose unconditionally; a newer worker may have replaced the model info
            workerContext.RunOptions?.Dispose();
            workerContext.Session?.Dispose();

            // Stale generations must not clobber their successor's init state
            if (generation == Volatile.Read(ref _workerGeneration))
            {
                _modelInfo = null;
                _isInitializing = false;
                if (failedToInit)
                    _initFailed = true;
            }

            if (debug) Log.Debug("[DepthAnything] Worker loop stopped", this);
        }
    }

    private ModelInfo? InitializeSession(WorkerContext workerContext, ModelFile modelFile, string modelPath, ExecutionProvider provider, CancellationToken token, bool debug)
    {
        InferenceSession session;
        try
        {
            if (token.IsCancellationRequested)
            {
                return null;
            }

            session = CreateSession(modelPath, provider);
        }
        catch (Exception ex)
        {
            _initFailureMessage = $"Initialization error: {ex.Message}";
            Log.Warning($"[DepthAnything] {_initFailureMessage}", this);
            return null;
        }

        workerContext.Session = session;
        workerContext.RunOptions = new RunOptions();

        var inputName = FirstMetadataKey(session.InputMetadata) ?? "pixel_values";
        workerContext.InputNames = [inputName];

        var inputMetadata = session.InputMetadata[inputName];
        var dimensions = inputMetadata.Dimensions;
        var isFloatPlanar = inputMetadata.ElementType == typeof(float) && dimensions.Length == 4 && dimensions[1] == 3;
        var isBytePacked = inputMetadata.ElementType == typeof(byte) && dimensions.Length == 4 && dimensions[3] == 3;
        if (!isFloatPlanar && !isBytePacked)
        {
            _initFailureMessage = "Unsupported model input: expected float32 NCHW or uint8 NHWC RGB";
            Log.Warning($"[DepthAnything] Unsupported input for {modelFile.Name}: {inputMetadata.ElementType.Name} [{string.Join(", ", dimensions)}]", this);

            // Leaving the session assigned would keep the worker loop alive forever while _modelInfo
            // stays null: the operator would report "Loading model..." and never fail or retry.
            session.Dispose();
            workerContext.Session = null;
            workerContext.RunOptions = null;
            return null;
        }

        var outputNames = new List<string>(4);
        workerContext.DepthIndex = AddOutput(outputNames, session.OutputMetadata, ["depth", "predicted_depth"], true);
        workerContext.ConfidenceIndex = AddOutput(outputNames, session.OutputMetadata, ["depth_conf", "conf", "confidence"], false);
        workerContext.SkyIndex = AddOutput(outputNames, session.OutputMetadata, ["sky"], false);
        workerContext.IntrinsicsIndex = AddOutput(outputNames, session.OutputMetadata, ["intrinsics"], false);
        workerContext.ExtrinsicsIndex = AddOutput(outputNames, session.OutputMetadata, ["extrinsics"], false);
        workerContext.OutputNames = outputNames.ToArray();

        // NCHW is [batch, 3, height, width]; NHWC is [batch, height, width, 3]
        var heightIndex = isBytePacked ? 1 : 2;
        return new ModelInfo
                   {
                       DeclaredHeight = dimensions[heightIndex] > 0 ? dimensions[heightIndex] : 0,
                       DeclaredWidth = dimensions[heightIndex + 1] > 0 ? dimensions[heightIndex + 1] : 0,
                       IsV3 = modelFile.IsV3,
                       IsMetric = modelFile.IsMetric,
                       InputIsBytes = isBytePacked
                   };
    }

    /** Appends the first output called one of <paramref name="aliases"/> and returns its index; without a match, a required output falls back to the first one. */
    private static int AddOutput(List<string> outputNames, IReadOnlyDictionary<string, NodeMetadata> metadata, string[] aliases, bool required)
    {
        foreach (var alias in aliases)
        {
            if (metadata.ContainsKey(alias))
            {
                outputNames.Add(alias);
                return outputNames.Count - 1;
            }
        }

        if (!required)
            return -1;

        outputNames.Add(FirstMetadataKey(metadata) ?? aliases[0]);
        return outputNames.Count - 1;
    }

    private static string? FirstMetadataKey(IReadOnlyDictionary<string, NodeMetadata> metadata)
    {
        foreach (var key in metadata.Keys)
        {
            return key;
        }

        return null;
    }

    private void WarmUpSession(WorkerContext workerContext, ModelInfo modelInfo, ModelSize modelSize, CancellationToken token, bool debug)
    {
        var session = workerContext.Session;
        if (session == null || token.IsCancellationRequested)
            return;

        try
        {
            // First DML inference compiles kernels; paying that on a dummy frame keeps it off a real one
            int width = modelInfo.DeclaredWidth;
            int height = modelInfo.DeclaredHeight;
            if (width <= 0 || height <= 0)
            {
                width = height = GetEffectiveInputSize(_requestedInputSize, modelSize) / PatchSize * PatchSize;
            }

            using var inputValue = modelInfo.InputIsBytes
                                       ? OrtValue.CreateTensorValueFromMemory(new byte[3 * width * height], [1, height, width, 3])
                                       : OrtValue.CreateTensorValueFromMemory(new float[3 * width * height], [1, 3, height, width]);
            workerContext.InputValues[0] = inputValue;

            var stopwatch = Stopwatch.StartNew();
            using var outputs = session.Run(workerContext.RunOptions, workerContext.InputNames, workerContext.InputValues!, workerContext.OutputNames);
            stopwatch.Stop();

            Log.Debug($"[DepthAnything] Warm-up inference ({width}x{height}) took {stopwatch.ElapsedMilliseconds}ms", this);
        }
        catch (Exception ex)
        {
            // Best-effort - a failure here resurfaces on the first real frame
            if (debug) Log.Debug($"[DepthAnything] Warm-up skipped: {ex.Message}", this);
        }
    }

    private static void LogGpuAdapters()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            LogDxgiAdapters();
        }
        catch
        {
            // Adapter enumeration is diagnostic only
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void LogDxgiAdapters()
    {
        using var factory = new SharpDX.DXGI.Factory1();
        foreach (var adapter in factory.Adapters)
        {
            Log.Info($"[DepthAnything] GPU adapter: {adapter.Description.Description}");
            adapter.Dispose();
        }
    }

    private InferenceSession CreateSession(string modelPath, ExecutionProvider provider)
    {
        if (provider == ExecutionProvider.DirectMl)
        {
            LogGpuAdapters();
        }

        // Windows has a CUDA and a DirectML ONNX Runtime native; the first request of the process binds one
        var wantedBuild = provider == ExecutionProvider.DirectMl ? OnnxNativeSelector.Build.DirectMl : OnnxNativeSelector.Build.Gpu;
        var build = OnnxNativeSelector.Select(typeof(InferenceSession).Assembly, wantedBuild);
        if (OperatingSystem.IsWindows() && provider is ExecutionProvider.DirectMl or ExecutionProvider.Cuda or ExecutionProvider.TensorRt && build != wantedBuild)
        {
            Log.Warning($"[DepthAnything] {OnnxNativeSelector.Describe(wantedBuild)} needs its own ONNX Runtime build, but this session already loaded " +
                        $"the {OnnxNativeSelector.Describe(build)} one - restart TiXL to switch.", this);
        }

        // Without a thread cap the CPU provider saturates all cores during inference
        // and starves the render thread
        int intraOpThreads = Math.Min(4, Math.Max(1, Environment.ProcessorCount));

        // Walk the chain and keep the first provider that initializes; CPU is always last and always works
        foreach (var candidate in GetProviderChain(provider))
        {
            if (!IsProviderAvailableOnThisPlatform(candidate, build))
            {
                continue;
            }

            string cudaMissing = string.Empty;
            if (candidate is ExecutionProvider.Cuda or ExecutionProvider.TensorRt
                && ((OperatingSystem.IsLinux() && !LinuxCudaRuntime.TryPrepare(out cudaMissing))
                    || (OperatingSystem.IsWindows() && !WindowsCudaRuntime.TryPrepare(out cudaMissing))))
            {
                Log.Info($"[DepthAnything] {ExecutionProviderNames[(int)candidate]} unavailable, trying next provider: {cudaMissing}", this);
                continue;
            }

            // TensorRT's provider DLL imports nvinfer_10.dll by name; load the runtime before asking ORT for the provider
            if (candidate == ExecutionProvider.TensorRt)
            {
                var tensorRtReady = OperatingSystem.IsLinux()
                                        ? LinuxCudaRuntime.TryPrepareTensorRt(out var trtMissing, out var trtDirectory)
                                        : WindowsCudaRuntime.TryPrepareTensorRt(out trtMissing, out trtDirectory);
                if (!tensorRtReady)
                {
                    Log.Info($"[DepthAnything] TensorRT unavailable, trying next provider: {trtMissing}", this);
                    continue;
                }

                Log.Info($"[DepthAnything] TensorRT runtime at {trtDirectory}", this);
            }

            SessionOptions? options = null;
            try
            {
                options = new SessionOptions
                {
                    // ORT 1.24 fails to initialize fp16 transformer graphs (SimplifiedLayerNormFusion) on the CPU at ENABLE_ALL; EXTENDED costs nothing measurable
                    GraphOptimizationLevel = candidate == ExecutionProvider.Cpu ? GraphOptimizationLevel.ORT_ENABLE_EXTENDED : GraphOptimizationLevel.ORT_ENABLE_ALL,
                    IntraOpNumThreads = intraOpThreads
                };

                switch (candidate)
                {
                    case ExecutionProvider.DirectMl:
                        // Required by the DirectML provider: it neither plans memory ahead nor runs nodes in parallel
                        options.EnableMemoryPattern = false;
                        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                        options.AppendExecutionProvider_DML(0);
                        break;
                    case ExecutionProvider.Cuda:
                        // HEURISTIC avoids the exhaustive cuDNN autotune at session creation. The DA3 exports have
                        // fixed input sizes, so the algorithm is chosen once and stays.
                        using (var cudaOptions = new OrtCUDAProviderOptions())
                        {
                            cudaOptions.UpdateOptions(new Dictionary<string, string>
                            {
                                ["device_id"] = "0",
                                ["cudnn_conv_algo_search"] = "HEURISTIC",
                            });
                            options.AppendExecutionProvider_CUDA(cudaOptions);
                        }
                        break;
                    case ExecutionProvider.TensorRt:
                        // Engines are expensive to build; persist them per model. The models live inside the package,
                        // so the cache belongs in the user profile rather than next to the asset.
                        var cacheRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                        if (string.IsNullOrEmpty(cacheRoot))
                            cacheRoot = System.IO.Path.GetDirectoryName(modelPath) ?? ".";

                        var trtCache = System.IO.Path.Combine(cacheRoot, "TiXL", "trt_cache", System.IO.Path.GetFileNameWithoutExtension(modelPath));
                        System.IO.Directory.CreateDirectory(trtCache);
                        using (var trtOptions = new OrtTensorRTProviderOptions())
                        {
                            trtOptions.UpdateOptions(new Dictionary<string, string>
                            {
                                ["device_id"] = "0",
                                ["trt_max_workspace_size"] = "2147483648",
                                ["trt_engine_cache_enable"] = "1",
                                ["trt_engine_cache_path"] = trtCache,
                                ["trt_timing_cache_enable"] = "1",
                                ["trt_timing_cache_path"] = trtCache
                            });
                            options.AppendExecutionProvider_Tensorrt(trtOptions);
                        }

                        // TensorRT only claims the subgraphs it can accelerate; CUDA runs the rest instead of the CPU
                        options.AppendExecutionProvider_CUDA(0);
                        break;
                    case ExecutionProvider.CoreMl:
                        options.AppendExecutionProvider_CoreML(CoreMLFlags.COREML_FLAG_USE_NONE);
                        break;
                }

                // From the path rather than from bytes, so models with external weight files load too
                var session = new InferenceSession(modelPath, options);
                _activeProviderLabel = ExecutionProviderNames[(int)candidate];
                return session;
            }
            catch (Exception ex) when (candidate != ExecutionProvider.Cpu)
            {
                Log.Info($"[DepthAnything] {ExecutionProviderNames[(int)candidate]} unavailable, trying next provider: {ex.Message}", this);
                options?.Dispose();
            }
        }

        throw new InvalidOperationException("No ONNX execution provider could create a session");
    }

    private static ExecutionProvider[] GetProviderChain(ExecutionProvider requested)
    {
        return requested switch
        {
            ExecutionProvider.DirectMl => [ExecutionProvider.DirectMl, ExecutionProvider.Cuda, ExecutionProvider.TensorRt, ExecutionProvider.CoreMl, ExecutionProvider.Cpu],
            ExecutionProvider.Cuda => [ExecutionProvider.Cuda, ExecutionProvider.TensorRt, ExecutionProvider.CoreMl, ExecutionProvider.Cpu],
            ExecutionProvider.TensorRt => [ExecutionProvider.TensorRt, ExecutionProvider.Cuda, ExecutionProvider.CoreMl, ExecutionProvider.Cpu],
            ExecutionProvider.CoreMl => [ExecutionProvider.CoreMl, ExecutionProvider.Cpu],
            _ => [ExecutionProvider.Cpu]
        };
    }

    /** DirectML exists only in the Windows DirectML build, CUDA in the Windows GPU build and on Linux, CoreML only on macOS. */
    private static bool IsProviderAvailableOnThisPlatform(ExecutionProvider provider, OnnxNativeSelector.Build build)
    {
        return provider switch
        {
            ExecutionProvider.DirectMl => OperatingSystem.IsWindows() && build == OnnxNativeSelector.Build.DirectMl,
            ExecutionProvider.Cuda or ExecutionProvider.TensorRt => (OperatingSystem.IsWindows() && build == OnnxNativeSelector.Build.Gpu) || OperatingSystem.IsLinux(),
            ExecutionProvider.CoreMl => OperatingSystem.IsMacOS(),
            _ => true
        };
    }

    private DepthEstimationResult ProcessFrame(WorkerContext workerContext, DepthEstimationRequest request, ModelInfo modelInfo, bool debug)
    {
        // Rented buffers handed to the returned result; the catch path returns anything not attached
        float[]? resultDepth = null;
        float[]? resultConfidence = null;
        float[]? resultSky = null;
        byte[]? colorData = null;
        try
        {
            var session = workerContext.Session;
            if (session == null || (request.InputData == null && request.InputBytes == null))
            {
                return ErrorResult(request, "No session or pixel data");
            }

            // A layout mismatch means the request came from a different model; fail clearly instead of an ORT type error
            if ((request.InputBytes != null) != modelInfo.InputIsBytes)
            {
                return ErrorResult(request, modelInfo.InputIsBytes
                                                ? "Input format mismatch: the model expects uint8 NHWC"
                                                : "Input format mismatch: the model expects float NCHW");
            }

            using var inputValue = request.InputBytes != null
                                       ? OrtValue.CreateTensorValueFromMemory(request.InputBytes, [1, request.Height, request.Width, 3])
                                       : OrtValue.CreateTensorValueFromMemory(request.InputData!, [1, 3, request.Height, request.Width]);
            workerContext.InputValues[0] = inputValue;

            var stopwatch = Stopwatch.StartNew();
            using var outputs = session.Run(workerContext.RunOptions, workerContext.InputNames, workerContext.InputValues!, workerContext.OutputNames);
            stopwatch.Stop();

            var depthData = CopyPlane(outputs[workerContext.DepthIndex], out int outputWidth, out int outputHeight);
            if (depthData == null)
            {
                return ErrorResult(request, "Inference did not produce a float depth tensor");
            }

            resultDepth = depthData;
            int count = outputWidth * outputHeight;

            if (workerContext.ConfidenceIndex >= 0)
            {
                resultConfidence = CopyPlane(outputs[workerContext.ConfidenceIndex], out _, out _);
            }

            if (workerContext.SkyIndex >= 0)
            {
                resultSky = CopyPlane(outputs[workerContext.SkyIndex], out _, out _);
            }

            var verticalFov = ReadVerticalFov(workerContext, outputs, outputHeight, modelInfo, outputWidth);
            var cameraPose = ReadCameraPose(workerContext, outputs);

            if (modelInfo.IsMetric)
            {
                // The metric model predicts depth as seen by a 300 px reference camera; the real focal length turns it into meters
                var focalPx = 0.5f * outputWidth / MathF.Tan(_workerFieldOfViewDegrees * MathF.PI / 360f);
                var scale = focalPx / MetricReferenceFocalPx;
                for (int i = 0; i < count; i++)
                {
                    resultDepth[i] *= scale;
                }
            }

            var smoothing = _workerSmoothing;
            SmoothDepth(workerContext, resultDepth, outputWidth, outputHeight, smoothing);
            ComputeMinMax(resultDepth, count, out float min, out float max);
            if (smoothing > 0 && workerContext.HasRange)
            {
                // The model's depth scale drifts between frames; a settled range keeps the visualization from pulsing
                min += (workerContext.SmoothedMin - min) * smoothing;
                max += (workerContext.SmoothedMax - max) * smoothing;
            }

            workerContext.SmoothedMin = min;
            workerContext.SmoothedMax = max;
            workerContext.HasRange = true;

            // At model resolution the GPU sampler upscales when the texture is
            // used downstream, which skips all full-resolution CPU work per result
            int resultWidth = outputWidth;
            int resultHeight = outputHeight;
            if (_matchInputResolution)
            {
                resultDepth = ResizePlane(depthData, outputWidth, outputHeight, request.SourceWidth, request.SourceHeight);
                ReturnDepthBuffer(depthData);
                resultWidth = request.SourceWidth;
                resultHeight = request.SourceHeight;

                if (resultConfidence != null)
                {
                    var resizedConfidence = ResizePlane(resultConfidence, outputWidth, outputHeight, resultWidth, resultHeight);
                    ReturnDepthBuffer(resultConfidence);
                    resultConfidence = resizedConfidence;
                }

                if (resultSky != null)
                {
                    var resizedSky = ResizePlane(resultSky, outputWidth, outputHeight, resultWidth, resultHeight);
                    ReturnDepthBuffer(resultSky);
                    resultSky = resizedSky;
                }
            }

            var outputFormat = _workerOutputFormat;
            var enhanceContrast = _workerEnhanceContrast;
            var invertDepth = _workerInvertDepth;
            colorData = GetBuffer(resultWidth * resultHeight * 4);
            ColorizeDepth(resultDepth, resultWidth * resultHeight, colorData, min, max, outputFormat, enhanceContrast, invertDepth, modelInfo.IsV3);

            if (!workerContext.LoggedFirstInference)
            {
                workerContext.LoggedFirstInference = true;
                Log.Debug($"[DepthAnything] First inference ok: {outputWidth}x{outputHeight}, {stopwatch.ElapsedMilliseconds}ms", this);
            }

            return new DepthEstimationResult
            {
                DepthData = resultDepth,
                ConfidenceData = resultConfidence,
                SkyData = resultSky,
                ColorData = colorData,
                Width = resultWidth,
                Height = resultHeight,
                Min = min,
                Max = max,
                VerticalFovDegrees = verticalFov,
                HasCameraPose = cameraPose.HasPose,
                CameraPosition = cameraPose.Position,
                CameraForward = cameraPose.Forward,
                CameraUp = cameraPose.Up,
                InferenceMs = (float)stopwatch.Elapsed.TotalMilliseconds,
                CaptureTicks = request.CaptureTicks,
                ColorFormat = outputFormat,
                ColorEnhance = enhanceContrast,
                ColorInvert = invertDepth,
                FarIsLarge = modelInfo.IsV3
            };
        }
        catch (Exception ex)
        {
            if (debug) Log.Error($"[DepthAnything] ProcessFrame error: {ex.Message}", this);
            ReturnDepthBuffer(resultDepth);
            ReturnDepthBuffer(resultConfidence);
            ReturnDepthBuffer(resultSky);
            ReturnBufferToPool(colorData);
            return ErrorResult(request, ex.Message);
        }
    }

    /** Vertical field of view in degrees from the model's camera estimate, or from the FieldOfView input for the metric model; 0 when unknown. */
    private float ReadVerticalFov(WorkerContext workerContext, IReadOnlyList<OrtValue> outputs, int outputHeight, ModelInfo modelInfo, int outputWidth)
    {
        if (workerContext.IntrinsicsIndex >= 0)
        {
            // Row-major 3x3 camera matrix on the output pixel grid: fx, 0, cx / 0, fy, cy / 0, 0, 1
            var intrinsics = outputs[workerContext.IntrinsicsIndex].GetTensorDataAsSpan<float>();
            if (intrinsics.Length >= 9)
            {
                var focalY = intrinsics[intrinsics.Length - 9 + 4];
                if (focalY > 0)
                {
                    return 2f * MathF.Atan(outputHeight / (2f * focalY)) * 180f / MathF.PI;
                }
            }
        }

        if (modelInfo.IsMetric)
        {
            var tanHalfHorizontal = MathF.Tan(_workerFieldOfViewDegrees * MathF.PI / 360f);
            return 2f * MathF.Atan(tanHalfHorizontal * outputHeight / outputWidth) * 180f / MathF.PI;
        }

        return 0f;
    }

    /** Copies a [.., H, W] float output into a pooled array; null when the tensor isn't float. */
    private float[]? CopyPlane(OrtValue value, out int width, out int height)
    {
        width = 0;
        height = 0;

        var info = value.GetTensorTypeAndShape();
        if (info.ElementDataType != TensorElementType.Float || info.Shape.Length < 2)
            return null;

        height = (int)info.Shape[^2];
        width = (int)info.Shape[^1];

        // Copy out before the result collection is disposed. Pooled arrays may exceed w*h
        // (size-class rounding) - downstream code must use explicit pixel counts
        var span = value.GetTensorDataAsSpan<float>();
        if (span.Length < width * height)
            return null;

        var buffer = TakeDepthBuffer(width * height);
        span.Slice(0, width * height).CopyTo(buffer);
        return buffer;
    }

    private static void SmoothDepth(WorkerContext workerContext, float[] depth, int width, int height, float smoothing)
    {
        var count = width * height;
        var previous = workerContext.PreviousDepth;
        if (smoothing > 0 && previous != null && workerContext.PreviousWidth == width && workerContext.PreviousHeight == height)
        {
            for (int i = 0; i < count; i++)
            {
                var current = depth[i];
                var last = previous[i];
                if (float.IsFinite(current) && float.IsFinite(last))
                {
                    depth[i] = current + (last - current) * smoothing;
                }
            }
        }

        if (previous == null || previous.Length != count)
        {
            previous = new float[count];
            workerContext.PreviousDepth = previous;
        }

        Array.Copy(depth, previous, count);
        workerContext.PreviousWidth = width;
        workerContext.PreviousHeight = height;
    }

    private static DepthEstimationResult ErrorResult(DepthEstimationRequest request, string message)
    {
        return new DepthEstimationResult
        {
            Width = request.Width,
            Height = request.Height,
            Error = message
        };
    }

    private float[] ResizePlane(float[] sourceDepth, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var resized = TakeDepthBuffer(targetWidth * targetHeight);

        float scaleX = (float)sourceWidth / targetWidth;
        float scaleY = (float)sourceHeight / targetHeight;

        Parallel.For(0, targetHeight, y =>
        {
            int srcY = Math.Min((int)(y * scaleY), sourceHeight - 1);
            int rowOffset = srcY * sourceWidth;
            int dstRow = y * targetWidth;
            for (int x = 0; x < targetWidth; x++)
            {
                int srcX = Math.Min((int)(x * scaleX), sourceWidth - 1);
                resized[dstRow + x] = sourceDepth[rowOffset + srcX];
            }
        });

        return resized;
    }

    private static void ComputeMinMax(float[] depthData, int count, out float minDepth, out float maxDepth)
    {
        float min = float.MaxValue;
        float max = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            float depth = depthData[i];
            if (!float.IsNaN(depth) && !float.IsInfinity(depth))
            {
                if (depth < min)
                    min = depth;
                if (depth > max)
                    max = depth;
            }
        }

        if (min == float.MaxValue)
        {
            min = 0.0f;
            max = 1.0f;
        }

        minDepth = min;
        maxDepth = max;
    }

    private void StopWorker(bool debug)
    {
        lock (_workerLock)
        {
            _cancellationTokenSource?.Cancel();
            try
            {
                // Never wait for a running inference on the render thread - the worker
                // disposes its own session when it notices the cancellation
                _processingTask?.Wait(0);
            }
            catch (Exception ex)
            {
                if (debug) Log.Error($"[DepthAnything] Error waiting for worker task: {ex.Message}", this);
            }

            _workerWake.Set();
            _preprocessor.Cancel();

            while (_inputQueue.TryDequeue(out _))
            {
            }

            while (_outputQueue.TryDequeue(out var stale))
            {
                ReturnResultBuffers(stale);
            }

            // Pauses request submission during re-init; the exiting worker disposes its own session
            _modelInfo = null;
            _requestInFlight = false;
        }
    }

    private void PauseProcessing()
    {
        // Disabling must not tear down the worker - re-enabling would reload the model
        if (_processingTask == null)
            return;

        _hasPendingInput = false;
        // Re-enabling only recaptures when the input turns dirty or its texture changes. A paused op that is
        // resumed on the same static texture would otherwise never capture again and stay blank forever.
        _lastInputTexture = null;
        _preprocessor.Cancel();

        while (_inputQueue.TryDequeue(out _))
        {
        }

        // Also drops results an in-flight request produced after it was paused
        while (_outputQueue.TryDequeue(out var stale))
        {
            ReturnResultBuffers(stale);
        }
    }

    #endregion

    #region Memory Management

    private byte[] GetBuffer(int size)
    {
        return _bufferPool?.RentByte(size) ?? new byte[size];
    }

    private void ReturnBufferToPool(byte[]? b)
    {
        if (b != null)
            _bufferPool?.ReturnByte(b);
    }

    private float[] TakeDepthBuffer(int length)
    {
        return _bufferPool?.RentFloat(length) ?? new float[length];
    }

    private void ReturnDepthBuffer(float[]? buffer)
    {
        if (buffer != null)
            _bufferPool?.ReturnFloat(buffer);
    }

    #endregion

    #region Output Updates

    private void ApplyResult(DepthEstimationResult result, DepthOutputFormat outputFormat, bool enhanceContrast, bool invertDepth)
    {
        var depthData = result.DepthData;
        int width = result.Width;
        int height = result.Height;

        // The pool guarantees >= w*h - a shortfall is a logic bug, not data to patch up
        if (depthData == null || depthData.Length < width * height)
        {
            Log.Warning($"[DepthAnything] Discarding malformed depth result ({depthData?.Length ?? 0} floats < {width * height})", this);
            ClearOutputs();
            return;
        }

        MinDepth.Value = result.Min;
        MaxDepth.Value = result.Max;

        // Update depth texture (R32_Float)
        UploadFloatTexture(ref _depthTexture, depthData, width, height);
        DepthTexture.Value = _depthTexture;

        ConfidenceTexture.Value = UploadOptionalPlane(ref _confidenceTexture, result.ConfidenceData, width, height);
        SkyTexture.Value = UploadOptionalPlane(ref _skyTexture, result.SkyData, width, height);

        // Update normalized depth texture (R8G8B8A8_UNorm)
        var colorData = result.ColorData;
        if (colorData != null && colorData.Length >= width * height * 4
            && result.ColorFormat == outputFormat && result.ColorEnhance == enhanceContrast && result.ColorInvert == invertDepth)
        {
            UploadRgbaTexture(colorData, width, height);
        }
        else
        {
            // The worker colorized with different settings - colorize on the spot
            int count = width * height;
            if (_normalizedBuffer == null || _normalizedBuffer.Length < count * 4)
            {
                _normalizedBuffer = new byte[count * 4];
            }

            ColorizeDepth(depthData, count, _normalizedBuffer, result.Min, result.Max, outputFormat, enhanceContrast, invertDepth, result.FarIsLarge);
            UploadRgbaTexture(_normalizedBuffer, width, height);
        }
    }

    private Texture2D? UploadOptionalPlane(ref Texture2D? texture, float[]? data, int width, int height)
    {
        if (data == null || data.Length < width * height)
            return null;

        UploadFloatTexture(ref texture, data, width, height);
        return texture;
    }

    private static unsafe void UploadFloatTexture(ref Texture2D? texture, float[] data, int width, int height)
    {
        if (texture == null || texture.Description.Width != width || texture.Description.Height != height)
        {
            texture?.Dispose();
            texture = CreateTexture(width, height, Format.R32_Float);
        }

        fixed (float* pointer = data)
        {
            ResourceManager.Device.ImmediateContext.UpdateSubresource(new DataBox((IntPtr)pointer, width * sizeof(float), 0), texture);
        }
    }

    private unsafe void UploadRgbaTexture(byte[] rgbaData, int width, int height)
    {
        if (_normalizedDepthTexture == null || _normalizedDepthTexture.Description.Width != width || _normalizedDepthTexture.Description.Height != height)
        {
            _normalizedDepthTexture?.Dispose();
            _normalizedDepthTexture = CreateTexture(width, height, Format.R8G8B8A8_UNorm);
        }

        fixed (byte* pointer = rgbaData)
        {
            ResourceManager.Device.ImmediateContext.UpdateSubresource(new DataBox((IntPtr)pointer, width * 4, 0), _normalizedDepthTexture);
        }

        NormalizedDepthTexture.Value = _normalizedDepthTexture;
    }

    private static Texture2D CreateTexture(int width, int height, Format format)
    {
        return new Texture2D(new T3.Graphics.Compat.Texture2D(ResourceManager.Device, new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            OptionFlags = ResourceOptionFlags.None
        }));
    }

    private static void ColorizeDepth(float[] depthData, int pixelCount, byte[] rgbaData, float minDepth, float maxDepth,
                                      DepthOutputFormat outputFormat, bool enhanceContrast, bool invertDepth, bool farIsLarge)
    {
        float range = maxDepth - minDepth;
        if (range < 0.0001f) range = 1.0f;

        for (int i = 0; i < pixelCount; i++)
        {
            float depth = depthData[i];
            if (float.IsNaN(depth) || float.IsInfinity(depth))
                depth = minDepth;

            float normalizedDepth = (depth - minDepth) / range;

            // V2 predicts disparity (near is large), V3 predicts depth (far is large); near stays bright for both
            if (farIsLarge)
                normalizedDepth = 1.0f - normalizedDepth;

            // Invert if requested
            if (invertDepth)
                normalizedDepth = 1.0f - normalizedDepth;

            // Enhance contrast
            if (enhanceContrast)
            {
                normalizedDepth = (normalizedDepth - 0.5f) * 2.0f;
            }

            normalizedDepth = Math.Clamp(normalizedDepth, 0.0f, 1.0f);
            byte depthByte = (byte)(normalizedDepth * 255);

            int rgbaIdx = i * 4;

            // Apply color format
            switch (outputFormat)
            {
                case DepthOutputFormat.Color:
                    // Mid-range pixels glow green, extremes drift toward red/blue.
                    // The green term goes negative near 0, so clamp before the cast.
                    int green = 255 - Math.Abs(depthByte - 128) * 2;
                    rgbaData[rgbaIdx + 0] = depthByte;
                    rgbaData[rgbaIdx + 1] = (byte)Math.Clamp(green, 0, 255);
                    rgbaData[rgbaIdx + 2] = (byte)(255 - depthByte);
                    rgbaData[rgbaIdx + 3] = 255;
                    break;

                case DepthOutputFormat.Rainbow:
                    var rgb = HsvToRgb(normalizedDepth, 1.0f, 1.0f);
                    rgbaData[rgbaIdx + 0] = (byte)(rgb.X * 255);
                    rgbaData[rgbaIdx + 1] = (byte)(rgb.Y * 255);
                    rgbaData[rgbaIdx + 2] = (byte)(rgb.Z * 255);
                    rgbaData[rgbaIdx + 3] = 255;
                    break;

                default:
                    rgbaData[rgbaIdx + 0] = depthByte;
                    rgbaData[rgbaIdx + 1] = depthByte;
                    rgbaData[rgbaIdx + 2] = depthByte;
                    rgbaData[rgbaIdx + 3] = 255;
                    break;
            }
        }
    }

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        if (s == 0)
            return new Vector3(v, v, v);

        float i = (int)(h * 6);
        float f = h * 6 - i;
        float p = v * (1 - s);
        float q = v * (1 - f * s);
        float t = v * (1 - (1 - f) * s);

        return i switch
        {
            0 => new Vector3(v, t, p),
            1 => new Vector3(q, v, p),
            2 => new Vector3(p, v, t),
            3 => new Vector3(p, q, v),
            4 => new Vector3(t, p, v),
            _ => new Vector3(v, p, q)
        };
    }

    private Texture2D? _depthTexture;
    private Texture2D? _confidenceTexture;
    private Texture2D? _skyTexture;
    private Texture2D? _normalizedDepthTexture;

    #endregion

    #region Input Parameters

    [Input(Guid = "12345678-1234-1234-1234-123456789012")]
    public readonly InputSlot<Texture2D> InputTexture = new();

    [Input(Guid = "23456789-2345-2345-2345-234567890123")]
    public readonly InputSlot<bool> Enabled = new(true);

    [Input(Guid = "34567890-3456-3456-3456-345678901234", MappedType = typeof(ModelSize))]
    public readonly InputSlot<int> ModelSizeParam = new((int)ModelSize.Da3Small);

    [Input(Guid = "90123456-9012-9012-9012-901234567890", MappedType = typeof(Resolution))]
    public readonly InputSlot<int> InputResolution = new((int)Resolution.Auto);

    [Input(Guid = "012345ab-cdef-0123-4567-89abcdef0123", MappedType = typeof(ExecutionProvider))]
    public readonly InputSlot<int> ExecutionProviderParam = new((int)ExecutionProvider.DirectMl);

    [Input(Guid = "234567cd-8912-9012-3456-6789012345ab")]
    public readonly InputSlot<bool> MatchInputResolution = new(false);

    [Input(Guid = "d5c7a0fc-9eab-4c6e-b058-a06e2c4d7fbd")]
    public readonly InputSlot<float> Smoothing = new(0.25f);

    [Input(Guid = "f7e9c21e-bbcd-4e80-8a7a-c2800e6f91df")]
    public readonly InputSlot<float> FieldOfView = new(60f);

    [Input(Guid = "45678901-4567-4567-4567-456789012345", MappedType = typeof(DepthOutputFormat))]
    public readonly InputSlot<int> OutputFormat = new();

    [Input(Guid = "56789012-5678-5678-5678-567890123456")]
    public readonly InputSlot<bool> EnhanceContrast = new(false);

    [Input(Guid = "67890123-6789-6789-6789-678901234567")]
    public readonly InputSlot<bool> InvertDepth = new(false);

    [Input(Guid = "78901234-7890-7890-7890-789012345678")]
    public readonly InputSlot<bool> Debug = new(false);

    #endregion

    #region Enums

    /// <summary>The numeric values are saved in projects; new models are only ever appended.</summary>
    public enum ModelSize
    {
        Small,
        Base,
        Large,
        SmallFp32,
        SmallInt8,
        Da3Small,
        Da3Base,
        Da3MonoLarge,
        Da3MetricLarge
    }

    /// <summary>
    /// Inference input resolution. Auto uses the model's native size; smaller
    /// sizes trade depth detail for speed. Values are multiples of the ViT patch size.
    /// Models exported at a fixed size ignore it.
    /// </summary>
    public enum Resolution
    {
        Auto,
        R196,
        R280,
        R392,
        R518,
        R504
    }

    public enum ExecutionProvider
    {
        Cpu,
        DirectMl,
        Cuda,
        CoreMl,
        TensorRt
    }

    public enum DepthOutputFormat
    {
        Grayscale,
        Color,
        Rainbow
    }

    #endregion

    #region ICustomDropdownHolder

    private static readonly string[] OutputFormatNames = { "Grayscale", "Color", "Rainbow" };

    private static readonly string[] ModelSizeNames =
        { "V2 Small", "V2 Base", "V2 Large", "V2 Small Fp32", "V2 Small Int8", "V3 Small", "V3 Base", "V3 Mono Large", "V3 Metric Large" };

    private static readonly string[] ResolutionNames = { "Auto", "196", "280", "392", "518", "504" };
    private static readonly string[] ExecutionProviderNames = { "CPU", "DirectML", "CUDA", "CoreML", "TensorRT" };

    string ICustomDropdownHolder.GetValueForInput(Guid inputId)
    {
        if (inputId == OutputFormat.Id)
        {
            var val = _uiOutputFormat;
            return val >= 0 && val < OutputFormatNames.Length ? OutputFormatNames[val] : "Grayscale";
        }

        if (inputId == ModelSizeParam.Id)
        {
            var val = _uiModelSize;
            return val >= 0 && val < ModelSizeNames.Length ? ModelSizeNames[val] : "V3 Small";
        }

        if (inputId == InputResolution.Id)
        {
            var val = _uiInputResolution;
            return val >= 0 && val < ResolutionNames.Length ? ResolutionNames[val] : "Auto";
        }

        if (inputId == ExecutionProviderParam.Id)
        {
            var val = _uiExecutionProvider;
            return val >= 0 && val < ExecutionProviderNames.Length ? ExecutionProviderNames[val] : "CPU";
        }

        return "";
    }

    IEnumerable<string> ICustomDropdownHolder.GetOptionsForInput(Guid inputId)
    {
        if (inputId == OutputFormat.Id)
        {
            foreach (var name in OutputFormatNames)
                yield return name;
            yield break;
        }

        if (inputId == ModelSizeParam.Id)
        {
            foreach (var name in ModelSizeNames)
                yield return name;
            yield break;
        }

        if (inputId == InputResolution.Id)
        {
            foreach (var name in ResolutionNames)
                yield return name;
            yield break;
        }

        if (inputId == ExecutionProviderParam.Id)
        {
            foreach (var name in ExecutionProviderNames)
                yield return name;
            yield break;
        }

        yield return "undefined";
    }

    void ICustomDropdownHolder.HandleResultForInput(Guid inputId, string? selected, bool isAListItem)
    {
        if (string.IsNullOrEmpty(selected))
            return;

        if (inputId == OutputFormat.Id)
        {
            var index = Array.IndexOf(OutputFormatNames, selected);
            if (index >= 0)
                OutputFormat.SetTypedInputValue(index);
            return;
        }

        if (inputId == ModelSizeParam.Id)
        {
            var index = Array.IndexOf(ModelSizeNames, selected);
            if (index >= 0)
                ModelSizeParam.SetTypedInputValue(index);
            return;
        }

        if (inputId == InputResolution.Id)
        {
            var index = Array.IndexOf(ResolutionNames, selected);
            if (index >= 0)
                InputResolution.SetTypedInputValue(index);
            return;
        }

        if (inputId == ExecutionProviderParam.Id)
        {
            var index = Array.IndexOf(ExecutionProviderNames, selected);
            if (index >= 0)
                ExecutionProviderParam.SetTypedInputValue(index);
            return;
        }
    }

    #endregion

    #region Camera reference

    public Vector3 CameraPosition { get; set; }
    public Vector3 CameraTarget { get; set; }
    public float CameraRoll { get; set; }
    public CameraDefinition CameraDefinition => _cameraDefinition;
    public Matrix4x4 WorldToCamera { get; set; }
    public Matrix4x4 CameraToClipSpace { get; set; }
    public Matrix4x4 LastObjectToWorld { get; set; }

    private CameraDefinition _cameraDefinition = new();
    private bool _loggedCameraPose;

    #endregion

    #region Cleanup

    protected override void Dispose(bool isDisposing)
    {
        if (!isDisposing) return;

        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch { }

        _workerWake.Set();
        // The session is disposed by the worker's own exit path - disposing it here
        // could race an in-flight Run on the worker thread

        _preprocessor.Dispose();

        _depthTexture?.Dispose();
        _depthTexture = null;

        _confidenceTexture?.Dispose();
        _confidenceTexture = null;

        _skyTexture?.Dispose();
        _skyTexture = null;

        _normalizedDepthTexture?.Dispose();
        _normalizedDepthTexture = null;

        base.Dispose(isDisposing);
    }

    #endregion
}
