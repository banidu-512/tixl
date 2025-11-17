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
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Framework.Formats;
using Landmark = Mediapipe.Landmark;

namespace Lib.io.video.mediapipe;

[Guid("A1B2C3D4-E5F6-4798-89AB-CDEF12345683")]
public class GestureRecognition : Instance<GestureRecognition>
{
    [Output(Guid = "B2C3D4E5-F6A7-489A-9B0C-DEF123456784", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<Texture2D> OutputTexture = new();

    [Output(Guid = "C3D4E5F6-A7B8-49AB-AC1D-EF1234567895", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<string> RecognizedGesture = new();

    [Output(Guid = "D4E5F6A7-B8C9-4AB0-BD2E-F12345678906", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<float> Confidence = new();

    [Output(Guid = "E5F6A7B8-C9D0-4B01-CE3F-1234A5678907", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> HandCount = new();

    [Output(Guid = "F6A7B8C9-D0E1-4C12-DF4A-234567A89008", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> UpdateCount = new();

    public GestureRecognition()
    {
        OutputTexture.UpdateAction = Update;
        RecognizedGesture.UpdateAction = Update;
        Confidence.UpdateAction = Update;
        HandCount.UpdateAction = Update;
        UpdateCount.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var inputTexture = InputTexture.GetValue(context);
        var enabled = Enabled.GetValue(context);
        var minHandDetectionConfidence = MinHandDetectionConfidence.GetValue(context);
        var minHandPresenceConfidence = MinHandPresenceConfidence.GetValue(context);
        var maxHands = MaxHands.GetValue(context);

        // Lazy initialization: Initialize on first use or if it becomes null
        if (_handLandmarker == null)
        {
            InitializeMediaPipe();
        }

        // Reset outputs if detection is disabled
        if (!enabled || inputTexture == null)
        {
            OutputTexture.Value = inputTexture;
            RecognizedGesture.Value = "None";
            Confidence.Value = 0.0f;
            HandCount.Value = 0;
            _landmarksArray = null;
            return;
        }

        // Process hand detection
        if (ProcessTextureForHandDetection(inputTexture, minHandDetectionConfidence, minHandPresenceConfidence, maxHands))
        {
            RecognizeGestureAndUpdateOutputs();
            UpdateCount.Value++;
        }
        else
        {
            // Fall back to input texture if detection fails
            OutputTexture.Value = inputTexture;
            RecognizedGesture.Value = "None";
            Confidence.Value = 0.0f;
            Log.Debug("Hand detection failed, cannot recognize gesture.", this);
        }
    }

    #region MediaPipe Integration
    // DIAGNOSTIC: Replace low-level API with high-level task API
    private HandLandmarker? _handLandmarker;
    private Point[]? _landmarksArray;
    private long _frameTimestamp; // DIAGNOSTIC: Add missing timestamp field

    private void InitializeMediaPipe()
    {
        try
        {
            // DIAGNOSTIC: Use high-level HandLandmarker API instead of low-level CalculatorGraph
            Log.Debug("[GestureRecognition] Starting HandLandmarker initialization...", this);
            
            // DIAGNOSTIC: Add detailed logging for troubleshooting
            Log.Debug($"[GestureRecognition] Current working directory: {System.IO.Directory.GetCurrentDirectory()}", this);
            Log.Debug($"[GestureRecognition] Application base directory: {AppDomain.CurrentDomain.BaseDirectory}", this);
            
            // Check if model file exists - FIXED: Use absolute path resolution
            string modelPath = "../../Mediapipe-Sharp/src/Mediapipe/Models/hand_landmarker.task";
            string fullPath = System.IO.Path.GetFullPath(modelPath);
            
            Log.Debug($"[GestureRecognition] Checking model path: {modelPath}", this);
            Log.Debug($"[GestureRecognition] Full resolved path: {fullPath}", this);
            
            // ENHANCED: Check multiple possible model paths with better error handling
            string[] possibleModelPaths = {
                fullPath,
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "hand_landmarker.task"),
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Models", "hand_landmarker.task"),
                "../../Mediapipe-Sharp/src/Mediapipe/Models/hand_landmarker.task",
                "../../../Mediapipe-Sharp/src/Mediapipe/Models/hand_landmarker.task"
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
                Log.Error($"[GestureRecognition] Model file not found at any of the checked paths", this);
                foreach (string path in possibleModelPaths)
                {
                    string testPath = System.IO.Path.GetFullPath(path);
                    bool exists = System.IO.File.Exists(path);
                    Log.Debug($"[GestureRecognition] Path check: {path} -> {testPath} (Exists: {exists})", this);
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
                Log.Error("[GestureRecognition] CRITICAL: MediaPipe native library not found!", this);
                foreach (string path in possibleDllPaths)
                {
                    string testPath = System.IO.Path.GetFullPath(path);
                    bool exists = System.IO.File.Exists(path);
                    Log.Debug($"[GestureRecognition] DLL Path check: {path} -> {testPath} (Exists: {exists})", this);
                }
                return;
            }
            
            Log.Debug($"[GestureRecognition] Model file found at: {fullPath}", this);
            
            // DIAGNOSTIC: Check file size and accessibility
            var fileInfo = new System.IO.FileInfo(fullPath);
            Log.Debug($"[GestureRecognition] Model file size: {fileInfo.Length} bytes", this);
            Log.Debug($"[GestureRecognition] Model file accessible: {fileInfo.Exists}", this);
            
            Log.Debug($"[GestureRecognition] Native DLL found at: {nativeDllPath}", this);
            
            // DIAGNOSTIC: Log MediaPipe library loading status
            Log.Debug("[GestureRecognition] Creating CoreBaseOptions...", this);
            
            // Initialize HandLandmarker with video mode for real-time processing
            var baseOptions = new Mediapipe.Tasks.Core.CoreBaseOptions(
                modelAssetPath: fullPath,  // FIXED: Use resolved absolute path
                delegateCase: Mediapipe.Tasks.Core.CoreBaseOptions.Delegate.CPU
            );

            Log.Debug("[GestureRecognition] Creating HandLandmarkerOptions...", this);
            // FIXED: Simplified configuration matching HandMeshApp approach
            // Let MediaPipe handle confidence filtering internally
            HandLandmarkerOptions options = new(
                baseOptions,
                VisionRunningMode.VIDEO,
                2  // numHands
            );

            Log.Debug("[GestureRecognition] Calling HandLandmarker.CreateFromOptions...", this);
            Log.Debug($"[GestureRecognition] Options - BaseOptions model path: {baseOptions.ModelAssetPath}", this);
            Log.Debug($"[GestureRecognition] Options - Running mode: {options.RunningMode}", this);
            Log.Debug($"[GestureRecognition] Options - Min hand detection confidence: {options.MinHandDetectionConfidence}", this);
            Log.Debug($"[GestureRecognition] Options - Min hand presence confidence: {options.MinHandPresenceConfidence}", this);
            Log.Debug($"[GestureRecognition] Options - Min tracking confidence: {options.MinTrackingConfidence}", this);
            Log.Debug($"[GestureRecognition] Options - Num hands: {options.NumHands}", this);
            
            // DIAGNOSTIC: Add detailed exception handling with inner exceptions
            try
            {
                Log.Debug("[GestureRecognition] About to call HandLandmarker.CreateFromOptions...", this);
                _handLandmarker = HandLandmarker.CreateFromOptions(options);
                Log.Debug($"[GestureRecognition] HandLandmarker.CreateFromOptions returned: {_handLandmarker != null}", this);
                
                if (_handLandmarker != null)
                {
                    Log.Debug("[GestureRecognition] HandLandmarker object created successfully", this);
                    Log.Debug($"[GestureRecognition] HandLandmarker type: {_handLandmarker.GetType().FullName}", this);
                }
                else
                {
                    Log.Error("[GestureRecognition] HandLandmarker.CreateFromOptions returned null without exception", this);
                }
            }
            catch (System.IO.FileNotFoundException fnfEx)
            {
                Log.Error($"[GestureRecognition] File not found during HandLandmarker creation: {fnfEx.Message}", this);
                Log.Error($"[GestureRecognition] File not found details: {fnfEx.FileName}", this);
                _handLandmarker = null;
            }
            catch (System.DllNotFoundException dllEx)
            {
                Log.Error($"[GestureRecognition] Native DLL not found during HandLandmarker creation: {dllEx.Message}", this);
                Log.Error($"[GestureRecognition] Missing DLL: {dllEx.Message}", this);
                _handLandmarker = null;
            }
            catch (System.BadImageFormatException imgEx)
            {
                Log.Error($"[GestureRecognition] Invalid DLL format during HandLandmarker creation: {imgEx.Message}", this);
                _handLandmarker = null;
            }
            catch (Exception createEx)
            {
                Log.Error($"[GestureRecognition] Exception during HandLandmarker.CreateFromOptions: {createEx.Message}", this);
                Log.Error($"[GestureRecognition] Exception type: {createEx.GetType().Name}", this);
                Log.Error($"[GestureRecognition] Exception stack trace: {createEx.StackTrace}", this);
                _handLandmarker = null;
            }
            
            if (_handLandmarker != null)
            {
                Log.Debug("[GestureRecognition] HandLandmarker initialization successful!", this);
            }
            else
            {
                Log.Error("[GestureRecognition] ERROR: HandLandmarker is null after creation", this);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[GestureRecognition] Failed to initialize HandLandmarker: {ex.Message}", this);
            Log.Error($"[GestureRecognition] Outer exception type: {ex.GetType().Name}", this);
        }
    }

    private bool ProcessTextureForHandDetection(Texture2D texture, float minHandDetectionConfidence, float minHandPresenceConfidence, int maxHands)
    {
        if (texture == null) return false;

        try
        {
            // DIAGNOSTIC: Use HandLandmarker API instead of low-level graph
            Log.Debug("[GestureRecognition] Starting hand detection process...", this);
            
            // Convert Tixl Texture2D to OpenCV Mat (minimal usage - just for conversion)
            using var mat = Texture2DToMat(texture);
            if (mat.Empty()) 
            {
                Log.Debug("[GestureRecognition] ERROR: Converted Mat is empty", this);
                return false;
            }

            Log.Debug($"[GestureRecognition] Converted texture to Mat: {mat.Width}x{mat.Height}", this);

            // Convert Mat to MediaPipe Image
            var image = MatToMediaPipeImage(mat);
            if (image == null) 
            {
                Log.Debug("[GestureRecognition] ERROR: Failed to convert Mat to MediaPipe Image", this);
                return false;
            }

            Log.Debug($"[GestureRecognition] Converted to MediaPipe Image: {image.Width()}x{image.Height()}", this);

            // Process through HandLandmarker
            if (_handLandmarker == null)
            {
                Log.Debug("[GestureRecognition] ERROR: HandLandmarker is null", this);
                return false;
            }

            // Get current timestamp for video processing
            _frameTimestamp += 33; // Approximate 30 FPS (33ms per frame)

            Log.Debug($"[GestureRecognition] Detecting hands for timestamp {_frameTimestamp}...", this);
            HandLandmarkerResult result = _handLandmarker.DetectForVideo(image, _frameTimestamp);

            if (result.HandLandmarks == null || result.HandLandmarks.Count == 0)
            {
                Log.Debug("[GestureRecognition] WARNING: No hand landmarks detected", this);
                return false;
            }

            Log.Debug($"[GestureRecognition] Detected {result.HandLandmarks.Count} hand(s)", this);

            // FIXED: Add detailed landmark diagnostics similar to HandMeshApp
            if (result.HandLandmarks != null && result.HandLandmarks.Count > 0)
            {
                Log.Debug($"[GestureRecognition] Processing {result.HandLandmarks.Count} hand landmark sets", this);
                for (int handIndex = 0; handIndex < result.HandLandmarks.Count; handIndex++)
                {
                    NormalizedLandmarks handLandmarks = result.HandLandmarks[handIndex];
                    Log.Debug($"[GestureRecognition] Hand {handIndex}: {handLandmarks.landmarks.Count} landmarks", this);
                    
                    // Check first few landmarks for visibility and coordinate values
                    int sampleCount = Math.Min(5, handLandmarks.landmarks.Count);
                    for (int i = 0; i < sampleCount; i++)
                    {
                        NormalizedLandmark landmark = handLandmarks.landmarks[i];
                        Log.Debug($"[GestureRecognition]   Landmark {i}: X={landmark.X:F3}, Y={landmark.Y:F3}, Z={landmark.Z:F3}, Visibility={landmark.Visibility}, Presence={landmark.Presence}", this);
                    }
                    
                    // Count visible landmarks for debugging (similar to HandMeshApp)
                    int visibleCount = 0;
                    foreach (var landmark in handLandmarks.landmarks)
                        if (landmark.Visibility.HasValue && landmark.Visibility.Value > 0.1f)
                            visibleCount++;
                    Log.Debug($"[GestureRecognition]   Visible landmarks (vis > 0.1): {visibleCount}/{handLandmarks.landmarks.Count}", this);
                }
            }
            else
            {
                Log.Debug("[GestureRecognition] WARNING: HandLandmarks is null or empty", this);
            }

            // Convert MediaPipe results to our landmark format
            _landmarksArray = ConvertHandLandmarkerResultToLandmarks(result, minHandDetectionConfidence, minHandPresenceConfidence, maxHands);
            return _landmarksArray != null;
        }
        catch (Exception ex)
        {
            Log.Error($"[GestureRecognition] Error in hand detection: {ex.Message}", this);
            return false;
        }
    }

    private Point[]? ConvertHandLandmarkerResultToLandmarks(HandLandmarkerResult result, float minHandDetectionConfidence, float minHandPresenceConfidence, int maxHands)
    {
        if (result.HandLandmarks == null) return null;

        var landmarks = new List<Point>();

        try
        {
            Log.Debug($"[ConvertHandLandmarkerResultToLandmarks] Processing {result.HandLandmarks.Count} hand landmark sets", this);
            
            for (int handIndex = 0; handIndex < result.HandLandmarks.Count && handIndex < maxHands; handIndex++)
            {
                var handLandmarks = result.HandLandmarks[handIndex];

                if (handLandmarks.landmarks != null && handLandmarks.landmarks.Count > 0)
                {
                    Log.Debug($"[ConvertHandLandmarkerResultToLandmarks] Processing hand {handIndex} with {handLandmarks.landmarks.Count} landmarks", this);

                    int sampleCount = Math.Min(5, handLandmarks.landmarks.Count);
                    for (int i = 0; i < sampleCount; i++)
                    {
                        var landmark = handLandmarks.landmarks[i];
                        Log.Debug($"[LANDMARK_DEBUG] Hand {handIndex} - Landmark {i}: X={landmark.X:F3}, Y={landmark.Y:F3}, Z={landmark.Z:F3}, Visibility={landmark.Visibility}, Presence={landmark.Presence}", this);
                    }
                    
                    int visibleCount = 0;
                    foreach (var landmark in handLandmarks.landmarks)
                        if (landmark.Visibility.HasValue && landmark.Visibility.Value > 0.1f)
                            visibleCount++;
                    Log.Debug($"[LANDMARK_DEBUG] Hand {handIndex} - Visible landmarks (vis > 0.1): {visibleCount}/{handLandmarks.landmarks.Count}", this);

                    for (int i = 0; i < handLandmarks.landmarks.Count && i < 21; i++)
                    {
                        var landmark = handLandmarks.landmarks[i];
                        
                        var normalizedX = landmark.X;
                        var normalizedY = landmark.Y;
                        
                        landmarks.Add(new Point
                        {
                            Position = new Vector3(normalizedX, normalizedY, landmark.Z),
                            F1 = i,
                            F2 = landmark.Visibility ?? 0f,
                            Color = Vector4.One,
                            Scale = Vector3.One,
                            Orientation = Quaternion.Identity
                        });
                    }
                }
            }

            Log.Debug($"[ConvertHandLandmarkerResultToLandmarks] Converted {landmarks.Count} landmarks total", this);
            return landmarks.Count > 0 ? landmarks.ToArray() : null;
        }
        catch (Exception ex)
        {
            Log.Error($"[ConvertHandLandmarkerResultToLandmarks] Error converting MediaPipe landmarks: {ex.Message}", this);
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

    #region Gesture Recognition Logic
    private void RecognizeGestureAndUpdateOutputs()
    {
        // Set output texture (input texture with optional overlay)
        OutputTexture.Value = InputTexture.Value; // Tixl will handle visualization

        if (_landmarksArray == null || _landmarksArray.Length == 0)
        {
            RecognizedGesture.Value = "None";
            Confidence.Value = 0.0f;
            HandCount.Value = 0;
            return;
        }

        // For now, we'll implement a simple gesture recognition based on finger positions
        // This can be extended with more complex logic or machine learning models
        var gesture = RecognizeBasicGesture(_landmarksArray);
        RecognizedGesture.Value = gesture.Name;
        Confidence.Value = gesture.Confidence;
        HandCount.Value = _landmarksArray.Length / 21; // 21 landmarks per hand
    }

    private (string Name, float Confidence) RecognizeBasicGesture(Point[] landmarks)
    {
        if (landmarks.Length < 21) return ("None", 0.0f);

        // Simple gesture recognition based on finger states
        // This is a placeholder implementation and should be replaced with a more robust solution
        
        // Get key landmarks for finger tips and bases
        var thumbTip = landmarks[4];
        var indexTip = landmarks[8];
        var middleTip = landmarks[12];
        var ringTip = landmarks[16];
        var pinkyTip = landmarks[20];
        
        var indexBase = landmarks[5];
        var middleBase = landmarks[9];
        var ringBase = landmarks[13];
        var pinkyBase = landmarks[17];

        var wrist = landmarks[0];

        // Check if fingers are extended (tip is higher than base)
        bool thumbIsUp = thumbTip.Position.Y < indexBase.Position.Y;
        bool indexIsUp = indexTip.Position.Y < indexBase.Position.Y;
        bool middleIsUp = middleTip.Position.Y < middleBase.Position.Y;
        bool ringIsUp = ringTip.Position.Y < ringBase.Position.Y;
        bool pinkyIsUp = pinkyTip.Position.Y < pinkyBase.Position.Y;

        // Simple gesture recognition
        if (!indexIsUp && !middleIsUp && !ringIsUp && !pinkyIsUp && thumbIsUp)
        {
            return ("Thumbs Up", 0.9f);
        }
        else if (indexIsUp && !middleIsUp && !ringIsUp && !pinkyIsUp)
        {
            return ("Point", 0.8f);
        }
        else if (indexIsUp && middleIsUp && !ringIsUp && !pinkyIsUp)
        {
            return ("Peace", 0.8f);
        }
        else if (indexIsUp && middleIsUp && ringIsUp && pinkyIsUp)
        {
            return ("Open Palm", 0.7f);
        }
        else if (!indexIsUp && !middleIsUp && !ringIsUp && !pinkyIsUp && !thumbIsUp)
        {
            return ("Fist", 0.8f);
        }
        
        return ("None", 0.1f);
    }
    #endregion

    #region Cleanup
    protected override void Dispose(bool isDisposing)
    {
        if (!isDisposing) return;

        _handLandmarker?.Close();
        
        base.Dispose(isDisposing);
    }
    #endregion

    #region Input Parameters
    [Input(Guid = "A7B8C9D0-E1F2-4D23-E05B-345678901240")]
    public readonly InputSlot<Texture2D> InputTexture = new();

    [Input(Guid = "B8C9D0E1-F2A3-4E34-F16C-456789012341")]
    public readonly InputSlot<bool> Enabled = new(true);

    [Input(Guid = "C9D0E1F2-A3B4-4F45-A27D-567890123452")]
    public readonly InputSlot<int> MaxHands = new(2);

    [Input(Guid = "D0E1F2A3-B4C5-4056-B38E-678901234562")]
    public readonly InputSlot<float> MinHandDetectionConfidence = new(0.5f);

    [Input(Guid = "E1F2A3B4-C5D6-4167-C49F-789012345673")]
    public readonly InputSlot<float> MinHandPresenceConfidence = new(0.5f);
    #endregion
}
