# Physics

The physics package is three operators and nothing else: bodies and joints are **pure data
producers**, and one `PhysicsWorld` operator owns the actual Bepu simulation. The world collects the
descriptions from the surrounding graph, creates/updates/removes the Bepu bodies and constraints,
steps them at a fixed rate, interpolates the poses for rendering and publishes everything as a point
buffer.

That split is why `RigidBody` and `RigidJoint` have no simulation state of their own: a body's id is
fixed for the lifetime of the operator instance, which is how the world recognises the same body
across evaluations and only rebuilds what actually changed. Joints become solver constraints between
two bodies; a static body cannot be a joint anchor, so use a kinematic body as the fixed point
instead. A body is rebuilt only when its shape, kind or mass changes, and a joint whose settings
change is removed and re-added, so a constraint is never duplicated.

## PhysicsWorld

Owns the Bepu simulation for the whole graph. It gathers the `RigidBody` and `RigidJoint`
descriptions, builds or updates the world, advances it at `FixedRate` with optional substepping, and
publishes the resulting poses as a point buffer that `DrawMeshAtPoints` instances can render. `SimTime`
is the simulated seconds since the last rebuild, useful for syncing or debugging.

- `Gravity` is the world gravity vector (default is Earth-like).
- `FixedRate` is the simulation step rate in Hz; `Substeps` splits each step for stability.
- `SolverIterations` trades solver quality against cost.
- `TimeScale` slows or speeds the simulation without changing the graph time.
- `Interpolate` smooths rendered poses between fixed steps.
- `IncludeStatics` includes static bodies in the published buffer.
- `Multithreaded` and `Deterministic` control the solver's threading/determinism.
- `ContactStiffness` tunes contact softness.
- `Reset` rebuilds the simulation state; `Paused` freezes stepping while keeping the graph running.

*Robustness:* a non-finite input from an upstream graph (NaN or Infinity) cannot poison the
simulation — gravity keeps its last valid value, and `FixedRate`, `TimeScale` and `ContactStiffness`
fall back to their defaults before being clamped. An exception inside the world is logged once and
resets the outputs instead of escaping into the evaluator.

**Outputs:** `OutBuffer` (per-body pose data for the mesh renderers), `BodyCount`, `SimTime`.

*Tip:* the simulation advances with `context.Playback.FxTimeInBars`, so it runs while playback is
stopped — pump frames to see motion.

![PhysicsWorld selected in the bouncing balls example](shots/PhysicsWorld-ui.png)
*Gravity, solver and stepping parameters with the live example.*

![PhysicsWorld node and its body buffer](shots/PhysicsWorld-graph.png)
*The world collects the bodies and feeds the draw operator.*

![Bouncing balls example](shots/example-PhysicsBouncingBallsExample.png)
*Five dynamic bodies above a static floor.*

## RigidBody

Describes one Bepu body: shape, kind, transform, size, mass and material parameters. The operator
publishes the description; `PhysicsWorld` turns it into a Bepu body and keeps it in sync. The body id
is fixed for the lifetime of this instance, so the world recognises the same body across evaluations.

- `Shape` selects box, sphere, capsule, cylinder and the other supported primitives.
- `Kind` is dynamic, kinematic or static.
- `Position`, `Rotation` place the body; `Size`, `Radius`, `Length` dimension it.
- `Mass` sets the dynamic mass (kinematic/static ignore it).
- `LinearVelocity` / `AngularVelocity` give it an initial push.
- `Friction` and `Bounciness` are the contact material.
- `LinearDamping` / `AngularDamping` bleed off motion.
- `GravityScale` scales gravity for this body (0 = floaty, negative = inverted).
- `Enabled` removes it from the simulation without deleting the operator.

**Output:** `OutBody` — the description consumed by `PhysicsWorld` and by joints.

*Tip:* render the bodies by connecting instances of the same mesh through `TransformMesh` to
`DrawMeshAtPoints`, or draw a single mesh and let the buffer place it per body.

![RigidBody selected in the bouncing balls example](shots/RigidBody-ui.png)
*One of the five ball descriptions.*

![RigidBody node](shots/RigidBody-graph.png)
*A body wired into `PhysicsWorld`.*

![The simulated balls](shots/example-PhysicsBouncingBallsExample.png)
*Bodies rendered at the world's published poses.*

## RigidJoint

Declares a constraint between two bodies: **BallSocket**, **Hinge**, **Weld** or **DistanceServo**.
Like `RigidBody` it is pure data — `PhysicsWorld` creates, updates and removes the Bepu constraint.
Both bodies must be dynamic or kinematic; a static body cannot be an anchor, so use a kinematic body
as the fixed point.

- `BodyA` / `BodyB` are the two constrained bodies.
- `JointType` selects the constraint.
- `LocalOffsetA` / `LocalOffsetB` are the anchor points in each body's local space; for a ball socket
  they are the same world point seen from each body.
- `HingeAxisA` / `HingeAxisB` are the hinge axes in each body's local space.
- `Frequency` / `DampingRatio` tune the servo's spring.
- `TargetDistance` is the rest length for the distance servo.
- `Enabled` removes the constraint without deleting the operator.

**Output:** `OutJoint`. Chain several joints to build ropes, bridges, ragdolls and mechanisms.

![RigidJoint selected in the rope bridge example](shots/RigidJoint-ui.png)
*A hinge constraint between two bridge planks.*

![RigidJoint node](shots/RigidJoint-graph.png)
*Joints linking the bridge segments.*

![Rope bridge example](shots/example-PhysicsRopeBridgeExample.png)
*The constraint chain holding the sagging bridge.*
