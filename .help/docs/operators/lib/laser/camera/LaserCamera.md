# LaserCamera

*in [Lib.laser.camera](README.md)*

**Universal geometry-to-laser converter and a standard TiXL camera in one.**

LaserCamera combines the functionality of a regular Camera op with geometry capture for laser output:
- It renders the scene connected to its `Scene` input through its own lens (camera dropdown, mouse control)
- Every point/line/mesh drawn inside that scene is captured and encoded into the same laser frame
- Dots, line segments, and mesh wireframes are merged with Group/transform scopes applied

## Key Features

**Dual Purpose:**
- **Camera functionality:** Renders scenes and provides standard camera operations (position, target, field of view, mouse dragging)
- **Laser functionality:** Captures scene geometry and converts it to laser points for projection

**Flexible Inputs:**
- Direct point, line, and mesh buffers work alongside scene capture
- Older patches using Camera ops continue to work unchanged
- Supports both laser output setup and legacy camera ops

**Laser Output:**
- Projects captured geometry into laser coordinate system
- Converts to 16-bit ILDA format (0..32767)
- Supports individual colors per point/line/edge
- Handles perspective projection from camera view
- Compatible with EtherDream, PONK, and other laser outputs

**Setup Integration:**
- Uses output setup laser outputs to drive venue-aligned projection
- Includes calibration solve when available
- Supports manual look-at configuration in output setup
- Automatically aligns laser frame with physical stage geometry

## Usage

**Basic Laser Projection:**
```
LaserCamera
  → Scene (your scene to render and capture)
  → OutputRef (laser output from output setup)
```

**Legacy Camera + Laser:**
```
LaserCamera
  → Camera (optional external camera op)
  → Scene
  → OutputRef (for laser alignment)
```

**Laser-Only (Direct Inputs):**
```
LaserCamera
  → PointBuffer (particle clouds)
  → LineBuffer (stroke lines)
  → Mesh (wireframe geometry)
  → OutputRef (laser output for alignment)
```

## Output Types

- **LaserPoints:** Structured list of laser points with X, Y coordinates and RGB colors (16-bit each)
- **PointCount:** Number of points in the current laser frame
- **SampleX/SampleY:** Up to 10 sample point positions for debugging
- **Output:** Command output for pipeline control
- **Reference:** Camera reference (for connection to external camera ops)

## Notes

- **Lighting efficiency:** Uses dot dwell repeats for brighter, more stable dots
- **Projection quality:** Maintains perspective correction for accurate laser projection
- **Performance:** Scene capture and rendering happen simultaneously in one pass
- **Compatibility:** Works with existing patches using standard Camera ops
- **Safety:** Includes MaxDotDwellPoints (64) limit to stay within laser output capabilities

**See also:** [PONKInput](../input/PONKInput.md) and the laser outputs in the [Output Setup](../../../../using/OutputSetup.md)

---
*Generated from operator definition in Lib.laser.camera*