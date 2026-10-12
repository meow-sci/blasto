---
type: How-To
title: Find the kitten branches in the worker and patch them from outside the solver
description: A map of the seams mods use around the kitten character controller - read KittenLocomotionTuning.Current, patch KittenLocomotion.StepGrounded to supply a heading, flip VehicleUpdateState.IsKitten to take the vessel branches - with what thread each runs on and what is safe there
category: kittens
group: eva
topic: kitten-locomotion-and-servo-reference
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/iron-man-kitten-rocket-flight
  - mods/unscience/features/kitten-animations-locomotion-tuning
  - mods/ksastructures/features/first-person-kitten-view
techniques: [custom-physics, harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.KittenLocomotion.StepGrounded
  - harmony-postfix KSA.KittenLocomotion.StepGrounded
  - harmony-postfix KSA.VehicleUpdateState.PrepareFromVehicle
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver, physics:bepu]
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
tags: [kitten, locomotion, patch-points, worker-thread, seams]
---
The [reference](overview.md) describes how the kitten is simulated. This article answers the modding question that follows from it: which of those places have mods patched or read, on which thread does each run, and what is legal there. There are three kinds of seam: a **static** the main thread and the renderer both read (`KittenLocomotionTuning.Current`), a **pure function** called from the vehicle worker (`KittenLocomotion.StepGrounded` and friends), and a **snapshot flag** on the worker's per-vehicle state (`IsKitten`). Prerequisites: StarMap and Harmony; the worker-thread rules in [write worker-thread-safe patches](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md).

## Background: how the game does it

- `KittenLocomotion` is a static class of pure-ish functions: `Advance(in CharacterControlInputs, in LocomotionFacts, in KittenLocomotionTuning, ref LocomotionState, in ManualControlInputs, in LadderWorkerInputs, ReadOnlySpan<LadderNode>)` returns a `LocomotionCommand` and mutates the `LocomotionState` by reference. It is called from `PhysicsBubble.AdvanceKittenLocomotion` on the vehicle-solver worker thread after each physics timestep, that is after every slice of at most 1/60 s of simulated time that `PhysicsBubble.FullPhysicsConstrainedStep` simulates, so several times per frame under timewarp (the main thread copies the inputs only once per frame, in `KittenEva.PrepareWorker`) ([current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L155-L266), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1969-L2013)).
- The mode steppers (`StepGrounded`, `StepMmu`, `StepSwimming`, `StepLadder`...) are public static and take `in` parameters, so Harmony prefixes can take them by `ref` to substitute inputs and postfixes receive `ref LocomotionCommand __result` and `ref LocomotionState state`.
- `VehicleUpdateState.IsKitten` is assigned on the main thread in `PrepareFromVehicle` (called in `Universe.PrepareVehicleWorkers` for each vehicle right before the worker snapshot) and read by the worker for the whole step.
- `KittenLocomotionTuning.Current` is read on the main thread by `KittenEva.PrepareWorker` and by `KittenRenderable` each frame; the worker gets a copy.

## Lifecycle and timing

| Seam | Thread and moment | Lifecycle |
|---|---|---|
| `KittenLocomotionTuning.Current` fields | main thread; the physics sees a copy made at `PrepareWorker` for the next step, the renderer reads it every frame | `frame:draw-ui` (edit), `frame:dispatch-solvers` (copy), `frame:render` |
| Prefix/postfix on `KittenLocomotion.StepGrounded` | vehicle worker, once per physics timestep (at most 1/60 s of simulated time) per grounded kitten, so possibly several times per frame | `physics:vehicle-solver` |
| Postfix on `VehicleUpdateState.PrepareFromVehicle` | main thread inside `ExecuteNextVehicleSolvers`, per vehicle, before the worker job runs | `frame:dispatch-solvers` |
| `PoseIntegratorCallbacks` servo | Bepu worker, every substep | `physics:bepu` |

