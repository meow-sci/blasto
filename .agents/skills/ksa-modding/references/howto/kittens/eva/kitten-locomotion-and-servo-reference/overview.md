---
type: Game Feature
title: Kitten locomotion, modes and the servo, from the decompiled game
description: A reference for how a walking, floating or swimming kitten is simulated - the IsKitten flag on the worker snapshot, LocomotionMode and LocomotionState, KittenLocomotion.Advance and its steppers, the KittenServoPrecomp traction and upright gains, wake rules and the tuning struct copied to the worker
category: kittens
group: eva
topic: kitten-locomotion-and-servo-reference
demonstrated_by:
  - mods/unscience/features/iron-man-kitten-rocket-flight
  - mods/unscience/features/kitten-animations-locomotion-tuning
  - mods/ksastructures/features/first-person-kitten-view
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver, physics:bepu, frame:draw-ui, frame:render]
game_types:
  - KSA.KittenLocomotion
  - KSA.KittenServoPrecomp
  - KSA.LocomotionMode
  - KSA.LocomotionState
  - KSA.LocomotionFacts
  - KSA.LocomotionCommand
  - KSA.KittenLocomotionTuning
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateState
  - KSA.PoseIntegratorCallbacks
  - KSA.KittenEva
  - KSA.KittenTuningWindow
  - KSA.KittenRenderable
  - KSA.Rendering.AnimationPairBlendSampler
tags: [kitten, locomotion, servo, physics, bepu, tuning, reference]
---
An EVA kitten is a real vessel in the physics bubble, but it does not behave like a rocket: the game gives it a character controller. On every physics timestep (the bubble splits a step into slices of at most 1/60 s of simulated time, so a frame can run several) a small state machine (`KittenLocomotion.Advance`) decides what mode the kitten is in and what it wants to do; the result is turned into a handful of gains (`KittenServoPrecomp`) that the Bepu pose integrator applies every substep as velocity corrections, so the kitten walks, stays upright, turns toward its motion, jumps, swims and climbs ladders without being an animated mesh pushed through the world. This page is a reference to that machinery as it stands in build 2026.10.10.5554, written for mods that patch around it (the first-person mods patch `StepGrounded`, Iron Man flips `IsKitten`) or tune it. It is from the decompile only; mods listed under "demonstrated by" show where they hook in.

## Engine background

### Where the kitten branches live

Worker code branches on the boolean `VehicleUpdateState.IsKitten`, set on the main thread in `PrepareFromVehicle` as `ReadOnlyVehicle is KittenEva`, not on the CLR type ([current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L306)). The branches:

