namespace Lib.physics;

/// <summary>
/// The simulated bodies of one or more <see cref="RigidBody"/> operators as geometry: the way back from physics into the geometry
/// operators. A body made from geometry returns that same geometry with every part moved by its body, so one
/// geometry describes the object, collides and is drawn. A primitive body returns a box, sphere, capsule or
/// cylinder of its size, so it needs no separate mesh either. Every body is one part of the result.
/// </summary>
[Guid("8e417a73-0f16-4f5e-b253-39ec13c2ace0")]
internal sealed class RigidBodyGeometry : Instance<RigidBodyGeometry>
{
    [Output(Guid = "27dd76aa-73b8-4a36-97d5-f6a10b334f48", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<MeshGeometry> Result = new();

    [Output(Guid = "d1e9cddf-c53f-4380-afbd-2ac91a0304bc", DirtyFlagTrigger = DirtyFlagTrigger.Animated)]
    public readonly Slot<int> Count = new();

    public RigidBodyGeometry()
    {
        Result.UpdateAction += Update;
        Count.UpdateAction += Update;
    }

    private void Update(EvaluationContext context)
    {
        // Pulling the world's buffer is what makes the world step this frame and publish the poses read below.
        var worldPoints = World.GetValue(context);
        _bodies.Clear();
        var inputs = Body.GetCollectedTypedInputs();
        if (inputs != null)
        {
            for (var index = 0; index < inputs.Count; index++)
            {
                var body = inputs[index].GetValue(context);
                if (body != null)
                    _bodies.Add(body);
            }
        }

        var count = 0;
        try
        {
            if (worldPoints != null && _bodies.Count > 0)
                count = PhysicsGeometry.BuildPosed(_bodies, _output, _scratch);
        }
        catch (Exception e)
        {
            // Geometry arrives from the graph in any state; a bad index must not take the evaluator down.
            count = 0;
            if (!_hasLoggedError)
                Log.Warning($"RigidBodyGeometry failed: {e.Message}", this);
            _hasLoggedError = true;
        }

        Result.Value = count > 0 ? _output : null;
        Result.DirtyFlag.Invalidate();
        Count.Value = count;
        Count.DirtyFlag.Invalidate();
    }

    private readonly MeshGeometry _output = new();
    private readonly List<PhysicsBody> _bodies = new();
    private readonly List<PhysicsGeometry.PosedBody> _scratch = new();
    private bool _hasLoggedError;

    [Input(Guid = "eb4be503-426c-4ea3-af0d-47933b2fd641")]
    public readonly InputSlot<BufferWithViews> World = new();

    [Input(Guid = "9039d3ba-121e-4956-8b74-944bd4170f69")]
    public readonly MultiInputSlot<PhysicsBody> Body = new();
}
