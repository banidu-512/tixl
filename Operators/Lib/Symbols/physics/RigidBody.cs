using T3.Core.Utils;

namespace Lib.physics;

/// <summary>
/// Describes one rigid body for <see cref="PhysicsWorld"/>. This operator owns no simulation
/// state: it only publishes a description that the world turns into a Bepu body. The body id
/// is fixed for the lifetime of this operator instance, which is how the world recognises the
/// same body across evaluations.
/// </summary>
[Guid("efa358f6-8f07-448f-ad9e-d911c41a2098")]
internal sealed class RigidBody : Instance<RigidBody>
{
    [Output(Guid = "165a613b-df3a-4d84-b5af-1c72a381fa15", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<PhysicsBody> OutBody = new();

    public RigidBody()
    {
        _descriptor.Id = _id;
        OutBody.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        _descriptor.Shape = (PhysicsShapeType)Shape.GetValue(context);
        _descriptor.Kind = (PhysicsBodyKind)Kind.GetValue(context);
        _descriptor.Position = Position.GetValue(context);

        var rotation = Rotation.GetValue(context);
        _descriptor.Orientation = Quaternion.CreateFromYawPitchRoll(MathUtils.ToRad * rotation.Y,
                                                                    MathUtils.ToRad * rotation.X,
                                                                    MathUtils.ToRad * rotation.Z);

        _descriptor.Size = Size.GetValue(context);
        _descriptor.Radius = Radius.GetValue(context);
        _descriptor.Length = Length.GetValue(context);
        _descriptor.Mass = Mass.GetValue(context);
        _descriptor.LinearVelocity = LinearVelocity.GetValue(context);
        _descriptor.AngularVelocity = AngularVelocity.GetValue(context);
        _descriptor.Friction = Friction.GetValue(context);
        _descriptor.Bounciness = Bounciness.GetValue(context);
        _descriptor.LinearDamping = LinearDamping.GetValue(context);
        _descriptor.AngularDamping = AngularDamping.GetValue(context);
        _descriptor.GravityScale = GravityScale.GetValue(context);
        _descriptor.Enabled = Enabled.GetValue(context);

        OutBody.Value = _descriptor;
        OutBody.DirtyFlag.Invalidate();
    }

    private readonly Guid _id = Guid.NewGuid();
    private readonly PhysicsBody _descriptor = new();

    [Input(Guid = "5f6cc54b-9221-42b9-87d7-a0f9669c64b2", MappedType = typeof(PhysicsShapeType))]
    public readonly InputSlot<int> Shape = new(0);

    [Input(Guid = "70f094d8-c499-40bf-ab28-0081297a5985", MappedType = typeof(PhysicsBodyKind))]
    public readonly InputSlot<int> Kind = new(0);

    [Input(Guid = "5072f21a-83a3-41a7-83cd-f702d9c89c84")]
    public readonly InputSlot<Vector3> Position = new();

    [Input(Guid = "890a457a-af9a-4d23-80b5-07014c95ee27")]
    public readonly InputSlot<Vector3> Rotation = new();

    [Input(Guid = "050f6d8c-40ff-4450-b4a1-69bf4ff2b3be")]
    public readonly InputSlot<Vector3> Size = new(Vector3.One);

    [Input(Guid = "c63d5ebe-108d-4afd-ba39-2cbda0d62011")]
    public readonly InputSlot<float> Radius = new(0.5f);

    [Input(Guid = "d8d048f0-6517-497e-878d-24fc95ed8a64")]
    public readonly InputSlot<float> Length = new(1f);

    [Input(Guid = "974ca0a2-5f02-4ccd-817b-5114070b243e")]
    public readonly InputSlot<float> Mass = new(1f);

    [Input(Guid = "e965e0ce-38ce-46da-a7ac-e730d7876d29")]
    public readonly InputSlot<Vector3> LinearVelocity = new();

    [Input(Guid = "3d837671-9883-43b0-a889-764ae0fbb6e6")]
    public readonly InputSlot<Vector3> AngularVelocity = new();

    [Input(Guid = "28d83ea4-beb0-4e39-bb27-dc714c48c69f")]
    public readonly InputSlot<float> Friction = new(0.5f);

    [Input(Guid = "847babc8-53b1-457d-ab45-f85b8eff80c9")]
    public readonly InputSlot<float> Bounciness = new();

    [Input(Guid = "a7a684cf-2d9b-4cc5-948b-a07dd990e08a")]
    public readonly InputSlot<float> LinearDamping = new();

    [Input(Guid = "acd5e765-3086-4489-bbab-aab68881a2c0")]
    public readonly InputSlot<float> AngularDamping = new();

    [Input(Guid = "1b9b7bbd-aba7-4a23-8f78-c1366f260f75")]
    public readonly InputSlot<float> GravityScale = new(1f);

    [Input(Guid = "09fe2b00-ad21-4103-84fd-bf970c329bb2")]
    public readonly InputSlot<bool> Enabled = new(true);
}