| Where | What the flag does | Source |
|---|---|---|
| `PhysicsBubble.AdvanceKittenLocomotion` | Builds `LocomotionFacts`, runs `KittenLocomotion.Advance`, updates the servo impulse budget, applies the MMU attitude target | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1969-L2013) |
| `PhysicsBubble` step loop | Computes `KittenServoPrecomp.Compute` per substep batch before `constraintSim.Simulate`, then reads back and advances locomotion after it | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2225-L2252) |
| `PoseIntegratorCallbacks` | Applies traction, jump, vertical damping and the upright/facing servos as velocity changes; skips the generic terrain angular damping when the servo owns the angular rate | [current/decomp/KSA/PoseIntegratorCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PoseIntegratorCallbacks.cs#L75-L150) |
| `PhysicsBubble.KittenWantsWake` | Keeps a kitten off rails while it has input or is not parked on a ladder | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1800-L1818) |
| `PhysicsBubble.FlightComputerInputsFor` | Zeroes thruster commands for kittens outside MMU mode | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1822-L1829) |
| `PhysicsBubble` structural check | 2.5 times the G-load allowance (`KITTEN_STRENGTH_MULTIPLIER`) | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1001-L1008) |
| `PartFailure.Detect` | Kittens are skipped | [current/decomp/KSA/PartFailure.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartFailure.cs#L47) |
| `ConstraintSim` | Kittens stay awake longer (`MinimumTimestepsUnderThreshold` 32 instead of 255) | [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L356) |

The main thread feeds the worker in `KittenEva.PrepareWorker`: it copies the main camera's forward, right and up (in CCE) into `CharacterControlInputs`, runs ladder detection, and passes `in KittenLocomotionTuning.Current` to `PrepareCharacterWorkerState`, which stores a **copy** in the snapshot ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L333), [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L308-L312)).

### Modes

`LocomotionMode` has seven values ([current/decomp/KSA/LocomotionMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocomotionMode.cs)): `Mmu`, `Grounded`, `Airborne`, `Tumbling`, `Rightening`, `Ladder`, `Swimming`. `KittenLocomotion.DeriveMode` chooses the next mode from `LocomotionFacts` (terrain contact, speeds, upright alignment, support ratio, time) and the tuning; defaults in brackets are from `KittenLocomotionTuning.Default` ([current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L30-L105)):

| From | To | Condition |
|---|---|---|
| any non-Ladder | Swimming | no terrain contact for `CoyoteTime` (0.15 s), `SupportRatio >= SwimEnterSupportRatio` (0.4), and not in MMU |
| any non-Ladder | Tumbling | terrain contact and tangential speed `>= TumbleSpeedGate` (7 m/s) |
| Grounded | Tumbling | contact and tilted past `TipOverTiltDeg` (60 degrees) |
| Grounded | Airborne | jump fired, or no contact for `CoyoteTime` |
| Airborne, Mmu | Grounded | contact and slow, upright within `FeetDownTiltDeg` (30 degrees), and the `LiftoffGraceTime` (0.1 s) is over; a tilted landing is Tumbling instead |
| Airborne | Mmu | RCS enabled (`facts.RcsEnabled`) and no ground contact; Mmu returns to Airborne when RCS is off |
| Tumbling | Rightening | contact, slow, for `SettleTime` (0.15 s) |
| Tumbling, Rightening | Airborne | no contact for `TumbleAirborneExitTime` (0.5 s) |
| Rightening | Grounded | contact, feet down, grace over |
| Swimming | Airborne | `SupportRatio <= SwimExitSupportRatio` (0.001) |
| Swimming | Grounded or Tumbling | contact and `SupportRatio < SwimEnterSupportRatio`; Grounded when feet down |
| Ladder | Airborne | jump held and not consumed, or forced release; Ladder is entered from `Advance` by a pending attach or a grab candidate |

### Steppers

`Advance` updates the bookkeeping fields, handles ladder entry, then calls the stepper for the current mode and finally `DeriveMode` for the next ([current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L155-L266)). Each stepper returns a `LocomotionCommand`:

| Stepper | Command |
|---|---|
| `StepGrounded` | Active; target ground velocity = (walk or run speed) along the camera-derived basis; facing = the move direction; jump impulse `JumpDeltaV` (3 m/s) once per press ([current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L340-L369)) |
| `StepMmu` | Inactive servo; carries the camera forward and up so the MMU attitude target can use them |
| `StepAirborne`, `StepTumbling` | Inactive, facing kept |
| `StepRightening` | Inactive but with an upright direction so the servo rights the kitten |
| `StepSwimming` | Active; swim speed (`SwimSpeed` 2.5 or `SwimFastSpeed` 4), upright direction tilted toward the move by `ResolveSwimTiltDeg` |
| `StepLadder` | Advances `LadderRailT` along the rail, resolves an anchor pose (`ComputeAnchorPose`), blends across hand-offs, sets `LadderActive` |

`StepGrounded` builds its move basis in `ResolveTangentBasis`: the main camera's forward flattened onto the ground; within about 3 degrees of straight down or up it falls back to the camera's up, which points behind the view ([current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L320-L338)).

### The servo

`KittenServoPrecomp.Compute` turns the command, tuning and state into per-substep gains once per physics timestep, that is once per slice of at most 1/60 s inside `PhysicsBubble.FullPhysicsConstrainedStep` ([current/decomp/KSA/KittenServoPrecomp.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenServoPrecomp.cs#L53-L114)):

| Quantity | Formula |
|---|---|
| substep `dt` | `dt / solverSubstepCount` |
| traction gain | `1 - exp(-dt / TractionTimeConstant)` (0.04 s) |
| max traction delta-v | `TractionFrictionCoeff * budget / totalMass`, the budget being the lesser of the accumulated terrain impulse and `m * g * dt` |
| jump | `up * (JumpImpulse / substeps)` |
| servo owns angular rate | `Grounded`, `Rightening`, `Swimming` |
| servo owns tangential velocity | `Swimming` only (generic drag is cancelled in that plane) |
| upright and facing gains | `(1 - DampingExact(1, 0, halfLife, dt)) / dt` with `UprightHalfLife` 0.15 s (swimming 0.06) and `FacingHalfLife` 0.1 s |
| deadbands and angular budget | `UprightDeadbandDeg` and `FacingDeadbandDeg` 2 degrees; max angular delta-omega `angularBudget / mass * AngularImpulseBudget` (30), where the angular budget is the traction budget, except `min(terrain impulse, 8 * m * g * dt)` for the first `LandingAssistTime` (0.1 s) after entering Grounded (`LandingAssistImpulseFactor` 8) and `m * g * dt * SwimAngularBudgetFactor` while swimming |

The pose integrator then applies, each substep: tangential traction toward the target (clamped to the max delta-v), the jump delta-v, vertical damping when swimming, and a cross-product servo that rotates the body's up axis onto the target and its forward axis onto the projected facing ([current/decomp/KSA/PoseIntegratorCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PoseIntegratorCallbacks.cs#L106-L150)). Ladder follow is a separate velocity match against the host (`ComputeLadderFollow`, clamped by `LadderMaxCorrectionSpeed` 5 and `LadderMaxCorrectionRate` 10).

### Wake rules

A kitten on rails is cheap. `KittenWantsWake` forces it off rails when it has any thruster command or grab held, or is on a ladder that is not parked, or has a valid pending ladder attach ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1800-L1818), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1286-L1293)). A ladder kitten parks after `LadderParkTime` (1 s) of no input with a quiescent host; a swimmer after `SwimParkTime`.

