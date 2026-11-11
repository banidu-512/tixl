using System.Numerics;
using Lib.physics;
using T3.Core.DataTypes;
using Xunit;

namespace Lib.Tests.Physics;

/// <summary>
/// The step between a <see cref="MeshGeometry"/> and the collision data of a body: which points make up a
/// piece, where its pivot is, and whether a change in the geometry is noticed.
/// </summary>
public class PhysicsGeometryTests
{
    [Fact]
    public void EachPartBecomesAHullAroundItsPivot()
    {
        var geometry = TwoCubes(out var pivotA, out var pivotB);
        var hulls = PhysicsGeometry.BuildHulls(geometry, merge: false);

        Assert.Equal(2, hulls.Length);
        Assert.Equal(pivotA, hulls[0].Pivot);
        Assert.Equal(pivotB, hulls[1].Pivot);
        Assert.Equal(8, hulls[0].Points.Length);
        Assert.Equal(8, hulls[1].Points.Length);

        // Points are stored relative to the pivot: a unit cube spans -0.5..0.5 whatever its place in the geometry.
        foreach (var hull in hulls)
        {
            foreach (var point in hull.Points)
            {
                Assert.Equal(0.5f, MathF.Abs(point.X), 0.0001f);
                Assert.Equal(0.5f, MathF.Abs(point.Y), 0.0001f);
                Assert.Equal(0.5f, MathF.Abs(point.Z), 0.0001f);
            }
        }
    }

    [Fact]
    public void MergeAndPartlessGeometryGiveOneHullAroundTheOrigin()
    {
        var geometry = TwoCubes(out _, out _);
        var merged = PhysicsGeometry.BuildHulls(geometry, merge: true);
        Assert.Single(merged);
        Assert.Equal(16, merged[0].Points.Length);
        Assert.Equal(Vector3.Zero, merged[0].Pivot);

        geometry.Parts = [];
        var partless = PhysicsGeometry.BuildHulls(geometry, merge: false);
        Assert.Single(partless);
        Assert.Equal(16, partless[0].Points.Length);
    }

    [Fact]
    public void QuadsAreFannedIntoTriangles()
    {
        var geometry = TwoCubes(out _, out _);
        var triangles = PhysicsGeometry.BuildTriangles(geometry);

        // Two cubes, six quads each, two triangles per quad.
        Assert.Equal(2 * 6 * 2 * 3, triangles.Length);
        Assert.Equal(geometry.GetTriangleCount() * 3, triangles.Length);
    }

    [Fact]
    public void OutOfRangeIndicesAreSkippedInsteadOfThrowing()
    {
        var geometry = TwoCubes(out _, out _);
        geometry.CornerPointIndices[0] = 9999;
        geometry.CornerPointIndices[5] = -3;
        geometry.Parts = [new GeometryPart(-5, 3, Vector3.Zero, 0, 0), new GeometryPart(10, 500, Vector3.Zero, 1, 1)];

        var hulls = PhysicsGeometry.BuildHulls(geometry, merge: false);
        var triangles = PhysicsGeometry.BuildTriangles(geometry);

        Assert.Equal(2, hulls.Length);
        Assert.True(triangles.Length < 72);
        Assert.Equal(0, triangles.Length % 3);
    }

    [Fact]
    public void LargePiecesAreThinnedOut()
    {
        const int pointCount = PhysicsGeometry.MaxHullPoints * 3 + 17;
        var geometry = new MeshGeometry
                           {
                               Positions = new Vector3[pointCount],
                               CornerPointIndices = new int[pointCount],
                               FaceCornerOffsets = new int[pointCount / 3 + 1],
                           };
        for (var index = 0; index < pointCount; index++)
        {
            geometry.Positions[index] = new Vector3(MathF.Cos(index), MathF.Sin(index * 0.7f), MathF.Cos(index * 1.3f));
            geometry.CornerPointIndices[index] = index;
        }

        for (var face = 0; face < geometry.FaceCornerOffsets.Length; face++)
            geometry.FaceCornerOffsets[face] = face * 3;

        var hull = PhysicsGeometry.BuildHulls(geometry, merge: false)[0];
        Assert.InRange(hull.Points.Length, PhysicsGeometry.MaxHullPoints / 2, PhysicsGeometry.MaxHullPoints);
    }

