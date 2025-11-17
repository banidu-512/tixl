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
using Mediapipe.Tasks.Vision.FaceDetector;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Framework.Formats;

namespace Lib.io.video.mediapipe;

[Guid("A1B2C3D4-E5F6-4798-89AB-CDEF12345679")]
public class FaceDetection : Instance<FaceDetection>
{
    [Output(Guid = "B2C3D4E5-F6A7-489A-9B0C-DEF123456790", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D> OutputTexture = new();

    [Output(Guid = "C3D4E5F6-A7B8-49AB-AC1D-EF1234567891", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<BufferWithViews> DetectionsBuffer = new();

    [Output(Guid = "D4E5F6A7-B8C9-4AB0-BD2E-F12345678902", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Dict<float>> FaceData = new();

    [Output(Guid = "E5F6A7B8-C9D0-4B01-CE3F-12345678A903", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> FaceCount = new();

    [Output(Guid = "F6A7B8C9-D0E1-4C12-DF4A-234567A89004", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> UpdateCount = new();

    public FaceDetection()
    {
        OutputTexture.UpdateAction = Update;
        DetectionsBuffer.UpdateAction = Update;
        FaceData.UpdateAction = Update;
        FaceCount.UpdateAction = Update;
        UpdateCount.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var inputTexture = InputTexture.GetValue(context);
        var enableDetection = EnableDetection.GetValue(context);
        var showDetections = ShowDetections.GetValue(context);
        var showKeypoints = ShowKeypoints.GetValue(context);
        var confidenceThreshold = ConfidenceThreshold.GetValue(context);
        var maxFaces = MaxFaces.GetValue(context);

        // Lazy initialization: Initialize on first use or if it becomes null
        if (_faceDetector == null)
        {
            InitializeMediaPipe();
        }

        // Reset outputs if detection is disabled
        if (!enableDetection || inputTexture == null)
        {
            OutputTexture.Value = inputTexture;
            DetectionsBuffer.Value = null;
            FaceData.Value = new Dict<float>(0f);
            FaceCount.Value = 0;
            _detectionsArray = null;
            return;
        }

        // Process face detection
        if (ProcessTextureForFaceDetection(inputTexture, confidenceThreshold, maxFaces))
        {
            UpdateOutputs(showDetections, showKeypoints);
            UpdateCount.Value++;
        }
        else
        {
            // Fall back to input texture if detection fails
            OutputTexture.Value = inputTexture;
            Log.Debug("Face detection failed", this);
        }
    }

    #region MediaPipe Integration
    // DIAGNOSTIC: Replace low-level API with high-level task API
    private Mediapipe.Tasks.Vision.FaceDetector.FaceDetector? _faceDetector;
    private Point[]? _detectionsArray;
    private long _frameTimestamp; // DIAGNOSTIC: Add missing timestamp field

    private void InitializeMediaPipe()
    {
        try
        {
            // DIAGNOSTIC: Use high-level FaceDetector API instead of low-level CalculatorGraph
            Log.Debug("[FaceDetection] Starting FaceDetector initialization...", this);
            
            // DIAGNOSTIC: Add detailed logging for troubleshooting
            Log.Debug($"[FaceDetection] Current working directory: {System.IO.Directory.GetCurrentDirectory()}", this);
            Log.Debug($"[FaceDetection] Application base directory: {AppDomain.CurrentDomain.BaseDirectory}", this);
            
            // Check if model file exists - FIXED: Use absolute path resolution
            string modelPath = "../../Mediapipe-Sharp/src/Mediapipe/Models/blaze_face_short_range.tflite";
            string fullPath = System.IO.Path.GetFullPath(modelPath);
            
            Log.Debug($"[FaceDetection] Checking model path: {modelPath}", this);
            Log.Debug($"[FaceDetection] Full resolved path: {fullPath}", this);
            
            // ENHANCED: Check multiple possible model paths with better error handling
            string[] possibleModelPaths = {
                fullPath,
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "blaze_face_short_range.tflite"),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Models", "blaze_face_short_range.tflite"),
                "../../Mediapipe-Sharp/src/Mediapipe/Models/blaze_face_short_range.tflite",
                "../../../Mediapipe-Sharp/src/Mediapipe/Models/blaze_face_short_range.tflite"
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
                Log.Error($"[FaceDetection] Model file not found at any of the checked paths", this);
                foreach (string path in possibleModelPaths)
                {
                    string testPath = System.IO.Path.GetFullPath(path);
                    bool exists = System.IO.File.Exists(path);
                    Log.Debug($"[FaceDetection] Path check: {path} -> {testPath} (Exists: {exists})", this);
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
                Log.Error("[FaceDetection] CRITICAL: MediaPipe native library not found!", this);
                foreach (string path in possibleDllPaths)
                {
                    string testPath = System.IO.Path.GetFullPath(path);
                    bool exists = System.IO.File.Exists(path);
                    Log.Debug($"[FaceDetection] DLL Path check: {path} -> {testPath} (Exists: {exists})", this);
                }
                return;
            }
            
            Log.Debug($"[FaceDetection] Model file found at: {fullPath}", this);
            
            // DIAGNOSTIC: Check file size and accessibility
            var fileInfo = new System.IO.FileInfo(fullPath);
            Log.Debug($"[FaceDetection] Model file size: {fileInfo.Length} bytes", this);
            Log.Debug($"[FaceDetection] Model file accessible: {fileInfo.Exists}", this);
            
            // DIAGNOSTIC: Validate model format and compatibility
            Log.Debug($"[FaceDetection] Model file extension: {System.IO.Path.GetExtension(fullPath)}", this);
            Log.Debug($"[FaceDetection] Model file format validation: .tflite vs .task", this);
            
            // DIAGNOSTIC: Check if model is readable
            try
            {
                byte[] modelBytes = System.IO.File.ReadAllBytes(fullPath);
                Log.Debug($"[FaceDetection] Model file readable: {modelBytes.Length > 0} bytes", this);
                
                // Check for TFLite header signature
                if (modelBytes.Length >= 4)
                {
                    string header = System.Text.Encoding.ASCII.GetString(modelBytes, 0, 4);
                    Log.Debug($"[FaceDetection] Model file header: {header}", this);
                    bool isTflite = header == "TFL3";
                    Log.Debug($"[FaceDetection] Is valid TFLite format: {isTflite}", this);
                }
            }
            catch (Exception readEx)
            {
                Log.Error($"[FaceDetection] Error reading model file: {readEx.Message}", this);
            }
            
            Log.Debug($"[FaceDetection] Native DLL found at: {nativeDllPath}", this);
            
            // DIAGNOSTIC: Log MediaPipe library loading status
            Log.Debug("[FaceDetection] Creating CoreBaseOptions...", this);
            
            // Initialize FaceDetector with video mode for real-time processing
            // DIAGNOSTIC: Fix model path bug - use resolved fullPath instead of relative modelPath
            Log.Debug($"[FaceDetection] DIAGNOSTIC: Using modelPath={modelPath}, fullPath={fullPath}", this);
            var baseOptions = new Mediapipe.Tasks.Core.CoreBaseOptions(
                modelAssetPath: fullPath,  // FIXED: Use resolved absolute path
                delegateCase: Mediapipe.Tasks.Core.CoreBaseOptions.Delegate.CPU
            );

            Log.Debug("[FaceDetection] Creating FaceDetectorOptions...", this);
            FaceDetectorOptions options = new(
                baseOptions,
                VisionRunningMode.VIDEO,
                minDetectionConfidence: 0.5f,
                minSuppressionThreshold: 0.3f
            );

            Log.Debug("[FaceDetection] Calling FaceDetector.CreateFromOptions...", this);
            Log.Debug($"[FaceDetection] Options - BaseOptions model path: {baseOptions.ModelAssetPath}", this);
            Log.Debug($"[FaceDetection] Options - Running mode: {options.RunningMode}", this);
            Log.Debug($"[FaceDetection] Options - Min detection confidence: {options.MinDetectionConfidence}", this);
            Log.Debug($"[FaceDetection] Options - Min suppression threshold: {options.MinSuppressionThreshold}", this);
            
            // DIAGNOSTIC: Add detailed exception handling with inner exceptions
            try
            {
                Log.Debug("[FaceDetection] About to call FaceDetector.CreateFromOptions...", this);
                _faceDetector = Mediapipe.Tasks.Vision.FaceDetector.FaceDetector.CreateFromOptions(options);
                Log.Debug($"[FaceDetection] FaceDetector.CreateFromOptions returned: {_faceDetector != null}", this);
                
                if (_faceDetector != null)
                {
                    Log.Debug("[FaceDetection] FaceDetector object created successfully", this);
                    Log.Debug($"[FaceDetection] FaceDetector type: {_faceDetector.GetType().FullName}", this);
                }
                else
                {
                    Log.Error("[FaceDetection] FaceDetector.CreateFromOptions returned null without exception", this);
                }
            }
            catch (System.IO.FileNotFoundException fnfEx)
            {
                Log.Error($"[FaceDetection] File not found during FaceDetector creation: {fnfEx.Message}", this);
                Log.Error($"[FaceDetection] File not found details: {fnfEx.FileName}", this);
                _faceDetector = null;
            }
            catch (System.DllNotFoundException dllEx)
            {
                Log.Error($"[FaceDetection] Native DLL not found during FaceDetector creation: {dllEx.Message}", this);
                Log.Error($"[FaceDetection] Missing DLL: {dllEx.Message}", this);
                _faceDetector = null;
            }
            catch (System.BadImageFormatException imgEx)
            {
                Log.Error($"[FaceDetection] Invalid DLL format during FaceDetector creation: {imgEx.Message}", this);
                _faceDetector = null;
            }
            catch (Exception createEx)
            {
                Log.Error($"[FaceDetection] Exception during FaceDetector.CreateFromOptions: {createEx.Message}", this);
                Log.Error($"[FaceDetection] Exception type: {createEx.GetType().Name}", this);
                Log.Error($"[FaceDetection] Exception stack trace: {createEx.StackTrace}", this);
                _faceDetector = null;
            }
            
            if (_faceDetector != null)
            {
                Log.Debug("[FaceDetection] FaceDetector initialization successful!", this);
            }
            else
            {
                Log.Error("[FaceDetection] ERROR: FaceDetector is null after creation", this);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[FaceDetection] Failed to initialize FaceDetector: {ex.Message}", this);
            Log.Error($"[FaceDetection] Outer exception type: {ex.GetType().Name}", this);
        }
    }

    private bool ProcessTextureForFaceDetection(Texture2D texture, float confidenceThreshold, int maxFaces)
    {
        if (texture == null) return false;
 
        try
        {
            // DIAGNOSTIC: Use FaceDetector API instead of low-level graph
            Log.Debug("[FaceDetection] Starting face detection process...", this);
            
            // Convert Tixl Texture2D to OpenCV Mat (minimal usage - just for conversion)
            using var mat = Texture2DToMat(texture);
            if (mat.Empty()) 
            {
                Log.Debug("[FaceDetection] ERROR: Converted Mat is empty", this);
                return false;
            }

            Log.Debug($"[FaceDetection] Converted texture to Mat: {mat.Width}x{mat.Height}", this);

            // Convert Mat to MediaPipe Image
            var image = MatToMediaPipeImage(mat);
            if (image == null) 
            {
                Log.Debug("[FaceDetection] ERROR: Failed to convert Mat to MediaPipe Image", this);
                return false;
            }

            Log.Debug($"[FaceDetection] Converted to MediaPipe Image: {image.Width()}x{image.Height()}", this);

            // Process through FaceDetector
            if (_faceDetector == null)
            {
                Log.Debug("[FaceDetection] ERROR: FaceDetector is null", this);
                Log.Debug("[FaceDetection] DIAGNOSTIC: Checking initialization state...", this);
                Log.Debug($"[FaceDetection] _faceDetector field value: {_faceDetector}", this);
                Log.Debug("[FaceDetection] This indicates initialization failed silently or object was garbage collected", this);
                return false;
            }

            // Get current timestamp for video processing
            _frameTimestamp += 33; // Approximate 30 FPS (33ms per frame)

            Log.Debug($"[FaceDetection] Detecting faces for timestamp {_frameTimestamp}...", this);
            DetectionResult result = _faceDetector.DetectForVideo(image, _frameTimestamp);

            if (result.Detections == null || result.Detections.Count == 0)
            {
                Log.Debug("[FaceDetection] WARNING: No faces detected", this);
                return false;
            }

            Log.Debug($"[FaceDetection] Detected {result.Detections.Count} face(s)", this);

            // Convert MediaPipe results to our detection format
            _detectionsArray = ConvertDetectionResultToPoints(result, texture.Description.Width, texture.Description.Height, confidenceThreshold, maxFaces);
            return _detectionsArray != null;
        }
        catch (Exception ex)
        {
            Log.Error($"[FaceDetection] Error in face detection: {ex.Message}", this);
            return false;
        }
    }

    private Point[]? ConvertDetectionResultToPoints(DetectionResult result, int imageWidth, int imageHeight, float confidenceThreshold, int maxFaces)
    {
        if (result.Detections == null) return null;

        var points = new List<Point>();
        var faceDataDict = new Dict<float>(0f);

        try
        {
            Log.Debug($"[ConvertDetectionResultToPoints] Processing {result.Detections.Count} face detections", this);
            
            int detectedFaces = 0;
            foreach (var detection in result.Detections)
            {
                if (detectedFaces >= maxFaces) break;

                // DIAGNOSTIC: Log detection structure to understand available properties
                Log.Debug($"[DIAGNOSTIC] Detection type: {detection.GetType().FullName}", this);
                Log.Debug($"[DIAGNOSTIC] Detection Categories count: {detection.Categories?.Count ?? 0}", this);
                Log.Debug($"[DIAGNOSTIC] Detection BoundingBox: {detection.BoundingBox}", this);
                Log.Debug($"[DIAGNOSTIC] Detection Keypoints count: {detection.Keypoints?.Count ?? 0}", this);
                
                // Check confidence threshold - FIXED: Access Score through Categories
                float confidence = 0f;
                if (detection.Categories?.Count > 0)
                {
                    confidence = detection.Categories[0].Score;
                    Log.Debug($"[DIAGNOSTIC] First category confidence: {confidence}", this);
                }
                
                if (confidence < confidenceThreshold)
                {
                    Log.Debug($"[ConvertDetectionResultToPoints] Skipping face {detectedFaces} due to low confidence: {confidence}", this);
                    continue;
                }

                Log.Debug($"[ConvertDetectionResultToPoints] Processing face {detectedFaces}", this);

                // Add bounding box corners as points
                if (detection.BoundingBox.Right > detection.BoundingBox.Left && detection.BoundingBox.Bottom > detection.BoundingBox.Top)
                {
                    var bbox = detection.BoundingBox;
                    
                    // DIAGNOSTIC: Validate bounding box coordinates
                    Log.Debug($"[ConvertDetectionResultToPoints] Face {detectedFaces} bbox: L={bbox.Left}, T={bbox.Top}, R={bbox.Right}, B={bbox.Bottom}", this);
                    Log.Debug($"[ConvertDetectionResultToPoints] Face {detectedFaces} bbox dimensions: W={bbox.Right - bbox.Left}, H={bbox.Bottom - bbox.Top}", this);
                    
                    // Add bounding box corners (4 points)
                    var bboxPoint1 = new Point
                    {
                        Position = new Vector3((float)bbox.Left / imageWidth, (float)bbox.Top / imageHeight, 0),
                        F1 = detectedFaces * 10 + 0, // Unique ID for this point
                        F2 = confidence, // FIXED: Use confidence from Categories
                        Color = Vector4.One,
                        Scale = Vector3.One,
                        Orientation = Quaternion.Identity
                    };
                    points.Add(bboxPoint1);
                    Log.Debug($"[ConvertDetectionResultToPoints] Added bbox point 1 at ({bbox.Left}, {bbox.Top})", this);
                    
                    var bboxPoint2 = new Point
                    {
                        Position = new Vector3((float)bbox.Right / imageWidth, (float)bbox.Top / imageHeight, 0),
                        F1 = detectedFaces * 10 + 1,
                        F2 = confidence, // FIXED: Use confidence from Categories
                        Color = Vector4.One,
                        Scale = Vector3.One,
                        Orientation = Quaternion.Identity
                    };
                    points.Add(bboxPoint2);
                    Log.Debug($"[ConvertDetectionResultToPoints] Added bbox point 2 at ({bbox.Right}, {bbox.Top})", this);
                    
                    var bboxPoint3 = new Point
                    {
                        Position = new Vector3((float)bbox.Right / imageWidth, (float)bbox.Bottom / imageHeight, 0),
                        F1 = detectedFaces * 10 + 2,
                        F2 = confidence, // FIXED: Use confidence from Categories
                        Color = Vector4.One,
                        Scale = Vector3.One,
                        Orientation = Quaternion.Identity
                    };
                    points.Add(bboxPoint3);
                    Log.Debug($"[ConvertDetectionResultToPoints] Added bbox point 3 at ({bbox.Right}, {bbox.Bottom})", this);
                    
                    var bboxPoint4 = new Point
                    {
                        Position = new Vector3((float)bbox.Left / imageWidth, (float)bbox.Bottom / imageHeight, 0),
                        F1 = detectedFaces * 10 + 3,
                        F2 = confidence, // FIXED: Use confidence from Categories
                        Color = Vector4.One,
                        Scale = Vector3.One,
                        Orientation = Quaternion.Identity
                    };
                    points.Add(bboxPoint4);
                    Log.Debug($"[ConvertDetectionResultToPoints] Added bbox point 4 at ({bbox.Left}, {bbox.Bottom})", this);
                    
                    // Add keypoints (6 points per face)
                    if (detection.Keypoints != null && detection.Keypoints.Count >= 6)
                    {
                        Log.Debug($"[ConvertDetectionResultToPoints] Processing {detection.Keypoints.Count} keypoints for face {detectedFaces}", this);
                        for (int i = 0; i < 6; i++)
                        {
                            var keypoint = detection.Keypoints[i];
                            var keypointPoint = new Point
                            {
                                Position = new Vector3(keypoint.X, keypoint.Y, 0), // Already normalized
                                F1 = detectedFaces * 10 + 4 + i, // Unique ID for this keypoint
                                F2 = confidence, // FIXED: Use confidence from Categories
                                Color = Vector4.One,
                                Scale = Vector3.One,
                                Orientation = Quaternion.Identity
                            };
                            points.Add(keypointPoint);
                            Log.Debug($"[ConvertDetectionResultToPoints] Added keypoint {i} at ({keypoint.X}, {keypoint.Y})", this);
                        }
                    }
                    else
                    {
                        Log.Debug($"[ConvertDetectionResultToPoints] Adding placeholder keypoints for face {detectedFaces} (insufficient keypoints: {detection.Keypoints?.Count ?? 0})", this);
                        // Add placeholder keypoints if not available
                        var centerX = bbox.Left + (bbox.Right - bbox.Left) * 0.5f;
                        var centerY = bbox.Top + (bbox.Bottom - bbox.Top) * 0.5f;
                        for (int i = 0; i < 6; i++)
                        {
                            var placeholderPoint = new Point
                            {
                                Position = new Vector3(centerX, centerY, 0),
                                F1 = detectedFaces * 10 + 4 + i,
                                F2 = confidence, // FIXED: Use confidence from Categories
                                Color = Vector4.One,
                                Scale = Vector3.One,
                                Orientation = Quaternion.Identity
                            };
                            points.Add(placeholderPoint);
                            Log.Debug($"[ConvertDetectionResultToPoints] Added placeholder keypoint {i} at ({centerX}, {centerY})", this);
                        }
                    }
                }
                else
                {
                    Log.Debug($"[ConvertDetectionResultToPoints] Skipping face {detectedFaces} due to invalid bounding box", this);
                }

                detectedFaces++;
                
                // Add face-specific data to output dict
                var faceIndex = detectedFaces - 1;
                faceDataDict[$"face_{faceIndex}_confidence"] = confidence; // FIXED: Use confidence from Categories
                
                // Add bounding box data
                if (detection.BoundingBox.Right > detection.BoundingBox.Left && detection.BoundingBox.Bottom > detection.BoundingBox.Top)
                {
                    var bbox = detection.BoundingBox;
                    faceDataDict[$"face_{faceIndex}_bbox_x"] = (float)bbox.Left / imageWidth;
                    faceDataDict[$"face_{faceIndex}_bbox_y"] = (float)bbox.Top / imageHeight;
                    faceDataDict[$"face_{faceIndex}_bbox_width"] = (float)(bbox.Right - bbox.Left) / imageWidth;
                    faceDataDict[$"face_{faceIndex}_bbox_height"] = (float)(bbox.Bottom - bbox.Top) / imageHeight;
                }
                
                // Add keypoints data
                if (detection.Keypoints != null && detection.Keypoints.Count >= 6)
                {
                    for (int i = 0; i < 6; i++)
                    {
                        var keypoint = detection.Keypoints[i];
                        faceDataDict[$"face_{faceIndex}_keypoint_{i}_x"] = keypoint.X;
                        faceDataDict[$"face_{faceIndex}_keypoint_{i}_y"] = keypoint.Y;
                    }
                }
            }

            FaceData.Value = faceDataDict;
            Log.Debug($"[ConvertDetectionResultToPoints] Converted {points.Count} points total", this);
            return points.Count > 0 ? points.ToArray() : null;
        }
        catch (Exception ex)
        {
            Log.Error($"[ConvertDetectionResultToPoints] Error converting MediaPipe detections: {ex.Message}", this);
            return null;
        }
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

        // Map the staging texture to access its data on the CPU
        var dataBox = device.ImmediateContext.MapSubresource(stagingTexture, 0, MapMode.Read, MapFlags.None);
        if (dataBox.DataPointer == IntPtr.Zero)
        {
            device.ImmediateContext.UnmapSubresource(stagingTexture, 0);
            Log.Error("Failed to map staging texture.", this);
            return new Mat();
        }

        // Create a Mat and copy the data directly
        // Assuming the input texture format is BGRA (like from a webcam)
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
    private void UpdateOutputs(bool showDetections, bool showKeypoints)
    {
        // Set output texture (input texture with optional overlay)
        OutputTexture.Value = InputTexture.Value; // Tixl will handle visualization

        // Update detection buffer if we have detections
        if (_detectionsArray != null)
        {
            // DIAGNOSTIC: Log array state before buffer update
            Log.Debug($"[UpdateOutputs] About to update buffer with {_detectionsArray.Length} points", this);
            Log.Debug($"[UpdateOutputs] showDetections: {showDetections}, showKeypoints: {showKeypoints}", this);
            Log.Debug($"[UpdateOutputs] Calculated face count: {_detectionsArray.Length / 10}", this);
            
        // DIAGNOSTIC: Validate array integrity
            for (int i = 0; i < Math.Min(_detectionsArray.Length, 10); i++)
            {
                if (_detectionsArray[i].Equals(null))
                {
                    Log.Error($"[UpdateOutputs] NULL POINT in detections array at index {i}", this);
                    DetectionsBuffer.Value = null;
                    FaceCount.Value = 0;
                    return;
                }
                Log.Debug($"[UpdateOutputs] Detection point {i}: F1={_detectionsArray[i].F1}, Pos=({_detectionsArray[i].Position.X},{_detectionsArray[i].Position.Y})", this);
            }
            
            UpdateDetectionBuffer(_detectionsArray, showDetections, showKeypoints);
            FaceCount.Value = _detectionsArray.Length / 10; // 10 points per face (4 bbox corners + 6 keypoints)
            
            Log.Debug("[UpdateOutputs] Buffer update completed successfully", this);
        }
        else
        {
            Log.Debug("[UpdateOutputs] No detections available, clearing outputs", this);
            DetectionsBuffer.Value = null;
            FaceCount.Value = 0;
        }
    }

    private BufferWithViews? _detectionBuffer;

    private void UpdateDetectionBuffer(Point[] points, bool showDetections, bool showKeypoints)
    {
        if (points == null || points.Length == 0) return;

        // DIAGNOSTIC: Add detailed logging for buffer update
        Log.Debug($"[UpdateDetectionBuffer] Starting buffer update with {points.Length} points", this);
        Log.Debug($"[UpdateDetectionBuffer] showDetections: {showDetections}, showKeypoints: {showKeypoints}", this);
        
        var pointCount = (showDetections ? points.Length / 10 * 4 : 0) + (showKeypoints ? points.Length / 10 * 6 : 0);

        // DIAGNOSTIC: Validate pointCount calculation
        Log.Debug($"[UpdateDetectionBuffer] Calculated pointCount: {pointCount}", this);
        Log.Debug($"[UpdateDetectionBuffer] Total faces: {points.Length / 10}", this);
        Log.Debug($"[UpdateDetectionBuffer] Expected points per face: 10 (4 bbox + 6 keypoints)", this);
        
        if (pointCount <= 0)
        {
            Log.Debug("[UpdateDetectionBuffer] No points to display, clearing buffer", this);
            _detectionBuffer?.Dispose();
            _detectionBuffer = null;
            DetectionsBuffer.Value = null;
            return;
        }
        
        // Filter points based on what to show
        var filteredPoints = new List<Point>(pointCount);
        for (int i = 0; i < points.Length; i++)
        {
            var pointType = points[i].F1 % 10; // 0-3 for bbox, 4-9 for keypoints
            if ((showDetections && pointType < 4) || (showKeypoints && pointType >= 4))
            {
                filteredPoints.Add(points[i]);
            }
        }

        // DIAGNOSTIC: Validate filtered points array integrity
        for (int i = 0; i < Math.Min(points.Length, 20); i++) // Check first 20 points
        {
            if (points[i].Equals(null))
            {
                Log.Error($"[UpdateDetectionBuffer] NULL POINT DETECTED at index {i}", this);
                return;
            }
            Log.Debug($"[UpdateDetectionBuffer] Point {i}: F1={points[i].F1}, F2={points[i].F2}, Pos=({points[i].Position.X},{points[i].Position.Y})", this);
        }
        
        var filteredArray = filteredPoints.ToArray();
        var newSize = filteredArray.Length * Point.Stride;

        if (_detectionBuffer == null || _detectionBuffer.Buffer.Description.SizeInBytes != newSize)
        {
            Log.Debug("[UpdateDetectionBuffer] Recreating buffer due to size mismatch or null buffer", this);
            
            _detectionBuffer?.Dispose();
            
            if (filteredArray.Length > 0)
            {
                // DIAGNOSTIC: Validate filtered points before buffer creation
                Log.Debug($"[UpdateDetectionBuffer] Creating buffer with {filteredArray.Length} points, stride: {Point.Stride}", this);
                Log.Debug($"[UpdateDetectionBuffer] Total buffer size: {filteredArray.Length * Point.Stride} bytes", this);
                
                try
                {
                    _detectionBuffer = new BufferWithViews();
                    ResourceManager.SetupStructuredBuffer(filteredArray,
                        newSize,
                        Point.Stride,
                        ref _detectionBuffer.Buffer);
                    ResourceManager.CreateStructuredBufferSrv(_detectionBuffer.Buffer, ref _detectionBuffer.Srv);
                    ResourceManager.CreateStructuredBufferUav(_detectionBuffer.Buffer,
                        UnorderedAccessViewBufferFlags.None,
                        ref _detectionBuffer.Uav);
                    
                    Log.Debug("[UpdateDetectionBuffer] Buffer creation successful", this);
                }
                catch (Exception ex)
                {
                    Log.Error($"[UpdateDetectionBuffer] Exception during buffer creation: {ex.Message}", this);
                    Log.Error($"[UpdateDetectionBuffer] Stack trace: {ex.StackTrace}", this);
                    _detectionBuffer = null;
                    DetectionsBuffer.Value = null;
                    return;
                }
            }
        }
        else if (filteredArray.Length > 0)
        {
            // Update existing buffer
            Log.Debug("[UpdateDetectionBuffer] Updating existing buffer", this);
            
            try
            {
                // DIAGNOSTIC: Validate buffer state before update
                if (_detectionBuffer == null || _detectionBuffer.Buffer == null || _detectionBuffer.Buffer.IsDisposed)
                {
                    Log.Error("[UpdateDetectionBuffer] Buffer is null or disposed before update", this);
                    return;
                }
                
                Log.Debug($"[UpdateDetectionBuffer] Buffer size: {_detectionBuffer.Buffer.Description.SizeInBytes} bytes", this);
                Log.Debug($"[UpdateDetectionBuffer] Expected size: {newSize} bytes", this);
                
                ResourceManager.Device.ImmediateContext.UpdateSubresource(filteredArray, _detectionBuffer.Buffer);
                
                Log.Debug("[UpdateDetectionBuffer] Buffer update successful", this);
            }
            catch (Exception ex)
            {
                Log.Error($"[UpdateDetectionBuffer] Exception during buffer update: {ex.Message}", this);
                Log.Error($"[UpdateDetectionBuffer] Stack trace: {ex.StackTrace}", this);
            }
        }
        else
        {
            // Clear buffer if no detections to show
            Log.Debug("[UpdateDetectionBuffer] Clearing buffer (pointCount <= 0)", this);
            _detectionBuffer?.Dispose();
            _detectionBuffer = null;
        }

        DetectionsBuffer.Value = _detectionBuffer;
        Log.Debug("[UpdateDetectionBuffer] Buffer update completed", this);
    }
    #endregion

    #region Cleanup
    protected override void Dispose(bool isDisposing)
    {
        if (isDisposing)
        {
            _faceDetector?.Close();
            _detectionBuffer?.Dispose();
        }
        // The base class Dispose(isDisposing) will be called automatically.
    }
    #endregion

    #region Input Parameters
    [Input(Guid = "A7B8C9D0-E1F2-4D23-E05B-345678901235")]
    public readonly InputSlot<Texture2D> InputTexture = new();

    [Input(Guid = "B8C9D0E1-F2A3-4E34-F16C-456789012346")]
    public readonly InputSlot<bool> EnableDetection = new(true);

    [Input(Guid = "C9D0E1F2-A3B4-4F45-A27D-567890123457")]
    public readonly InputSlot<float> ConfidenceThreshold = new(0.5f);

    [Input(Guid = "D0E1F2A3-B4C5-4056-B38E-678901234568")]
    public readonly InputSlot<int> MaxFaces = new(5);

    [Input(Guid = "E1F2A3B4-C5D6-4167-C49F-789012345679")]
    public readonly InputSlot<bool> ShowDetections = new(true);

    [Input(Guid = "F2A3B4C5-D6E7-4278-D5A0-890123456680")]
    public readonly InputSlot<bool> ShowKeypoints = new(true);

    [Input(Guid = "A3B4C5D6-E7F8-4389-E6B1-901234567681")]
    public readonly InputSlot<float> DetectionSize = new(3.0f);

    [Input(Guid = "B4C5D6E7-F8A9-4490-F7C2-012345678682")]
    public readonly InputSlot<Vector4> DetectionColor = new(Vector4.One);

    [Input(Guid = "C5D6E7F8-A9B0-45A1-A8D3-123456789683")]
    public readonly InputSlot<Vector4> KeypointColor = new(Vector4.One);
    #endregion
}