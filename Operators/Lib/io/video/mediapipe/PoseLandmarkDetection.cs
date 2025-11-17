using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using SharpDX;
using SharpDX.Direct3D11;
using T3.Core.DataTypes;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Resource;
using Google.Protobuf;
#nullable enable

// DIAGNOSTIC: Try using the actual working MediaPipe namespaces
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Framework.Formats;
using Landmark = Mediapipe.Landmark;

namespace Lib.io.video.mediapipe;

[Guid("A1B2C3D4-E5F6-4798-89AB-CDEF12345681")]
public class PoseLandmarkDetection : Instance<PoseLandmarkDetection>
{
    [Output(Guid = "B2C3D4E5-F6A7-489A-9B0C-DEF123456782", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D> OutputTexture = new();

    [Output(Guid = "C3D4E5F6-A7B8-49AB-AC1D-EF1234567893", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<BufferWithViews> LandmarksBuffer = new();

    [Output(Guid = "D4E5F6A7-B8C9-4AB0-BD2E-F12345678904", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Dict<float>> PoseData = new();

    [Output(Guid = "E5F6A7B8-C9D0-4B01-CE3F-1234A5678905", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> PoseCount = new();

    [Output(Guid = "F6A7B8C9-D0E1-4C12-DF4A-234567A89006", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> UpdateCount = new();

    public PoseLandmarkDetection()
    {
        OutputTexture.UpdateAction = Update;
        LandmarksBuffer.UpdateAction = Update;
        PoseData.UpdateAction = Update;
        PoseCount.UpdateAction = Update;
        UpdateCount.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var inputTexture = InputTexture.GetValue(context);
        var enabled = Enabled.GetValue(context);
        var maxPoses = MaxPoses.GetValue(context);
        var minPoseDetectionConfidence = MinPoseDetectionConfidence.GetValue(context);
        var minPosePresenceConfidence = MinPosePresenceConfidence.GetValue(context);
        var showLandmarks = ShowLandmarks.GetValue(context);
        var showConnections = ShowConnections.GetValue(context);

        // Lazy initialization: Initialize on first use or if it becomes null
        if (_poseLandmarker == null)
        {
            InitializeMediaPipe();
        }

        // Reset outputs if detection is disabled
        if (!enabled || inputTexture == null)
        {
            OutputTexture.Value = inputTexture;
            LandmarksBuffer.Value = null;
            PoseData.Value = new Dict<float>(0f);
            PoseCount.Value = 0;
            _landmarksArray = null;
            return;
        }

        // Process pose detection
        if (ProcessTextureForPoseDetection(inputTexture, minPoseDetectionConfidence, minPosePresenceConfidence, maxPoses))
        {
            UpdateOutputs(showLandmarks, showConnections);
            UpdateCount.Value++;
        }
        else
        {
            // Fall back to input texture if detection fails
            OutputTexture.Value = inputTexture;
            Log.Debug("Pose detection failed", this);
        }
    }

    #region MediaPipe Integration
    // DIAGNOSTIC: Replace low-level API with high-level task API
    private PoseLandmarker? _poseLandmarker;
    private Point[]? _landmarksArray;
    private long _frameTimestamp; // DIAGNOSTIC: Add missing timestamp field

    private void InitializeMediaPipe()
    {
        try
        {
            // DIAGNOSTIC: Use high-level PoseLandmarker API instead of low-level CalculatorGraph
            Log.Debug("[PoseLandmarkDetection] Starting PoseLandmarker initialization...", this);
            
            // DIAGNOSTIC: Add detailed logging for troubleshooting
            Log.Debug($"[PoseLandmarkDetection] Current working directory: {System.IO.Directory.GetCurrentDirectory()}", this);
            Log.Debug($"[PoseLandmarkDetection] Application base directory: {AppDomain.CurrentDomain.BaseDirectory}", this);
            
            // Check if model file exists - FIXED: Use absolute path resolution
            string modelPath = "../../Mediapipe-Sharp/src/Mediapipe/Models/pose_landmarker.task";
            string fullPath = System.IO.Path.GetFullPath(modelPath);
            
            Log.Debug($"[PoseLandmarkDetection] Checking model path: {modelPath}", this);
            Log.Debug($"[PoseLandmarkDetection] Full resolved path: {fullPath}", this);
            
            // ENHANCED: Check multiple possible model paths with better error handling
            string[] possibleModelPaths = {
                fullPath,
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "pose_landmarker.task"),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Models", "pose_landmarker.task"),
                "../../Mediapipe-Sharp/src/Mediapipe/Models/pose_landmarker.task",
                "../../../Mediapipe-Sharp/src/Mediapipe/Models/pose_landmarker.task"
            };
            
            bool modelFound = false;
            foreach (string path in possibleModelPaths)
            {
                if (System.IO.File.Exists(path))
                {
                    fullPath = System.IO.Path.GetFullPath(path);
                    modelFound = true;
                    break;
                }
            }
            
            if (!modelFound)
            {
                Log.Error($"[PoseLandmarkDetection] Model file not found at any of the checked paths", this);
                foreach (string path in possibleModelPaths)
                {
                    string testPath = System.IO.Path.GetFullPath(path);
                    bool exists = System.IO.File.Exists(path);
                    Log.Debug($"[PoseLandmarkDetection] Path check: {path} -> {testPath} (Exists: {exists})", this);
                }
                return;
            }
            
            // Check if native library exists with similar path resolution
            string[] possibleDllPaths = {
                "../../Mediapipe-Sharp/src/Mediapipe/Libs/mediapipe_c.dll",
                "../../../Mediapipe-Sharp/src/Mediapipe/Libs/mediapipe_c.dll",
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Libs", "mediapipe_c.dll")
            };
            
            bool dllFound = false;
            string nativeDllPath = string.Empty;
            foreach (string path in possibleDllPaths)
            {
                if (System.IO.File.Exists(path))
                {
                    nativeDllPath = System.IO.Path.GetFullPath(path);
                    dllFound = true;
                    break;
                }
            }
            
            if (!dllFound)
            {
                Log.Error("[PoseLandmarkDetection] CRITICAL: MediaPipe native library not found!", this);
                foreach (string path in possibleDllPaths)
                {
                    string testPath = System.IO.Path.GetFullPath(path);
                    bool exists = System.IO.File.Exists(path);
                    Log.Debug($"[PoseLandmarkDetection] DLL Path check: {path} -> {testPath} (Exists: {exists})", this);
                }
                return;
            }
            
            Log.Debug($"[PoseLandmarkDetection] Model file found at: {fullPath}", this);
            
            // DIAGNOSTIC: Check file size and accessibility
            var fileInfo = new System.IO.FileInfo(fullPath);
            Log.Debug($"[PoseLandmarkDetection] Model file size: {fileInfo.Length} bytes", this);
            Log.Debug($"[PoseLandmarkDetection] Model file accessible: {fileInfo.Exists}", this);
            
            Log.Debug($"[PoseLandmarkDetection] Native DLL found at: {nativeDllPath}", this);
            
            // DIAGNOSTIC: Log MediaPipe library loading status
            Log.Debug("[PoseLandmarkDetection] Creating CoreBaseOptions...", this);
            
            // Initialize PoseLandmarker with video mode for real-time processing
            var baseOptions = new Mediapipe.Tasks.Core.CoreBaseOptions(
                modelAssetPath: fullPath,  // FIXED: Use resolved absolute path
                delegateCase: Mediapipe.Tasks.Core.CoreBaseOptions.Delegate.CPU
            );

            Log.Debug("[PoseLandmarkDetection] Creating PoseLandmarkerOptions...", this);
            PoseLandmarkerOptions options = new(
                baseOptions,
                VisionRunningMode.VIDEO,
                minPoseDetectionConfidence: 0.5f,
                minPosePresenceConfidence: 0.5f,
                minTrackingConfidence: 0.5f,
                numPoses: 2
            );

            Log.Debug("[PoseLandmarkDetection] Calling PoseLandmarker.CreateFromOptions...", this);
            Log.Debug($"[PoseLandmarkDetection] Options - BaseOptions model path: {baseOptions.ModelAssetPath}", this);
            Log.Debug($"[PoseLandmarkDetection] Options - Running mode: {options.RunningMode}", this);
            Log.Debug($"[PoseLandmarkDetection] Options - Min pose detection confidence: {options.MinPoseDetectionConfidence}", this);
            Log.Debug($"[PoseLandmarkDetection] Options - Min pose presence confidence: {options.MinPosePresenceConfidence}", this);
            Log.Debug($"[PoseLandmarkDetection] Options - Min tracking confidence: {options.MinTrackingConfidence}", this);
            Log.Debug($"[PoseLandmarkDetection] Options - Num poses: {options.NumPoses}", this);
            
            // DIAGNOSTIC: Add detailed exception handling with inner exceptions
            try
            {
                Log.Debug("[PoseLandmarkDetection] About to call PoseLandmarker.CreateFromOptions...", this);
                _poseLandmarker = PoseLandmarker.CreateFromOptions(options);
                Log.Debug($"[PoseLandmarkDetection] PoseLandmarker.CreateFromOptions returned: {_poseLandmarker != null}", this);
                
                if (_poseLandmarker != null)
                {
                    Log.Debug("[PoseLandmarkDetection] PoseLandmarker object created successfully", this);
                    Log.Debug($"[PoseLandmarkDetection] PoseLandmarker type: {_poseLandmarker.GetType().FullName}", this);
                }
                else
                {
                    Log.Error("[PoseLandmarkDetection] PoseLandmarker.CreateFromOptions returned null without exception", this);
                }
            }
            catch (System.IO.FileNotFoundException fnfEx)
            {
                Log.Error($"[PoseLandmarkDetection] File not found during PoseLandmarker creation: {fnfEx.Message}", this);
                Log.Error($"[PoseLandmarkDetection] File not found details: {fnfEx.FileName}", this);
                _poseLandmarker = null;
            }
            catch (System.DllNotFoundException dllEx)
            {
                Log.Error($"[PoseLandmarkDetection] Native DLL not found during PoseLandmarker creation: {dllEx.Message}", this);
                Log.Error($"[PoseLandmarkDetection] Missing DLL: {dllEx.Message}", this);
                _poseLandmarker = null;
            }
            catch (System.BadImageFormatException imgEx)
            {
                Log.Error($"[PoseLandmarkDetection] Invalid DLL format during PoseLandmarker creation: {imgEx.Message}", this);
                _poseLandmarker = null;
            }
            catch (Exception createEx)
            {
                Log.Error($"[PoseLandmarkDetection] Exception during PoseLandmarker.CreateFromOptions: {createEx.Message}", this);
                Log.Error($"[PoseLandmarkDetection] Exception type: {createEx.GetType().Name}", this);
                Log.Error($"[PoseLandmarkDetection] Exception stack trace: {createEx.StackTrace}", this);
                _poseLandmarker = null;
            }
            
            if (_poseLandmarker != null)
            {
                Log.Debug("[PoseLandmarkDetection] PoseLandmarker initialization successful!", this);
            }
            else
            {
                Log.Error("[PoseLandmarkDetection] ERROR: PoseLandmarker is null after creation", this);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[PoseLandmarkDetection] Failed to initialize PoseLandmarker: {ex.Message}", this);
            Log.Error($"[PoseLandmarkDetection] Outer exception type: {ex.GetType().Name}", this);
        }
    }

    private bool ProcessTextureForPoseDetection(Texture2D texture, float minPoseDetectionConfidence, float minPosePresenceConfidence, int maxPoses)
    {
        if (texture == null) return false;

        try
        {
            // DIAGNOSTIC: Use PoseLandmarker API instead of low-level graph
            Log.Debug("[PoseLandmarkDetection] Starting pose detection process...", this);
            
            // Convert Tixl Texture2D to OpenCV Mat (minimal usage - just for conversion)
            using var mat = Texture2DToMat(texture);
            if (mat.Empty()) 
            {
                Log.Debug("[PoseLandmarkDetection] ERROR: Converted Mat is empty", this);
                return false;
            }

            Log.Debug($"[PoseLandmarkDetection] Converted texture to Mat: {mat.Width}x{mat.Height}", this);

            // Convert Mat to MediaPipe Image
            var image = MatToMediaPipeImage(mat);
            if (image == null) 
            {
                Log.Debug("[PoseLandmarkDetection] ERROR: Failed to convert Mat to MediaPipe Image", this);
                return false;
            }

            Log.Debug($"[PoseLandmarkDetection] Converted to MediaPipe Image: {image.Width()}x{image.Height()}", this);

            // Process through PoseLandmarker
            if (_poseLandmarker == null)
            {
                Log.Debug("[PoseLandmarkDetection] ERROR: PoseLandmarker is null", this);
                return false;
            }

            // Get current timestamp for video processing
            _frameTimestamp += 33; // Approximate 30 FPS (33ms per frame)

            Log.Debug($"[PoseLandmarkDetection] Detecting poses for timestamp {_frameTimestamp}...", this);
            PoseLandmarkerResult result = _poseLandmarker.DetectForVideo(image, _frameTimestamp);

            if (result.PoseLandmarks == null || result.PoseLandmarks.Count == 0)
            {
                Log.Debug("[PoseLandmarkDetection] WARNING: No pose landmarks detected", this);
                return false;
            }

            Log.Debug($"[PoseLandmarkDetection] Detected {result.PoseLandmarks.Count} pose(s)", this);

            // Convert MediaPipe results to our landmark format
            _landmarksArray = ConvertPoseLandmarkerResultToLandmarks(result, minPoseDetectionConfidence, minPosePresenceConfidence, maxPoses);
            return _landmarksArray != null;
        }
        catch (Exception ex)
        {
            Log.Error($"[PoseLandmarkDetection] Error in pose detection: {ex.Message}", this);
            return false;
        }
    }

    private Point[]? ConvertPoseLandmarkerResultToLandmarks(PoseLandmarkerResult result, float minPoseDetectionConfidence, float minPosePresenceConfidence, int maxPoses)
    {
        if (result.PoseLandmarks == null) return null;

        var landmarks = new List<Point>();
        var poseDataDict = new Dict<float>(0f);

        try
        {
            Log.Debug($"[ConvertPoseLandmarkerResultToLandmarks] Processing {result.PoseLandmarks.Count} pose landmark sets", this);
            
            int detectedPoses = 0;
            foreach (var poseLandmarks in result.PoseLandmarks)
            {
                if (detectedPoses >= maxPoses) break;

                // Check confidence thresholds
                if (poseLandmarks.landmarks != null && poseLandmarks.landmarks.Count > 0)
                {
                    var avgDetectionConfidence = CalculateAverageDetectionConfidence(poseLandmarks.landmarks);
                    var avgPresenceConfidence = CalculateAveragePresenceConfidence(poseLandmarks.landmarks);
                    
                    if (avgDetectionConfidence < minPoseDetectionConfidence) 
                    {
                        Log.Debug($"[ConvertPoseLandmarkerResultToLandmarks] Skipping pose {detectedPoses} due to low detection confidence: {avgDetectionConfidence}", this);
                        continue;
                    }
                    
                    if (avgPresenceConfidence < minPosePresenceConfidence) 
                    {
                        Log.Debug($"[ConvertPoseLandmarkerResultToLandmarks] Skipping pose {detectedPoses} due to low presence confidence: {avgPresenceConfidence}", this);
                        continue;
                    }

                    Log.Debug($"[ConvertPoseLandmarkerResultToLandmarks] Processing pose {detectedPoses} with {poseLandmarks.landmarks.Count} landmarks", this);

                    // Convert 33 landmarks to Point structures (BlazePose/Ghost model structure)
                    for (int i = 0; i < poseLandmarks.landmarks.Count && i < 33; i++)
                    {
                        var landmark = poseLandmarks.landmarks[i];
                        
                        // Normalize coordinates to 0-1 range
                        var normalizedX = landmark.X; // MediaPipe already normalized
                        var normalizedY = landmark.Y;
                        
                        // Map to Tixl Point structure
                        landmarks.Add(new Point
                        {
                            Position = new Vector3(normalizedX, normalizedY, landmark.Z),
                            F1 = i, // Store landmark index
                            F2 = landmark.Visibility ?? 0f, // Store visibility
                            Color = Vector4.One,
                            Scale = Vector3.One,
                            Orientation = Quaternion.Identity
                        });
                    }

                    detectedPoses++;
                    
                    // Add pose-specific data to output dict
                    var poseIndex = detectedPoses - 1;
                    poseDataDict[$"pose_{poseIndex}_detection_confidence"] = avgDetectionConfidence;
                    poseDataDict[$"pose_{poseIndex}_presence_confidence"] = avgPresenceConfidence;
                    poseDataDict[$"pose_{poseIndex}_landmark_count"] = poseLandmarks.landmarks.Count;
                    
                    // Calculate pose bounding box
                    if (poseLandmarks.landmarks.Count > 0)
                    {
                        var bounds = CalculateBoundingBox(poseLandmarks.landmarks);
                        poseDataDict[$"pose_{poseIndex}_bbox_x"] = bounds.X;
                        poseDataDict[$"pose_{poseIndex}_bbox_y"] = bounds.Y;
                        poseDataDict[$"pose_{poseIndex}_bbox_width"] = bounds.Width;
                        poseDataDict[$"pose_{poseIndex}_bbox_height"] = bounds.Height;
                    }
                }
            }

            PoseData.Value = poseDataDict;
            Log.Debug($"[ConvertPoseLandmarkerResultToLandmarks] Converted {landmarks.Count} landmarks total", this);
            return landmarks.Count > 0 ? landmarks.ToArray() : null;
        }
        catch (Exception ex)
        {
            Log.Error($"[ConvertPoseLandmarkerResultToLandmarks] Error converting MediaPipe landmarks: {ex.Message}", this);
            return null;
        }
    }

    private float CalculateAverageDetectionConfidence(List<NormalizedLandmark> landmarks)
    {
        if (landmarks.Count == 0) return 0f;
        
        var totalConfidence = 0f;
        var validLandmarks = 0;
        
        foreach (var landmark in landmarks)
        {
            // Only count if landmark is visible enough
            bool shouldCount = true;
            if (landmark.Visibility.HasValue)
                shouldCount = landmark.Visibility.Value > 0.1f;
            // If no visibility data, assume landmark is visible

            if (shouldCount)
            {
                totalConfidence += landmark.Visibility ?? 0f;
                validLandmarks++;
            }
        }
        
        return validLandmarks > 0 ? totalConfidence / validLandmarks : 0f;
    }

    private float CalculateAveragePresenceConfidence(List<NormalizedLandmark> landmarks)
    {
        if (landmarks.Count == 0) return 0f;
        
        var totalPresence = 0f;
        var validLandmarks = 0;
        
        foreach (var landmark in landmarks)
        {
            // Only count if landmark has presence data
            bool shouldCount = true;
            if (landmark.Presence.HasValue)
                shouldCount = landmark.Presence.Value > 0.1f;
            // If no presence data, assume landmark is present

            if (shouldCount)
            {
                totalPresence += landmark.Presence ?? 0f;
                validLandmarks++;
            }
        }
        
        return validLandmarks > 0 ? totalPresence / validLandmarks : 0f;
    }

    private (float X, float Y, float Width, float Height) CalculateBoundingBox(List<NormalizedLandmark> landmarks)
    {
        if (landmarks.Count == 0) return (0, 0, 0, 0);

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;

        foreach (var landmark in landmarks)
        {
            bool isVisible = true;
            if (landmark.Visibility.HasValue)
                isVisible = landmark.Visibility.Value > 0.1f;
            // If no visibility data, assume landmark is visible

            if (isVisible)
            {
                minX = Math.Min(minX, landmark.X);
                minY = Math.Min(minY, landmark.Y);
                maxX = Math.Max(maxX, landmark.X);
                maxY = Math.Max(maxY, landmark.Y);
            }
        }

        if (minX != float.MaxValue)
        {
            return (minX, minY, maxX - minX, maxY - minY);
        }

        return (0, 0, 0, 0);
    }

    private Image? MatToMediaPipeImage(Mat mat)
    {
        try
        {
            Log.Debug($"[MatToMediaPipeImage] Converting Mat of size {mat.Width}x{mat.Height}...", this);
            Log.Debug($"[MatToMediaPipeImage] Mat type: {mat.Type()}, channels: {mat.Channels()}, depth: {mat.Depth()}", this);

            if (mat.Empty())
            {
                Log.Debug("[MatToMediaPipeImage] ERROR: Input Mat is empty", this);
                return null;
            }

            // Ensure Mat is in correct format (8-bit, 3 channels)
            Mat processedMat = new();

            try
            {
                // Convert to 8-bit 3-channel BGR if needed
                if (mat.Type() != MatType.CV_8UC3)
                {
                    Log.Debug($"[MatToMediaPipeImage] Converting Mat from {mat.Type()} to CV_8UC3", this);

                    if (mat.Channels() == 1)
                        // Convert grayscale to BGR
                        Cv2.CvtColor(mat, processedMat, ColorConversionCodes.GRAY2BGR);
                    else if (mat.Channels() == 4)
                        // Convert BGRA to BGR
                        Cv2.CvtColor(mat, processedMat, ColorConversionCodes.BGRA2BGR);
                    else if (mat.Depth() != MatType.CV_8U)
                        // Convert depth to 8-bit
                        mat.ConvertTo(processedMat, MatType.CV_8UC3);
                    else
                        // Just copy if channels are already correct but type is different
                        mat.CopyTo(processedMat);
                }
                else
                {
                    // Mat is already in correct format, just copy it
                    mat.CopyTo(processedMat);
                }

                Log.Debug($"[MatToMediaPipeImage] Processed Mat type: {processedMat.Type()}, channels: {processedMat.Channels()}", this);

                // Convert BGR to RGB for MediaPipe
                Mat rgbMat = new();
                Cv2.CvtColor(processedMat, rgbMat, ColorConversionCodes.BGR2RGB);

                Log.Debug($"[MatToMediaPipeImage] Converted to RGB, size: {rgbMat.Width}x{rgbMat.Height}", this);

                // Extract raw byte data from Mat
                byte[] imageData = new byte[rgbMat.Width * rgbMat.Height * 3];
                IntPtr ptr = rgbMat.Data;
                System.Runtime.InteropServices.Marshal.Copy(ptr, imageData, 0, imageData.Length);

                Log.Debug($"[MatToMediaPipeImage] Extracted image data array of size {imageData.Length}", this);

                // Create MediaPipe Image with correct format
                Image image = new(
                    Mediapipe.ImageFormat.Types.Format.Srgb,
                    rgbMat.Width,
                    rgbMat.Height,
                    rgbMat.Width * 3, // stride = width * 3 channels (RGB)
                    imageData
                );

                rgbMat.Dispose();
                Log.Debug("[MatToMediaPipeImage] Conversion successful", this);
                return image;
            }
            finally
            {
                processedMat.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[MatToMediaPipeImage] ERROR: {ex.Message}", this);
            Log.Error($"[MatToMediaPipeImage] Stack trace: {ex.StackTrace}", this);
            return null;
        }
    }
    #endregion

    #region Texture Conversion (Minimal OpenCV Usage)
    private Mat Texture2DToMat(Texture2D texture)
    {
        var device = ResourceManager.Device;
        var desc = texture.Description;

        // Create a staging texture with CPU read access
        var stagingDesc = new Texture2DDescription
                              {
                                  Width = desc.Width,
                                  Height = desc.Height,
                                  MipLevels = 1,
                                  ArraySize = 1,
                                  Format = desc.Format,
                                  SampleDescription = new SampleDescription(1, 0),
                                  Usage = ResourceUsage.Staging,
                                  BindFlags = BindFlags.None,
                                  CpuAccessFlags = CpuAccessFlags.Read,
                                  OptionFlags = ResourceOptionFlags.None
                              };

        using var stagingTexture = new SharpDX.Direct3D11.Texture2D(device, stagingDesc);
        device.ImmediateContext.CopyResource(texture, stagingTexture);

        // Map staging texture to access its data on CPU
        var dataBox = device.ImmediateContext.MapSubresource(stagingTexture, 0, MapMode.Read, MapFlags.None);
        if (dataBox.DataPointer == IntPtr.Zero)
        {
            device.ImmediateContext.UnmapSubresource(stagingTexture, 0);
            Log.Error("Failed to map staging texture.", this);
            return new Mat();
        }

        // Create a Mat and copy data directly
        // Assuming input texture format is BGRA (like from a webcam)
        var mat = new Mat(desc.Height, desc.Width, MatType.CV_8UC4);
        try
        {
            Utilities.CopyMemory(mat.Data, dataBox.DataPointer, (int)mat.Total() * mat.ElemSize());
        }
        finally
        {
            device.ImmediateContext.UnmapSubresource(stagingTexture, 0);
        }

        return mat;
    }
    #endregion

    #region Output Management
    private void UpdateOutputs(bool showLandmarks, bool showConnections)
    {
        // Set output texture (input texture with optional overlay)
        OutputTexture.Value = InputTexture.Value; // Tixl will handle visualization

        // Update landmark buffer if we have landmarks
        if (_landmarksArray != null)
        {
            UpdateLandmarkBuffer(_landmarksArray, showLandmarks, showConnections);
            PoseCount.Value = _landmarksArray.Length / 33; // 33 landmarks per pose
        }
        else
        {
            LandmarksBuffer.Value = null;
            PoseCount.Value = 0;
        }
    }

    private BufferWithViews? _landmarkBuffer;

    private void UpdateLandmarkBuffer(Point[] landmarks, bool showLandmarks, bool showConnections)
    {
        if (landmarks == null || landmarks.Length == 0) return;

        var pointCount = showLandmarks ? landmarks.Length : 0;
        
        if (_landmarkBuffer == null || _landmarkBuffer.Buffer.Description.SizeInBytes / Point.Stride != pointCount)
        {
            _landmarkBuffer?.Dispose();
            
            if (pointCount > 0)
            {
                _landmarkBuffer = new BufferWithViews();
                ResourceManager.SetupStructuredBuffer(landmarks, 
                    Point.Stride * pointCount, 
                    Point.Stride, 
                    ref _landmarkBuffer.Buffer);
                ResourceManager.CreateStructuredBufferSrv(_landmarkBuffer.Buffer, ref _landmarkBuffer.Srv);
                ResourceManager.CreateStructuredBufferUav(_landmarkBuffer.Buffer, 
                    UnorderedAccessViewBufferFlags.None, 
                    ref _landmarkBuffer.Uav);
            }
        }
        else if (pointCount > 0)
        {
            // Update existing buffer
            ResourceManager.Device.ImmediateContext.UpdateSubresource(landmarks, _landmarkBuffer.Buffer, 0);
        }
        else
        {
            // Clear buffer if no landmarks to show
            _landmarkBuffer?.Dispose();
            _landmarkBuffer = null;
        }

        LandmarksBuffer.Value = _landmarkBuffer;
    }
    #endregion

    #region Cleanup
    protected override void Dispose(bool isDisposing)
    {
        if (!isDisposing) return;

        _poseLandmarker?.Close();
        _landmarkBuffer?.Dispose();
        
        base.Dispose(isDisposing);
    }
    #endregion

    #region Input Parameters
    [Input(Guid = "A7B8C9D0-E1F2-4D23-E05B-345678901238")]
    public readonly InputSlot<Texture2D> InputTexture = new();

    [Input(Guid = "B8C9D0E1-F2A3-4E34-F16C-456789012349")]
    public readonly InputSlot<bool> Enabled = new(true);

    [Input(Guid = "C9D0E1F2-A3B4-4F45-A27D-567890123450")]
    public readonly InputSlot<int> MaxPoses = new(2);

    [Input(Guid = "D0E1F2A3-B4C5-4056-B38E-678901234560")]
    public readonly InputSlot<float> MinPoseDetectionConfidence = new(0.5f);

    [Input(Guid = "E1F2A3B4-C5D6-4167-C49F-789012345671")]
    public readonly InputSlot<float> MinPosePresenceConfidence = new(0.5f);

    [Input(Guid = "F2A3B4C5-D6E7-4278-D5A0-890123456672")]
    public readonly InputSlot<bool> ShowLandmarks = new(true);

    [Input(Guid = "A3B4C5D6-E7F8-4389-E6B1-901234567673")]
    public readonly InputSlot<bool> ShowConnections = new(true);

    [Input(Guid = "B4C5D6E7-F8A9-4490-F7C2-012345678674")]
    public readonly InputSlot<float> LandmarkSize = new(3.0f);

    [Input(Guid = "C5D6E7F8-A9B0-45A1-A8D3-123456789675")]
    public readonly InputSlot<Vector4> LandmarkColor = new(Vector4.One);

    [Input(Guid = "D6E7F8A9-B0C1-46B2-B9E4-234567890676")]
    public readonly InputSlot<Vector4> ConnectionColor = new(Vector4.One);
    #endregion
}