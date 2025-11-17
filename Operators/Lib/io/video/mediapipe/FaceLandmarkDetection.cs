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
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Framework.Formats;
using Landmark = Mediapipe.Landmark;

namespace Lib.io.video.mediapipe;

[Guid("A1B2C3D4-E5F6-4798-89AB-CDEF12345678")]
public class FaceLandmarkDetection : Instance<FaceLandmarkDetection>
{
    [Output(Guid = "B2C3D4E5-F6A7-489A-9B0C-DEF123456789", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D> OutputTexture = new();

    [Output(Guid = "C3D4E5F6-A7B8-49AB-AC1D-EF1234567890", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<BufferWithViews> LandmarksBuffer = new();

    [Output(Guid = "D4E5F6A7-B8C9-4AB0-BD2E-F12345678901", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Dict<float>> FaceData = new();

    [Output(Guid = "E5F6A7B8-C9D0-4B01-CE3F-123456789012", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> FaceCount = new();

    [Output(Guid = "F6A7B8C9-D0E1-4C12-DF4A-234567890123", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> UpdateCount = new();

    public FaceLandmarkDetection()
    {
        OutputTexture.UpdateAction = Update;
        LandmarksBuffer.UpdateAction = Update;
        FaceData.UpdateAction = Update;
        FaceCount.UpdateAction = Update;
        UpdateCount.UpdateAction = Update;
        
        InitializeMediaPipe();
    }

    private void Update(EvaluationContext context)
    {
        var inputTexture = InputTexture.GetValue(context);
        var enableDetection = EnableDetection.GetValue(context);
        var showLandmarks = ShowLandmarks.GetValue(context);
        var showConnections = ShowConnections.GetValue(context);
        var confidenceThreshold = ConfidenceThreshold.GetValue(context);
        var maxFaces = MaxFaces.GetValue(context);

        // Reset outputs if detection is disabled
        if (!enableDetection || inputTexture == null)
        {
            OutputTexture.Value = inputTexture;
            LandmarksBuffer.Value = null;
            FaceData.Value = new Dict<float>(0f);
            FaceCount.Value = 0;
            _landmarksArray = null;
            return;
        }

        // Process face detection
        if (ProcessTextureForFaceDetection(inputTexture, confidenceThreshold, maxFaces))
        {
            UpdateOutputs(showLandmarks, showConnections);
            UpdateCount.Value++;
        }
        else
        {
            // Fall back to input texture if detection fails
            OutputTexture.Value = inputTexture;
            Log.Debug("Face landmark detection failed", this);
        }
    }

    #region MediaPipe Integration
    // DIAGNOSTIC: Replace low-level API with high-level task API
    private FaceLandmarker? _faceLandmarker;
    // private ImageFrame? _lastFrame; // Removed unused field
    private Point[]? _landmarksArray;
    private long _frameTimestamp; // DIAGNOSTIC: Add missing timestamp field

    private void InitializeMediaPipe()
    {
        try
        {
            // DIAGNOSTIC: Use high-level FaceLandmarker API instead of low-level CalculatorGraph
            Log.Debug("[FaceLandmarkDetection] Starting FaceLandmarker initialization...", this);
            
            // DIAGNOSTIC: Add detailed logging for troubleshooting
            Log.Debug($"[FaceLandmarkDetection] Current working directory: {System.IO.Directory.GetCurrentDirectory()}", this);
            Log.Debug($"[FaceLandmarkDetection] Application base directory: {AppDomain.CurrentDomain.BaseDirectory}", this);
            
            // Check if model file exists - FIXED: Use absolute path resolution
            string modelPath = "../../Mediapipe-Sharp/src/Mediapipe/Models/face_landmarker.task";
            string fullPath = System.IO.Path.GetFullPath(modelPath);
            
            Log.Debug($"[FaceLandmarkDetection] Checking model path: {modelPath}", this);
            Log.Debug($"[FaceLandmarkDetection] Full resolved path: {fullPath}", this);
            
            // ENHANCED: Check multiple possible model paths with better error handling
            string[] possibleModelPaths = {
                fullPath,
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "face_landmarker.task"),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Models", "face_landmarker.task"),
                "../../Mediapipe-Sharp/src/Mediapipe/Models/face_landmarker.task",
                "../../../Mediapipe-Sharp/src/Mediapipe/Models/face_landmarker.task"
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
                Log.Error($"[FaceLandmarkDetection] Model file not found at any of the checked paths", this);
                foreach (string path in possibleModelPaths)
                {
                    string testPath = System.IO.Path.GetFullPath(path);
                    bool exists = System.IO.File.Exists(path);
                    Log.Debug($"[FaceLandmarkDetection] Path check: {path} -> {testPath} (Exists: {exists})", this);
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
                Log.Error("[FaceLandmarkDetection] CRITICAL: MediaPipe native library not found!", this);
                foreach (string path in possibleDllPaths)
                {
                    string testPath = System.IO.Path.GetFullPath(path);
                    bool exists = System.IO.File.Exists(path);
                    Log.Debug($"[FaceLandmarkDetection] DLL Path check: {path} -> {testPath} (Exists: {exists})", this);
                }
                return;
            }
            
            Log.Debug($"[FaceLandmarkDetection] Model file found at: {fullPath}", this);
            
            // DIAGNOSTIC: Check file size and accessibility
            var fileInfo = new System.IO.FileInfo(fullPath);
            Log.Debug($"[FaceLandmarkDetection] Model file size: {fileInfo.Length} bytes", this);
            Log.Debug($"[FaceLandmarkDetection] Model file accessible: {fileInfo.Exists}", this);
            
            Log.Debug($"[FaceLandmarkDetection] Native DLL found at: {nativeDllPath}", this);
            
            // DIAGNOSTIC: Log MediaPipe library loading status
            Log.Debug("[FaceLandmarkDetection] Creating CoreBaseOptions...", this);
            
            // Initialize FaceLandmarker with video mode for real-time processing
            var baseOptions = new Mediapipe.Tasks.Core.CoreBaseOptions(
                modelAssetPath: fullPath,  // FIXED: Use resolved absolute path
                delegateCase: Mediapipe.Tasks.Core.CoreBaseOptions.Delegate.CPU
            );

            Log.Debug("[FaceLandmarkDetection] Creating FaceLandmarkerOptions...", this);
            FaceLandmarkerOptions options = new(
                baseOptions,
                VisionRunningMode.VIDEO
            );

            Log.Debug("[FaceLandmarkDetection] Calling FaceLandmarker.CreateFromOptions...", this);
            Log.Debug($"[FaceLandmarkDetection] Options - BaseOptions model path: {baseOptions.ModelAssetPath}", this);
            Log.Debug($"[FaceLandmarkDetection] Options - Running mode: {options.RunningMode}", this);
            
            // DIAGNOSTIC: Add detailed exception handling with inner exceptions
            try
            {
                Log.Debug("[FaceLandmarkDetection] About to call FaceLandmarker.CreateFromOptions...", this);
                _faceLandmarker = FaceLandmarker.CreateFromOptions(options);
                Log.Debug($"[FaceLandmarkDetection] FaceLandmarker.CreateFromOptions returned: {_faceLandmarker != null}", this);
                
                if (_faceLandmarker != null)
                {
                    Log.Debug("[FaceLandmarkDetection] FaceLandmarker object created successfully", this);
                    Log.Debug($"[FaceLandmarkDetection] FaceLandmarker type: {_faceLandmarker.GetType().FullName}", this);
                }
                else
                {
                    Log.Error("[FaceLandmarkDetection] FaceLandmarker.CreateFromOptions returned null without exception", this);
                }
            }
            catch (System.IO.FileNotFoundException fnfEx)
            {
                Log.Error($"[FaceLandmarkDetection] File not found during FaceLandmarker creation: {fnfEx.Message}", this);
                Log.Error($"[FaceLandmarkDetection] File not found details: {fnfEx.FileName}", this);
                _faceLandmarker = null;
            }
            catch (System.DllNotFoundException dllEx)
            {
                Log.Error($"[FaceLandmarkDetection] Native DLL not found during FaceLandmarker creation: {dllEx.Message}", this);
                Log.Error($"[FaceLandmarkDetection] Missing DLL: {dllEx.Message}", this);
                _faceLandmarker = null;
            }
            catch (System.BadImageFormatException imgEx)
            {
                Log.Error($"[FaceLandmarkDetection] Invalid DLL format during FaceLandmarker creation: {imgEx.Message}", this);
                _faceLandmarker = null;
            }
            catch (Exception createEx)
            {
                Log.Error($"[FaceLandmarkDetection] Exception during FaceLandmarker.CreateFromOptions: {createEx.Message}", this);
                Log.Error($"[FaceLandmarkDetection] Exception type: {createEx.GetType().Name}", this);
                Log.Error($"[FaceLandmarkDetection] Exception stack trace: {createEx.StackTrace}", this);
                _faceLandmarker = null;
            }
            
            if (_faceLandmarker != null)
            {
                Log.Debug("[FaceLandmarkDetection] FaceLandmarker initialization successful!", this);
            }
            else
            {
                Log.Error("[FaceLandmarkDetection] ERROR: FaceLandmarker is null after creation", this);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[FaceLandmarkDetection] Failed to initialize FaceLandmarker: {ex.Message}", this);
            Log.Error($"[FaceLandmarkDetection] Outer exception type: {ex.GetType().Name}", this);
        }
    }

    private bool ProcessTextureForFaceDetection(Texture2D texture, float confidenceThreshold, int maxFaces)
    {
        if (texture == null) return false;

        try
        {
            // DIAGNOSTIC: Use FaceLandmarker API instead of low-level graph
            Log.Debug("[FaceLandmarkDetection] Starting face detection process...", this);
            
            // Convert Tixl Texture2D to OpenCV Mat (minimal usage - just for conversion)
            using var mat = Texture2DToMat(texture);
            if (mat.Empty()) 
            {
                Log.Debug("[FaceLandmarkDetection] ERROR: Converted Mat is empty", this);
                return false;
            }

            Log.Debug($"[FaceLandmarkDetection] Converted texture to Mat: {mat.Width}x{mat.Height}", this);

            // Convert Mat to MediaPipe Image
            var image = MatToMediaPipeImage(mat);
            if (image == null) 
            {
                Log.Debug("[FaceLandmarkDetection] ERROR: Failed to convert Mat to MediaPipe Image", this);
                return false;
            }

            Log.Debug($"[FaceLandmarkDetection] Converted to MediaPipe Image: {image.Width()}x{image.Height()}", this);

            // Process through FaceLandmarker
            if (_faceLandmarker == null)
            {
                Log.Debug("[FaceLandmarkDetection] ERROR: FaceLandmarker is null", this);
                return false;
            }

            // Get current timestamp for video processing
            _frameTimestamp += 33; // Approximate 30 FPS (33ms per frame)

            Log.Debug($"[FaceLandmarkDetection] Detecting landmarks for timestamp {_frameTimestamp}...", this);
            FaceLandmarkerResult result = _faceLandmarker.DetectForVideo(image, _frameTimestamp);

            if (result.FaceLandmarks == null || result.FaceLandmarks.Count == 0)
            {
                Log.Debug("[FaceLandmarkDetection] WARNING: No face landmarks detected", this);
                return false;
            }

            Log.Debug($"[FaceLandmarkDetection] Detected {result.FaceLandmarks.Count} face(s)", this);

            // Convert MediaPipe results to our landmark format
            _landmarksArray = ConvertFaceLandmarkerResultToLandmarks(result, confidenceThreshold, maxFaces);
            
            // _lastFrame = null; // We don't need to store ImageFrame anymore - commented out since field was removed
            return _landmarksArray != null;
        }
        catch (Exception ex)
        {
            Log.Error($"[FaceLandmarkDetection] Error in face detection: {ex.Message}", this);
            return false;
        }
    }

    private Point[]? ConvertFaceLandmarkerResultToLandmarks(FaceLandmarkerResult result, float confidenceThreshold, int maxFaces)
    {
        if (result.FaceLandmarks == null) return null;

        var landmarks = new List<Point>();
        var faceDataDict = new Dict<float>(0f);

        try
        {
            Log.Debug($"[ConvertFaceLandmarkerResultToLandmarks] Processing {result.FaceLandmarks.Count} face landmark sets", this);
            
            int detectedFaces = 0;
            foreach (var faceLandmarks in result.FaceLandmarks)
            {
                if (detectedFaces >= maxFaces) break;

                // Check confidence threshold (simplified - MediaPipe provides visibility per landmark)
                if (faceLandmarks.landmarks != null && faceLandmarks.landmarks.Count > 0)
                {
                    var avgConfidence = CalculateAverageConfidence(faceLandmarks.landmarks);
                    if (avgConfidence < confidenceThreshold) 
                    {
                        Log.Debug($"[ConvertFaceLandmarkerResultToLandmarks] Skipping face {detectedFaces} due to low confidence: {avgConfidence}", this);
                        continue;
                    }

                    Log.Debug($"[ConvertFaceLandmarkerResultToLandmarks] Processing face {detectedFaces} with {faceLandmarks.landmarks.Count} landmarks", this);

                    // Convert 468 landmarks to Point structures
                    for (int i = 0; i < faceLandmarks.landmarks.Count && i < 468; i++)
                    {
                        var landmark = faceLandmarks.landmarks[i];
                        
                        // Normalize coordinates to 0-1 range
                        var normalizedX = landmark.X; // MediaPipe already normalized
                        var normalizedY = landmark.Y;
                        
                        // Map to Tixl Point structure
                        landmarks.Add(new Point
                        {
                            Position = new Vector3(normalizedX, normalizedY, 0),
                            F1 = i, // Store landmark index
                            F2 = landmark.Z * (landmark.Visibility ?? 0f), // Store depth/confidence
                            Color = Vector4.One,
                            Scale = Vector3.One,
                            Orientation = Quaternion.Identity
                        });
                    }

                    detectedFaces++;
                    
                    // Add face-specific data to output dict
                    var faceIndex = detectedFaces - 1;
                    faceDataDict[$"face_{faceIndex}_confidence"] = avgConfidence;
                    faceDataDict[$"face_{faceIndex}_landmark_count"] = faceLandmarks.landmarks.Count;
                    
                    // Calculate face bounding box
                    if (faceLandmarks.landmarks.Count > 0)
                    {
                        var bounds = CalculateBoundingBox(faceLandmarks.landmarks);
                        faceDataDict[$"face_{faceIndex}_bbox_x"] = bounds.X;
                        faceDataDict[$"face_{faceIndex}_bbox_y"] = bounds.Y;
                        faceDataDict[$"face_{faceIndex}_bbox_width"] = bounds.Width;
                        faceDataDict[$"face_{faceIndex}_bbox_height"] = bounds.Height;
                    }
                }
            }

            FaceData.Value = faceDataDict;
            Log.Debug($"[ConvertFaceLandmarkerResultToLandmarks] Converted {landmarks.Count} landmarks total", this);
            return landmarks.Count > 0 ? landmarks.ToArray() : null;
        }
        catch (Exception ex)
        {
            Log.Error($"[ConvertFaceLandmarkerResultToLandmarks] Error converting MediaPipe landmarks: {ex.Message}", this);
            return null;
        }
    }

    private float CalculateAverageConfidence(List<NormalizedLandmark> landmarks)
    {
        if (landmarks.Count == 0) return 0f;
        
        var totalConfidence = 0f;
        var validLandmarks = 0;
        
        foreach (var landmark in landmarks)
        {
            // Only count if landmark is visible enough
            // Modified: Lower visibility threshold and handle missing visibility data
            bool shouldDraw = true;
            if (landmark.Visibility.HasValue)
                shouldDraw = landmark.Visibility.Value > 0.1f; // Lowered threshold from 0.3f to 0.1f
            // If no visibility data, assume landmark is visible

            if (shouldDraw)
            {
                totalConfidence += landmark.Visibility ?? 0f;
                validLandmarks++;
            }
        }
        
        return validLandmarks > 0 ? totalConfidence / validLandmarks : 0f;
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
            // Modified: Lower visibility threshold and handle missing visibility data
            bool isVisible = true;
            if (landmark.Visibility.HasValue)
                isVisible = landmark.Visibility.Value > 0.1f; // Lowered threshold from 0.3f to 0.1f
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
        try
        {
            var device = ResourceManager.Device;
            var desc = texture.Description;
            
            // Create staging texture for CPU read
            var stagingDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription
                {
                    Count = 1,
                    Quality = 0
                },
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            };

            using var stagingTexture = new SharpDX.Direct3D11.Texture2D(device, stagingDesc);
            device.ImmediateContext.CopyResource(texture, stagingTexture);

            // Map texture to CPU memory
            var dataBox = device.ImmediateContext.MapSubresource(stagingTexture, 0, MapMode.Read, MapFlags.None);

            // Create bitmap from texture data
            var bitmap = new System.Drawing.Bitmap(desc.Width, desc.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            
            // Copy pixel data (simplified - assuming RGBA format)
            var bitmapData = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, desc.Width, desc.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            unsafe
            {
                var srcPtr = (byte*)dataBox.DataPointer.ToPointer();
                var dstPtr = (byte*)bitmapData.Scan0.ToPointer();
                
                for (int y = 0; y < desc.Height; y++)
                {
                    for (int x = 0; x < desc.Width; x++)
                    {
                        var srcIndex = (y * dataBox.RowPitch + x * 4);
                        var dstIndex = y * bitmapData.Stride + x * 4;
                        
                        // Copy RGBA data (converting BGRA to ARGB)
                        dstPtr[dstIndex + 2] = srcPtr[srcIndex + 0]; // R
                        dstPtr[dstIndex + 1] = srcPtr[srcIndex + 1]; // G
                        dstPtr[dstIndex + 0] = srcPtr[srcIndex + 2]; // B
                        dstPtr[dstIndex + 3] = srcPtr[srcIndex + 3]; // A
                    }
                }
            }

            bitmap.UnlockBits(bitmapData);
            device.ImmediateContext.UnmapSubresource(stagingTexture, 0);

            // Convert to OpenCV Mat
            var mat = new OpenCvSharp.Mat(bitmap.Width, bitmap.Height, OpenCvSharp.MatType.CV_8UC4);
            var bmpData = bitmap.LockBits(
                new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    var srcPtr = (byte*)bmpData.Scan0;
                    var dstPtr = (byte*)mat.Data;
                    
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            var srcIndex = y * bmpData.Stride + x * 4;
                            var dstIndex = y * mat.Step() + x * 4;
                            
                            // Copy BGRA to BGRA (OpenCV format)
                            dstPtr[dstIndex + 0] = srcPtr[srcIndex + 0]; // B
                            dstPtr[dstIndex + 1] = srcPtr[srcIndex + 1]; // G
                            dstPtr[dstIndex + 2] = srcPtr[srcIndex + 2]; // R
                            dstPtr[dstIndex + 3] = srcPtr[srcIndex + 3]; // A
                        }
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
            bitmap.Dispose();
            stagingTexture.Dispose();

            return mat;
        }
        catch (Exception ex)
        {
            Log.Error($"Texture to Mat conversion failed: {ex.Message}", this);
            return new Mat();
        }
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
            FaceCount.Value = _landmarksArray.Length / 468; // 468 landmarks per face
        }
        else
        {
            LandmarksBuffer.Value = null;
            FaceCount.Value = 0;
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
            var dataBox = new DataBox();
            ResourceManager.Device.ImmediateContext.UpdateSubresource(dataBox, _landmarkBuffer.Buffer, 0);
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

        _faceLandmarker?.Close();
        _landmarkBuffer?.Dispose();
        
        base.Dispose(isDisposing);
    }
    #endregion

    #region Input Parameters
    [Input(Guid = "A7B8C9D0-E1F2-4D23-E05B-345678901234")]
    public readonly InputSlot<Texture2D> InputTexture = new();

    [Input(Guid = "B8C9D0E1-F2A3-4E34-F16C-456789012345")]
    public readonly InputSlot<bool> EnableDetection = new(true);

    [Input(Guid = "C9D0E1F2-A3B4-4F45-A27D-567890123456")]
    public readonly InputSlot<float> ConfidenceThreshold = new(0.5f);

    [Input(Guid = "D0E1F2A3-B4C5-4056-B38E-678901234567")]
    public readonly InputSlot<int> MaxFaces = new(5);

    [Input(Guid = "E1F2A3B4-C5D6-4167-C49F-789012345678")]
    public readonly InputSlot<bool> ShowLandmarks = new(true);

    [Input(Guid = "F2A3B4C5-D6E7-4278-D5A0-890123456789")]
    public readonly InputSlot<bool> ShowConnections = new(true);

    [Input(Guid = "A3B4C5D6-E7F8-4389-E6B1-901234567890")]
    public readonly InputSlot<float> LandmarkSize = new(3.0f);

    [Input(Guid = "B4C5D6E7-F8A9-4490-F7C2-012345678901")]
    public readonly InputSlot<Vector4> LandmarkColor = new(Vector4.One);

    [Input(Guid = "C5D6E7F8-A9B0-45A1-A8D3-123456789012")]
    public readonly InputSlot<Vector4> ConnectionColor = new(Vector4.One);

    [Input(Guid = "D6E7F8A9-B0C1-46B2-B9E4-234567890123")]
    public readonly InputSlot<bool> ShowMesh = new(false);
    #endregion
}
