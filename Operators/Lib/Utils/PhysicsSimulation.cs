using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using T3.Core.DataTypes;
using T3.Core.Logging;

namespace Lib.physics;

/// <summary>Per-evaluation world parameters, already read from the operator's inputs.</summary>
internal struct PhysicsWorldSettings
{
    public Vector3 Gravity;
    public float ContactStiffness;
    public float FixedRate;
    public int Substeps;
    public int SolverIterations;
    public float TimeScale;
    public bool Multithreaded;
    public bool Deterministic;
    public bool Interpolate;
    public bool Paused;
    public bool Reset;
}

/// <summary>
/// Owns one Bepu simulation and turns the declarative PhysicsBody / PhysicsJoint descriptions
/// produced by the surrounding graph into rigid-body state.
///
/// Time is handled with a fixed-step accumulator: the render rate never enters Bepu, poses are
/// interpolated between the last two fixed steps, and a timeline jump or rewind rebuilds the
/// simulation instead of trying to step backwards. It has no dependency on the operator or GPU
/// types, so it can be driven by a scripted clock.
/// </summary>
internal sealed class PhysicsSimulation : IDisposable
{
    public PhysicsSimulation(object logSource)
    {
        _logSource = logSource;
    }

    /// <summary>Simulated seconds since the last rebuild.</summary>
    public float SimTime => _simTime;

    /// <summary>Number of live Bepu constraints, which differs from the joint count if a constraint leaks or is added twice.</summary>
    public int ConstraintCount => _simulation?.Solver.CountConstraints() ?? 0;

    public int RenderCount(bool includeStatics) => includeStatics ? _renderOrder.Count : _dynamicRenderCount;

    /// <summary>Simulated position of a body at the latest fixed step, without render interpolation.</summary>
    public bool TryGetBodyPosition(Guid bodyId, out Vector3 position)
    {
        if (_bodies.TryGetValue(bodyId, out var entry))
        {
            position = entry.CurrentPose.Position;
            return true;
        }

        position = default;
        return false;
    }

    public void Update(in PhysicsWorldSettings settings, double nowSeconds, List<PhysicsBody> bodies, List<PhysicsJoint> joints)
    {
        _frameBodies = bodies;
        _frameJoints = joints;

        var contactStiffness = Math.Clamp(settings.ContactStiffness, 5f, 120f);
        var rate = Math.Clamp(settings.FixedRate, 10f, 240f);
        var substeps = Math.Clamp(settings.Substeps, 1, 8);
        var iterations = Math.Clamp(settings.SolverIterations, 1, 32);
        var timeScale = Math.Clamp(settings.TimeScale, 0f, 4f);
        var resetTriggered = settings.Reset && !_wasResetPressed;
        _wasResetPressed = settings.Reset;

        // Gravity and stiffness are read through the callback's settings reference, so they can
        // change without rebuilding; the solver layout below is baked into the simulation.
        _settings.Gravity = settings.Gravity;
        _settings.ContactFrequency = contactStiffness;

        var configChanged = rate != _fixedRate
                            || substeps != _substeps
                            || iterations != _solverIterations
                            || settings.Multithreaded != _multithreaded
                            || settings.Deterministic != _deterministic;

        var delta = double.IsNaN(_lastTimeSeconds) ? 0 : nowSeconds - _lastTimeSeconds;
        var timeJumped = delta < -0.001 || delta > MaxTimeJump;
        _lastTimeSeconds = nowSeconds;

        if (resetTriggered || _simulation == null || configChanged || timeJumped)
        {
            RebuildSimulation(rate, substeps, iterations, settings.Multithreaded, settings.Deterministic);
            ReconcileBodies();
            ReconcileJoints();
            _accumulator = 0;
            _simTime = 0;
        }
        else
        {
            ReconcileBodies();
            ReconcileJoints();

            if (!settings.Paused)
                _accumulator += Math.Clamp(delta, 0, MaxFrameDelta) * timeScale;
        }

        var dt = 1.0 / _fixedRate;
        var maxSteps = Math.Max(1, (int)MathF.Ceiling(MaxFrameDelta / (float)dt));
        var steps = 0;
        while (_accumulator >= dt && steps < maxSteps)
        {
            Step((float)dt);
            _accumulator -= dt;
            steps++;
        }

        // Never let a slow frame build a debt that turns into slow motion forever.
        if (_accumulator > dt)
            _accumulator = dt;

        _interpolationAlpha = settings.Interpolate ? (float)Math.Clamp(_accumulator / dt, 0.0, 1.0) : 1f;
    }

