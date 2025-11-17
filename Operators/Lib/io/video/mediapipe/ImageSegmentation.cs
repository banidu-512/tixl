using OpenCvSharp;
using SharpDX;
using SharpDX.Direct3D11;
using System;
using System.IO;
using System.Runtime.InteropServices;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Resource;
using Mediapipe.Tasks.Vision.ImageSegmenter;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Framework.Formats;
using Image = Mediapipe.Framework.Formats.Image;

#nullable enable

namespace Lib.io.video.mediapipe
{
    [Guid("A1B2C3D4-E5F6-4798-89AB-CDEF12345684")]
    public class ImageSegmentation : Instance<ImageSegmentation>
    {
        #region Outputs
        [Output(Guid = "B2C3D4E5-F6A7-489A-9B0C-DEF123456785", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<Texture2D?> OutputTexture = new();

        [Output(Guid = "C3D4E5F6-A7B8-49AB-AC1D-EF1234567896", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<Texture2D?> CategoryMask = new();

        [Output(Guid = "D4E5F6A7-B8C9-4AB0-BD2E-F12345678907", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<Texture2D?> ConfidenceMask = new();

        [Output(Guid = "E5F6A7B8-C9D0-4B01-CE3F-1234A5678908", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<Texture2D?> ColoredMask = new();

        [Output(Guid = "F6A7B8C9-D0E1-4C12-DF4A-234567A89009", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
        public readonly Slot<int> UpdateCount = new();
        #endregion

        #region Enums
        public enum SegmentationOutputMode
        {
            Overlay,
            CategoryMask,
            ConfidenceMask,
            ColoredMask
        }
        #endregion

        public ImageSegmentation()
        {
            OutputTexture.UpdateAction = Update;
            CategoryMask.UpdateAction = Update;
            ConfidenceMask.UpdateAction = Update;
            ColoredMask.UpdateAction = Update;
            UpdateCount.UpdateAction = Update;
        }

        #region Main Update Method
        private void Update(EvaluationContext context)
        {
            var inputTexture = InputTexture.GetValue(context);
            var enabled = Enabled.GetValue(context);
            var outputMode = (SegmentationOutputMode)OutputMode.GetValue(context);
            var confidenceThreshold = ConfidenceThreshold.GetValue(context);

            if (_imageSegmenter == null)
            {
                InitializeMediaPipe();
            }

            if (!enabled || inputTexture == null || _imageSegmenter == null)
            {
                OutputTexture.Value = inputTexture;
                CategoryMask.Value = null;
                ConfidenceMask.Value = null;
                ColoredMask.Value = null;
                _lastSegmentationResult = null;
                return;
            }

            if (ProcessTextureForSegmentation(inputTexture))
            {
                GenerateOutputTextures(inputTexture, outputMode, confidenceThreshold);
                UpdateCount.Value++;
            }
            else
            {
                OutputTexture.Value = inputTexture;
                CategoryMask.Value = null;
                ConfidenceMask.Value = null;
                ColoredMask.Value = null;
                Log.Debug("Image segmentation failed, cannot generate output.", this);
            }
        }
        #endregion

        #region MediaPipe Integration
        private ImageSegmenter? _imageSegmenter;
        private ImageSegmenterResult? _lastSegmentationResult;
        private nint _frameTimestamp;

        private void InitializeMediaPipe()
        {
            try
            {
                Log.Debug("[ImageSegmentation] Starting ImageSegmenter initialization...", this);
                Log.Debug($"[ImageSegmentation] Current working directory: {Directory.GetCurrentDirectory()}", this);
                Log.Debug($"[ImageSegmentation] Application base directory: {AppDomain.CurrentDomain.BaseDirectory}", this);
                
                string modelPath = "../../Mediapipe-Sharp/src/ImageSegmentationApp/Models/selfie_segmenter.tflite";
                string fullPath = Path.GetFullPath(modelPath);
                
                string[] possibleModelPaths = {
                    fullPath,
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "selfie_segmenter.tflite"),
                    Path.Combine(Directory.GetCurrentDirectory(), "Models", "selfie_segmenter.tflite"),
                    "../../Mediapipe-Sharp/src/ImageSegmentationApp/Models/selfie_segmenter.tflite",
                    "../../../Mediapipe-Sharp/src/ImageSegmentationApp/Models/selfie_segmenter.tflite"
                };
                
                bool modelFound = false;
                foreach (string path in possibleModelPaths)
                {
                    string testPath = Path.GetFullPath(path);
                    bool exists = File.Exists(path);
                    Log.Debug($"[ImageSegmentation] Path check: {path} -> {testPath} (Exists: {exists})", this);
                    if (exists)
                    {
                        fullPath = testPath;
                        modelFound = true;
                        break;
                    }
                }
                
                if (!modelFound)
                {
                    Log.Error($"[ImageSegmentation] Model file not found at any of the checked paths", this);
                    return;
                }
                
                // Check if native library exists (similar to GestureRecognition)
                string[] possibleDllPaths = {
                    "../../Mediapipe-Sharp/src/Mediapipe/Libs/mediapipe_c.dll",
                    "../../../Mediapipe-Sharp/src/Mediapipe/Libs/mediapipe_c.dll",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Libs", "mediapipe_c.dll")
                };
                
                bool dllFound = false;
                string nativeDllPath = string.Empty;
                foreach (string path in possibleDllPaths)
                {
                    string testPath = Path.GetFullPath(path);
                    bool exists = File.Exists(path);
                    Log.Debug($"[ImageSegmentation] DLL Path check: {path} -> {testPath} (Exists: {exists})", this);
                    if (exists)
                    {
                        nativeDllPath = testPath;
                        dllFound = true;
                        break;
                    }
                }
                
                if (!dllFound)
                {
                    Log.Error("[ImageSegmentation] CRITICAL: MediaPipe native library not found!", this);
                    return;
                }
                
                Log.Debug($"[ImageSegmentation] Model file found at: {fullPath}", this);
                var fileInfo = new FileInfo(fullPath);
                Log.Debug($"[ImageSegmentation] Model file size: {fileInfo.Length} bytes", this);
                Log.Debug($"[ImageSegmentation] Native DLL found at: {nativeDllPath}", this);
                
                var baseOptions = new Mediapipe.Tasks.Core.CoreBaseOptions(
                    modelAssetPath: fullPath,
                    delegateCase: Mediapipe.Tasks.Core.CoreBaseOptions.Delegate.CPU
                );

                ImageSegmenterOptions options = new(
                    baseOptions,
                    VisionRunningMode.VIDEO,
                    outputCategoryMask: true,
                    outputConfidenceMasks: true
                );
                
                _imageSegmenter = ImageSegmenter.CreateFromOptions(options);
            }
            catch (Exception ex)
            {
                Log.Error($"[ImageSegmentation] Failed to initialize ImageSegmenter: {ex.Message}", this);
                _imageSegmenter = null;
            }
        }

        private bool ProcessTextureForSegmentation(Texture2D texture)
        {
            try
            {
                Log.Debug("[ImageSegmentation] Starting texture processing for segmentation...", this);
                
                using var mat = Texture2DToMat(texture);
                if (mat.Empty())
                {
                    Log.Debug("[ImageSegmentation] ERROR: Converted Mat is empty", this);
                    return false;
                }

                Log.Debug($"[ImageSegmentation] Converted texture to Mat: {mat.Width}x{mat.Height}, type: {mat.Type()}", this);

                var image = MatToMediaPipeImage(mat);
                if (image == null)
                {
                    Log.Debug("[ImageSegmentation] ERROR: Failed to convert Mat to MediaPipe Image", this);
                    return false;
                }

                Log.Debug($"[ImageSegmentation] Converted to MediaPipe Image: {image.Width()}x{image.Height()}", this);

                if (_imageSegmenter == null)
                {
                    Log.Debug("[ImageSegmentation] ERROR: ImageSegmenter is null", this);
                    return false;
                }

                _frameTimestamp += 33;
                Log.Debug($"[ImageSegmentation] Segmenting for timestamp {_frameTimestamp}...", this);
                _lastSegmentationResult = _imageSegmenter.SegmentForVideo(image, _frameTimestamp);

                // FIXED: Correct result validation logic
                bool resultIsValid = _lastSegmentationResult != null;
                Log.Debug($"[ImageSegmentation] Segmentation result valid: {resultIsValid}", this);
                
                if (resultIsValid)
                {
                    Log.Debug($"[ImageSegmentation] CategoryMask available: {_lastSegmentationResult.Value.CategoryMask != null}", this);
                    if (_lastSegmentationResult.Value.ConfidenceMasks != null)
                    {
                        Log.Debug($"[ImageSegmentation] ConfidenceMasks count: {_lastSegmentationResult.Value.ConfidenceMasks.Count}", this);
                    }
                }
                else
                {
                    Log.Debug("[ImageSegmentation] ERROR: SegmentForVideo returned null result", this);
                    Log.Debug($"[ImageSegmentation] ImageSegmenter is null: {_imageSegmenter == null}", this);
                    Log.Debug($"[ImageSegmentation] Image is null: {image == null}", this);
                    if (image != null)
                    {
                        Log.Debug($"[ImageSegmentation] Image width: {image.Width()}, height: {image.Height()}, format: {image.Format()}", this);
                    }
                }

                return resultIsValid;
            }
            catch (Exception ex)
            {
                Log.Error($"[ImageSegmentation] Error in image segmentation: {ex.Message}", this);
                Log.Error($"[ImageSegmentation] Exception type: {ex.GetType().Name}", this);
                Log.Error($"[ImageSegmentation] Stack trace: {ex.StackTrace}", this);
                return false;
            }
        }
        #endregion

        #region Output Texture Generation
        private void GenerateOutputTextures(Texture2D inputTexture, SegmentationOutputMode outputMode, float confidenceThreshold)
        {
            if (_lastSegmentationResult == null)
            {
                OutputTexture.Value = inputTexture;
                CategoryMask.Value = null;
                ConfidenceMask.Value = null;
                ColoredMask.Value = null;
                return;
            }

            try
            {
                using var inputMat = Texture2DToMat(inputTexture);
                if (inputMat.Empty()) return;

                switch (outputMode)
                {
                    case SegmentationOutputMode.Overlay:
                        OutputTexture.Value = CreateOverlayTexture(inputMat, _lastSegmentationResult.Value, confidenceThreshold);
                        break;
                    case SegmentationOutputMode.CategoryMask:
                        OutputTexture.Value = CreateCategoryMaskTexture(inputMat, _lastSegmentationResult.Value);
                        break;
                    case SegmentationOutputMode.ConfidenceMask:
                        OutputTexture.Value = CreateConfidenceMaskTexture(inputMat, _lastSegmentationResult.Value, confidenceThreshold);
                        break;
                    case SegmentationOutputMode.ColoredMask:
                        OutputTexture.Value = CreateColoredMaskTexture(inputMat, _lastSegmentationResult.Value, confidenceThreshold);
                        break;
                }

                CategoryMask.Value = CreateCategoryMaskTexture(inputMat, _lastSegmentationResult.Value);
                ConfidenceMask.Value = CreateConfidenceMaskTexture(inputMat, _lastSegmentationResult.Value, confidenceThreshold);
                ColoredMask.Value = CreateColoredMaskTexture(inputMat, _lastSegmentationResult.Value, confidenceThreshold);
            }
            catch (Exception ex)
            {
                Log.Error($"[ImageSegmentation] Error generating output textures: {ex.Message}", this);
                OutputTexture.Value = inputTexture;
            }
        }

        private Texture2D? CreateOverlayTexture(Mat inputMat, ImageSegmenterResult result, float confidenceThreshold)
        {
            try
            {
                var overlayMat = inputMat.Clone();

                if (result.CategoryMask != null)
                {
                    using var categoryMaskMat = ConvertImageToMat(result.CategoryMask);
                    if (categoryMaskMat != null)
                    {
                        using var resizedMask = new Mat();
                        Cv2.Resize(categoryMaskMat, resizedMask, overlayMat.Size(),0, 0, InterpolationFlags.Nearest);

                        // Ensure mask is same format as overlayMat for SetTo operation
                        using var maskForSetTo = new Mat();
                        if (resizedMask.Channels() == 1)
                            Cv2.CvtColor(resizedMask, maskForSetTo, ColorConversionCodes.GRAY2BGR);
                        else
                            resizedMask.CopyTo(maskForSetTo);

                        using var coloredMask = new Mat(overlayMat.Size(), MatType.CV_8UC3, new Scalar(0, 255, 0, 128));
                        coloredMask.SetTo(new Scalar(0, 255, 0, 128), maskForSetTo);

                        Cv2.AddWeighted(overlayMat, 1.0 - 0.6, coloredMask, 0.6, 0, overlayMat);
                    }
                }
                else if (result.ConfidenceMasks != null && result.ConfidenceMasks.Count > 0)
                {
                    using var confidenceMaskMat = ConvertImageToMat(result.ConfidenceMasks[0]);
                    if (confidenceMaskMat != null)
                    {
                        using var resizedMask = new Mat();
                        Cv2.Resize(confidenceMaskMat, resizedMask, overlayMat.Size());

                        using var binaryMask = new Mat();
                        Cv2.Threshold(resizedMask, binaryMask, confidenceThreshold, 255, ThresholdTypes.Binary);

                        // Ensure mask is same format as overlayMat for SetTo operation
                        using var maskForSetTo = new Mat();
                        if (binaryMask.Channels() == 1)
                            Cv2.CvtColor(binaryMask, maskForSetTo, ColorConversionCodes.GRAY2BGR);
                        else
                            binaryMask.CopyTo(maskForSetTo);

                        using var coloredMask = new Mat(overlayMat.Size(), MatType.CV_8UC3, new Scalar(0, 255, 0, 128));
                        coloredMask.SetTo(new Scalar(0, 255, 0, 128), maskForSetTo);

                        Cv2.AddWeighted(overlayMat, 1.0 - 0.6, coloredMask, 0.6, 0, overlayMat);
                    }
                }

                return MatToTexture2D(overlayMat);
            }
            catch (Exception ex)
            {
                Log.Error($"[ImageSegmentation] Error creating overlay texture: {ex.Message}", this);
                return MatToTexture2D(inputMat);
            }
        }

        private Texture2D? CreateCategoryMaskTexture(Mat inputMat, ImageSegmenterResult result)
        {
            try
            {
                if (result.CategoryMask == null)
                {
                    var blackMat = new Mat(inputMat.Size(), MatType.CV_8UC3, new Scalar(0, 0, 0));
                    return MatToTexture2D(blackMat);
                }

                using var categoryMaskMat = ConvertImageToMat(result.CategoryMask);
                if (categoryMaskMat == null)
                {
                    return MatToTexture2D(new Mat(inputMat.Size(), MatType.CV_8UC3, new Scalar(0, 0, 0)));
                }

                using var resizedMask = new Mat();
                Cv2.Resize(categoryMaskMat, resizedMask, inputMat.Size(), 0, 0, InterpolationFlags.Nearest);

                var binaryMat = new Mat(inputMat.Size(), MatType.CV_8UC3, new Scalar(0, 0, 0));
                binaryMat.SetTo(new Scalar(255, 255, 255), resizedMask);

                return MatToTexture2D(binaryMat);
            }
            catch (Exception ex)
            {
                Log.Error($"[ImageSegmentation] Error creating category mask texture: {ex.Message}", this);
                return MatToTexture2D(new Mat(inputMat.Size(), MatType.CV_8UC3, new Scalar(0, 0, 0)));
            }
        }

        private Texture2D? CreateConfidenceMaskTexture(Mat inputMat, ImageSegmenterResult result, float confidenceThreshold)
        {
            try
            {
                if (result.ConfidenceMasks == null || result.ConfidenceMasks.Count == 0)
                {
                    var blackMat = new Mat(inputMat.Size(), MatType.CV_8UC3, new Scalar(0, 0, 0));
                    return MatToTexture2D(blackMat);
                }

                using var confidenceMaskMat = ConvertImageToMat(result.ConfidenceMasks[0]);
                if (confidenceMaskMat == null)
                {
                    return MatToTexture2D(new Mat(inputMat.Size(), MatType.CV_8UC3, new Scalar(0, 0, 0)));
                }

                using var resizedMask = new Mat();
                Cv2.Resize(confidenceMaskMat, resizedMask, inputMat.Size());

                using var binaryMask = new Mat();
                Cv2.Threshold(resizedMask, binaryMask, confidenceThreshold, 255, ThresholdTypes.Binary);

                var grayMat = new Mat(inputMat.Size(), MatType.CV_8UC3, new Scalar(0, 0, 0));
                grayMat.SetTo(new Scalar(255, 255, 255), binaryMask);

                return MatToTexture2D(grayMat);
            }
            catch (Exception ex)
            {
                Log.Error($"[ImageSegmentation] Error creating confidence mask texture: {ex.Message}", this);
                return MatToTexture2D(new Mat(inputMat.Size(), MatType.CV_8UC3, new Scalar(0, 0, 0)));
            }
        }

        private Texture2D? CreateColoredMaskTexture(Mat inputMat, ImageSegmenterResult result, float confidenceThreshold)
        {
            try
            {
                var coloredMat = new Mat(inputMat.Size(), MatType.CV_8UC3, new Scalar(0, 0, 0));

                if (result.CategoryMask != null)
                {
                    using var categoryMaskMat = ConvertImageToMat(result.CategoryMask);
                    if (categoryMaskMat != null)
                    {
                        using var resizedMask = new Mat();
                        Cv2.Resize(categoryMaskMat, resizedMask, inputMat.Size(), 0, 0, InterpolationFlags.Nearest);
                        coloredMat.SetTo(new Scalar(0, 255, 0), resizedMask);
                    }
                }
                else if (result.ConfidenceMasks != null && result.ConfidenceMasks.Count > 0)
                {
                    using var confidenceMaskMat = ConvertImageToMat(result.ConfidenceMasks[0]);
                    if (confidenceMaskMat != null)
                    {
                        using var resizedMask = new Mat();
                        Cv2.Resize(confidenceMaskMat, resizedMask, inputMat.Size());

                        using var binaryMask = new Mat();
                        Cv2.Threshold(resizedMask, binaryMask, confidenceThreshold, 255, ThresholdTypes.Binary);

                        coloredMat.SetTo(new Scalar(0, 255, 0), binaryMask);
                    }
                }

                return MatToTexture2D(coloredMat);
            }
            catch (Exception ex)
            {
                Log.Error($"[ImageSegmentation] Error creating colored mask texture: {ex.Message}", this);
                return MatToTexture2D(new Mat(inputMat.Size(), MatType.CV_8UC3, new Scalar(0, 0, 0)));
            }
        }
        #endregion

        #region Texture Conversion Methods
        private Mat Texture2DToMat(Texture2D texture)
        {
            var device = ResourceManager.Device;
            var desc = texture.Description;

            var stagingDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            };

            using var stagingTexture = new SharpDX.Direct3D11.Texture2D(device, stagingDesc);
            device.ImmediateContext.CopyResource(texture, stagingTexture);

            var dataBox = device.ImmediateContext.MapSubresource(stagingTexture, 0, MapMode.Read, MapFlags.None);
            if (dataBox.DataPointer == IntPtr.Zero)
            {
                device.ImmediateContext.UnmapSubresource(stagingTexture, 0);
                return new Mat();
            }

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

                Mat processedMat = new();

                try
                {
                    // FIXED: Ensure proper conversion to 8-bit 3-channel BGR
                    if (mat.Type() != MatType.CV_8UC3)
                    {
                        Log.Debug($"[MatToMediaPipeImage] Converting from {mat.Type()} to CV_8UC3", this);

                        if (mat.Channels() == 1)
                            Cv2.CvtColor(mat, processedMat, ColorConversionCodes.GRAY2BGR);
                        else if (mat.Channels() == 4)
                            Cv2.CvtColor(mat, processedMat, ColorConversionCodes.BGRA2BGR);
                        else if (mat.Depth() != MatType.CV_8U)
                            mat.ConvertTo(processedMat, MatType.CV_8UC3);
                        else
                            mat.CopyTo(processedMat);
                    }
                    else
                    {
                        Log.Debug("[MatToMediaPipeImage] Mat is already CV_8UC3", this);
                        mat.CopyTo(processedMat);
                    }

                    Log.Debug($"[MatToMediaPipeImage] Processed Mat type: {processedMat.Type()}, channels: {processedMat.Channels()}", this);

                    // FIXED: Convert BGR to RGB for MediaPipe
                    Mat rgbMat = new();
                    Cv2.CvtColor(processedMat, rgbMat, ColorConversionCodes.BGR2RGB);

                    Log.Debug($"[MatToMediaPipeImage] Converted to RGB, size: {rgbMat.Width}x{rgbMat.Height}", this);

                    // FIXED: Ensure proper byte array creation and copying
                    byte[] imageData = new byte[rgbMat.Width * rgbMat.Height * 3];
                    Marshal.Copy(rgbMat.Data, imageData, 0, imageData.Length);

                    Log.Debug($"[MatToMediaPipeImage] Created image data array of size {imageData.Length}", this);

                    // FIXED: Use SRGB format which is more compatible
                    Image image = new(
                        Mediapipe.ImageFormat.Types.Format.Srgb,
                        rgbMat.Width,
                        rgbMat.Height,
                        rgbMat.Width * 3, // stride = width * 3 channels (RGB)
                        imageData
                    );

                    Log.Debug($"[MatToMediaPipeImage] Created MediaPipe Image: {image.Width()}x{image.Height()}, format: {image.Format()}", this);

                    rgbMat.Dispose();
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

        private Mat? ConvertImageToMat(Image image)
        {
            try
            {
                Log.Debug("[ConvertImageToMat] Converting MediaPipe Image to Mat...", this);
                
                if (image == null)
                {
                    Log.Debug("[ConvertImageToMat] ERROR: Input image is null", this);
                    return null;
                }

                int width = image.Width();
                int height = image.Height();
                var format = image.Format();
                
                Log.Debug($"[ConvertImageToMat] Image dimensions: {width}x{height}", this);
                Log.Debug($"[ConvertImageToMat] Image format: {format}", this);

                Mat mat;
                
                // Handle different MediaPipe image formats
                switch (format.ToString())
                {
                    case "Srgb":
                    case "Rgb24":
                        // 3-channel RGB format
                        mat = new Mat(height, width, MatType.CV_8UC3);
                        // Note: This is a simplified implementation
                        // In a real implementation, you'd need to extract the actual pixel data from MediaPipe
                        Log.Warning("[ConvertImageToMat] WARNING: RGB format - using placeholder conversion", this);
                        mat.SetTo(new Scalar(128, 128, 128)); // Placeholder gray
                        break;
                        
                    case "Gray8":
                    case "OneComponent8":
                        // Single channel grayscale
                        mat = new Mat(height, width, MatType.CV_8UC1);
                        Log.Warning("[ConvertImageToMat] WARNING: Grayscale format - using placeholder conversion", this);
                        mat.SetTo(new Scalar(128)); // Placeholder gray
                        break;
                        
                    case "Gray32":
                    case "GrayFloat32":
                        // Single channel float grayscale
                        mat = new Mat(height, width, MatType.CV_32FC1);
                        Log.Warning("[ConvertImageToMat] WARNING: Float grayscale format - using placeholder conversion", this);
                        mat.SetTo(new Scalar(0.5f)); // Placeholder gray
                        break;
                        
                    default:
                        Log.Error($"[ConvertImageToMat] Unsupported format: {format}", this);
                        return null;
                }
                
                Log.Debug("[ConvertImageToMat] Conversion completed", this);
                return mat;
            }
            catch (Exception ex)
            {
                Log.Error($"[ConvertImageToMat] ERROR: {ex.Message}", this);
                return null;
            }
        }

        private Texture2D? MatToTexture2D(Mat mat)
        {
            try
            {
                if (mat.Empty()) return null;

                var device = ResourceManager.Device;
                var desc = new Texture2DDescription
                {
                    Width = mat.Width,
                    Height = mat.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                    SampleDescription = new SharpDX.DXGI.SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                };

                // Convert all input Mats to BGRA format for DirectX texture
                Mat bgraMat = new Mat();
                try
                {
                    if (mat.Channels() == 3)
                    {
                        Cv2.CvtColor(mat, bgraMat, ColorConversionCodes.BGR2BGRA);
                    }
                    else if (mat.Channels() == 1)
                    {
                        Cv2.CvtColor(mat, bgraMat, ColorConversionCodes.GRAY2BGRA);
                    }
                    else if (mat.Channels() == 4)
                    {
                        mat.CopyTo(bgraMat);
                    }
                    else
                    {
                        Log.Error($"[MatToTexture2D] Unsupported channel count: {mat.Channels()}", this);
                        return null;
                    }

                    // Ensure we have the correct format for DirectX
                    if (bgraMat.Type() != MatType.CV_8UC4)
                    {
                        var tempMat = new Mat();
                        bgraMat.ConvertTo(tempMat, MatType.CV_8UC4);
                        bgraMat.Dispose();
                        bgraMat = tempMat;
                    }

                    var texture = new SharpDX.Direct3D11.Texture2D(device, desc);
                    int pitch = mat.Width * 4;
                    var dataBox = device.ImmediateContext.MapSubresource(texture, 0, MapMode.WriteDiscard, MapFlags.None);
                    try
                    {
                        for (int y = 0; y < mat.Height; y++)
                        {
                            IntPtr srcPtr = new IntPtr(bgraMat.Data.ToInt64() + y * bgraMat.Step());
                            IntPtr dstPtr = dataBox.DataPointer + y * pitch;
                            Utilities.CopyMemory(dstPtr, srcPtr, pitch);
                        }
                    }
                    finally
                    {
                        device.ImmediateContext.UnmapSubresource(texture, 0);
                    }

                    return new Texture2D(texture);
                }
                finally
                {
                    bgraMat.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[MatToTexture2D] Error: {ex.Message}", this);
                return null;
            }
        }
        #endregion

        #region Cleanup
        protected override void Dispose(bool isDisposing)
        {
            if (!isDisposing) return;

            _imageSegmenter?.Close();
            _lastSegmentationResult = null;
            
            base.Dispose(isDisposing);
        }
        #endregion

        #region Input Parameters
        [Input(Guid = "A7B8C9D0-E1F2-4D23-E05C-345678901241")]
        public readonly InputSlot<Texture2D?> InputTexture = new();

        [Input(Guid = "B8C9D0E1-F2A3-4E34-F16D-456789012342")]
        public readonly InputSlot<bool> Enabled = new(true);

        [Input(Guid = "C9D0E1F2-A3B4-4F45-A27E-567890123453", MappedType = typeof(SegmentationOutputMode))]
        public readonly InputSlot<int> OutputMode = new(0); // 0 = SegmentationOutputMode.Overlay

        [Input(Guid = "D0E1F2A3-B4C5-4056-B38F-678901234563")]
        public readonly InputSlot<float> ConfidenceThreshold = new(0.5f);
        #endregion
    }
}
