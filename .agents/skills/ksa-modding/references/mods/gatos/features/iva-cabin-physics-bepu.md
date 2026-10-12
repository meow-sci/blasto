---
type: Mod Feature
title: IVA cabin physics in an own Bepu world
description: Make loose cabin props float, slam aft under thrust and fling under rotation by simulating them in a separate BepuPhysics Simulation in the vessel's assembly frame, driven by the vessel's accelerometer and body rates and written back onto IVA SubPart transforms
mod: gatos
category: physics
techniques: [bepu-physics, custom-physics, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types:
  - BepuPhysics.Simulation
  - BepuUtilities.Memory.BufferPool
  - KSA.Vehicle
  - KSA.Part
  - KSA.PartModelModule
  - KSA.MeshReference
  - KSA.JobSystems
  - KSA.ConstraintSim
tags: [physics, bepu, iva, subparts, fictitious-forces, accelerometer, interiors]
---

`/sim/debug/iva` adopts IVA props (cups, clipboards, the cat) as free-floating rigid bodies: they
drift while coasting, settle on the floor on the pad, slam aft during a burn and swing around
during RCS rotation, colliding with the real interior surfaces. Instead of adding bodies to KSA's
vehicle physics, gatOS runs its own small Bepu simulation per vessel and only moves render-side
SubParts. It is off by default; with the master switch off nothing is allocated.

## How it works

**Why a separate world** (repo analysis, [scope/ksa-runtime-coupling.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/scope/ksa-runtime-coupling.md#L356-L380)): each KSA
vehicle is one dynamic Bepu body with a coarse compound collider approximating the outside, and IVA
interior parts declare no colliders. A cabin body inside that collider would be ejected through the
hull and would push the spacecraft (dynamic-dynamic contacts always apply); the vehicle constraint
simulation is not stepped for a coasting vessel; and it runs on solver worker threads. `ConstraintSim`
also shares a static `GlobalShapes` across the game's solver threads
([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L86-L88)), which gatOS never touches.

**The world** ([gatOS.GameMod/Game/Ksa/Iva/CabinSim.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/CabinSim.cs)): per vessel, a new `BufferPool` and
`Simulation.Create(pool, narrowPhase, poseIntegrator, new SolveDescription(8, 1))` against KSA's
embedded BepuPhysics 2.5, in the vessel's **assembly frame**, where the interior is static geometry
that never moves. `Timestep` is called without an `IThreadDispatcher`, so every Bepu callback runs on
the calling thread. Fixed substeps with a per-frame cap; sleeping is enabled, and every body is woken
when the forcing field changes (a settled prop would otherwise stay asleep through launch).

**The forcing field** ([gatOS.SimFs/Iva/CabinPhysics.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Iva/CabinPhysics.cs#L36-L65)): game-free and unit-tested.
For an object at `r_b = r - CoM` with cabin velocity `v`,
`a = -a_p - alpha x r_b - 2 omega x v - omega x (omega x r_b)` where `a_p` is the proper
acceleration (`Vehicle.AccelerationBody`, an accelerometer reading), `omega` is `BodyRates` and
`alpha` is `AngularAccelerationBody`. Gravity never appears explicitly: on the pad `a_p` is 1 g up,
in orbit it is zero, under thrust objects slam aft. The pose integrator callbacks apply it.

**Interior collision mesh** ([gatOS.GameMod/Game/Ksa/Iva/InteriorGeometry.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/InteriorGeometry.cs)): built from
parts and subparts whose `PartModelModule.Template.Internal` is true, using
`MeshReference.PositionCompare` (a de-indexed `double3[]` triangle soup KSA keeps for part raycasts)
transformed into the assembly frame, excluding adopted objects. Cached per vessel and rebuilt only on a
part-count change or adopt/release, never on a timer.

**Driving SubParts** ([gatOS.GameMod/Game/Ksa/Iva/FloatingObject.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/FloatingObject.cs#L96-L141)): each body's
pose is written onto the adopted SubPart's `PositionParentAsmb` and `Asmb2ParentAsmb` (both setters
reset KSA's cached matrices). Only SubParts are allowed, because KSA serializes top-level part
transforms into saves; release restores the captured rest pose. Rendering, lighting, ray tracing and
IVA visibility come from the game for free.

## Lifecycle and timing

- `IvaPhysicsManager.Update` runs from `DrivePostSolver` in `[StarMapAfterGui]` (or
  `[StarMapAfterOnFrame]` when the HUD is hidden), right after the welds driver
  ([gatOS.GameMod/Game/Ksa/Iva/IvaPhysicsManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/IvaPhysicsManager.cs#L303-L360)).
- It calls `JobSystems.VehicleSolver.Wait()` first. gatOS's comments say this makes
  `AccelerationBody`, `BodyRates`, `AngularAccelerationBody` and `CenterOfMassAsmb` "the settled
  values for this step"; in the decomp those main-thread fields change only when
  `Vehicle.UpdateFromTaskResultsUnsynchronized` copies a finished step in during the next frame's
  `ApplyVehicleSolvers` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2531)), so the reads return the step applied at the
  start of this frame with or without the wait. What the wait does buy is that the SubPart pose
  writes land while no solver worker is running; its cost is a main-thread stall until the in-flight
  step finishes, paid only while the feature is active.
- The cabin step uses the frame `dt` (wall-clock), because cabin motion is visual, not part of the
  flight simulation.
- Objects park (velocities zeroed, poses held) while paused, under time warp, in the editor and
  outside the IVA camera.

## Key code

| Path | Role |
|---|---|
| [gatOS.SimFs/Iva/CabinPhysics.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Iva/CabinPhysics.cs) | the forcing field, pure math |
| [gatOS.GameMod/Game/Ksa/Iva/CabinSim.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/CabinSim.cs) | own Bepu simulation, substeps, sleep and wake, impacts |
| [gatOS.GameMod/Game/Ksa/Iva/CabinCallbacks.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/CabinCallbacks.cs) | narrow-phase and pose-integrator callbacks |
| [gatOS.GameMod/Game/Ksa/Iva/InteriorGeometry.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/InteriorGeometry.cs) | interior collision mesh from IVA meshes |
| [gatOS.GameMod/Game/Ksa/Iva/FloatingObject.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/FloatingObject.cs) | SubPart driver and restore |
| [gatOS.GameMod/Game/Ksa/Iva/IvaPhysicsManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Iva/IvaPhysicsManager.cs) | registry, park logic, per-frame driver |
| [plans/IVA_MOVEMENTS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/plans/IVA_MOVEMENTS.md) | design and decomp evidence |

## Snippets

```csharp
// gatOS.SimFs/Iva/CabinPhysics.cs L54-L65
    public static double3Snap ApparentAcceleration(
        double3Snap properAccel, double3Snap bodyRates, double3Snap angularAccel,
        double3Snap offsetFromCom, double3Snap velocity)
    {
        var euler = Cross(angularAccel, offsetFromCom);
        var coriolis = Scale(Cross(bodyRates, velocity), 2.0);
        var centrifugal = Cross(bodyRates, Cross(bodyRates, offsetFromCom));
        return new double3Snap(
            -properAccel.X - euler.X - coriolis.X - centrifugal.X,
            -properAccel.Y - euler.Y - coriolis.Y - centrifugal.Y,
            -properAccel.Z - euler.Z - coriolis.Z - centrifugal.Z);
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/Iva/FloatingObject.cs L130-L141
        var scale = parent.Scale.X;
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;

        var local = double3.Transform(positionVehicle - parent.PositionVehicleAsmb,
            doubleQuat.Conjugate(parentOrientation)) / scale;
        if (!IsFinite(local))
            return;

        Part.PositionParentAsmb = local;
        Part.Asmb2ParentAsmb = doubleQuat.Concatenate(orientation, doubleQuat.Conjugate(parentOrientation));
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Simulation.Create`, `Simulation.Timestep(float, IThreadDispatcher = null)` | [current/decomp/BepuPhysics/Simulation.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics/Simulation.cs#L158-L288) | own world, single-threaded step |
| `Vehicle.AccelerationBody`, `AngularAccelerationBody`, `BodyRates`, `CenterOfMassAsmb` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L511-L575) | forcing-field inputs |
| `JobSystems.VehicleSolver.Wait()` | [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs) | join the in-flight vehicle step before writing SubPart poses |
| `MeshReference.PositionCompare` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L55-L55) | interior triangles |
| `Part.PositionParentAsmb`, `Asmb2ParentAsmb`, `SubParts` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs) | SubPart pose writes |
| `ConstraintSim` | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L86-L88) | the vehicle physics gatOS deliberately avoids |

## Pitfalls and version notes

- Never add bodies to KSA's vehicle constraint simulation for cosmetic physics; it is coarse, on worker
  threads, and couples back into flight.
- Never move top-level parts for effects: their transforms are saved.
- A change in an external acceleration field is invisible to Bepu's sleep logic; wake bodies yourself.
- In-game validation is listed as pending in the repo.

## See also

- [Welds that pin one vessel to another](welds-vessel-anchoring.md)
- [Per-vessel scale and force-render cheats](scale-and-force-render-cheats.md)
- [Vessel flight-state reads](vessel-flight-state-reads.md)
- [Frame hooks with an F2-proof fallback](frame-hooks-and-f2-fallback.md)
- [gatOS overview](../overview.md)
- [Single-threaded Timestep with own BufferPool in the assembly frame](../../../howto/physics/colliders/run-your-own-bepu-simulation/run-your-own-bepu-simulation.md) - how-to article demonstrating this feature