    /// <summary>Writes the interpolated pose of every rendered body and returns how many were written.</summary>
    public int WritePoints(Point[] points, bool includeStatics)
    {
        var count = Math.Min(RenderCount(includeStatics), points.Length);
        var writeIndex = 0;

        for (var i = 0; i < _renderOrder.Count && writeIndex < count; i++)
        {
            var entry = _renderOrder[i];
            if (entry.IsStatic && !includeStatics)
                continue;

            var pose = InterpolatePose(entry.PreviousPose, entry.CurrentPose, _interpolationAlpha);
            points[writeIndex++] = new Point
                                       {
                                           Position = pose.Position,
                                           Orientation = pose.Orientation,
                                           Scale = entry.RenderScale,
                                       };
        }

        return writeIndex;
    }

    /// <summary>Drops the Bepu simulation and every body; the next <see cref="Update"/> builds a fresh one.</summary>
    public void Release()
    {
        ReleaseSimulation();
    }

    public void Dispose()
    {
        ReleaseSimulation();
        _dispatcher?.Dispose();
        _dispatcher = null;
    }

    private void RebuildSimulation(float rate, int substeps, int iterations, bool multithreaded, bool deterministic)
    {
        ReleaseSimulation();

        _fixedRate = rate;
        _substeps = substeps;
        _solverIterations = iterations;
        _multithreaded = multithreaded;
        _deterministic = deterministic;

        if (multithreaded && _dispatcher == null)
            _dispatcher = new ThreadDispatcher(Math.Max(1, Environment.ProcessorCount - 1));

        _pool = new BufferPool();
        _materials.Clear();

        // Bepu reports worker indices below the dispatcher's thread count, or 0 without a dispatcher.
        _bounceRequests = new List<BounceRequest>[multithreaded ? _dispatcher.ThreadCount : 1];
        for (var index = 0; index < _bounceRequests.Length; index++)
            _bounceRequests[index] = new List<BounceRequest>();

        _simulation = Simulation.Create(_pool,
                                       new NarrowPhaseCallbacks { Materials = _materials, Settings = _settings, BounceRequests = _bounceRequests },
                                       new PoseIntegratorCallbacks { Settings = _settings },
                                       new SolveDescription(_solverIterations, _substeps),
                                       new DefaultTimestepper());
        _simulation.Deterministic = deterministic;
    }

    private void ReleaseSimulation()
    {
        _simulation?.Dispose();
        _simulation = null;

        _pool?.Clear();
        _pool = null;

        _bodies.Clear();
        _joints.Clear();
        _renderOrder.Clear();
        _dynamicRenderCount = 0;
        _materials.Clear();
    }

    private void ReconcileBodies()
    {
        _seenBodies.Clear();
        _renderOrder.Clear();
        _dynamicRenderCount = 0;

        foreach (var descriptor in _frameBodies)
        {
            var id = ResolveId(descriptor);
            _seenBodies.Add(id);

            if (!_bodies.TryGetValue(id, out var entry))
            {
                if (!descriptor.Enabled)
                    continue;

                entry = CreateBody(descriptor, id);
                if (entry == null)
                    continue;

                _bodies.Add(id, entry);
            }
            else
            {
                if (!descriptor.Enabled)
                {
                    RemoveBody(entry);
                    _bodies.Remove(id);
                    continue;
                }

                entry = UpdateBody(entry, descriptor, id);
                if (entry == null)
                {
                    _bodies.Remove(id);
                    continue;
                }

                _bodies[id] = entry;
            }

            entry.Friction = descriptor.Friction;
            entry.Bounciness = descriptor.Bounciness;
            entry.LinearDamping = Math.Max(0f, descriptor.LinearDamping);
            entry.AngularDamping = Math.Max(0f, descriptor.AngularDamping);
            entry.GravityScale = descriptor.GravityScale;

            if (entry.IsStatic)
                _materials.SetStatic(entry.StaticHandle.Value, entry.Friction, entry.Bounciness);
            else
                _materials.SetBody(entry.Handle.Value, entry.Friction, entry.Bounciness);

            _renderOrder.Add(entry);
            if (!entry.IsStatic)
                _dynamicRenderCount++;
        }

        _removeBodies.Clear();
        foreach (var pair in _bodies)
        {
            if (!_seenBodies.Contains(pair.Key))
                _removeBodies.Add(pair.Key);
        }

        foreach (var id in _removeBodies)
        {
            RemoveBody(_bodies[id]);
            _bodies.Remove(id);
        }
    }

