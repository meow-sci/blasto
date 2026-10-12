---
type: Mod Feature
title: Body-of-revolution aerodynamics
description: Replaces stock bounding-box drag with an axisymmetric cylinder model that gives angle-of-attack drag, lift with a stall, tail-first lift and a transonic drag rise, evaluated per substep inside the vehicle solver
mod: ksaero
category: physics
techniques: [harmony-transpiler, custom-physics, reflection]
hooks: [harmony-transpiler KSA.PhysicsStates.ComputeDerivatives]
lifecycle: [physics:vehicle-solver]
game_types:
  - KSA.PhysicsStates
  - KSA.PhysicsEnvironment
  - KSA.VehicleProperties
  - KSA.KinematicStates
  - KSA.Disturbances
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateTask
  - BepuPhysics.Collidables.Box
tags: [physics, aerodynamics, lift, drag, mach, vehicle-solver, worker-threads]
---

With KSAero installed, a rocket flying nose-first through the air sees far less drag than stock (the
README's 50 m by 3.7 m launcher goes from a stock C_D of 7.38 to 0.435 on the frontal area), drag rises
smoothly with angle of attack instead of from the first degree, the body produces lift that stalls
between 20 and 30 degrees, a booster flying engine-first lifts towards the side its engine end is
tilted, a capsule flying heat shield first gets an Apollo-like L/D of about -0.2 to -0.3, and
zero-alpha drag peaks just past Mach 1. Rolling the vehicle changes the direction of the lift, never its size.[^ksaero-readme]

Mechanism in one line: the rewritten `ComputeDrag` call site
([ComputeDrag call-site transpiler](computedrag-call-site-transpiler.md)) lands in
`AirForcePatch.ComputeAirForces`, which turns the vehicle's bounding box into a cylinder, evaluates
`AeroModel.Evaluate` and adds the resulting body-axis force to the `Disturbances` accumulator the
integrator consumes.[^ksaero-airforcepatch]

## How it works

### What stock does (and what is replaced)

`VehicleProperties.RecomputeAerodynamicProperties` builds a box model: face areas
`pi/4 dy dz` on X, `dx dz` on Y, `dx dy` on Z; drag coefficients 0.3 on the +X face, 1.0 on the -X face,
1.2 on every side; `TotalSurfaceArea` is twice the sum of the three areas
([current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L115-L130)).[^ksa-vehicleproperties]
`BoundingBoxCdA.ComputeCdA` dots the velocity direction with those per-face CdA values
([current/decomp/KSA/BoundingBoxCdA.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoundingBoxCdA.cs)).[^ksa-boundingboxcda]
`PhysicsStates.ComputeDrag` then applies `(CdA + 0.1 * surfaceArea) * q` against the airspeed, through
the centre of mass, divided by a semi-implicit limiter, plus a rotational damping torque
([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1111)).[^ksa-physicsstates]
There is no lift and no Mach dependence. KSAero keeps the gate, the limiter idea and the torque, and
replaces the force.

### Geometry: an equivalent cylinder

`AirForcePatch.BodyFor` reads `VehicleProperties.BoundingBoxAsmb` (a Bepu `Box` whose `HalfWidth`,
`HalfHeight`, `HalfLength` are the X, Y, Z half extents) and calls `AeroBody.FromBoxExtents(dx, dy, dz)`
([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L135-L139),
[current/decomp/BepuPhysics.Collidables/Box.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics.Collidables/Box.cs#L11-L15)).[^bepu-box]
The cylinder lies along assembly +X (KSA's long and thrust axis, nose at +X) with length `L = dx` and
diameter `D = sqrt(dy dz)`, which keeps stock's frontal area `pi/4 dy dz` and therefore stock's nose-on
pressure drag ([KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L7-L43)).[^ksaero-aeromodel] Derived areas:

| Quantity | Formula | Used for |
|---|---|---|
| `ReferenceArea` | `pi D^2 / 4` | Every coefficient is on this area |
| `PlanformArea` | `L D` | Crossflow normal force |
| `WettedArea` | `pi D L` | Skin friction |
| `Fineness` | `L / D` | Finite-length factor, tail-first attached-lift fade |

### Coefficients: `AeroModel.Evaluate`

Allen and Perkins' viscous-crossflow method for bodies of revolution as extended to 0-180 degrees by
Jorgensen, with `a` measured from whichever end leads
([KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L127-L215)):

| Term | Expression in code |
|---|---|
| Leading angle | `a = atan2(sqrt(uy^2 + uz^2), abs(ux))`; tail-first when `ux < 0` |
| Attached (slender-body) normal force | `N_att = share * sin(2a) * cos(a/2)`; `share = 1` nose-first, `SmoothStep(1, 2, L/D)` tail-first |
| Separated crossflow normal force | `N_x = eta * c_dc(M sin a) * (A_p / A_ref) * sin^2 a` |
| Finite-length factor | `eta = Lerp(eta(L/D), 1, SmoothStep(0.8, 1.2, M sin a))` |
| Axial force | `C_A = (Cd0 * f(M) + Cf * S_wet / A_ref) * cos^2 a` |
| Lift | `C_L = (s(a) N_att + N_x) cos a - C_A sin a` |
| Drag | `C_D = (N_att + N_x) sin a + C_A cos a` |
| Stall share | `s(a) = Lerp(1, 0, SmoothStep(20 deg, 30 deg, a))` |
| Transonic factor | `M <= 1.05`: `Lerp(1, peak, SmoothStep(0.8, 1.05, M))`; above: `sup + (peak - sup) / (1 + x^2)`, `x = (M - 1.05) / 0.6` |
| Body-axis force | `ForceX = -C_D ux + C_L sign sin a`; `-C_D cross - C_L cos a` split along `(uy, uz) / cross` |

The stall removes only the lift share of the attached term; its drag stays, as on a separated body.
The force vector's length is `hypot(C_L, C_D)` and it carries exactly those two coefficients, which the
test project checks ([Headless model tests](headless-model-tests.md)).

### Parameters (`AeroParameters`)

All tunables are plain constants on one page
([KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L70-L125)):

| Constant | Value | Meaning |
|---|---|---|
| `Gamma` | 1.4 | Ratio of specific heats for the speed of sound |
| `CdNoseFirst`, `CdTailFirst` | 0.30, 1.00 | Zero-alpha pressure plus base drag; stock's +X and -X face values |
| `SkinFrictionCf` | 0.0025 | Turbulent skin friction on `pi D L` (stock: 0.1 on the whole box surface) |
| `TailFirstAttachedStartFineness`, `...FullFineness` | 1.0, 2.0 | Tail-first attached lift fades in over this L/D range |
| `StallAngleDeg`, `StallWidthDeg` | 20, 10 | Attached lift share fades out from 20 to 30 degrees |
| `PostStallAttachedFraction` | 0.0 | Attached lift share left after the stall |
| `DragRiseStartMach`, `DragPeakMach`, `DragDecayWidthMach` | 0.8, 1.05, 0.6 | Shape of the transonic factor |
| `NoseFirstPeakFactor`, `NoseFirstSupersonicFactor` | 2.0, 1.3 | Transonic multipliers on `CdNoseFirst` |
| `TailFirstPeakFactor`, `TailFirstSupersonicFactor` | 1.7, 1.6 | Transonic multipliers on `CdTailFirst` |
| `CrossflowMach` / `CrossflowCd` | 0, 0.4, 0.7, 1.0, 1.3, 1.6, 2.0, 3.0, 5.0 / 1.2, 1.2, 1.55, 2.0, 1.85, 1.7, 1.55, 1.42, 1.36 | Infinite-cylinder crossflow Cd against crossflow Mach, linear interpolation, clamped |
| `FinenessGrid` / `FinenessEta` | 1, 2, 4, 6, 10, 20, 40 / 0.55, 0.57, 0.60, 0.62, 0.66, 0.74, 0.82 | Allen and Perkins' finite-length factor |
| `EtaBlendStartMach`, `EtaBlendEndMach` | 0.8, 1.2 | `eta` blends to 1 across this crossflow Mach range |

### Force application: `AirForcePatch.ComputeAirForces`

Receives exactly what stock `ComputeDrag` would have received
([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L99-L133)):

1. Invalid cylinder: forward to stock (see [Stock ComputeDrag delegate fallback](stock-drag-delegate-fallback.md)).
2. `q = 0.5 rho v^2`; return when it is nearly zero.
3. Velocity direction into body axes with `kinematic.Body2Phys.Inverse()`; Mach from the game's pressure
   and density (see [Mach number from the game atmosphere](mach-from-game-atmosphere.md)).
4. `exposed = surfaceArea / props.TotalSurfaceArea`, clamped to 0..1: stock passes the surface area still
   above water, so a half-submerged vehicle feels half the air.
5. `F = q * A_ref * exposed * (ForceX, ForceY, ForceZ)`, then stock's semi-implicit limiter applied to
   the whole vector, `F *= 1 / (1 + |F| dt / (m v))`, so one substep can never reverse the airspeed.
6. `disturbances.AddForceBody(double3.Zero, F)`: zero lever arm, so no torque from lift or drag
   ([current/decomp/KSA/Disturbances.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Disturbances.cs#L22-L33)).[^ksa-disturbances]
7. Stock's rotational damping reproduced with stock's constant 0.1:
   `0.1 * surfaceArea * 0.5 rho (|omega| R)^2` against the body rates, `R` from
   `ComputeBoundingSphereRadiusAsmb`.

The `dragCdA` argument (stock's box CdA) is ignored except on the fallback path.

## Lifecycle and timing

**Where it runs.** `ComputeAirForces` runs on the vehicle solver's worker threads, never on the main
thread, once (constrained substep) or twice (Verlet substep) per vehicle per substep, for every vehicle
in air. Verified call chain in build 2026.10.10.5554:

| Step | Thread | Code |
|---|---|---|
| 1. `Program.PrepareFrame` waits for the previous step, applies it, then calls `Universe.ExecuteNextVehicleSolvers` | Main | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2220) |
| 2. `ExecuteNextVehicleSolvers` queues the `VehicleUpdateTask` on `JobSystems.VehicleSolver` and starts it | Main | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2138) |
| 3. `VehicleSolver` is a one-runner `JobScheduler` at `ThreadPriority.Highest`; `VehicleWorkerPool` is a `DynamicWorkerPool` | Setup | [current/decomp/KSA/JobSystems.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/JobSystems.cs#L40-L52) |
| 4. `VehicleUpdateTask.Run` builds contact islands and runs them as a `ParallelBatch` on `VehicleWorkerPool` | VehicleSolver | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L283) |
| 5. `ContactIsland.Execute` calls `PhysicsBubble.RunStep` (or `RunIsland`); exceptions go to `MarkFailed` | VehicleWorker | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L58-L81) |
| 6. `RunStep` (or a `BubbleStepSegmentJob` inside a multi-bubble island) -> `StepFrameTo`. A bubble with two or more vehicles goes straight to `StepFullPhysicsTo` -> `FullPhysicsStepUntil`, a loop of substeps. A lone vehicle goes through `ApplySingleVehicleMotion`: on rails it takes one analytic segment, but it drops to full physics when it is off rails (manoeuvring, rolling, sailing, dragging) or when its freefall is inside, or will enter, the atmosphere (`FreefallNeedsFullPhysics`), so every vehicle in air reaches the force function | VehicleWorker | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L688-L714), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1352), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1123-L1149) |
| 7. Per substep: a freefall vehicle in the atmosphere is switched off rails, an `UpdateModulesJob` runs per vehicle, then `FullPhysicsConstrainedStep` when `ConstraintSim.IsAnyConstrained`, else `FullPhysicsUnconstrainedStep` | VehicleWorker | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1458-L1461), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1636-L1645) |
| 8a. Constrained: `ReadOnlyPhysicsStates.ComputeDerivatives` once per vehicle per substep, serially on the bubble's thread, before the Bepu timesteps of at most 1/60 s; the force it returns is held for all of them | VehicleWorker | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2189-L2207) |
| 8b. Unconstrained: `FullPhysicsUnconstrainedStep` builds one `UnconstrainedVehicleStepJob` per vehicle and runs the leader (heaviest vehicle) first, then the rest, each through `ForEachVehicleParallel` (inline under two jobs, else a `ParallelBatch`) -> `RunUnconstrainedVehicleStep` | VehicleWorker(s) | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2292-L2323), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1354-L1370) |
| 9. Manoeuvring, rolling, sailing or dragging vehicles integrate with `IntegrateVelocityVerlet`, or with `IntegrateRk8` when the substep is over 2 s and `CanTakeLongSteps` holds | VehicleWorker | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2385-L2407) |
| 10. Velocity Verlet calls `ComputeDerivatives` twice per substep: at the start state and again after moving and `RecomputePositionalEnvironmentValues` | VehicleWorker | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1121-L1194) |
| 11. RK8 calls it once per Dormand-Prince stage (12 per step, through `Rk8BodyDerivatives.Evaluate`), but only when `CanTakeLongSteps`, which requires a CCI bubble frame and `!InPhysicsRadius`, so the drag branch is never taken there | VehicleWorker | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L37-L49), [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1196-L1199), [current/decomp/KSA/DormandPrince8.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DormandPrince8.cs#L103-L118) |
| 12. Instance forwarders -> static `PhysicsStates.ComputeDerivatives` -> drag block -> rewritten call -> `ComputeAirForces` | VehicleWorker | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L951-L1017), [current/decomp/KSA/ReadOnlyPhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ReadOnlyPhysicsStates.cs#L116-L119) |
| 13. Next frame: `Universe.ApplyVehicleSolvers` -> `VehicleUpdateTask.ApplyResultsToMainThread` -> `Vehicle.UpdateFromTaskResultsUnsynchronized` copies kinematics, props and environment onto the main-thread `Vehicle` | Main, plus `VehicleWorkerPool`: the copy runs as a `ParallelBatch` of `BubbleApplyResultsJob`s that the waiting main thread also helps execute | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L258), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2460) |

