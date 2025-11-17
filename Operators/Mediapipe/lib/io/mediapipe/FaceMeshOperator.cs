using Mediapipe;
using Mediapipe.Core;
using Mediapipe.Framework.Formats;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Tasks.Components.Containers;
using System;
using System.Runtime.InteropServices;
using Mediapipe.Framework.Port;
using T3.Core.Resource;
using T3.Core.Operator.Attributes;
using System.Collections.Concurrent;
using System.Threading;
using System.IO;

namespace t3.mediapipe.lib.io.mediapipe
{
    [Operator(Guid = "a1b2c3d4-e5f6-7890-1234-567890abcdef")]
    [Guid("a1b2c3d4-e5f6-7890-1234-567890abcdef")]
    public sealed class FaceMeshOperator : T3.Core.Operator.Instance<FaceMeshOperator>
    {
        #region Inputs and Outputs
        
        [Input(Guid = "12345678-1234-5678-9ABC-DEF123456789")]
        public readonly T3.Core.Operator.Slots.InputSlot<T3.Core.DataTypes.Texture2D> ImageInput = new(null);

        [Input(Guid = "34567890-3456-7890-ABCD-EF3456789012")]
        public readonly T3.Core.Operator.Slots.InputSlot<int> NumFaces = new(1);

        [Input(Guid = "45678901-4567-8901-ABCD-EF4567890123")]
        public readonly T3.Core.Operator.Slots.InputSlot<float> MinFaceDetectionConfidence = new(0.5f);

        [Input(Guid = "56789012-5678-9012-ABCD-EF5678901234")]
        public readonly T3.Core.Operator.Slots.InputSlot<float> MinFacePresenceConfidence = new(0.5f);

        [Input(Guid = "67890123-6789-0123-ABCD-EF6789012345")]
        public readonly T3.Core.Operator.Slots.InputSlot<float> MinTrackingConfidence = new(0.5f);

        [Input(Guid = "78901234-7890-1234-ABCD-EF7890123456")]
        public readonly T3.Core.Operator.Slots.InputSlot<bool> OutputFaceBlendshapes = new(false);

        [Input(Guid = "89012345-8901-2345-ABCD-EF8901234567")]
        public readonly T3.Core.Operator.Slots.InputSlot<bool> OutputFaceTransformationMatrixes = new(false);

        [Output(Guid = "87654321-4321-8765-CBA9-FED987654321")]
        public readonly T3.Core.Operator.Slots.Slot<T3.Core.DataTypes.Texture2D?> ImageOutput = new();

        [Output(Guid = "ABCDEF12-3456-7890-ABCD-EF1234567890")]
        public readonly T3.Core.Operator.Slots.Slot<T3.Core.DataTypes.BufferWithViews> Landmarks = new();

        [Output(Guid = "FEDCBA09-8765-4321-DCBA-0987654321F0")]
        public readonly T3.Core.Operator.Slots.Slot<T3.Core.DataTypes.Dict<float>?> AdditionalData = new();

        #endregion

        #region Constructor and Initialization

        public FaceMeshOperator()
        {
            T3.Core.Logging.Log.Info($"[CONSTRUCTOR] FaceMeshOperator created on thread {Thread.CurrentThread.ManagedThreadId}", this);
            ImageOutput.UpdateAction = Update;
            T3.Core.Logging.Log.Info("[CONSTRUCTOR] FaceMeshOperator initialization completed", this);
        }

        #endregion

        #region Update Method

        private void Update(T3.Core.Operator.EvaluationContext context)
        {
            var inputTexture = ImageInput.GetValue(context);
            if (inputTexture == null)
            {
                ResetOutputs();
                return;
            }

            // Get configuration values
            var numFaces = NumFaces.GetValue(context);
            var minFaceDetectionConfidence = MinFaceDetectionConfidence.GetValue(context);
            var minFacePresenceConfidence = MinFacePresenceConfidence.GetValue(context);
            var minTrackingConfidence = MinTrackingConfidence.GetValue(context);
            var outputFaceBlendshapes = OutputFaceBlendshapes.GetValue(context);
            var outputFaceTransformationMatrixes = OutputFaceTransformationMatrixes.GetValue(context);

            // Initialize or reinitialize FaceLandmarker if configuration changed
            if (_faceLandmarker == null || HasConfigurationChanged(numFaces, minFaceDetectionConfidence, 
                minFacePresenceConfidence, minTrackingConfidence, outputFaceBlendshapes, outputFaceTransformationMatrixes))
            {
                InitializeFaceLandmarker(numFaces, minFaceDetectionConfidence, 
                    minFacePresenceConfidence, minTrackingConfidence, outputFaceBlendshapes, outputFaceTransformationMatrixes);
            }

            if (_faceLandmarker == null)
            {
                ResetOutputs();
                return;
            }

            try
            {
                using var mpImage = ConvertTextureToMediaPipeImage(inputTexture);
                long timestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds();
                var result = _faceLandmarker.DetectForVideo(mpImage, timestamp);
                ProcessFaceLandmarkResult(result, inputTexture);
            }
            catch (Exception ex)
            {
                T3.Core.Logging.Log.Error($"[LIVE_STREAM] Failed to process face landmarks: {ex.Message}", this);
                T3.Core.Logging.Log.Error($"[LIVE_STREAM] Exception type: {ex.GetType().Name}", this);
                T3.Core.Logging.Log.Error($"[LIVE_STREAM] Stack trace: {ex.StackTrace}", this);
                
                // Set fallback outputs
                lock (_outputLock)
                {
                    ImageOutput.Value = inputTexture;
                    Landmarks.Value = null;
                    AdditionalData.Value = null;
                }
            }
        }

