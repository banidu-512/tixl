---
id: output-setup-laser
title: Output Setup — Laser Output, Devices and Calibration
scope: output-window
tags: [laser]
added: 2026-10-25
added-in-version: 4.4
prerequisites:
  - A writable project is open whose active setup has one laser output, with a [LaserCamera] placed in the graph and its OutputRef set to that output.
  - A laser device, or a bench substitute - a PONK endpoint with Multicast off and any unicast address is enough to see the plug connect without hardware.
  - The graph window, the Parameter window and one output window with the Flow Outliner shown are visible.
related-help:
  - ../.help/docs/using/OutputSetup.md
---

Covers the laser output card end to end: the aim rectangle before calibration, **Send**, the
scanner camera's calibration solve, live device settings, and several outputs on one plug.

## Step: An uncalibrated output streams the aim rectangle

**Action:**
With the laser output selected, click the **LASER** button in the app bar, then look at the output's card.

**Expected:**
- The **LASER** button turns armed and stays orange (an uncalibrated laser output exists).
- **Laser frame** shows a green rectangle at the size and place of the **Safety → setup rectangle** values, not the camera's scene.
- The device bound under **Local Bindings** reports that it is streaming.

## Step: Send mutes the output and keeps the device

**Action:**
Untick **Send** at the top of the laser output card, wait a second, then tick it again.

**Expected:**
- While unticked the device stops receiving frames and the card says so, but the plug stays connected - no discovery or reconnect status appears.
- Ticking it again resumes streaming within a frame or two; nothing about the binding changes.

## Step: A laser camera freezes like a send

**Action:**
Select the [LaserCamera] in the flow outliner's CONTENT column, untick **Update** on its card, then animate something in its **Scene** (or move an object in it).

**Expected:**
- The outliner item reads "laser · frozen" and the card names the output it streams to with its point count.
- **Laser frame** and the device keep showing the last frame; the scene's changes no longer reach the laser, and the editor's frame time drops.
- Ticking **Update** again resumes at once. The change is a normal undoable edit of the camera's **Update** input.

## Step: A camera reference takes over the laser camera's aim

**Action:**
1. Add a [Camera] to the graph and wire its **Reference** output into the [LaserCamera]'s **CameraReference** input.
2. Move the [Camera]'s **Position** and **Target** - or use an [OrbitCamera] with some **Spin Rate** instead.
3. Move the [LaserCamera]'s own **Position** and **Target**, then toggle its **Use Rotation**.

**Expected:**
- The **Laser frame** follows the referenced camera live, including while it animates; the laser camera's own aim inputs change nothing while the reference resolves.
- The drawn view frame sits on the referenced camera, and the laser camera's position/target gizmo handles disappear - the aim belongs to the referenced camera.
- The frame stays **square** (the scanner field): a 16:9 camera gives a square crop of its view, not a horizontally squeezed picture.
- Removing the connection returns the camera to its own aim. A connected output that is not a camera makes the node report `Camera reference is not usable - using this op's own aim`.

## Step: The calibration solve replaces the manual aim

**Action:**
1. In **Scanner Camera**, note the current **Laser frame** drawing and the **Position/Target/Scan angle** rows.
2. Under **Calibration**, click **Add Point** six times.
3. For each row, set **Stage (m)** to a mark in the venue and **Field** to where the beam hits it - use at least two non-parallel planes (a wall and the floor, or two walls).
4. Click **Solve**, then look at the frame and at the graph's camera view.
5. Click **Clear Solve**, then `Ctrl+Z` twice.

**Expected:**
- With fewer than six points **Solve** is disabled and the card says how many more are needed.
- After **Solve** the card reads `Calibration solve active (residual N px) - it overrides the manual aim`, and the **Laser frame** drawing changes to the solved camera's view (the manual Position/Target rows no longer describe it).
- The residual is small (a few pixels) when the entered field positions match where the beam actually landed.
- **Clear Solve** returns to the manual aim at once, and the points stay in the list. `Ctrl+Z` restores the solve, another `Ctrl+Z` clears it again.

## Step: Device settings apply while streaming

**Action:**
With the laser output streaming, select the bound laser plug under **Local Bindings** and drag its **Brightness ceiling** down to about half.

**Expected:**
- The plug reconnects once, about a quarter second after the drag ends (not once per frame while dragging), and then streams again.
- The device's output is dimmer with the new ceiling; raising it back brightens the beam without a restart.

## Step: Two outputs share one plug

**Action:**
Add a second laser output (`+` on **OUTPUTS** → **Laser**), then drag it onto the same laser plug in **LOCAL BINDINGS**.

**Expected:**
- Both laser outputs stay listed under that plug - the first one is not unbound.
- The plug reports one connection; each output keeps its own frame (**Laser frame** differs per output).

## Step: A dense frame stays real time

**Action:**
Feed the [LaserCamera] a dense mesh scene (`SphereMesh` with the default 64 × 32 segments into a [DrawMesh], its command into the camera's **Scene** input), confirm its **OutputRef** names the calibrated laser output, and watch the editor's frame rate and the **Laser frame** preview for ten seconds.

**Expected:**
- The editor stays at interactive frame rate (well above 30 fps on a laptop-class GPU) - the frame has about 19 000 points and the optimizer must not stall it.
- The preview animates smoothly and the device keeps streaming; no `frame watchdog` warning appears.