### The tuning struct

`KittenLocomotionTuning` is a struct of about seventy floats plus `SeatedOffset`; `KittenLocomotionTuning.Current` is a public static field initialised to `Default` ([current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L5-L221)). The game's own **Debug > Kitten Tuning** window (`KittenTuningWindow`) passes `ref KittenLocomotionTuning.Current.X` to `ImGui.DragFloat`, with "Reset to Defaults" and "Copy as C#" buttons ([current/decomp/KSA/KittenTuningWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenTuningWindow.cs#L16-L50), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3850-L3862)). Two readers: `KittenRenderable` reads `Current` directly every frame (animation), and the physics sees the copy made at `PrepareWorker` (one step later).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Where the kitten branches live and which of them mods have patched](kitten-locomotion-and-servo-reference-1.md) | unscience, KSAStructures | `custom-physics` | `frame:dispatch-solvers`, `physics:vehicle-solver`, `physics:bepu` | A map of seams: `StepGrounded` (first-person), `IsKitten` (Iron Man), `Current` (tuning) |
| [2. Tune locomotion and animation parameters live from an ImGui panel](kitten-locomotion-and-servo-reference-2.md) | unscience | `imgui-window` | `frame:draw-ui`, `frame:render` | Twenty animation fields; ranges are drag limits; scoped reset |

## See also

- [Make a walking kitten move and face along the look direction](../steer-a-walking-kitten/overview.md), [Fly an EVA kitten as an ordinary rocket vessel](../treat-an-eva-kitten-as-a-rocket/overview.md)
- [Add static colliders to physics bubbles](../../../physics/colliders/add-static-colliders-to-physics-bubbles/overview.md), [Write worker-thread-safe patches inside the vehicle solver](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md)
- [Restore kitten colours, clips and tuning after a save load](../../characters/restore-kitten-customizations-after-a-load/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step)
- [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md) - related topic

