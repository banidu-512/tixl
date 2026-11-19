#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using OpenCvSharp;

namespace Lib.io.video.depthanything.Utils;

/// <summary>
/// Camera rotation from image features for a camera that only pans and tilts, or sits on a tripod: ORB features are matched
/// against a keyframe, a RANSAC homography keeps the matches that agree with one rotation, and the rotation is fitted between
/// the inliers' viewing rays. Depth is not used, so relative-depth wobble cannot disturb it. The pose composes like
/// <see cref="DepthPoseTracker"/>'s: <c>world = keyframe * delta</c>, <c>delta</c> mapping current-camera rays into the keyframe
/// camera, in the OpenCV camera frame (x right, y down, +Z forward).
/// </summary>
internal sealed class ImageRotationTracker : IDisposable
{
    private const int FeatureCount = 1500;
    private const float RatioTest = 0.75f;
    private const int MinMatches = 20;
    private const int MinInliers = 15;
    private const double RansacPixels = 3.0;

    /** A keyframe is replaced once the camera turned this far from it: further on, too little of the view is shared. */
    private const float KeyframeAngleDeg = 14f;
    private const int KeyframeInliers = 40;

    private readonly ORB _orb = ORB.Create(FeatureCount);
    private readonly BFMatcher _matcher = new(NormTypes.Hamming);
    private Mat? _keyDescriptors;
    private KeyPoint[] _keyPoints = [];
    private Quaternion _keyRotation = Quaternion.Identity;

    public Quaternion Rotation { get; private set; } = Quaternion.Identity;
    public bool HasPose { get; private set; }
    public bool Tracking { get; private set; }
    public int LastInliers { get; private set; }

    public void Reset()
    {
        _keyDescriptors?.Dispose();
        _keyDescriptors = null;
        _keyPoints = [];
        _keyRotation = Quaternion.Identity;
        Rotation = Quaternion.Identity;
        HasPose = false;
        Tracking = false;
        LastInliers = 0;
    }

    /** Feeds one grayscale frame (width x height bytes) with the camera matrix of that pixel grid and updates the world rotation. */
    public void Update(byte[] gray, int width, int height, float fx, float fy, float cx, float cy)
    {
        using var image = Mat.FromPixelData(height, width, MatType.CV_8UC1, gray);
        using var descriptors = new Mat();
        _orb.DetectAndCompute(image, null, out var keyPoints, descriptors);
        if (keyPoints.Length < MinMatches || descriptors.Empty())
        {
            Tracking = false;
            return;
        }

        if (_keyDescriptors == null)
        {
            Promote(keyPoints, descriptors);
            HasPose = true;
            Tracking = true;
            return;
        }

        var matches = _matcher.KnnMatch(descriptors, _keyDescriptors, 2);
        var current = new List<Point2f>();
        var key = new List<Point2f>();
        foreach (var pair in matches)
        {
            if (pair.Length < 2 || pair[0].Distance >= RatioTest * pair[1].Distance)
                continue;

            current.Add(keyPoints[pair[0].QueryIdx].Pt);
            key.Add(_keyPoints[pair[0].TrainIdx].Pt);
        }

        if (current.Count < MinMatches)
        {
            Lost(keyPoints, descriptors);
            return;
        }

        using var currentMat = Mat.FromArray(current.ToArray());
        using var keyMat = Mat.FromArray(key.ToArray());
        using var mask = new Mat();
        using var homography = Cv2.FindHomography(currentMat, keyMat, HomographyMethods.Ransac, RansacPixels, mask);
        if (homography.Empty())
        {
            Lost(keyPoints, descriptors);
            return;
        }

        mask.GetArray(out byte[] inlierMask);
        var src = new List<Vector3>();
        var dst = new List<Vector3>();
        var weight = new List<float>();
        for (var i = 0; i < inlierMask.Length; i++)
        {
            if (inlierMask[i] == 0)
                continue;

            src.Add(Ray(current[i], fx, fy, cx, cy));
            dst.Add(Ray(key[i], fx, fy, cx, cy));
            weight.Add(1f);
        }

        LastInliers = src.Count;
        if (src.Count < MinInliers || !Sim3Fit.Fit(src, dst, weight, out var fit))
        {
            Lost(keyPoints, descriptors);
            return;
        }

        var delta = fit.Rotation;
        Rotation = Quaternion.Normalize(Quaternion.Multiply(_keyRotation, delta));
        HasPose = true;
        Tracking = true;

        var turned = 2f * MathF.Acos(Math.Clamp(MathF.Abs(delta.W), 0f, 1f)) * 180f / MathF.PI;
        if (turned > KeyframeAngleDeg || src.Count < KeyframeInliers)
            Promote(keyPoints, descriptors);
    }

    private static Vector3 Ray(Point2f p, float fx, float fy, float cx, float cy) =>
        Vector3.Normalize(new Vector3((p.X - cx) / fx, (p.Y - cy) / fy, 1f));

    /** The view cannot be matched: keep the pose and use this frame as the new reference so tracking can pick up again. */
    private void Lost(KeyPoint[] keyPoints, Mat descriptors)
    {
        Tracking = false;
        Promote(keyPoints, descriptors);
    }

    private void Promote(KeyPoint[] keyPoints, Mat descriptors)
    {
        _keyDescriptors?.Dispose();
        _keyDescriptors = descriptors.Clone();
        _keyPoints = keyPoints;
        _keyRotation = Rotation;
    }

    public void Dispose()
    {
        _keyDescriptors?.Dispose();
        _orb.Dispose();
        _matcher.Dispose();
    }
}