Anything that runs on the worker must not allocate, touch ImGui or Vulkan, or mutate main-thread objects; pass data across with an immutable value published through `Volatile.Write` and read with `Volatile.Read` ([game loop reference: threading model](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#threading-model)).

## Steps

1. **Read the tuning, do not write physics fields casually.** Animation-facing fields (clip nominal speeds, blend times) are safe to edit live; physics fields (traction, servo half-lives, speeds) change handling for every kitten at the next `PrepareWorker` ([approach 2](kitten-locomotion-and-servo-reference-2.md)).

2. **Supply a heading by patching `StepGrounded`.** Stock derives the walk basis from the main camera's forward flattened onto the ground (and falls back to the camera's up when looking nearly straight down). KSAStructures' prefix replaces `CameraForwardCce` in the inputs, which the stepper takes `in`; the prefix takes it by `ref`:

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L142-L152
    private static void BeforeStepGrounded(ref CharacterControlInputs inputs)
    {
        try
        {
            if (_heading is { Length: 3 } h) inputs.CameraForwardCce = new float3(h[0], h[1], h[2]);
        }
        catch
        {
            // The camera's own forward is the only failure allowed.
        }
    }
```

   The postfix then pins the facing the stock stepper set from the move direction, so side-stepping does not turn the kitten away (this is the whole of [steer a walking kitten](../steer-a-walking-kitten/overview.md)):

```csharp
// src/KSAStructures/Ksa/FirstPersonHook.cs L154-L175
    // Only the flown kitten's inputs carry a camera, so the rest are left alone.
    private static void AfterStepGrounded(ref LocomotionCommand __result, in CharacterControlInputs inputs,
                                          in LocomotionFacts facts, ref LocomotionState state)
    {
        try
        {
            if (_heading is null || inputs.CameraForwardCce.LengthSquared() < 1e-6f) return;

            float3 forward = float3.Transform(inputs.CameraForwardCce, facts.Cce2Phys);
            float3 up = facts.UpDirPhys;
            float3 flat = forward - (up * float3.Dot(forward, up));
            if (flat.LengthSquared() < 0.0025f) return;

            flat = flat.Normalized();
            __result.FacingDirPhys = flat;
            state.FacingDirPhys = flat;
        }
        catch
        {
            // A facing left as KSA chose it is the only failure allowed.
        }
    }
```

3. **Take the vessel branches by flipping `IsKitten`.** Iron Man mode sets `IsKitten = false` on the worker snapshot for enabled kittens in a postfix on `PrepareFromVehicle`, so `AdvanceKittenLocomotion`, the servo, the wake rule, the structural allowance and `PartFailure` all treat that kitten as an ordinary vessel; the postfix is on the main thread, before the job, so writing the field is race-free ([fly a kitten as a rocket](../treat-an-eva-kitten-as-a-rocket/overview.md)).

4. **Know the wake rule before you park a kitten.** A kitten with no thruster command, no grab held and no pending ladder attach goes on rails (or stays parked on a ladder); a mod that moves a resting kitten must wake it or the move may not simulate ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1800-L1818)).

5. **Know which values the servo owns.** While `Grounded`, `Rightening` or `Swimming`, the servo owns the body's angular rate (the generic terrain angular damping is skipped), so applying a torque from a mod fights it; in other modes (`Airborne`, `Tumbling`, `Mmu`) the body is free ([current/decomp/KSA/PoseIntegratorCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PoseIntegratorCallbacks.cs#L84-L106)).

## Complete example

- [src/KSAStructures/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/FirstPersonHook.cs#L46-L175) - `StepGrounded` prefix and postfix with the volatile heading.
- Feature pages: [Iron Man kitten rocket flight](../../../../mods/unscience/features/iron-man-kitten-rocket-flight.md) (the `IsKitten` postfix), [First-person kitten view](../../../../mods/ksastructures/features/first-person-kitten-view.md) (the `StepGrounded` patch), [Kitten animations locomotion tuning](../../../../mods/unscience/features/kitten-animations-locomotion-tuning.md) (the `Current` seam).

## Variations and gotchas

- **`in` parameters.** `StepGrounded` takes `CharacterControlInputs` by `in`; a Harmony prefix declares it `ref` to write through. The result and state are mutable in a postfix via `ref`.
- **One heading, whole process.** The first-person hooks publish a single heading for all kittens; only the flown kitten's inputs carry a camera, so the postfix returns early for the rest.
- **Method identity.** `StepGrounded` is public static, so `AccessTools.Method(typeof(KittenLocomotion), nameof(KittenLocomotion.StepGrounded))` resolves it; a renamed or moved stepper disables the feature with a warning, not an exception ([validate patch targets](../../../patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md)).
- **Do not read the main thread's `LocomotionState` for the step in flight.** The state object the worker mutates is replaced by `NewLocomotionState` when the step is applied; `KittenEva.LocomotionState` reflects the last applied step.
- **Servo parameters are physics.** Changing `UprightHalfLife`, `TractionTimeConstant` and friends alters handling for all kittens and can destabilise a patched controller.

## See also

- [Kitten locomotion, modes and the servo, from the decompiled game](overview.md), [approach 2: live tuning panel](kitten-locomotion-and-servo-reference-2.md)
- [Make a walking kitten move and face along the look direction](../steer-a-walking-kitten/overview.md), [Fly an EVA kitten as an ordinary rocket vessel](../treat-an-eva-kitten-as-a-rocket/overview.md)
- [Write worker-thread-safe patches inside the vehicle solver](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md), [Add static colliders to physics bubbles](../../../physics/colliders/add-static-colliders-to-physics-bubbles/overview.md)
