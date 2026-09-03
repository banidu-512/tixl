# Laser Safety

Show lasers are not normal stage equipment: the beam concentrates enough optical power into your eye to cause permanent damage *before you can blink*. Read this page before enabling a real output. This is practical orientation, not a substitute for your local regulations (in the EU: EN 60825-1 and EN 60825-3 laser safety standards; in the US: FDA/CDRH rules) and venue-specific requirements.

## Before first light

* **Never** power up a projector pointing at people. Set it up aimed at a wall/ceiling in an empty room first.
* Check the projector's **interlock and key switch** work; know where the **emergency stop** is before enabling output. The [[EtherDreamOutput]] `ClearEStop` input exists because E-stops are a normal part of the chain - use a physical one too.
* Reduce the projector's **power/attenuation** while programming. You almost never need full power for content work.

## Audience scanning

Beam effects that cross the audience are legally regulated *audience scanning* in most countries and require:

* projectors certified and aligned for it (scanners with fail-safe behavior, deflection failure detection),
* exposure calculations/measurement (MPE - maximum permissible exposure),
* trained personnel and documented safety cases.

TiXL cannot guarantee any of this for you. As a rule of thumb for DIY setups: **keep the beam above the audience and above camera height**.

## While programming with TiXL

* Use `SimulationMode` (both output operators) until everything else is verified.
* Use blanked/low-intensity test frames first; check what the projector actually shows against what the software thinks (`StatusMessage`, `BufferFullness`, `PrintToLog`).
* Remember that scan failures smear the beam into a stationary blob: if the scanners stop (or your point data collapses to a point), a full-power stationary beam burns. Blanking (`I = 0`) handling in [[LaserOptimizer]] reduces - not removes - this risk.
* Never disable hardware safety features (interlocks, E-stop, key switches) "just to test".

## Checklist for shows

1. Mechanical aiming locked; beam path clear of people and reflective surfaces.
2. E-stop reachable from the operating position, tested under load.
3. Software outputs disabled by default at startup (`Enable` off / `SimulationMode` on).
4. Someone responsible for safety while the laser is on.
5. Local regulations checked (notification/permits may be required).