    private BodyEntry UpdateBody(BodyEntry entry, PhysicsBody descriptor, Guid id)
    {
        var shapeKey = ShapeKey.From(descriptor);
        if (!entry.ShapeKey.Equals(shapeKey))
        {
            // Shape or mass changed: Bepu can't morph a collidable, so replace the body.
            RemoveBody(entry);
            return CreateBody(descriptor, id);
        }

        var orientation = NormalizeOrientation(descriptor.Orientation);
        var descriptorPosition = descriptor.Position;

        if (descriptorPosition != entry.LastInputPosition || orientation != entry.LastInputOrientation)
        {
            var pose = new RigidPose(descriptorPosition, orientation);
            ApplyPose(entry, pose);
            entry.LastInputPosition = descriptorPosition;
            entry.LastInputOrientation = orientation;
        }

        if (!entry.IsStatic
            && (descriptor.LinearVelocity != entry.LastInputLinearVelocity
                || descriptor.AngularVelocity != entry.LastInputAngularVelocity))
        {
            var reference = _simulation.Bodies[entry.Handle];
            reference.Velocity = new BodyVelocity(descriptor.LinearVelocity, descriptor.AngularVelocity);
            reference.Awake = true;
            entry.LastInputLinearVelocity = descriptor.LinearVelocity;
            entry.LastInputAngularVelocity = descriptor.AngularVelocity;
        }

        // A sleeping body ignores the pose integrator, so a gravity-scale change has to wake it
        // or the correction would only start once something else touches the body.
        if (entry.Kind == PhysicsBodyKind.Dynamic && MathF.Abs(descriptor.GravityScale - entry.GravityScale) > 0.0001f)
        {
            var reference = _simulation.Bodies[entry.Handle];
            reference.Awake = true;
        }

        return entry;
    }

    private BodyEntry CreateBody(PhysicsBody descriptor, Guid id)
    {
        var orientation = NormalizeOrientation(descriptor.Orientation);
        var pose = new RigidPose(descriptor.Position, orientation);
        var entry = new BodyEntry
                        {
                            Id = id,
                            Kind = descriptor.Kind,
                            IsStatic = descriptor.Kind == PhysicsBodyKind.Static,
                            ShapeKey = ShapeKey.From(descriptor),
                            PreviousPose = pose,
                            CurrentPose = pose,
                            LastInputPosition = descriptor.Position,
                            LastInputOrientation = orientation,
                            LastInputLinearVelocity = descriptor.LinearVelocity,
                            LastInputAngularVelocity = descriptor.AngularVelocity,
                            RenderScale = RenderScaleFor(descriptor),
                        };

        switch (descriptor.Shape)
        {
            case PhysicsShapeType.Sphere:
                CreateBodyWithShape(entry, descriptor, pose, new Sphere(MathF.Max(0.001f, descriptor.Radius)));
                break;

            case PhysicsShapeType.Capsule:
                CreateBodyWithShape(entry, descriptor, pose,
                                    new Capsule(MathF.Max(0.001f, descriptor.Radius), MathF.Max(0.001f, descriptor.Length)));
                break;

            case PhysicsShapeType.Cylinder:
                CreateBodyWithShape(entry, descriptor, pose,
                                    new Cylinder(MathF.Max(0.001f, descriptor.Radius), MathF.Max(0.001f, descriptor.Length)));
                break;

            default:
                CreateBodyWithShape(entry, descriptor, pose,
                                    new Box(MathF.Max(0.001f, descriptor.Size.X),
                                            MathF.Max(0.001f, descriptor.Size.Y),
                                            MathF.Max(0.001f, descriptor.Size.Z)));
                break;
        }

        return entry;
    }

