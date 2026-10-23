# Laser Safety

Show lasers are not normal stage equipment: the beam concentrates enough optical power into your eye to cause permanent damage *before you can blink*. Read this page before enabling a real output. This is practical orientation, not a substitute for your local regulations (in the EU: EN 60825-1 and EN 60825-3 laser safety standards; in the US: FDA/CDRH rules) and venue-specific requirements.

## Before first light

* **Never** power up a projector pointing at people. Set it up aimed at a wall/ceiling in an empty room first.
* Check the projector's **interlock and key switch** work; know where the **emergency stop** is before enabling output. E-stops are a normal part of the chain - use a physical one too.
* Reduce the projector's **power/attenuation** while programming. You almost never need full power for content work.

## Audience scanning

Beam effects that cross the audience are legally regulated *audience scanning* in most countries and require:

* projectors certified and aligned for it (scanners with fail-safe behavior, deflection failure detection),
* exposure calculations/measurement (MPE - maximum permissible exposure),
* trained personnel and documented safety cases.

TiXL cannot guarantee any of this for you. As a rule of thumb for DIY setups: **keep the beam above the audience and above camera height**.

## While programming with TiXL

* Keep the app-bar **LASER** master button off until everything else is verified - it defaults to off on every editor start, and nothing transmits while it is off.
* New laser outputs start **uncalibrated**: content is ignored and only a tiny alignment rectangle streams. Aim it on the wall, extend it step by step, and only then switch **Calibrated** on.
* Use blanked/low-intensity test frames first; check what the projector actually shows against what the software thinks (the output's live frame preview and the plug's status line).
* Remember that scan failures smear the beam into a stationary blob: if the scanners stop (or your point data collapses to a point), a full-power stationary beam burns. The optimization blanking (see [Laser optimization](LaserOptimizer.md)) reduces - not removes - this risk.
* Never disable hardware safety features (interlocks, E-stop, key switches) "just to test".

## Software safety built into TiXL

Three layers of software safety ship with the laser output. They reduce risk from bad content and software stalls, but they **cannot replace a hardware scan-fail interlock** - software cannot cut beam power if the scanners physically stop.

**The output's Safety settings** (Output Setup - select the laser output):

* **Calibrated gate**: while off, upstream content is ignored and only the setup rectangle streams.
* **Safe zone**: hard-clips all points into a configurable region of the field (build audience keep-out areas).
* **Jump limiting**: subdivides over-long jumps so scanners track linearly instead of overshooting.
* **Static-dwell blanking**: blanks a beam that sits near-motionless too long (the classic scan-fail hazard).
* **Show brightness ceiling**: hard cap on R/G/B regardless of what content requests.
* **Beam enable**: per-output master switch; off blanks every point but keeps the geometry.

**The laser plug's device settings** (Output Setup - select the plug under Local Bindings):

* **Brightness ceiling**: intensity ceiling applied to the literal points right before encoding, inside the transport - so no upstream bug can exceed it.

**The app-bar LASER master button**:

* Defaults to **off** on every editor start; nothing transmits until you click it.
* A click drops all transmission - every projector goes dark.
* It stays orange while any laser output is still uncalibrated: the alignment rectangle, not show content, is what streams.

**The frame watchdog** (built into the transports, always active):

* If no fresh frames arrive for about a second (a stalled graph, a paused project), the transports stop re-sending the last frame and blank the beam instead of leaving a frozen lit picture on the projector.

## Checklist for shows

1. Mechanical aiming locked; beam path clear of people and reflective surfaces.
2. E-stop reachable from the operating position, tested under load.
3. The LASER master button off by default at editor start - arm deliberately, every session.
4. Someone responsible for safety while the laser is on.
5. Local regulations checked (notification/permits may be required).
