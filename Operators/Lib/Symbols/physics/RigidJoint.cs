namespace Lib.physics;

/// <summary>
/// Describes one constraint between two bodies. Like <see cref="RigidBody"/> this is pure data:
/// <see cref="PhysicsWorld"/> creates, updates and removes the actual Bepu constraint. Both
/// bodies must be Dynamic or Kinematic; a static ground cannot be a constraint anchor, so use
/// a Kinematic body when you need a fixed hinge point.
/// </summary>
[Guid("f3ce9edf-04d2-4169-b2fe-1d850d3650b5")]
internal sealed class RigidJoint : Instance<RigidJoint>
{
    [Output(Guid = "80ed0637-9fe2-4377-83b9-9f4e949cac70", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<PhysicsJoint> OutJoint = new();

    public RigidJoint()
    {
        _descriptor.Id = _id;
        OutJoint.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        _descriptor.BodyA = BodyA.GetValue(context);
        _descriptor.BodyB = BodyB.GetValue(context);
        _descriptor.Type = (PhysicsJointType)JointType.GetValue(context);
        _descriptor.LocalOffsetA = LocalOffsetA.GetValue(context);
        _descriptor.LocalOffsetB = LocalOffsetB.GetValue(context);
        _descriptor.HingeAxisA = HingeAxisA.GetValue(context);
        _descriptor.HingeAxisB = HingeAxisB.GetValue(context);
        _descriptor.Frequency = Frequency.GetValue(context);
        _descriptor.DampingRatio = DampingRatio.GetValue(context);
        _descriptor.TargetDistance = TargetDistance.GetValue(context);
        _descriptor.Enabled = Enabled.GetValue(context);

        OutJoint.Value = _descriptor;
        OutJoint.DirtyFlag.Invalidate();
    }

    private readonly Guid _id = Guid.NewGuid();
    private readonly PhysicsJoint _descriptor = new();

    [Input(Guid = "8cd42379-133b-419e-8bf6-ef810b3e7f92")]
    public readonly InputSlot<PhysicsBody> BodyA = new();

    [Input(Guid = "5704514e-be1d-4fff-89d0-dfbe94963cb8")]
    public readonly InputSlot<PhysicsBody> BodyB = new();

    [Input(Guid = "da042534-228c-44e5-8f92-37626fb97964", MappedType = typeof(PhysicsJointType))]
    public readonly InputSlot<int> JointType = new(0);

    [Input(Guid = "4e0ce046-5f3b-4f57-8ca2-cbea40152c62")]
    public readonly InputSlot<Vector3> LocalOffsetA = new();

    [Input(Guid = "aa42544d-b3a1-4f00-85d3-a7e7780c132e")]
    public readonly InputSlot<Vector3> LocalOffsetB = new();

    [Input(Guid = "6e24ef86-ece5-4972-8ceb-8f67cc24407c")]
    public readonly InputSlot<Vector3> HingeAxisA = new(Vector3.UnitY);

    [Input(Guid = "d204c5b2-35b1-47cc-8997-134d6897a75e")]
    public readonly InputSlot<Vector3> HingeAxisB = new(Vector3.UnitY);

    [Input(Guid = "29e6a3fb-2b44-4d19-9962-641e9fc8c479")]
    public readonly InputSlot<float> Frequency = new(30f);

    [Input(Guid = "1afa70ca-48d2-4466-bb58-e626ddad58bd")]
    public readonly InputSlot<float> DampingRatio = new(1f);

    [Input(Guid = "b254f1b9-ea4e-4634-b469-d384caa032a9")]
    public readonly InputSlot<float> TargetDistance = new();

    [Input(Guid = "e3722753-f66c-4e44-a9fd-1d72f809ce5e")]
    public readonly InputSlot<bool> Enabled = new(true);
}
