using System;
using System.Numerics;

namespace T3.Core.DataTypes;

public enum PhysicsShapeType
{
    Box = 0,
    Sphere = 1,
    Capsule = 2,
    Cylinder = 3,
}

public enum PhysicsBodyKind
{
    Dynamic = 0,
    Static = 1,
    Kinematic = 2,
}

/// <summary>
/// Declarative description of one rigid body. Produced by a body operator and consumed by
/// <c>Lib.physics.PhysicsWorld</c>; it carries no Bepu state, so the world can rebuild its
/// simulation without the graph knowing about physics engine types.
/// </summary>
public sealed class PhysicsBody
{
    /// <summary>Stable per operator instance, so the world keeps a body across frames even
    /// though the operator writes a fresh description each evaluation.</summary>
    public Guid Id;

    public PhysicsShapeType Shape = PhysicsShapeType.Box;
    public PhysicsBodyKind Kind = PhysicsBodyKind.Dynamic;

    public Vector3 Position;
    public Quaternion Orientation = Quaternion.Identity;

    /// <summary>Full extents for <see cref="PhysicsShapeType.Box"/>.</summary>
    public Vector3 Size = Vector3.One;

    public float Radius = 0.5f;

    /// <summary>Length of the cylindrical section for capsules and cylinders.</summary>
    public float Length = 1f;

    public Vector3 LinearVelocity;
    public Vector3 AngularVelocity;

    public float Mass = 1f;
    public float Friction = 0.5f;
    public float Bounciness;
    public float LinearDamping;
    public float AngularDamping;
    public float GravityScale = 1f;
    public bool Enabled = true;

    public void CopyFrom(PhysicsBody other)
    {
        Shape = other.Shape;
        Kind = other.Kind;
        Position = other.Position;
        Orientation = other.Orientation;
        Size = other.Size;
        Radius = other.Radius;
        Length = other.Length;
        LinearVelocity = other.LinearVelocity;
        AngularVelocity = other.AngularVelocity;
        Mass = other.Mass;
        Friction = other.Friction;
        Bounciness = other.Bounciness;
        LinearDamping = other.LinearDamping;
        AngularDamping = other.AngularDamping;
        GravityScale = other.GravityScale;
        Enabled = other.Enabled;
    }
}

public enum PhysicsJointType
{
    BallSocket = 0,
    Hinge = 1,
    Weld = 2,
    DistanceServo = 3,
}

/// <summary>
/// Declarative description of one constraint between two bodies.
/// </summary>
public sealed class PhysicsJoint
{
    public Guid Id;
    public PhysicsBody BodyA;
    public PhysicsBody BodyB;
    public PhysicsJointType Type = PhysicsJointType.BallSocket;

    public Vector3 LocalOffsetA;
    public Vector3 LocalOffsetB;
    public Vector3 HingeAxisA = Vector3.UnitY;
    public Vector3 HingeAxisB = Vector3.UnitY;

    /// <summary>Spring frequency in Hz. Higher is stiffer.</summary>
    public float Frequency = 30f;

    /// <summary>1 = critically damped, lower values add springiness.</summary>
    public float DampingRatio = 1f;

    public float TargetDistance;
    public bool Enabled = true;
}