        #endregion

        #region FaceLandmarker Management

        private void InitializeFaceLandmarker(int numFaces, float minFaceDetectionConfidence,
            float minFacePresenceConfidence, float minTrackingConfidence, bool outputFaceBlendshapes,
            bool outputFaceTransformationMatrixes)
        {
            // Dispose existing instance
            if (_faceLandmarker != null)
            {
                ((IDisposable)_faceLandmarker).Dispose();
                _faceLandmarker = null;
            }

            try
            {
                const string modelPath = "/t3.mediapipe/face_landmarker.task";
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Attempting to resolve model path: {modelPath}", this);

                if (!T3.Core.Resource.ResourceManager.TryResolvePath(modelPath, this, out var absolutePath, out _))
                {
                    T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Model file could not be resolved: {modelPath}", this);
                    return;
                }

                T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Initializing FaceLandmarker with resolved model path: {absolutePath}", this);
                if (File.Exists(absolutePath))
                {
                    var fileInfo = new FileInfo(absolutePath);
                    T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Model file exists, size: {fileInfo.Length} bytes", this);
                    
                    // Check if file is readable and not corrupted
                    try
                    {   
                        using var fileStream = File.OpenRead(absolutePath);
                        var header = new byte[4];
                        _ = fileStream.Read(header, 0, header.Length);
                        T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Model file header bytes: {BitConverter.ToString(header).Replace("-", " ")}", this);

                        // A .task file is a zip archive, which starts with 'PK' (0x50, 0x4B)
                        // Handle both old style (PK...) and new style (00 00 PK...) formats
                        bool isValidFormat = (header[0] == 0x50 && header[1] == 0x4B) || 
                                           (header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x50 && header[3] == 0x4B);
                        
                        if (!isValidFormat)
                        {
                            T3.Core.Logging.Log.Error("[DIAGNOSTIC] Invalid model file format. The provided file is not a valid '.task' bundle.", this);
                            T3.Core.Logging.Log.Error("[DIAGNOSTIC] Expected 'PK' (0x50 0x4B) or '00 00 PK' (0x00 0x00 0x50 0x4B) header.", this);
                            T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Please ensure that required model '{modelPath}' is present in the operator's resources.", this);
                            return;
                        }
                        
                        T3.Core.Logging.Log.Info("[DIAGNOSTIC] Valid task file format detected", this);
                    }
                    catch (Exception headerEx)
                    {
                        T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Failed to read model file header: {headerEx.Message}", this);
                        return;
                    }
                }
                else
                {
                    T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Resolved model file does not exist: {absolutePath}", this);
                    return;
                }

                T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Creating CoreBaseOptions...", this);
                var baseOptions = new CoreBaseOptions(modelAssetPath: absolutePath);
                
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Creating FaceLandmarkerOptions with parameters:", this);
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC]   - numFaces: {numFaces}", this);
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC]   - minFaceDetectionConfidence: {minFaceDetectionConfidence}", this);
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC]   - minFacePresenceConfidence: {minFacePresenceConfidence}", this);
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC]   - minTrackingConfidence: {minTrackingConfidence}", this);
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC]   - outputFaceBlendshapes: {outputFaceBlendshapes}", this);
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC]   - outputFaceTransformationMatrixes: {outputFaceTransformationMatrixes}", this);
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC]   - runningMode: VIDEO", this);
                
                var options = new FaceLandmarkerOptions(
                    baseOptions,
                    runningMode: Mediapipe.Tasks.Vision.Core.VisionRunningMode.VIDEO,
                    numFaces: numFaces,
                    minFaceDetectionConfidence: minFaceDetectionConfidence,
                    minFacePresenceConfidence: minFacePresenceConfidence,
                    minTrackingConfidence: minTrackingConfidence,
                    outputFaceBlendshapes: outputFaceBlendshapes,
                    outputFaceTransformationMatrixes: outputFaceTransformationMatrixes
                );

                T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Calling FaceLandmarker.CreateFromOptions...", this);
                _faceLandmarker = FaceLandmarker.CreateFromOptions(options);
                
                if (_faceLandmarker != null)
                {
                    T3.Core.Logging.Log.Info($"[DIAGNOSTIC] FaceLandmarker created successfully", this);
                }
                else
                {
                    T3.Core.Logging.Log.Error($"[DIAGNOSTIC] FaceLandmarker.CreateFromOptions returned null", this);
                }
                
                // Store current configuration
                _currentConfig = new Configuration(numFaces, minFaceDetectionConfidence,
                    minFacePresenceConfidence, minTrackingConfidence, outputFaceBlendshapes, outputFaceTransformationMatrixes);
            }
            catch (BadStatusException bse) when (bse.StatusCode == StatusCode.NotFound)
            {
                T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Failed to initialize FaceLandmarker. A 'Not Found' error suggests a model mismatch or corrupted file.", this);
                T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Please ensure that required model file is present in the operator resources and is not corrupted.", this);
                T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Underlying MediaPipe Error: {bse.Message}", this);
                _faceLandmarker = null;
            }
            catch (Exception ex)
            {
                T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Failed to initialize FaceLandmarker: {ex.Message}", this);
                T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Exception type: {ex.GetType().Name}", this);
                T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Stack trace: {ex.StackTrace}", this);
            }
        }

        private bool HasConfigurationChanged(int numFaces, float minFaceDetectionConfidence,
            float minFacePresenceConfidence, float minTrackingConfidence, bool outputFaceBlendshapes,
            bool outputFaceTransformationMatrixes)
        {
            if (_currentConfig == null) return true;
            
            // Add null checks before accessing Configuration properties
            return _currentConfig.Value.NumFaces != numFaces ||
                   Math.Abs(_currentConfig.Value.MinFaceDetectionConfidence - minFaceDetectionConfidence) > 0.001f ||
                   Math.Abs(_currentConfig.Value.MinFacePresenceConfidence - minFacePresenceConfidence) > 0.001f ||
                   Math.Abs(_currentConfig.Value.MinTrackingConfidence - minTrackingConfidence) > 0.001f ||
                   _currentConfig.Value.OutputFaceBlendshapes != outputFaceBlendshapes ||
                   _currentConfig.Value.OutputFaceTransformationMatrixes != outputFaceTransformationMatrixes;
        }

        #endregion

        #region Helper Methods

        private void ResetOutputs()
        {
            T3.Core.Logging.Log.Info("[RESET_OUTPUTS] Resetting all outputs and disposing resources", this);
            
            ImageOutput.Value = null;
            Landmarks.Value = null;
            AdditionalData.Value = null;
            
            // Use buffer lock for thread-safe resource disposal
            lock (_bufferLock)
            {
                if (_pointBuffer != null)
                {
                    T3.Core.Logging.Log.Info("[RESET_OUTPUTS] Disposing PointBuffer", this);
                    ((IDisposable)_pointBuffer).Dispose();
                    _pointBuffer = null;
                    T3.Core.Logging.Log.Info("[RESET_OUTPUTS] PointBuffer disposed", this);
                }
                
                if (_bufferWithViews != null)
                {
                    T3.Core.Logging.Log.Info("[RESET_OUTPUTS] Disposing BufferWithViews", this);
                    ((IDisposable)_bufferWithViews).Dispose();
                    _bufferWithViews = null;
                    T3.Core.Logging.Log.Info("[RESET_OUTPUTS] BufferWithViews disposed", this);
                }
            }
            
            if (_stagingTexture != null)
            {
                T3.Core.Logging.Log.Info("[RESET_OUTPUTS] Disposing StagingTexture", this);
                ((IDisposable)_stagingTexture).Dispose();
                _stagingTexture = null;
                T3.Core.Logging.Log.Info("[RESET_OUTPUTS] StagingTexture disposed", this);
            }
            
            T3.Core.Logging.Log.Info("[RESET_OUTPUTS] Reset completed", this);
        }

        private unsafe Image ConvertTextureToMediaPipeImage(T3.Core.DataTypes.Texture2D texture)
        {
            if (texture == null)
                throw new ArgumentNullException(nameof(texture));

            try
            {
                var width = texture.Description.Width;
                var height = texture.Description.Height;
                var device = T3.Core.Resource.ResourceManager.Device;
                var context = device.ImmediateContext;

                // Recreate staging texture if size has changed
                if (_stagingTexture == null || _stagingTexture.Description.Width != width || _stagingTexture.Description.Height != height)
                {
                    if (_stagingTexture != null)
                    {
                        ((IDisposable)_stagingTexture).Dispose();
                    }
                    var stageDesc = new SharpDX.Direct3D11.Texture2DDescription
                                    {
                                        Width = width,
                                        Height = height,
                                        MipLevels = 1,
                                        ArraySize = 1,
                                        Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                                        SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                                        Usage = SharpDX.Direct3D11.ResourceUsage.Staging,
                                        BindFlags = SharpDX.Direct3D11.BindFlags.None,
                                        CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.Read,
                                        OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
                                    };
                    _stagingTexture = new SharpDX.Direct3D11.Texture2D(device, stageDesc);
                    _pixelData = new byte[width * height * 4];
                }

                var stagingTexture = _stagingTexture;
                context.CopyResource(texture, stagingTexture);

                // Map staging texture to get pixel data
                var dataBox = context.MapSubresource(stagingTexture, 0, SharpDX.Direct3D11.MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                var pixelData = _pixelData;
                
                // Copy pixel data from GPU to CPU
                for (int y = 0; y < height; y++)
                {
                    var srcRow = (byte*)dataBox.DataPointer + y * dataBox.RowPitch;
                    Marshal.Copy(new IntPtr(srcRow), pixelData, y * width * 4, width * 4);
                }

                context.UnmapSubresource(stagingTexture, 0);

                int widthStep = width * 4;
                return new Image(ImageFormat.Types.Format.Srgba, width, height, widthStep, pixelData);
            }
            catch (Exception ex)
            {
                T3.Core.Logging.Log.Error($"Failed to convert texture to MediaPipe Image: {ex.Message}", this);
                throw;
            }
        }


        private void ProcessFaceLandmarkResult(FaceLandmarkerResult result, T3.Core.DataTypes.Texture2D inputTexture)
        {
            lock (_outputLock)
            {
                ImageOutput.Value = inputTexture;

                if (result.FaceLandmarks == null || result.FaceLandmarks.Count == 0 ||
                    result.FaceLandmarks[0].landmarks == null)
                {
                    T3.Core.Logging.Log.Warning("[LIVE_STREAM] No face landmarks detected or landmark data is null.", this);
                    Landmarks.Value = null;
                    AdditionalData.Value = null;
                    return;
                }

                var landmarkPoints = result.FaceLandmarks[0].landmarks;
                
                // Log buffer creation details
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Processing {landmarkPoints.Count} landmark points", this);
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Thread ID: {System.Threading.Thread.CurrentThread.ManagedThreadId}", this);
                
                // Ensure thread-safe buffer management following same pattern as Video2DPointScanner
                lock (_bufferLock)
                {
                    try
                    {
                        // Convert NormalizedLandmark to T3.Core.DataTypes.Point
                        var landmarkArray = landmarkPoints.ToArray();
                        var landmarkArrayCopy = new T3.Core.DataTypes.Point[landmarkArray.Length];
                        
                        for (int i = 0; i < landmarkArray.Length; i++)
                        {
                            var normalizedLandmark = landmarkArray[i];
                            landmarkArrayCopy[i] = new T3.Core.DataTypes.Point
                            {
                                Position = new System.Numerics.Vector3(
                                    normalizedLandmark.X,
                                    normalizedLandmark.Y,
                                    normalizedLandmark.Z
                                ),
                                F1 = normalizedLandmark.Visibility ?? 0.0f,  // Store visibility in F1
                                F2 = normalizedLandmark.Presence ?? 0.0f     // Store presence in F2
                            };
                        }
                        
                        // Use the new UpdateGpuBufferWithPoints method following Video2DPointScanner pattern
                        UpdateGpuBufferWithPoints(ref _bufferWithViews, landmarkArrayCopy);
                        Landmarks.Value = _bufferWithViews;
                        T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Successfully set Landmarks output with buffer", this);
                    }
                    catch (Exception ex)
                    {
                        T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Failed to create/update landmark buffer: {ex.Message}", this);
                        T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Exception type: {ex.GetType().Name}", this);
                        
                        // Set fallback outputs on buffer failure to maintain consistency - following Video2DPointScanner pattern
                        Landmarks.Value = null;
                        AdditionalData.Value = null;
                        ImageOutput.Value = inputTexture; // Ensure consistent fallback output setting
                        return;
                    }
                }

                // Calculate detailed facial measurements and add to AdditionalData
                var facialMeasurements = CalculateFacialMeasurements(landmarkPoints.ToArray());
                var additionalData = new T3.Core.DataTypes.Dict<float>(facialMeasurements.Count + 2)
                                     {
                                         { "face_count", result.FaceLandmarks.Count },
                                         { "landmark_count", landmarkPoints.Count }
                                     };
                
                // Add all calculated measurements
                foreach (var measurement in facialMeasurements)
                {
                    additionalData[measurement.Key] = measurement.Value;
                }
                
                AdditionalData.Value = additionalData;
                T3.Core.Logging.Log.Info($"[DIAGNOSTIC] Successfully set AdditionalData output with {facialMeasurements.Count + 2} measurements", this);
            }
        }

        /// <summary>
        /// Calculates detailed facial measurements from landmark points
        /// Based on MediaPipe Face Mesh landmark indices (468 points for standard face mesh)
        /// </summary>
        private Dictionary<string, float> CalculateFacialMeasurements(Mediapipe.Tasks.Components.Containers.NormalizedLandmark[] landmarks)
        {
            var measurements = new Dictionary<string, float>();
            
            try
            {
                // Eye measurements (using standard MediaPipe face mesh indices)
                // Left eye landmarks: 362, 382, 381, 380, 374, 373, 390, 249, 263, 466, 388, 387, 386, 385, 384, 398
                // Right eye landmarks: 33, 7, 163, 144, 145, 153, 154, 155, 133, 173, 157, 158, 159, 160, 161, 246
                
                var leftEyeCenter = CalculateEyeCenter(landmarks, new[] { 362, 382, 381, 380, 374, 373, 390, 249, 263, 466, 388, 387, 386, 385, 384, 398 });
                var rightEyeCenter = CalculateEyeCenter(landmarks, new[] { 33, 7, 163, 144, 145, 153, 154, 155, 133, 173, 157, 158, 159, 160, 161, 246 });
                
                measurements["left_eye_x"] = leftEyeCenter.X;
                measurements["left_eye_y"] = leftEyeCenter.Y;
                measurements["left_eye_z"] = leftEyeCenter.Z;
                measurements["right_eye_x"] = rightEyeCenter.X;
                measurements["right_eye_y"] = rightEyeCenter.Y;
                measurements["right_eye_z"] = rightEyeCenter.Z;
                
                // Eye distance
                var eyeDistance = System.Numerics.Vector3.Distance(leftEyeCenter, rightEyeCenter);
                measurements["eye_distance"] = eyeDistance;
                
                // Eye aspect ratio (EAR - Eye Aspect Ratio) for blink detection
                measurements["left_ear"] = CalculateEyeAspectRatio(landmarks, new[] { 33, 160, 158, 133, 155, 154 }); // Left eye vertical
                measurements["right_ear"] = CalculateEyeAspectRatio(landmarks, new[] { 362, 385, 387, 263, 386, 374 }); // Right eye vertical
                
                // Mouth measurements
                // Mouth outer landmarks: 61, 84, 17, 314, 405, 291, 375, 321, 308, 324, 318, 402, 317, 14, 87, 178, 88, 95
                // Mouth inner landmarks: 78, 191, 80, 81, 82, 13, 312, 311, 310, 415, 308, 324, 318, 402, 317, 14, 87, 178, 88, 95
                
                var mouthCenter = CalculateMouthCenter(landmarks);
                measurements["mouth_x"] = mouthCenter.X;
                measurements["mouth_y"] = mouthCenter.Y;
                measurements["mouth_z"] = mouthCenter.Z;
                
                // Mouth width (horizontal distance between corners)
                var mouthWidth = CalculateMouthWidth(landmarks);
                measurements["mouth_width"] = mouthWidth;
                
                // Mouth height (vertical distance between top and bottom)
                var mouthHeight = CalculateMouthHeight(landmarks);
                measurements["mouth_height"] = mouthHeight;
                
                // Mouth aspect ratio (MAR - Mouth Aspect Ratio) for mouth opening detection
                measurements["mouth_aspect_ratio"] = mouthHeight / mouthWidth;
                
                // Eyebrow measurements
                var leftEyebrowHeight = CalculateEyebrowHeight(landmarks, true); // Left eyebrow
                var rightEyebrowHeight = CalculateEyebrowHeight(landmarks, false); // Right eyebrow
                measurements["left_eyebrow_height"] = leftEyebrowHeight;
                measurements["right_eyebrow_height"] = rightEyebrowHeight;
                
                // Face orientation estimates
                var faceRotation = EstimateFaceRotation(landmarks, leftEyeCenter, rightEyeCenter, mouthCenter);
                measurements["face_yaw"] = faceRotation.X; // Left-right rotation
                measurements["face_pitch"] = faceRotation.Y; // Up-down rotation
                measurements["face_roll"] = faceRotation.Z; // Tilting
                
                // Face bounding box
                var boundingBox = CalculateBoundingBox(landmarks);
                measurements["face_center_x"] = boundingBox.Center.X;
                measurements["face_center_y"] = boundingBox.Center.Y;
                measurements["face_width"] = boundingBox.Width;
                measurements["face_height"] = boundingBox.Height;
                
                // Additional face metrics
                measurements["face_area"] = boundingBox.Width * boundingBox.Height;
                measurements["nose_tip_x"] = landmarks[1].X; // Nose tip
                measurements["nose_tip_y"] = landmarks[1].Y;
                measurements["nose_tip_z"] = landmarks[1].Z;
            }
            catch (Exception ex)
            {
                T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Error calculating facial measurements: {ex.Message}", this);
            }
            
            return measurements;
        }

        private System.Numerics.Vector3 CalculateEyeCenter(Mediapipe.Tasks.Components.Containers.NormalizedLandmark[] landmarks, int[] eyeIndices)
        {
            var center = System.Numerics.Vector3.Zero;
            int validPoints = 0;
            
            foreach (int index in eyeIndices)
            {
                if (index < landmarks.Length)
                {
                    var landmark = landmarks[index];
                    center += new System.Numerics.Vector3(landmark.X, landmark.Y, landmark.Z);
                    validPoints++;
                }
            }
            
            return validPoints > 0 ? center / validPoints : System.Numerics.Vector3.Zero;
        }

        private float CalculateEyeAspectRatio(Mediapipe.Tasks.Components.Containers.NormalizedLandmark[] landmarks, int[] eyeIndices)
        {
            try
            {
                // Calculate eye aspect ratio using 6 key points
                // Vertical distances
                var p1 = new System.Numerics.Vector2(landmarks[eyeIndices[1]].X, landmarks[eyeIndices[1]].Y);
                var p2 = new System.Numerics.Vector2(landmarks[eyeIndices[5]].X, landmarks[eyeIndices[5]].Y);
                var verticalDist1 = System.Numerics.Vector2.Distance(p1, p2);
                
                var p3 = new System.Numerics.Vector2(landmarks[eyeIndices[2]].X, landmarks[eyeIndices[2]].Y);
                var p4 = new System.Numerics.Vector2(landmarks[eyeIndices[4]].X, landmarks[eyeIndices[4]].Y);
                var verticalDist2 = System.Numerics.Vector2.Distance(p3, p4);
                
                // Horizontal distance
                var p5 = new System.Numerics.Vector2(landmarks[eyeIndices[0]].X, landmarks[eyeIndices[0]].Y);
                var p6 = new System.Numerics.Vector2(landmarks[eyeIndices[3]].X, landmarks[eyeIndices[3]].Y);
                var horizontalDist = System.Numerics.Vector2.Distance(p5, p6);
                
                // Eye Aspect Ratio: (sum of vertical distances) / (2 * horizontal distance)
                return horizontalDist > 0 ? (verticalDist1 + verticalDist2) / (2.0f * horizontalDist) : 0.0f;
            }
            catch
            {
                return 0.0f;
            }
        }

        private System.Numerics.Vector3 CalculateMouthCenter(Mediapipe.Tasks.Components.Containers.NormalizedLandmark[] landmarks)
        {
            // Use key mouth landmarks for center calculation
            var mouthIndices = new[] { 13, 14, 78, 80, 81, 82, 87, 88, 95, 178, 308, 310, 311, 312, 317, 318, 324, 402, 317, 14, 87, 178, 88, 95 };
            return CalculateEyeCenter(landmarks, mouthIndices); // Reuse eye center calculation logic
        }

        private float CalculateMouthWidth(Mediapipe.Tasks.Components.Containers.NormalizedLandmark[] landmarks)
        {
            try
            {
                // Distance between left and right mouth corners
                var leftCorner = new System.Numerics.Vector2(landmarks[61].X, landmarks[61].Y);
                var rightCorner = new System.Numerics.Vector2(landmarks[291].X, landmarks[291].Y);
                return System.Numerics.Vector2.Distance(leftCorner, rightCorner);
            }
            catch
            {
                return 0.0f;
            }
        }

        private float CalculateMouthHeight(Mediapipe.Tasks.Components.Containers.NormalizedLandmark[] landmarks)
        {
            try
            {
                // Distance between top and bottom mouth points
                var topLip = new System.Numerics.Vector2(landmarks[13].X, landmarks[13].Y);
                var bottomLip = new System.Numerics.Vector2(landmarks[14].X, landmarks[14].Y);
                return System.Numerics.Vector2.Distance(topLip, bottomLip);
            }
            catch
            {
                return 0.0f;
            }
        }

        private float CalculateEyebrowHeight(Mediapipe.Tasks.Components.Containers.NormalizedLandmark[] landmarks, bool isLeftEyebrow)
        {
            try
            {
                // Calculate average eyebrow height relative to eye center
                var eyebrowIndices = isLeftEyebrow 
                    ? new[] { 46, 53, 52, 51, 48, 115, 131, 134, 102, 49, 220, 305, 292, 283, 282, 295, 285, 336, 296, 334 } // Left eyebrow
                    : new[] { 276, 283, 282, 295, 285, 336, 296, 334, 293, 300, 276, 283, 282, 295, 285, 336, 296, 334, 293, 300 }; // Right eyebrow
                
                var eyeCenterIndices = isLeftEyebrow
                    ? new[] { 362, 382, 381, 380, 374, 373, 390, 249, 263, 466, 388, 387, 386, 385, 384, 398 } // Left eye
                    : new[] { 33, 7, 163, 144, 145, 153, 154, 155, 133, 173, 157, 158, 159, 160, 161, 246 }; // Right eye
                
                var eyebrowCenter = CalculateEyeCenter(landmarks, eyebrowIndices);
                var eyeCenter = CalculateEyeCenter(landmarks, eyeCenterIndices);
                
                return Math.Abs(eyebrowCenter.Y - eyeCenter.Y);
            }
            catch
            {
                return 0.0f;
            }
        }

        private System.Numerics.Vector3 EstimateFaceRotation(Mediapipe.Tasks.Components.Containers.NormalizedLandmark[] landmarks, System.Numerics.Vector3 leftEyeCenter, System.Numerics.Vector3 rightEyeCenter, System.Numerics.Vector3 mouthCenter)
        {
            var rotation = System.Numerics.Vector3.Zero;
            
            try
            {
                // Calculate face center
                var faceCenter = (leftEyeCenter + rightEyeCenter + mouthCenter) / 3.0f;
                
                // Yaw (left-right rotation) - based on eye positions relative to center
                var eyeVector = rightEyeCenter - leftEyeCenter;
                rotation.X = MathF.Atan2(eyeVector.X, eyeVector.Z) * (180.0f / MathF.PI);
                
                // Pitch (up-down rotation) - based on mouth position relative to eyes
                var eyeMidpoint = (leftEyeCenter + rightEyeCenter) / 2.0f;
                var mouthVector = mouthCenter - eyeMidpoint;
                rotation.Y = MathF.Atan2(mouthVector.Y, mouthVector.Z) * (180.0f / MathF.PI);
                
                // Roll (tilting) - based on eye line angle
                var eyeLine = new System.Numerics.Vector2(rightEyeCenter.X - leftEyeCenter.X, rightEyeCenter.Y - leftEyeCenter.Y);
                rotation.Z = MathF.Atan2(eyeLine.Y, eyeLine.X) * (180.0f / MathF.PI);
            }
            catch (Exception ex)
            {
                T3.Core.Logging.Log.Error($"[DIAGNOSTIC] Error estimating face rotation: {ex.Message}", this);
            }
            
            return rotation;
        }

        private (System.Numerics.Vector2 Center, float Width, float Height) CalculateBoundingBox(Mediapipe.Tasks.Components.Containers.NormalizedLandmark[] landmarks)
        {
            if (landmarks.Length == 0)
                return (System.Numerics.Vector2.Zero, 0, 0);
            
            var minX = landmarks[0].X;
            var maxX = landmarks[0].X;
            var minY = landmarks[0].Y;
            var maxY = landmarks[0].Y;
            
            foreach (var landmark in landmarks)
            {
                minX = Math.Min(minX, landmark.X);
                maxX = Math.Max(maxX, landmark.X);
                minY = Math.Min(minY, landmark.Y);
                maxY = Math.Max(maxY, landmark.Y);
            }
            
            var center = new System.Numerics.Vector2((minX + maxX) / 2.0f, (minY + maxY) / 2.0f);
            var width = maxX - minX;
            var height = maxY - minY;
            
            return (center, width, height);
        }

        /// <summary>
        /// Updates GPU buffer with landmark points following the same pattern as Video2DPointScanner
        /// </summary>
        private void UpdateGpuBufferWithPoints(ref T3.Core.DataTypes.BufferWithViews buffer, T3.Core.DataTypes.Point[] points)
        {
            var num = points?.Length ?? 0;
            
            // Check if buffer needs to be recreated due to size change
            if (num != (buffer?.Buffer.Description.SizeInBytes / T3.Core.DataTypes.Point.Stride ?? 0))
            {
                buffer?.Dispose();
                buffer = null;
            }

            // Early return for empty data - following Video2DPointScanner pattern
            if (num == 0) return;
            
            // Add proper null check before accessing points array - following Video2DPointScanner pattern
            if (points == null) return;
            
            if (buffer == null)
            {
                // Create new buffer with proper size
                buffer = new T3.Core.DataTypes.BufferWithViews();
                // Fix parameter order: first parameter is the array, then size parameters
                T3.Core.Resource.ResourceManager.SetupStructuredBuffer(points, T3.Core.DataTypes.Point.Stride * num, T3.Core.DataTypes.Point.Stride, ref buffer.Buffer);
                T3.Core.Resource.ResourceManager.CreateStructuredBufferSrv(buffer.Buffer, ref buffer.Srv);
                T3.Core.Resource.ResourceManager.CreateStructuredBufferUav(buffer.Buffer, SharpDX.Direct3D11.UnorderedAccessViewBufferFlags.None, ref buffer.Uav);
            }
            else
            {
                // Update existing buffer
                T3.Core.Resource.ResourceManager.Device.ImmediateContext.UpdateSubresource(points, buffer.Buffer);
            }
        }

        #endregion

        #region Helper Methods for Debugging

        private bool IsImageDisposed(Image image)
        {
            if (image == null) return true;
            
            try
            {
                var _ = image.Width();
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Resource Management

        protected override void Dispose(bool isDisposing)
        {
            T3.Core.Logging.Log.Info($"[DISPOSE] Dispose called with isDisposing={isDisposing} on thread {Thread.CurrentThread.ManagedThreadId}", this);
            
            base.Dispose(isDisposing);
            if (!isDisposing)
            {
                T3.Core.Logging.Log.Info("[DISPOSE] Early return - not disposing managed resources", this);
                return;
            }

            // Prevent double-disposal with thread safety check
            if (_disposed)
            {
                T3.Core.Logging.Log.Warning("[DISPOSE] Already disposed - skipping", this);
                return;
            }

            T3.Core.Logging.Log.Info("[DISPOSE] Starting managed resource disposal...", this);
            
            // Simplify lock pattern to avoid nested locks and potential deadlock - following Video2DPointScanner pattern
            lock (_bufferLock) // Use only buffer lock for thread-safe disposal
            {
                T3.Core.Logging.Log.Info("[DISPOSE] Acquired buffer disposal lock", this);
                
                if (_faceLandmarker != null)
                {
                    T3.Core.Logging.Log.Info("[DISPOSE] Disposing FaceLandmarker", this);
                    ((IDisposable)_faceLandmarker).Dispose();
                    _faceLandmarker = null;
                    T3.Core.Logging.Log.Info("[DISPOSE] FaceLandmarker disposed successfully", this);
                }
                
                if (_pointBuffer != null)
                {
                    T3.Core.Logging.Log.Info("[DISPOSE] Disposing PointBuffer", this);
                    ((IDisposable)_pointBuffer).Dispose();
                    _pointBuffer = null;
                    T3.Core.Logging.Log.Info("[DISPOSE] PointBuffer disposed successfully", this);
                }
                
                if (_bufferWithViews != null)
                {
                    T3.Core.Logging.Log.Info("[DISPOSE] Disposing BufferWithViews", this);
                    ((IDisposable)_bufferWithViews).Dispose();
                    _bufferWithViews = null;
                    T3.Core.Logging.Log.Info("[DISPOSE] BufferWithViews disposed successfully", this);
                }
                
                if (_stagingTexture != null)
                {
                    T3.Core.Logging.Log.Info("[DISPOSE] Disposing StagingTexture", this);
                    ((IDisposable)_stagingTexture).Dispose();
                    _stagingTexture = null;
                    T3.Core.Logging.Log.Info("[DISPOSE] StagingTexture disposed successfully", this);
                }
                
                _pixelData = null; // Also clear pixel data array
                
                // Mark as disposed to prevent double-disposal
                _disposed = true;
                
                T3.Core.Logging.Log.Info("[DISPOSE] All managed resources disposed successfully", this);
            }
        }

        #endregion

        #region Private Fields

        private FaceLandmarker? _faceLandmarker;
        private Configuration? _currentConfig;
        private SharpDX.Direct3D11.Buffer? _pointBuffer;
        private T3.Core.DataTypes.BufferWithViews? _bufferWithViews;
        private SharpDX.Direct3D11.Texture2D? _stagingTexture;
        private byte[]? _pixelData;

        private readonly object _outputLock = new object();
        private readonly object _bufferLock = new object(); // Dedicated buffer lock for thread safety
        private long _timestampCounter = 0;
        
        // Thread safety flag to prevent double-disposal
        private bool _disposed = false;

        private readonly struct Configuration
        {
            public readonly int NumFaces;
            public readonly float MinFaceDetectionConfidence;
            public readonly float MinFacePresenceConfidence;
            public readonly float MinTrackingConfidence;
            public readonly bool OutputFaceBlendshapes;
            public readonly bool OutputFaceTransformationMatrixes;

            public Configuration(int numFaces, float minFaceDetectionConfidence,
                float minFacePresenceConfidence, float minTrackingConfidence, bool outputFaceBlendshapes, 
                bool outputFaceTransformationMatrixes)
            {
                NumFaces = numFaces;
                MinFaceDetectionConfidence = minFaceDetectionConfidence;
                MinFacePresenceConfidence = minFacePresenceConfidence;
                MinTrackingConfidence = minTrackingConfidence;
                OutputFaceBlendshapes = outputFaceBlendshapes;
                OutputFaceTransformationMatrixes = outputFaceTransformationMatrixes;
            }
        }

        #endregion
    }
}
