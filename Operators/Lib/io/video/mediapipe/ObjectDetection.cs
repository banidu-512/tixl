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
using Mediapipe.Tasks.Vision.ObjectDetector;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Framework.Formats;

namespace Lib.io.video.mediapipe;

[Guid("A1B2C3D4-E5F6-4798-89AB-CDEF12345680")]
public class ObjectDetection : Instance<ObjectDetection>
{
    [Output(Guid = "B2C3D4E5-F6A7-489A-9B0C-DEF123456781", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D> OutputTexture = new();

    [Output(Guid = "C3D4E5F6-A7B8-49AB-AC1D-EF1234567892", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<BufferWithViews> DetectionsBuffer = new();

    [Output(Guid = "D4E5F6A7-B8C9-4AB0-BD2E-F12345678903", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Dict<float>> ObjectData = new();

    [Output(Guid = "E5F6A7B8-C9D0-4B01-CE3F-12345678A904", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> ObjectCount = new();

    [Output(Guid = "F6A7B8C9-D0E1-4C12-DF4A-234567A89005", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> UpdateCount = new();

    public ObjectDetection()
    {
        OutputTexture.UpdateAction = Update;
        DetectionsBuffer.UpdateAction = Update;
        ObjectData.UpdateAction = Update;
        ObjectCount.UpdateAction = Update;
        UpdateCount.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var inputTexture = InputTexture.GetValue(context);
        var enableDetection = EnableDetection.GetValue(context);
        var showDetections = ShowDetections.GetValue(context);
        var showBoundingBoxes = ShowBoundingBoxes.GetValue(context);
        var showLabels = ShowLabels.GetValue(context);
        var confidenceThreshold = ConfidenceThreshold.GetValue(context);
        var maxObjects = MaxObjects.GetValue(context);
        var categoryFilter = CategoryFilter.GetValue(context);
        var allowListMode = AllowListMode.GetValue(context);

        // Lazy initialization: Initialize on first use or if it becomes null
        if (_objectDetector == null)
        {
            InitializeMediaPipe();
        }

        // Reset outputs if detection is disabled
        if (!enableDetection || inputTexture == null)
        {
            OutputTexture.Value = inputTexture;
            DetectionsBuffer.Value = null;
            ObjectData.Value = new Dict<float>(0f);
            ObjectCount.Value = 0;
            _detectionsArray = null;
            return;
        }

        // Process object detection
        if (ProcessTextureForObjectDetection(inputTexture, confidenceThreshold, maxObjects, categoryFilter, allowListMode))
        {
            UpdateOutputs(showDetections, showBoundingBoxes, showLabels);
            UpdateCount.Value++;
        }
        else
        {
            // Fall back to input texture if detection fails
            OutputTexture.Value = inputTexture;
            Log.Debug("Object detection failed", this);
        }
    }

    #region MediaPipe Integration
    // DIAGNOSTIC: Replace low-level API with high-level task API
    private Mediapipe.Tasks.Vision.ObjectDetector.ObjectDetector? _objectDetector;
    private Point[]? _detectionsArray;
    private long _frameTimestamp; // DIAGNOSTIC: Add missing timestamp field

    private void InitializeMediaPipe()
    {
        try
        {
            // DIAGNOSTIC: Use high-level ObjectDetector API instead of low-level CalculatorGraph
            Log.Debug("[ObjectDetection] Starting ObjectDetector initialization...", this);
            
            // DIAGNOSTIC: Add detailed logging for troubleshooting
            Log.Debug($"[ObjectDetection] Current working directory: {System.IO.Directory.GetCurrentDirectory()}", this);
            Log.Debug($"[ObjectDetection] Application base directory: {AppDomain.CurrentDomain.BaseDirectory}", this);
            
            // Check if model file exists - FIXED: Use absolute path resolution
            string modelPath = "../../Mediapipe-Sharp/src/Mediapipe/Models/efficientdet_lite0.tflite";
            string fullPath = System.IO.Path.GetFullPath(modelPath);
            
            Log.Debug($"[ObjectDetection] Checking model path: {modelPath}", this);
            Log.Debug($"[ObjectDetection] Full resolved path: {fullPath}", this);
            
            // ENHANCED: Check multiple possible model paths with better error handling
            string[] possibleModelPaths = {
                fullPath,
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "efficientdet_lite0.tflite"),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Models", "efficientdet_lite0.tflite"),
                "../../Mediapipe-Sharp/src/Mediapipe/Models/eefficientdet_lite0.tflite",
                "../../../Mediapipe-Sharp/src/Mediapipe/Models/efficientdet_lite0.tflite"
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
                Log.Error($"[ObjectDetection] Model file not found at any of the checked paths", this);
                foreach (string path in possibleModelPaths)
                {
                    string testPath = System.IO.Path.GetFullPath(path);
                    bool exists = System.IO.File.Exists(path);
                    Log.Debug($"[ObjectDetection] Path check: {path} -> {testPath} (Exists: {exists})", this);
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
                Log.Error("[ObjectDetection] CRITICAL: MediaPipe native library not found!", this);
                foreach (string path in possibleDllPaths)
                {
                    string testPath = System.IO.Path.GetFullPath(path);
                    bool exists = System.IO.File.Exists(path);
                    Log.Debug($"[ObjectDetection] DLL Path check: {path} -> {testPath} (Exists: {exists})", this);
                }
                return;
            }
            
            Log.Debug($"[ObjectDetection] Model file found at: {fullPath}", this);
            
            // DIAGNOSTIC: Check file size and accessibility
            var fileInfo = new System.IO.FileInfo(fullPath);
            Log.Debug($"[ObjectDetection] Model file size: {fileInfo.Length} bytes", this);
            Log.Debug($"[ObjectDetection] Model file accessible: {fileInfo.Exists}", this);
            
            // DIAGNOSTIC: Validate model format and compatibility
            Log.Debug($"[ObjectDetection] Model file extension: {System.IO.Path.GetExtension(fullPath)}", this);
            Log.Debug($"[ObjectDetection] Model file format validation: .tflite vs .task", this);
            
            // DIAGNOSTIC: Check if model is readable
            try
            {
                byte[] modelBytes = System.IO.File.ReadAllBytes(fullPath);
                Log.Debug($"[ObjectDetection] Model file readable: {modelBytes.Length > 0} bytes", this);
                
                // Check for TFLite header signature
                if (modelBytes.Length >= 4)
                {
                    string header = System.Text.Encoding.ASCII.GetString(modelBytes, 0, 4);
                    Log.Debug($"[ObjectDetection] Model file header: {header}", this);
                    bool isTflite = header == "TFL3";
                    Log.Debug($"[ObjectDetection] Is valid TFLite format: {isTflite}", this);
                }
            }
            catch (Exception readEx)
            {
                Log.Error($"[ObjectDetection] Error reading model file: {readEx.Message}", this);
            }
            
            Log.Debug($"[ObjectDetection] Native DLL found at: {nativeDllPath}", this);
            
            // DIAGNOSTIC: Log MediaPipe library loading status
            Log.Debug("[ObjectDetection] Creating CoreBaseOptions...", this);
            
            // Initialize ObjectDetector with video mode for real-time processing
            // DIAGNOSTIC: Fix model path bug - use resolved fullPath instead of relative modelPath
            Log.Debug($"[ObjectDetection] DIAGNOSTIC: Using modelPath={modelPath}, fullPath={fullPath}", this);
            var baseOptions = new Mediapipe.Tasks.Core.CoreBaseOptions(
                modelAssetPath: fullPath,  // FIXED: Use resolved absolute path
                delegateCase: Mediapipe.Tasks.Core.CoreBaseOptions.Delegate.CPU
            );

            Log.Debug("[ObjectDetection] Creating ObjectDetectorOptions...", this);
            ObjectDetectorOptions options = new(
                baseOptions,
                VisionRunningMode.VIDEO,
                scoreThreshold: 0.5f,
                maxResults: 40
            );

            Log.Debug("[ObjectDetection] Calling ObjectDetector.CreateFromOptions...", this);
            Log.Debug($"[ObjectDetection] Options - BaseOptions model path: {baseOptions.ModelAssetPath}", this);
            Log.Debug($"[ObjectDetection] Options - Running mode: {options.RunningMode}", this);
            Log.Debug($"[ObjectDetection] Options - Score threshold: {options.ScoreThreshold}", this);
            Log.Debug($"[ObjectDetection] Options - Max results: {options.MaxResults}", this);
            
            // DIAGNOSTIC: Add detailed exception handling with inner exceptions
            try
            {
                Log.Debug("[ObjectDetection] About to call ObjectDetector.CreateFromOptions...", this);
                _objectDetector = Mediapipe.Tasks.Vision.ObjectDetector.ObjectDetector.CreateFromOptions(options);
                Log.Debug($"[ObjectDetection] ObjectDetector.CreateFromOptions returned: {_objectDetector != null}", this);
                
                if (_objectDetector != null)
                {
                    Log.Debug("[ObjectDetection] ObjectDetector object created successfully", this);
                    Log.Debug($"[ObjectDetection] ObjectDetector type: {_objectDetector.GetType().FullName}", this);
                }
                else
                {
                    Log.Error("[ObjectDetection] ObjectDetector.CreateFromOptions returned null without exception", this);
                }
            }
            catch (System.IO.FileNotFoundException fnfEx)
            {
                Log.Error($"[ObjectDetection] File not found during ObjectDetector creation: {fnfEx.Message}", this);
                Log.Error($"[ObjectDetection] File not found details: {fnfEx.FileName}", this);
                _objectDetector = null;
            }
            catch (System.DllNotFoundException dllEx)
            {
                Log.Error($"[ObjectDetection] Native DLL not found during ObjectDetector creation: {dllEx.Message}", this);
                Log.Error($"[ObjectDetection] Missing DLL: {dllEx.Message}", this);
                _objectDetector = null;
            }
            catch (System.BadImageFormatException imgEx)
            {
                Log.Error($"[ObjectDetection] Invalid DLL format during ObjectDetector creation: {imgEx.Message}", this);
                _objectDetector = null;
            }
            catch (Exception createEx)
            {
                Log.Error($"[ObjectDetection] Exception during ObjectDetector.CreateFromOptions: {createEx.Message}", this);
                Log.Error($"[ObjectDetection] Exception type: {createEx.GetType().Name}", this);
                Log.Error($"[ObjectDetection] Exception stack trace: {createEx.StackTrace}", this);
                _objectDetector = null;
            }
            
            if (_objectDetector != null)
            {
                Log.Debug("[ObjectDetection] ObjectDetector initialization successful!", this);
            }
            else
            {
                Log.Error("[ObjectDetection] ERROR: ObjectDetector is null after creation", this);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[ObjectDetection] Failed to initialize ObjectDetector: {ex.Message}", this);
            Log.Error($"[ObjectDetection] Outer exception type: {ex.GetType().Name}", this);
        }
    }

    private bool ProcessTextureForObjectDetection(Texture2D texture, float confidenceThreshold, int maxObjects, string categoryFilter, bool allowListMode)
    {
        if (texture == null) return false;
 
        try
        {
            // DIAGNOSTIC: Use ObjectDetector API instead of low-level graph
            Log.Debug("[ObjectDetection] Starting object detection process...", this);
            
            // Convert Tixl Texture2D to OpenCV Mat (minimal usage - just for conversion)
            using var mat = Texture2DToMat(texture);
            if (mat.Empty()) 
            {
                Log.Debug("[ObjectDetection] ERROR: Converted Mat is empty", this);
                return false;
            }

            Log.Debug($"[ObjectDetection] Converted texture to Mat: {mat.Width}x{mat.Height}", this);

            // Convert Mat to MediaPipe Image
            var image = MatToMediaPipeImage(mat);
            if (image == null) 
            {
                Log.Debug("[ObjectDetection] ERROR: Failed to convert Mat to MediaPipe Image", this);
                return false;
            }

            Log.Debug($"[ObjectDetection] Converted to MediaPipe Image: {image.Width()}x{image.Height()}", this);

            // Process through ObjectDetector
            if (_objectDetector == null)
            {
                Log.Debug("[ObjectDetection] ERROR: ObjectDetector is null", this);
                Log.Debug("[ObjectDetection] DIAGNOSTIC: Checking initialization state...", this);
                Log.Debug($"[ObjectDetection] _objectDetector field value: {_objectDetector}", this);
                Log.Debug("[ObjectDetection] This indicates initialization failed silently or object was garbage collected", this);
                return false;
            }

            // Get current timestamp for video processing
            _frameTimestamp += 33; // Approximate 30 FPS (33ms per frame)

            Log.Debug($"[ObjectDetection] Detecting objects for timestamp {_frameTimestamp}...", this);
            DetectionResult result = _objectDetector.DetectForVideo(image, _frameTimestamp);

            if (result.Detections == null || result.Detections.Count == 0)
            {
                Log.Debug("[ObjectDetection] WARNING: No objects detected", this);
                return false;
            }

            Log.Debug($"[ObjectDetection] Detected {result.Detections.Count} object(s)", this);

            // Convert MediaPipe results to our detection format
            _detectionsArray = ConvertDetectionResultToPoints(result, texture.Description.Width, texture.Description.Height, confidenceThreshold, maxObjects, categoryFilter, allowListMode);
            return _detectionsArray != null;
        }
        catch (Exception ex)
        {
            Log.Error($"[ObjectDetection] Error in object detection: {ex.Message}", this);
            return false;
        }
    }

    private Point[]? ConvertDetectionResultToPoints(DetectionResult result, int imageWidth, int imageHeight, float confidenceThreshold, int maxObjects, string categoryFilter, bool allowListMode)
    {
        if (result.Detections == null) return null;

        var points = new List<Point>();
        var objectDataDict = new Dict<float>(0f);

        try
        {
            Log.Debug($"[ConvertDetectionResultToPoints] Processing {result.Detections.Count} object detections", this);
            
            int detectedObjects = 0;
            foreach (var detection in result.Detections)
            {
                if (detectedObjects >= maxObjects) break;

                // DIAGNOSTIC: Log detection structure to understand available properties
                Log.Debug($"[DIAGNOSTIC] Detection type: {detection.GetType().FullName}", this);
                Log.Debug($"[DIAGNOSTIC] Detection Categories count: {detection.Categories?.Count ?? 0}", this);
                Log.Debug($"[DIAGNOSTIC] Detection BoundingBox: {detection.BoundingBox}", this);
                Log.Debug($"[DIAGNOSTIC] Detection Keypoints count: {detection.Keypoints?.Count ?? 0}", this);
                
                // Check confidence threshold - FIXED: Access Score through Categories
                float confidence = 0f;
                string categoryName = "";
                int categoryIndex = -1;
                
                if (detection.Categories?.Count > 0)
                {
                    confidence = detection.Categories[0].Score;
                    categoryIndex = detection.Categories[0].Index;
                    categoryName = detection.Categories[0].DisplayName ?? detection.Categories[0].CategoryName ?? $"category_{categoryIndex}";
                    Log.Debug($"[DIAGNOSTIC] First category confidence: {confidence}, name: {categoryName}", this);
                }
                
                // Apply category filtering if specified
                if (!string.IsNullOrEmpty(categoryFilter))
                {
                    bool categoryMatches = categoryName.Contains(categoryFilter, StringComparison.OrdinalIgnoreCase);
                    if (allowListMode && !categoryMatches)
                    {
                        Log.Debug($"[ConvertDetectionResultToPoints] Skipping object {detectedObjects} due to allowlist filter: {categoryName}", this);
                        continue;
                    }
                    if (!allowListMode && categoryMatches)
                    {
                        Log.Debug($"[ConvertDetectionResultToPoints] Skipping object {detectedObjects} due to denylist filter: {categoryName}", this);
                        continue;
                    }
                }
                
                if (confidence < confidenceThreshold)
                {
                    Log.Debug($"[ConvertDetectionResultToPoints] Skipping object {detectedObjects} due to low confidence: {confidence}", this);
                    continue;
                }

                Log.Debug($"[ConvertDetectionResultToPoints] Processing object {detectedObjects}", this);

                // Add bounding box corners as points
                if (detection.BoundingBox.Right > detection.BoundingBox.Left && detection.BoundingBox.Bottom > detection.BoundingBox.Top)
                {
                    var bbox = detection.BoundingBox;
                    
                    // DIAGNOSTIC: Validate bounding box coordinates
                    Log.Debug($"[ConvertDetectionResultToPoints] Object {detectedObjects} bbox: L={bbox.Left}, T={bbox.Top}, R={bbox.Right}, B={bbox.Bottom}", this);
                    Log.Debug($"[ConvertDetectionResultToPoints] Object {detectedObjects} bbox dimensions: W={bbox.Right - bbox.Left}, H={bbox.Bottom - bbox.Top}", this);
                    
                    // Add bounding box corners (4 points)
                    var bboxPoint1 = new Point
                    {
                        Position = new Vector3((float)bbox.Left / imageWidth, (float)bbox.Top / imageHeight, 0),
                        F1 = detectedObjects * 10 + 0, // Unique ID for this point
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
                        F1 = detectedObjects * 10 + 1,
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
                        F1 = detectedObjects * 10 + 2,
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
                        F1 = detectedObjects * 10 + 3,
                        F2 = confidence, // FIXED: Use confidence from Categories
                        Color = Vector4.One,
                        Scale = Vector3.One,
                        Orientation = Quaternion.Identity
                    };
                    points.Add(bboxPoint4);
                    Log.Debug($"[ConvertDetectionResultToPoints] Added bbox point 4 at ({bbox.Left}, {bbox.Bottom})", this);
                    
                    // Add center point for label (1 point)
                    var centerX = bbox.Left + (bbox.Right - bbox.Left) * 0.5f;
                    var centerY = bbox.Top + (bbox.Bottom - bbox.Top) * 0.5f;
                    var centerPoint = new Point
                    {
                        Position = new Vector3(centerX / imageWidth, centerY / imageHeight, 0),
                        F1 = detectedObjects * 10 + 4, // Unique ID for center point
                        F2 = confidence,
                        Color = Vector4.One,
                        Scale = Vector3.One,
                        Orientation = Quaternion.Identity
                    };
                    points.Add(centerPoint);
                    Log.Debug($"[ConvertDetectionResultToPoints] Added center point at ({centerX}, {centerY})", this);
                }
                else
                {
                    Log.Debug($"[ConvertDetectionResultToPoints] Skipping object {detectedObjects} due to invalid bounding box", this);
                }

                detectedObjects++;
                
                // Add object-specific data to output dict
                var objectIndex = detectedObjects - 1;
                objectDataDict[$"object_{objectIndex}_confidence"] = confidence; // FIXED: Use confidence from Categories
                objectDataDict[$"object_{objectIndex}_category"] = categoryIndex;
                
                // Add bounding box data
                if (detection.BoundingBox.Right > detection.BoundingBox.Left && detection.BoundingBox.Bottom > detection.BoundingBox.Top)
                {
                    var bbox = detection.BoundingBox;
                    objectDataDict[$"object_{objectIndex}_bbox_x"] = (float)bbox.Left / imageWidth;
                    objectDataDict[$"object_{objectIndex}_bbox_y"] = (float)bbox.Top / imageHeight;
                    objectDataDict[$"object_{objectIndex}_bbox_width"] = (float)(bbox.Right - bbox.Left) / imageWidth;
                    objectDataDict[$"object_{objectIndex}_bbox_height"] = (float)(bbox.Bottom - bbox.Top) / imageHeight;
                }
                
                // Add keypoints data if available
                if (detection.Keypoints != null && detection.Keypoints.Count > 0)
                {
                    objectDataDict[$"object_{objectIndex}_keypoint_count"] = detection.Keypoints.Count;
                    for (int i = 0; i < detection.Keypoints.Count; i++)
                    {
                        var keypoint = detection.Keypoints[i];
                        objectDataDict[$"object_{objectIndex}_keypoint_{i}_x"] = keypoint.X;
                        objectDataDict[$"object_{objectIndex}_keypoint_{i}_y"] = keypoint.Y;
                        objectDataDict[$"object_{objectIndex}_keypoint_{i}_score"] = keypoint.Score ?? 0f;
                    }
                }
            }

            ObjectData.Value = objectDataDict;
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
    private void UpdateOutputs(bool showDetections, bool showBoundingBoxes, bool showLabels)
    {
        // Set output texture (input texture with optional overlay)
        OutputTexture.Value = InputTexture.Value; // Tixl will handle visualization

        // Update detection buffer if we have detections
        if (_detectionsArray != null)
        {
            // DIAGNOSTIC: Log array state before buffer update
            Log.Debug($"[UpdateOutputs] About to update buffer with {_detectionsArray.Length} points", this);
            Log.Debug($"[UpdateOutputs] showDetections: {showDetections}, showBoundingBoxes: {showBoundingBoxes}, showLabels: {showLabels}", this);
            Log.Debug($"[UpdateOutputs] Calculated object count: {_detectionsArray.Length / 5}", this);
            
        // DIAGNOSTIC: Validate array integrity
            for (int i = 0; i < Math.Min(_detectionsArray.Length, 10); i++)
            {
                if (_detectionsArray[i].Equals(null))
                {
                    Log.Error($"[UpdateOutputs] NULL POINT in detections array at index {i}", this);
                    DetectionsBuffer.Value = null;
                    ObjectCount.Value = 0;
                    return;
                }
                Log.Debug($"[UpdateOutputs] Detection point {i}: F1={_detectionsArray[i].F1}, Pos=({_detectionsArray[i].Position.X},{_detectionsArray[i].Position.Y})", this);
            }
            
            UpdateDetectionBuffer(_detectionsArray, showDetections, showBoundingBoxes, showLabels);
            ObjectCount.Value = _detectionsArray.Length / 5; // 5 points per object (4 bbox corners + 1 center)
            
            Log.Debug("[UpdateOutputs] Buffer update completed successfully", this);
        }
        else
        {
            Log.Debug("[UpdateOutputs] No detections available, clearing outputs", this);
            DetectionsBuffer.Value = null;
            ObjectCount.Value = 0;
        }
    }

    private BufferWithViews? _detectionBuffer;

    private void UpdateDetectionBuffer(Point[] points, bool showDetections, bool showBoundingBoxes, bool showLabels)
    {
        if (points == null || points.Length == 0) return;

        // DIAGNOSTIC: Add detailed logging for buffer update
        Log.Debug($"[UpdateDetectionBuffer] Starting buffer update with {points.Length} points", this);
        Log.Debug($"[UpdateDetectionBuffer] showDetections: {showDetections}, showBoundingBoxes: {showBoundingBoxes}, showLabels: {showLabels}", this);
        
        var pointCount = 0;
        
        // Calculate points to show based on options
        for (int i = 0; i < points.Length; i++)
        {
            var pointType = points[i].F1 % 10; // 0-3 for bbox corners, 4 for center
            if (showDetections && ((showBoundingBoxes && pointType < 4) || (showLabels && pointType == 4)))
            {
                pointCount++;
            }
        }

        // DIAGNOSTIC: Validate pointCount calculation
        Log.Debug($"[UpdateDetectionBuffer] Calculated pointCount: {pointCount}", this);
        Log.Debug($"[UpdateDetectionBuffer] Total objects: {points.Length / 5}", this);
        Log.Debug($"[UpdateDetectionBuffer] Expected points per object: 5 (4 bbox corners + 1 center)", this);
        
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
            var pointType = points[i].F1 % 10; // 0-3 for bbox corners, 4 for center
            if (showDetections && ((showBoundingBoxes && pointType < 4) || (showLabels && pointType == 4)))
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
            _objectDetector?.Close();
            _detectionBuffer?.Dispose();
        }
        // The base class Dispose(isDisposing) will be called automatically.
    }
    #endregion

    #region Input Parameters
    [Input(Guid = "A7B8C9D0-E1F2-4D23-E05B-345678901236")]
    public readonly InputSlot<Texture2D> InputTexture = new();

    [Input(Guid = "B8C9D0E1-F2A3-4E34-F16C-456789012347")]
    public readonly InputSlot<bool> EnableDetection = new(true);

    [Input(Guid = "C9D0E1F2-A3B4-4F45-A27D-567890123458")]
    public readonly InputSlot<float> ConfidenceThreshold = new(0.5f);

    [Input(Guid = "D0E1F2A3-B4C5-4056-B38E-678901234569")]
    public readonly InputSlot<int> MaxObjects = new(10);

    [Input(Guid = "E1F2A3B4-C5D6-4167-C49F-789012345670")]
    public readonly InputSlot<bool> ShowDetections = new(true);

    [Input(Guid = "F2A3B4C5-D6E7-4278-D5A0-890123456671")]
    public readonly InputSlot<bool> ShowBoundingBoxes = new(true);

    [Input(Guid = "A3B4C5D6-E7F8-4389-E6B1-901234567672")]
    public readonly InputSlot<bool> ShowLabels = new(true);

    [Input(Guid = "B4C5D6E7-F8A9-4490-F7C2-012345678673")]
    public readonly InputSlot<string> CategoryFilter = new("");

    [Input(Guid = "C5D6E7F8-A9B0-45A1-A8D3-123456789674")]
    public readonly InputSlot<bool> AllowListMode = new(true); // true = allowlist, false = denylist

    [Input(Guid = "D6E7F8A9-B0C1-46B2-B9E4-234567890675")]
    public readonly InputSlot<float> DetectionSize = new(3.0f);

    [Input(Guid = "E7F8A9B0-C1D2-47C3-C0A5-345678901676")]
    public readonly InputSlot<Vector4> BoundingBoxColor = new(Vector4.One);

    [Input(Guid = "F8A9B0C1-D2E3-4D74-D1B6-456789012677")]
    public readonly InputSlot<Vector4> LabelColor = new(Vector4.One);
    #endregion
}