"VehicleWorker" in the thread column means any `VehicleWorkerPool` thread or the `VehicleSolver`
runner: a thread that waits on a `ParallelBatch` dequeues and executes pool items itself
(`DynamicWorkerPool.AwaitBatch`,
[current/decomp/KSA.Concurrency/DynamicWorkerPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Concurrency/DynamicWorkerPool.cs#L174-L189)).
The main thread never runs the game's own step: its only batch on that pool is the apply in step 13,
after `JobSystems.VehicleSolver.Wait()`.[^ksa-dynamicworkerpool] [^ksa-dormandprince8]

Sources for the chain:[^ksa-program] [^ksa-universe] [^ksa-jobsystems] [^ksa-vehicleupdatetask] [^ksa-physicsbubble] [^ksa-readonlyphysicsstates] [^ksa-vehicle]

The air branch itself only runs when `environment.InPhysicsRadius`, `AtmosphericDensity > 0`, stock
CdA `> 0`, and some surface area or volume is above water
([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L993-L1017)).

**What that imposes on the replacement.**

- **Thread-safe and re-entrant.** Different bubbles run on different `VehicleWorker` threads, and
  vehicles inside one bubble run in parallel in the unconstrained step. `ComputeAirForces` touches
  only its arguments, `const`s, `static readonly` tables it never writes, and a delegate set once at
  startup.
- **Use only the arguments.** The `in` structs are the worker's own copies of the vehicle state for
  this substep (or a probe copy, see below). Reading `Program.ControlledVehicle` or a `Vehicle`'s
  properties here would read the main-thread copy instead of the state being integrated: it is one
  step stale, and it belongs to the main thread, which keeps running its frame while the step executes.
- **Allocation-free.** The call rate is up to vehicles x substeps x 2 per frame. Everything is `double` and
  struct math; no LINQ, boxing, string formatting or logging.
- **Never throw.** An exception is caught by the bubble job wrapper (`IBubbleJob` or
  `ContactIsland.Execute`) and handed to `PhysicsBubble.MarkFailed`, which logs
  `Update task failed for vehicle(s): ...` as an error and marks the bubble failed. A failed bubble
  skips `PublishResults`, so `ApplyResultsToMainThread` neither applies nor rethrows its step
  (`RethrowApplyFailure` only covers failures in the apply job itself): the main-thread `Vehicle`s in
  that bubble do not advance that frame, and the error repeats every frame the throw does
  ([current/decomp/KSA/IBubbleJob.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IBubbleJob.cs#L10-L20),
  [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L58-L81),
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1072-L1121),
  [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L258)).[^ksa-ibubblejob]
  KSAero has no `try`/`catch` on this path; it avoids throwing instead: it returns early on near-zero
  `q`, guards the Mach denominator with `Math.Max(a, 1e-9)`, and `Evaluate` guards a zero direction
  and a zero crossflow component.
- **Deterministic and side-effect free.** Verlet evaluates the force function at two states per
  substep, the end state of one substep is evaluated again at the start of the next, and other mods
  call `PhysicsStates.ComputeDerivatives` on probe copies: KSA-AdvancedFlightComputer reads
  its drag table this way ([AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs#L334)).
  No caching keyed on "the vehicle", no counters.[^afc-aerosweep]
- **No ImGui, Vulkan or logging.** Diagnostics go through the main-thread readout, which re-evaluates
  the same pure function (see [Aero readout window](aero-readout-window.md)).
- **Respect `dt`.** Substep length varies and nothing in the solver shortens it for drag: an off-rails
  vehicle's substep is capped at 2 s and then by rotation rate, ocean buoyancy and parachutes
  (`PhysicsStates.UpdateSituationAndComputeTimestep`,
  [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L541-L599)), plus the flight-computer tick and the frame
  boundary. The semi-implicit limiter, which uses the `dt` it is given, is what keeps a long substep in
  dense air from reversing the airspeed.

**Why this point.** `ComputeDerivatives` is the one force function behind every integrator path
(Verlet, the Bepu constrained step, RK8) and behind other mods' predictions, so one rewritten call
changes the forces everywhere at once.

## Key code

| Path | Role |
|---|---|
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L99-L133) | `ComputeAirForces`: fallback, q, body-axis direction, Mach, force, limiter, damping torque |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L135-L139) | `BodyFor`: bounding box to `AeroBody` |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L7-L43) | `AeroBody`: cylinder and derived areas |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L45-L68) | `AeroCoefficients` |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L70-L125) | `AeroParameters` |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L156-L215) | `AeroModel.Evaluate` |
| [KSAero/AeroModel.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroModel.cs#L217-L260) | Stall share, transonic factor, table interpolation, `SmoothStep`, `Lerp` |

## Snippets

```csharp
// KSAero/AirForcePatch.cs L110-L133
        double speedSquared = airVelocityBub.LengthSquared();
        double q = 0.5 * density * speedSquared;
        if (q.IsNearlyZero())
            return;

        double speed = Math.Sqrt(speedSquared);
        double3 dirBody = (airVelocityBub / speed).Transform(kinematic.Body2Phys.Inverse());
        double mach = speed / Math.Max(AeroModel.SpeedOfSound(environment.AtmosphericPressure, density), 1e-9);
        AeroCoefficients c = AeroModel.Evaluate(in body, dirBody.X, dirBody.Y, dirBody.Z, mach);

        // Stock passes the surface area still out of the water, so a half-submerged vehicle feels half the air.
        double exposed = props.TotalSurfaceArea > 0f ? Math.Clamp(surfaceArea / props.TotalSurfaceArea, 0.0, 1.0) : 1.0;
        double3 force = q * body.ReferenceArea * exposed * new double3(c.ForceX, c.ForceY, c.ForceZ);

        // Stock's semi-implicit limiter, applied to the whole force: one substep can never reverse the airspeed.
        double mass = props.TotalMass;
        force *= 1.0 / (1.0 + force.Length() * dt / (mass * speed));
        disturbances.AddForceBody(double3.Zero, force);

        double3 bodyRates = PhysicsStates.GetBodyRates(in origin, in kinematic, in environment);
        double rimSpeed = bodyRates.Length() * props.ComputeBoundingSphereRadiusAsmb();
        double rotationalQ = 0.5 * density * rimSpeed * rimSpeed;
        disturbances.AddPureTorqueBody(StockSkinDragCoefficient * surfaceArea * rotationalQ * -bodyRates.NormalizeOrZero());
    }
```

```csharp
// KSAero/AeroModel.cs L168-L191
        bool tailFirst = ux < 0.0;
        double cosLead = Math.Abs(ux);
        double sinLead = cross;
        double lead = Math.Atan2(sinLead, cosLead);

        // sin(2a) cos(a/2), written out so it needs no trig past the atan2 above.
        double attachedShare = tailFirst
            ? SmoothStep(AeroParameters.TailFirstAttachedStartFineness, AeroParameters.TailFirstAttachedFullFineness, body.Fineness)
            : 1.0;
        double attached = attachedShare * 2.0 * sinLead * cosLead * Math.Sqrt(0.5 * (1.0 + cosLead));

        double crossMach = mach * sinLead;
        double eta = Lerp(FinenessEta(body.Fineness), 1.0,
            SmoothStep(AeroParameters.EtaBlendStartMach, AeroParameters.EtaBlendEndMach, crossMach));
        double crossflow = eta * CrossflowCd(crossMach) * (body.PlanformArea / body.ReferenceArea) * sinLead * sinLead;

        double pressureDrag = tailFirst
            ? AeroParameters.CdTailFirst * TransonicFactor(mach, AeroParameters.TailFirstPeakFactor, AeroParameters.TailFirstSupersonicFactor)
            : AeroParameters.CdNoseFirst * TransonicFactor(mach, AeroParameters.NoseFirstPeakFactor, AeroParameters.NoseFirstSupersonicFactor);
        double skin = AeroParameters.SkinFrictionCf * body.WettedArea / body.ReferenceArea;
        double axial = (pressureDrag + skin) * cosLead * cosLead;

        double lift = (AttachedFraction(lead) * attached + crossflow) * cosLead - axial * sinLead;
        double drag = (attached + crossflow) * sinLead + axial * cosLead;
```

```csharp
// KSAero/AeroModel.cs L217-L234
    /// <summary>s(a): how much of the attached-flow lift survives at leading-end angle a.</summary>
    public static double AttachedFraction(double leadingAngle)
    {
        double start = AeroParameters.StallAngleDeg * DegToRad;
        double end = (AeroParameters.StallAngleDeg + AeroParameters.StallWidthDeg) * DegToRad;
        return Lerp(1.0, AeroParameters.PostStallAttachedFraction, SmoothStep(start, end, leadingAngle));
    }

    /// <summary>Multiplier on the zero-alpha pressure and base drag at Mach M.</summary>
    public static double TransonicFactor(double mach, double peak, double supersonic)
    {
        if (mach <= AeroParameters.DragPeakMach)
            return Lerp(1.0, peak, SmoothStep(AeroParameters.DragRiseStartMach, AeroParameters.DragPeakMach, mach));

        // Zero slope at the peak, then a slow 1/x^2 settle rather than an exponential cliff.
        double x = (mach - AeroParameters.DragPeakMach) / AeroParameters.DragDecayWidthMach;
        return supersonic + (peak - supersonic) / (1.0 + x * x);
    }
```

```csharp
// current/decomp/KSA/PhysicsStates.cs L1089-L1111
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void ComputeDrag(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props, in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density, double dragCdA, double surfaceArea, double dt)
	{
		double num = airVelocityBub.LengthSquared();
		double num2 = 0.5 * density * num;
		if (!num2.IsNearlyZero())
		{
			double num3 = Math.Sqrt(num);
			double3 double5 = -airVelocityBub / num3;
			double num4 = (dragCdA + 0.1 * surfaceArea) * num2;
			double num5 = props.TotalMass;
			double num6 = num4 * dt / (num5 * num3);
			num4 /= 1.0 + num6;
			double3 value = num4 * double5;
			double3 forceBody = value.Transform(kinematic.Body2Phys.Inverse());
			disturbances.AddForceBody(double3.Zero, forceBody);
			double3 bodyRates = GetBodyRates(in origin, in kinematic, in environment);
			double num7 = bodyRates.Length() * (double)props.ComputeBoundingSphereRadiusAsmb();
			double num8 = 0.5 * density * num7 * num7;
			double3 torqueBody = 0.1 * surfaceArea * num8 * -bodyRates.NormalizeOrZero();
			disturbances.AddPureTorqueBody(torqueBody);
		}
	}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `PhysicsStates.ComputeDerivatives` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956-L1087) | Host of the rewritten call; supplies all arguments |
| `PhysicsStates.ComputeDrag` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1111) | Stock model being replaced; signature template |
| `PhysicsStates.GetBodyRates(in, in, in)` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L152-L156) | Body rates for the damping torque |
| `KinematicStates.Body2Phys` | [current/decomp/KSA/KinematicStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KinematicStates.cs#L12) | Rotates the airspeed direction into body axes[^ksa-kinematicstates] |
| `VehicleProperties.BoundingBoxAsmb`, `TotalSurfaceArea`, `TotalMass`, `ComputeBoundingSphereRadiusAsmb` | [current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L24-L56) | Geometry, exposed fraction, limiter, damping radius |
| `VehicleProperties.RecomputeAerodynamicProperties`, `AerodynamicCdABody` | [current/decomp/KSA/VehicleProperties.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleProperties.cs#L115-L130) | Stock box model; still computed, still gates the call |
| `BoundingBoxCdA.ComputeCdA` | [current/decomp/KSA/BoundingBoxCdA.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoundingBoxCdA.cs) | Stock CdA passed as `dragCdA` (ignored by KSAero) |
| `Disturbances.AddForceBody`, `AddPureTorqueBody` | [current/decomp/KSA/Disturbances.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Disturbances.cs#L22-L33) | Accumulate force and torque for the integrator |
| `BepuPhysics.Collidables.Box.HalfWidth`, `HalfHeight`, `HalfLength` | [current/decomp/BepuPhysics.Collidables/Box.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/BepuPhysics.Collidables/Box.cs#L11-L15) | Bounding box half extents on X, Y, Z |
| `PhysicsBubble`, `VehicleUpdateTask`, `JobSystems` | see the call-chain table | Where and on which threads the code runs |

## Pitfalls and version notes

- **Bounding box is the only geometry.** Side boosters, fins and wings widen the box and the model
  flies a fat cylinder. `D = sqrt(dy dz)` treats an elliptical section as round.[^ksaero-readme]
- **No aero torques.** Forces act through the centre of mass, so nothing weathervanes and lift does
  not trim the vehicle; only stock's rotational damping torque remains.
- **Every vehicle.** The patch changes the force for everything the solver steps, including EVA
  kittens (`KittenEva` derives from `Vehicle`,
  [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L13)) and debris.[^ksa-kitteneva]
- **Limiter scales lift too.** Stock limits the drag magnitude; KSAero limits the whole vector.
- **Ocean is untouched.** A partly submerged vehicle gets stock water drag plus KSAero air drag on the
  exposed fraction.
- **Much less ascent drag** means higher speeds low in the atmosphere and a higher max-Q. The stock
  structural limit constant is `VehicleStructuralLimits.MaxDynamicPressure = 200000.0` Pa
  ([current/decomp/KSA/VehicleStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleStructuralLimits.cs#L15)).[^ksa-structurallimits]
- **Readout differs slightly.** The UI shows coefficients times `q A_ref` without the exposed fraction
  or the limiter.
- Checked against build 2026.10.10.5554; the mod targets 5541.

## See also

- [ComputeDrag call-site transpiler](computedrag-call-site-transpiler.md)
- [Mach number from the game atmosphere](mach-from-game-atmosphere.md)
- [Stock ComputeDrag delegate fallback](stock-drag-delegate-fallback.md)
- [Aero readout window](aero-readout-window.md)
- [Headless model tests](headless-model-tests.md)
- [Run code inside the physics step on worker threads](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) - how-to article demonstrating this feature
- [Call-site transpiler on ComputeDerivatives writing the Disturbances accumulator](../../../howto/physics/aero-and-ground/replace-the-stock-aerodynamic-force-model/replace-the-stock-aerodynamic-force-model-1.md) - how-to article demonstrating this feature
- [Pure, allocation-free, never-throwing code over the in arguments](../../../howto/physics/solver/write-worker-thread-safe-solver-patches/write-worker-thread-safe-solver-patches-1.md) - how-to article demonstrating this feature

[^ksaero-readme]: KSAero/README.md
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksa-vehicleproperties]: KSA.VehicleProperties (decompiled, build 2026.10.10.5554)
[^ksa-boundingboxcda]: KSA.BoundingBoxCdA (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^bepu-box]: BepuPhysics.Collidables.Box (decompiled, build 2026.10.10.5554)
[^ksaero-aeromodel]: KSAero/AeroModel.cs
[^ksa-disturbances]: KSA.Disturbances (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-jobsystems]: KSA.JobSystems (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-readonlyphysicsstates]: KSA.ReadOnlyPhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^afc-aerosweep]: AdvancedFlightComputer/Features/Guidance/Adapters/KsaAeroSweep.cs
[^ksa-kitteneva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-structurallimits]: KSA.VehicleStructuralLimits (decompiled, build 2026.10.10.5554)
[^ksa-kinematicstates]: KSA.KinematicStates (decompiled, build 2026.10.10.5554)
[^ksa-ibubblejob]: KSA.IBubbleJob (decompiled, build 2026.10.10.5554)
[^ksa-dynamicworkerpool]: KSA.Concurrency.DynamicWorkerPool (decompiled, build 2026.10.10.5554)
[^ksa-dormandprince8]: KSA.DormandPrince8 (decompiled, build 2026.10.10.5554)