    [Fact]
    public void StampNoticesAPointMovedInPlace()
    {
        var geometry = TwoCubes(out _, out _);
        var before = PhysicsGeometry.Stamp(geometry);
        Assert.Equal(before, PhysicsGeometry.Stamp(geometry));

        // Producers rebuild into the same instance, so the reference and the counts stay the same.
        geometry.Positions[3] += new Vector3(0, 0.25f, 0);
        var moved = PhysicsGeometry.Stamp(geometry);
        Assert.NotEqual(before, moved);

        geometry.Parts[1] = geometry.Parts[1] with { Pivot = new Vector3(9, 9, 9) };
        Assert.NotEqual(moved, PhysicsGeometry.Stamp(geometry));
    }

    [Fact]
    public void GeometryPiecesSimulateAndStayPublishedAtTheirPivots()
    {
        var geometry = TwoCubes(out var pivotA, out var pivotB);
        var hulls = PhysicsGeometry.BuildHulls(geometry, merge: false);

        using var simulation = new PhysicsSimulation(this);
        var settings = new PhysicsWorldSettings { Gravity = new Vector3(0, -9.81f, 0), ContactStiffness = 30f, FixedRate = 60f, Substeps = 2, SolverIterations = 4, TimeScale = 1f };
        var bodies = new List<PhysicsBody>
                         {
                             new() { Id = Guid.NewGuid(), Kind = PhysicsBodyKind.Static, Position = new Vector3(0, -0.5f, 0), Size = new Vector3(40, 1, 40) },
                             new()
                                 {
                                     Id = Guid.NewGuid(),
                                     Shape = PhysicsShapeType.ConvexHull,
                                     Hulls = hulls,
                                     InstanceCount = 2,
                                     Instances =
                                         [
                                             new PhysicsBodyInstance { Orientation = Quaternion.Identity, Scale = Vector3.One, Color = Vector4.One, HullIndex = 0 },
                                             new PhysicsBodyInstance { Orientation = Quaternion.Identity, Scale = Vector3.One, Color = Vector4.One, HullIndex = 1 },
                                         ],
                                 },
                         };
        var joints = new List<PhysicsJoint>();
        var points = new Point[2];

        simulation.Update(settings, 0, bodies, joints);
        simulation.WritePoints(points, false);
        Assert.True(Vector3.Distance(pivotA, points[0].Position) < 0.001f, "a piece starts exactly where the geometry has it");
        Assert.True(Vector3.Distance(pivotB, points[1].Position) < 0.001f);

        for (var frame = 1; frame <= 240; frame++)
            simulation.Update(settings, frame / 60.0, bodies, joints);

        simulation.WritePoints(points, false);
        Assert.InRange(points[0].Position.Y, 0.45f, 0.56f);
        Assert.InRange(points[1].Position.Y, 0.45f, 0.56f);
        Assert.Equal(pivotA.X, points[0].Position.X, 0.05f);
        Assert.Equal(pivotB.X, points[1].Position.X, 0.05f);
    }

    /// <summary>Two unit cubes built from quads, each its own part with the pivot at its centre.</summary>
    private static MeshGeometry TwoCubes(out Vector3 pivotA, out Vector3 pivotB)
    {
        pivotA = new Vector3(-3, 2, 0);
        pivotB = new Vector3(4, 5, 1);
        int[] quads = [0, 1, 3, 2, 4, 6, 7, 5, 0, 4, 5, 1, 2, 3, 7, 6, 0, 2, 6, 4, 1, 5, 7, 3];

        var geometry = new MeshGeometry
                           {
                               Positions = new Vector3[16],
                               CornerPointIndices = new int[48],
                               FaceCornerOffsets = new int[13],
                           };
        Vector3[] pivots = [pivotA, pivotB];
        for (var cube = 0; cube < 2; cube++)
        {
            for (var corner = 0; corner < 8; corner++)
            {
                geometry.Positions[cube * 8 + corner] = pivots[cube] + new Vector3((corner & 1) == 0 ? -0.5f : 0.5f,
                                                                                    (corner & 2) == 0 ? -0.5f : 0.5f,
                                                                                    (corner & 4) == 0 ? -0.5f : 0.5f);
            }

            for (var index = 0; index < 24; index++)
                geometry.CornerPointIndices[cube * 24 + index] = cube * 8 + quads[index];
        }

        for (var face = 0; face <= 12; face++)
            geometry.FaceCornerOffsets[face] = face * 4;

        geometry.Parts = [new GeometryPart(0, 6, pivotA, 0, 0), new GeometryPart(6, 6, pivotB, 1, 1)];
        return geometry;
    }
}
