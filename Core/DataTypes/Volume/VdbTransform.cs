#nullable enable
using System;
using System.Numerics;

namespace T3.Core.DataTypes.Volume;

/// <summary>
/// Maps voxel index space to world space with a uniform voxel size and a translation,
/// matching OpenVDB's UniformScaleTranslateMap (the map used for level sets and fog volumes).
/// Voxel (i,j,k) is sampled at translation + (i,j,k) * voxelSize, which is the center of the world
/// cell [t + (i - 0.5) * s, t + (i + 0.5) * s) it covers.
/// </summary>
public sealed class VdbTransform
{
    public static readonly VdbTransform Identity = new(0.1f, Vector3.Zero);

    public VdbTransform(float voxelSize, Vector3 translation)
    {
        VoxelSize = Math.Clamp(voxelSize, 1e-7f, 1e7f);
        Translation = translation;
    }

    public float VoxelSize { get; }
    public Vector3 Translation { get; }

    public Vector3 IndexToWorld(VdbCoord coord) => IndexToWorld(coord.ToVector3());

    public Vector3 IndexToWorld(Vector3 indexCenter) => Translation + indexCenter * VoxelSize;

    /// <summary>Maps the lower corner of the cell of voxel <paramref name="indexCorner"/> into world space.</summary>
    public Vector3 IndexCornerToWorld(Vector3 indexCorner) => Translation + (indexCorner - new Vector3(0.5f)) * VoxelSize;

    public Vector3 WorldToIndexf(Vector3 world) => (world - Translation) / VoxelSize;

    public VdbCoord WorldToIndex(Vector3 world)
    {
        var index = WorldToIndexf(world);
        return new VdbCoord((int)MathF.Floor(index.X), (int)MathF.Floor(index.Y), (int)MathF.Floor(index.Z));
    }

    public VdbTransform WithTranslation(Vector3 translation) => new(VoxelSize, translation);

    public VdbTransform WithVoxelSize(float voxelSize) => new(voxelSize, Translation);

    public VdbCoordBBox WorldBoxToIndex(Vector3 worldMin, Vector3 worldMax)
        => new(WorldToIndex(worldMin), WorldToIndex(worldMax));

    /// <summary>
    /// Serializes as an OpenVDB UniformScaleTranslateMap: a type-name string followed by
    /// six Vec3d values (translation, scale, voxelSize, inverse scale, invScaleSqr, invTwiceScale).
    /// </summary>
    internal (string MapType, double[] Values) ToOpenVdbMap()
    {
        var s = (double)VoxelSize;
        return ("UniformScaleTranslateMap",
                [
                    (double)Translation.X, Translation.Y, Translation.Z,
                    s, s, s,
                    s, s, s,
                    1 / s, 1 / s, 1 / s,
                    1 / (s * s), 1 / (s * s), 1 / (s * s),
                    1 / (2 * s), 1 / (2 * s), 1 / (2 * s),
                ]);
    }

    internal static VdbTransform FromOpenVdbMap(string mapType, ReadOnlySpan<double> values)
    {
        switch (mapType)
        {
            case "UniformScaleTranslateMap":
            {
                // layout: translation(3), scale(3), voxelSize(3), inverse scale(3), invScaleSqr(3), invTwiceScale(3)
                if (values.Length < 9)
                    return Identity;
                var t = new Vector3((float)values[0], (float)values[1], (float)values[2]);
                var s = (float)values[6];
                return new VdbTransform(s, t);
            }
            case "ScaleTranslateMap":
            {
                if (values.Length < 9)
                    return Identity;
                var t = new Vector3((float)values[0], (float)values[1], (float)values[2]);
                var s = (float)((values[3] + values[4] + values[5]) / 3.0);
                return new VdbTransform(s, t);
            }
            case "UniformScaleMap":
            case "ScaleMap":
            {
                // layout: scale(3), voxelSize(3), inverse scale(3), invScaleSqr(3), invTwiceScale(3)
                var s = values.Length >= 3 ? (float)values[0] : 0.1f;
                return new VdbTransform(s, Vector3.Zero);
            }
            case "AffineMap":
            case "UnitaryMap":
            {
                // Row-major 4x4: the upper 3x3 holds the linear part and column 3 the translation.
                if (values.Length < 16)
                    return Identity;
                var sx = (float)new Vector3((float)values[0], (float)values[1], (float)values[2]).Length();
                var t = new Vector3((float)values[3], (float)values[7], (float)values[11]);
                return new VdbTransform(sx > 1e-7f ? sx : 0.1f, t);
            }
            case "TranslationMap":
            {
                var t = values.Length >= 3 ? new Vector3((float)values[0], (float)values[1], (float)values[2]) : Vector3.Zero;
                return new VdbTransform(0.1f, t);
            }
            default:
                // UnitaryMap/AffineMap/NonlinearFrustumMap are not representable here; fall back to identity.
                return Identity;
        }
    }
}
