---
type: Game Feature
title: Keep a craft under full physics while your mod pushes it
description: Why a railed craft ignores velocity writes, which per-craft checks decide rails status, and two ways to keep a pushed craft in the physics bubble
category: physics
group: solver
topic: keep-a-pushed-craft-off-rails
demonstrated_by:
  - mods/ksacars/features/keep-thrusting-craft-off-rails
  - mods/ksa-advancedflightcomputer/features/flight-computer-command-sink
lifecycle: [startup:all-mods-loaded, frame:dispatch-solvers, physics:vehicle-solver]
game_types: [KSA.PhysicsBubble, KSA.Vehicle, KSA.FlightComputer]
tags: [physics, rails, physics-bubble, wake, vehicle, harmony, flight-computer]
---

KSA puts a craft "on rails" whenever it can: a landed craft that has stood still is held to the ground, and a craft
coasting above the atmosphere is moved along its orbit analytically. A railed craft ignores velocity writes: the
analytic motion never integrates `Kinematic.VelocityPhys`. A mod that pushes a craft with something the engine does
not count as propulsion (a car's rocket "engines", a blast, a scripted force) must therefore do two things: wake
the craft when it is railed, and keep it awake while the push continues. This topic compares the two ways the
reference mods do it.

## Engine background

The decision is made in `PhysicsBubble` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1351):

| Place | Rule |
|---|---|
| `ApplySingleVehicleMotion` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1292) (a bubble with one vehicle, start of its step) | Runs `ComputeControl` on the railed vehicle; the vehicle leaves rails when `NextWakeupDeltaTime` is shorter than the window, `AnyActuatorCommanded`, `AnyActuatorActive()` or `KittenWantsWake` is true |
| `FullPhysicsStepUntil` [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1434-L1478) (every full-physics sub-step, per vehicle) | The wake reasons (`AnyActuatorCommanded`, active actuators, high-fidelity ocean, keyframe animation, `KittenWantsWake`) come first; only if none fired does the chain reach `CanInstantlyFloatOnRails`, `TryToPutCoastingOnRails` or `PhysicsStates.TryToPutOnRails` |
| `KittenWantsWake(VehicleUpdateState)` (private static) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1800-L1820) | Returns false for any non-kitten; for a kitten returns true while thruster keys or grab are held or a ladder move is pending |

So the two levers are: make `KittenWantsWake` answer true for your craft (approach 1), or make the flight
computer's output say an actuator was commanded or a wake is due (approach 2). A craft inside the physics radius
that is coasting above the atmosphere is railed by `TryToPutCoastingOnRails`; one outside the radius is railed
at once by `PhysicsStates.TryToPutOnRails` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L927-L949) behind the same wake check.

The write-side rule for the craft's state (rebuild from the orbit when railed and coasting, wake by
`Vehicle.TakeOffRails()` when landed) is in
[inject forces and impulses](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Postfix `PhysicsBubble.KittenWantsWake` from a concurrent set](keep-a-pushed-craft-off-rails-1.md) | KSACars | harmony-postfix | Targets a private method meant for kittens; both rails decisions ask it per craft; needs a concurrent set and an install-time signature check |
| [2: Request wake or off-rails from the flight computer command sink](keep-a-pushed-craft-off-rails-2.md) | KSA-AdvancedFlightComputer | harmony-postfix | Uses the flight computer's own outputs; no patch on the bubble, but only fires when `ComputeControl` runs and only for features that already write actuators |

## See also

- [Inject forces, impulses and velocity changes at the physics safe point](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/overview.md)
- [Write worker-thread-safe patches inside the vehicle solver](../../solver/write-worker-thread-safe-solver-patches/overview.md)
- [Inject forces, impulses and velocity changes at the physics safe point](../inject-forces-and-impulses-at-the-physics-safe-point/overview.md) - related topic
- [Write worker-thread-safe patches inside the vehicle solver](../write-worker-thread-safe-solver-patches/overview.md) - related topic