    private void CreateBodyWithShape<TShape>(BodyEntry entry, PhysicsBody descriptor, RigidPose pose, TShape shape)
        where TShape : unmanaged, IConvexShape
    {
        if (descriptor.Kind == PhysicsBodyKind.Static)
        {
            entry.ShapeIndex = _simulation.Shapes.Add(shape);
            entry.StaticHandle = _simulation.Statics.Add(new StaticDescription(pose, entry.ShapeIndex));
            entry.StaticPose = pose;
            return;
        }

        var velocity = new BodyVelocity(descriptor.LinearVelocity, descriptor.AngularVelocity);
        var description = descriptor.Kind == PhysicsBodyKind.Kinematic
                              ? BodyDescription.CreateConvexKinematic(pose, velocity, _simulation.Shapes, shape)
                              : BodyDescription.CreateConvexDynamic(pose, velocity, MathF.Max(0.0001f, descriptor.Mass), _simulation.Shapes, shape);

        entry.Handle = _simulation.Bodies.Add(description);
        entry.ShapeIndex = description.Collidable.Shape;
    }

    private void ReconcileJoints()
    {
        _seenJoints.Clear();

        foreach (var descriptor in _frameJoints)
        {
            if (!descriptor.Enabled || descriptor.BodyA == null || descriptor.BodyB == null)
                continue;

            if (!_bodies.TryGetValue(ResolveId(descriptor.BodyA), out var bodyA)
                || !_bodies.TryGetValue(ResolveId(descriptor.BodyB), out var bodyB))
                continue;

            if (bodyA.IsStatic || bodyB.IsStatic)
            {
                if (!_hasLoggedStaticJointWarning)
                {
                    Log.Warning("PhysicsWorld: joints to static bodies are ignored. Use a Kinematic body as the anchor.", _logSource);
                    _hasLoggedStaticJointWarning = true;
                }

                continue;
            }

            var id = ResolveId(descriptor);
            _seenJoints.Add(id);
            var hash = JointHash(descriptor, bodyA.Handle, bodyB.Handle);

            if (_joints.TryGetValue(id, out var existing))
            {
                if (existing.Hash == hash)
                    continue;

                _simulation.Solver.Remove(existing.Handle);
                _joints.Remove(id);
            }

            var handle = AddConstraint(descriptor, bodyA.Handle, bodyB.Handle);
            _joints.Add(id, new JointEntry { Handle = handle, Hash = hash });
        }

        _removeJoints.Clear();
        foreach (var pair in _joints)
        {
            if (!_seenJoints.Contains(pair.Key))
                _removeJoints.Add(pair.Key);
        }

        foreach (var id in _removeJoints)
        {
            _simulation.Solver.Remove(_joints[id].Handle);
            _joints.Remove(id);
        }
    }

    private ConstraintHandle AddConstraint(PhysicsJoint descriptor, BodyHandle bodyA, BodyHandle bodyB)
    {
        var spring = new SpringSettings(Math.Clamp(descriptor.Frequency, 0.1f, 120f),
                                        Math.Clamp(descriptor.DampingRatio, 0.01f, 2f));

        switch (descriptor.Type)
        {
            case PhysicsJointType.Hinge:
                return _simulation.Solver.Add(bodyA, bodyB, new Hinge
                                                                  {
                                                                      LocalOffsetA = descriptor.LocalOffsetA,
                                                                      LocalHingeAxisA = NormalizeAxis(descriptor.HingeAxisA),
                                                                      LocalOffsetB = descriptor.LocalOffsetB,
                                                                      LocalHingeAxisB = NormalizeAxis(descriptor.HingeAxisB),
                                                                      SpringSettings = spring,
                                                                  });

            case PhysicsJointType.Weld:
                return _simulation.Solver.Add(bodyA, bodyB, new Weld
                                                                  {
                                                                      LocalOffset = descriptor.LocalOffsetB,
                                                                      LocalOrientation = Quaternion.Identity,
                                                                      SpringSettings = spring,
                                                                  });

            case PhysicsJointType.DistanceServo:
                return _simulation.Solver.Add(bodyA, bodyB,
                                              new DistanceServo(descriptor.LocalOffsetA, descriptor.LocalOffsetB,
                                                                descriptor.TargetDistance, spring));

            default:
                return _simulation.Solver.Add(bodyA, bodyB, new BallSocket
                                                                  {
                                                                      LocalOffsetA = descriptor.LocalOffsetA,
                                                                      LocalOffsetB = descriptor.LocalOffsetB,
                                                                      SpringSettings = spring,
                                                                  });
        }
    }

    private void Step(float dt)
    {
        foreach (var entry in _bodies.Values)
            entry.PreviousPose = entry.CurrentPose;

        ApplyDampingAndGravityScale(dt);
        _simulation.Timestep(dt, _multithreaded ? _dispatcher : null);
        ApplyBounce();

        foreach (var entry in _bodies.Values)
            ReadPose(entry);

        _simTime += dt;
    }

    /// <summary>
    /// Adds the missing restitution impulse to pairs that were closing fast and that the solver has just
    /// brought to rest, so slow resting contacts and bodies still approaching are left alone.
    /// </summary>
    private void ApplyBounce()
    {
        foreach (var requests in _bounceRequests)
        {
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                var inverseMassA = 0f;
                var inverseMassB = 0f;
                var velocityA = Vector3.Zero;
                var velocityB = Vector3.Zero;

                if (request.BodyA != NoBody)
                {
                    var body = _simulation.Bodies[new BodyHandle(request.BodyA)];
                    velocityA = body.Velocity.Linear;
                    inverseMassA = body.LocalInertia.InverseMass;
                }

                if (request.BodyB != NoBody)
                {
                    var body = _simulation.Bodies[new BodyHandle(request.BodyB)];
                    velocityB = body.Velocity.Linear;
                    inverseMassB = body.LocalInertia.InverseMass;
                }

                var approachSpeed = Vector3.Dot(velocityA - velocityB, request.Normal);
                var wasStopped = approachSpeed < request.ApproachSpeed * 0.5f;
                var missingSeparation = request.Bounciness * request.ApproachSpeed + approachSpeed;
                var totalInverseMass = inverseMassA + inverseMassB;
                if (!wasStopped || missingSeparation <= 0f || totalInverseMass <= 0f)
                    continue;

                var impulse = request.Normal * (missingSeparation / totalInverseMass);
                if (inverseMassA > 0f)
                {
                    var body = _simulation.Bodies[new BodyHandle(request.BodyA)];
                    body.Velocity.Linear -= impulse * inverseMassA;
                    body.Awake = true;
                }

                if (inverseMassB > 0f)
                {
                    var body = _simulation.Bodies[new BodyHandle(request.BodyB)];
                    body.Velocity.Linear += impulse * inverseMassB;
                    body.Awake = true;
                }
            }

            requests.Clear();
        }
    }

    private void ApplyDampingAndGravityScale(float dt)
    {
        foreach (var entry in _bodies.Values)
        {
            if (entry.IsStatic || entry.Kind != PhysicsBodyKind.Dynamic)
                continue;

            var reference = _simulation.Bodies[entry.Handle];
            if (!reference.Awake)
                continue;

            var velocity = reference.Velocity;
            var changed = false;

            if (entry.LinearDamping > 0)
            {
                velocity.Linear *= 1f / (1f + entry.LinearDamping * dt);
                changed = true;
            }

            if (entry.AngularDamping > 0)
            {
                velocity.Angular *= 1f / (1f + entry.AngularDamping * dt);
                changed = true;
            }

            // The pose integrator applies full gravity; this corrects the difference for bodies
            // with a custom scale. Applied as a velocity delta so sleeping bodies keep sleeping.
            if (MathF.Abs(entry.GravityScale - 1f) > 0.0001f)
            {
                velocity.Linear += _settings.Gravity * (entry.GravityScale - 1f) * dt;
                changed = true;
            }

            if (changed)
                reference.Velocity = velocity;
        }
    }

    private void ReadPose(BodyEntry entry)
    {
        entry.CurrentPose = entry.IsStatic ? entry.StaticPose : _simulation.Bodies[entry.Handle].Pose;
    }

    private void ApplyPose(BodyEntry entry, RigidPose pose)
    {
        if (entry.IsStatic)
        {
            var description = _simulation.Statics.GetDescription(entry.StaticHandle);
            description.Pose = pose;
            _simulation.Statics.ApplyDescription(entry.StaticHandle, description);
            entry.StaticPose = pose;
        }
        else
        {
            var reference = _simulation.Bodies[entry.Handle];
            reference.Pose = pose;
            reference.UpdateBounds();
            reference.Awake = true;
        }

        // A teleport should snap, not smear across one interpolation interval.
        entry.PreviousPose = pose;
        entry.CurrentPose = pose;
    }

    private void RemoveBody(BodyEntry entry)
    {
        // Constraints that survive the body removal would otherwise keep their old handles while
        // _joints forgets them, and the next reconcile would add a second copy of each. Remove
        // every constraint explicitly first; removing ones attached to this body is a no-op
        // because Bepu would have done the same during Bodies.Remove.
        foreach (var joint in _joints.Values)
            _simulation.Solver.Remove(joint.Handle);
        _joints.Clear();

        if (entry.IsStatic)
        {
            _simulation.Statics.Remove(entry.StaticHandle);
        }
        else
        {
            // Bodies.Remove also removes every constraint attached to the body, which
            // invalidates the handles cached in _joints.
            _simulation.Bodies.Remove(entry.Handle);
        }

        if (entry.ShapeIndex.Exists)
            _simulation.Shapes.RemoveAndDispose(entry.ShapeIndex, _pool);
    }

    private static RigidPose InterpolatePose(in RigidPose from, in RigidPose to, float t)
    {
        if (t <= 0f)
            return from;

        if (t >= 1f)
            return to;

        return new RigidPose(Vector3.Lerp(from.Position, to.Position, t),
                             Quaternion.Normalize(Quaternion.Slerp(from.Orientation, to.Orientation, t)));
    }

    private static Quaternion NormalizeOrientation(Quaternion orientation)
    {
        var lengthSquared = orientation.LengthSquared();
        return lengthSquared < 1e-8f ? Quaternion.Identity : Quaternion.Normalize(orientation);
    }

    private static Vector3 NormalizeAxis(Vector3 axis)
    {
        var lengthSquared = axis.LengthSquared();
        return lengthSquared < 1e-8f ? Vector3.UnitY : Vector3.Normalize(axis);
    }

    private static Vector3 RenderScaleFor(PhysicsBody descriptor)
    {
        switch (descriptor.Shape)
        {
            case PhysicsShapeType.Sphere:
                return new Vector3(descriptor.Radius * 2f);
            case PhysicsShapeType.Capsule:
                return new Vector3(descriptor.Radius * 2f, descriptor.Length + descriptor.Radius * 2f, descriptor.Radius * 2f);
            case PhysicsShapeType.Cylinder:
                return new Vector3(descriptor.Radius * 2f, descriptor.Length, descriptor.Radius * 2f);
            default:
                return descriptor.Size;
        }
    }

    private static int JointHash(PhysicsJoint descriptor, BodyHandle a, BodyHandle b)
    {
        var hash = new HashCode();
        hash.Add(descriptor.Type);
        hash.Add(a.Value);
        hash.Add(b.Value);
        hash.Add(descriptor.LocalOffsetA);
        hash.Add(descriptor.LocalOffsetB);
        hash.Add(descriptor.HingeAxisA);
        hash.Add(descriptor.HingeAxisB);
        hash.Add(descriptor.Frequency);
        hash.Add(descriptor.DampingRatio);
        hash.Add(descriptor.TargetDistance);
        return hash.ToHashCode();
    }

    private Guid ResolveId(PhysicsBody descriptor)
    {
        if (descriptor.Id != Guid.Empty)
            return descriptor.Id;

        // Defensive: a body built outside an operator has no id. Key it by reference so it
        // survives frames instead of being recreated every evaluation.
        if (!_implicitIds.TryGetValue(descriptor, out var id))
        {
            id = Guid.NewGuid();
            _implicitIds[descriptor] = id;
        }

        return id;
    }

    private Guid ResolveId(PhysicsJoint descriptor)
    {
        if (descriptor.Id != Guid.Empty)
            return descriptor.Id;

        if (!_implicitJointIds.TryGetValue(descriptor, out var id))
        {
            id = Guid.NewGuid();
            _implicitJointIds[descriptor] = id;
        }

        return id;
    }

    private readonly struct ShapeKey : IEquatable<ShapeKey>
    {
        private readonly PhysicsShapeType _shape;
        private readonly PhysicsBodyKind _kind;
        private readonly Vector3 _size;
        private readonly float _radius;
        private readonly float _length;
        private readonly float _mass;

        private ShapeKey(PhysicsBody descriptor)
        {
            _shape = descriptor.Shape;
            _kind = descriptor.Kind;
            _size = descriptor.Size;
            _radius = descriptor.Radius;
            _length = descriptor.Length;
            _mass = descriptor.Kind == PhysicsBodyKind.Dynamic ? descriptor.Mass : 0f;
        }

        public static ShapeKey From(PhysicsBody descriptor) => new(descriptor);

        public bool Equals(ShapeKey other)
        {
            return _shape == other._shape
                   && _kind == other._kind
                   && _size == other._size
                   && _radius.Equals(other._radius)
                   && _length.Equals(other._length)
                   && _mass.Equals(other._mass);
        }

        public override bool Equals(object obj) => obj is ShapeKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine((int)_shape, (int)_kind, _size, _radius, _length, _mass);
    }

    private sealed class BodyEntry
    {
        public Guid Id;
        public PhysicsBodyKind Kind;
        public bool IsStatic;
        public BodyHandle Handle;
        public StaticHandle StaticHandle;
        public TypedIndex ShapeIndex;
        public ShapeKey ShapeKey;

        public RigidPose PreviousPose;
        public RigidPose CurrentPose;
        public RigidPose StaticPose;

        public Vector3 LastInputPosition;
        public Quaternion LastInputOrientation;
        public Vector3 LastInputLinearVelocity;
        public Vector3 LastInputAngularVelocity;

        public Vector3 RenderScale = Vector3.One;
        public float Friction;
        public float Bounciness;
        public float LinearDamping;
        public float AngularDamping;
        public float GravityScale = 1f;
    }

    private sealed class JointEntry
    {
        public ConstraintHandle Handle;
        public int Hash;
    }

    private readonly struct PhysicsMaterial
    {
        public readonly float Friction;
        public readonly float Bounciness;

        public PhysicsMaterial(float friction, float bounciness)
        {
            Friction = Math.Clamp(friction, 0f, 4f);
            Bounciness = Math.Clamp(bounciness, 0f, 1f);
        }
    }

    /// <summary>
    /// Per-handle materials read from the narrow-phase callback. Handles are dense, so arrays
    /// are cheaper than dictionaries on the contact path. Only written between timesteps.
    /// </summary>
    private sealed class PhysicsMaterials
    {
        private PhysicsMaterial[] _bodies = [];
        private PhysicsMaterial[] _statics = [];
        private static readonly PhysicsMaterial DefaultMaterial = new(0.5f, 0f);

        public void Clear()
        {
            Array.Clear(_bodies);
            Array.Clear(_statics);
        }

        public void SetBody(int handle, float friction, float bounciness)
        {
            EnsureCapacity(ref _bodies, handle);
            _bodies[handle] = new PhysicsMaterial(friction, bounciness);
        }

        public void SetStatic(int handle, float friction, float bounciness)
        {
            EnsureCapacity(ref _statics, handle);
            _statics[handle] = new PhysicsMaterial(friction, bounciness);
        }

        public PhysicsMaterial Get(CollidableReference reference)
        {
            var materials = reference.Mobility == CollidableMobility.Static ? _statics : _bodies;
            var handle = reference.Mobility == CollidableMobility.Static ? reference.StaticHandle.Value : reference.BodyHandle.Value;
            return (uint)handle < (uint)materials.Length ? materials[handle] : DefaultMaterial;
        }

        private static void EnsureCapacity(ref PhysicsMaterial[] materials, int handle)
        {
            if (handle < materials.Length)
                return;

            Array.Resize(ref materials, Math.Max(handle + 1, Math.Max(4, materials.Length * 2)));
        }
    }

    private sealed class PhysicsSettings
    {
        public Vector3 Gravity = new(0f, -9.81f, 0f);
        public float ContactFrequency = 30f;
    }

    private struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        public PhysicsMaterials Materials;
        public PhysicsSettings Settings;

        /** One list per worker thread, so recording an impact needs no synchronization. */
        public List<BounceRequest>[] BounceRequests;

        public void Initialize(Simulation simulation)
        {
            _simulation = simulation;
        }

        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        {
            return true;
        }

        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        {
            return true;
        }

        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
                                                        out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            var materialA = Materials.Get(pair.A);
            var materialB = Materials.Get(pair.B);

            var friction = MathF.Sqrt(materialA.Friction * materialB.Friction);
            var bounciness = MathF.Max(materialA.Bounciness, materialB.Bounciness);

            // Speculative contacts stop a body exactly at the surface, so no spring setting can make it
            // rebound. Bounce is added after the step from the impacts recorded here.
            pairMaterial = new PairMaterialProperties(friction, 2f, new SpringSettings(Settings.ContactFrequency, 1f));

            if (bounciness > 0f && manifold.Count > 0)
                RecordImpact(workerIndex, pair, manifold.GetNormal(0), bounciness);

            return true;
        }

        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
                                             ref ConvexContactManifold manifold)
        {
            return true;
        }

        public void Dispose()
        {
        }

        private void RecordImpact(int workerIndex, CollidablePair pair, Vector3 normal, float bounciness)
        {
            var bodyA = ReadMotion(pair.A, out var velocityA, out var inverseMassA);
            var bodyB = ReadMotion(pair.B, out var velocityB, out var inverseMassB);

            // Bepu's manifold normal points from B to A; flipped, a positive value means the pair is closing.
            normal = -normal;
            var approachSpeed = Vector3.Dot(velocityA - velocityB, normal);
            if (approachSpeed < MinBounceSpeed)
                return;

            BounceRequests[workerIndex].Add(new BounceRequest
                                                {
                                                    BodyA = bodyA,
                                                    BodyB = bodyB,
                                                    Normal = normal,
                                                    ApproachSpeed = approachSpeed,
                                                    Bounciness = bounciness,
                                                });
        }

        private int ReadMotion(CollidableReference reference, out Vector3 velocity, out float inverseMass)
        {
            if (reference.Mobility == CollidableMobility.Static)
            {
                velocity = Vector3.Zero;
                inverseMass = 0f;
                return NoBody;
            }

            var body = _simulation.Bodies[reference.BodyHandle];
            velocity = body.Velocity.Linear;
            inverseMass = body.LocalInertia.InverseMass;
            return reference.BodyHandle.Value;
        }

        private Simulation _simulation;
    }

    /// <summary>A pair that was closing fast when its contact was configured, to be given a rebound once the step has resolved it. The normal points from A to B.</summary>
    private struct BounceRequest
    {
        public int BodyA;
        public int BodyB;
        public Vector3 Normal;
        public float ApproachSpeed;
        public float Bounciness;
    }

    private struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        public PhysicsSettings Settings;

        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.ConserveMomentum;
        public bool AllowSubstepsForUnconstrainedBodies => false;

        // Kinematic bodies are moved by their velocity input, so their poses must integrate.
        public bool IntegrateVelocityForKinematics => true;

        public void Initialize(Simulation simulation)
        {
        }

        public void PrepareForIntegration(float dt)
        {
        }

        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
                                      BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex,
                                      Vector<float> dt, ref BodyVelocityWide velocity)
        {
            var gravity = Settings.Gravity;
            var hasMass = Vector.GreaterThan(localInertia.InverseMass, Vector<float>.Zero);

            velocity.Linear.X = Vector.ConditionalSelect(hasMass, velocity.Linear.X + new Vector<float>(gravity.X) * dt, velocity.Linear.X);
            velocity.Linear.Y = Vector.ConditionalSelect(hasMass, velocity.Linear.Y + new Vector<float>(gravity.Y) * dt, velocity.Linear.Y);
            velocity.Linear.Z = Vector.ConditionalSelect(hasMass, velocity.Linear.Z + new Vector<float>(gravity.Z) * dt, velocity.Linear.Z);
        }
    }

    private const float MaxTimeJump = 0.5f;
    private const float MaxFrameDelta = 0.1f;

    /** Closing speed in m/s below which a bounce is skipped, so resting contacts do not jitter. */
    private const float MinBounceSpeed = 1f;

    private const int NoBody = -1;

    private readonly object _logSource;
    private Simulation _simulation;
    private BufferPool _pool;
    private ThreadDispatcher _dispatcher;
    private List<BounceRequest>[] _bounceRequests = [];
    private readonly PhysicsSettings _settings = new();
    private readonly PhysicsMaterials _materials = new();
    private readonly Dictionary<Guid, BodyEntry> _bodies = new();
    private readonly Dictionary<Guid, JointEntry> _joints = new();
    private readonly Dictionary<PhysicsBody, Guid> _implicitIds = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PhysicsJoint, Guid> _implicitJointIds = new(ReferenceEqualityComparer.Instance);
    private readonly List<BodyEntry> _renderOrder = new();
    private readonly HashSet<Guid> _seenBodies = new();
    private readonly HashSet<Guid> _seenJoints = new();
    private readonly List<Guid> _removeBodies = new();
    private readonly List<Guid> _removeJoints = new();
    private List<PhysicsBody> _frameBodies;
    private List<PhysicsJoint> _frameJoints;

    private float _fixedRate = 60f;
    private int _substeps = 2;
    private int _solverIterations = 4;
    private bool _multithreaded = true;
    private bool _deterministic;
    private double _accumulator;
    private double _lastTimeSeconds = double.NaN;
    private float _simTime;
    private float _interpolationAlpha = 1f;
    private int _dynamicRenderCount;
    private bool _hasLoggedStaticJointWarning;
    private bool _wasResetPressed;
}
