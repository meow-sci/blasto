---
type: How-To
title: Add velocity deltas in the PrepareWorker prefix
description: Push or turn a craft by adding to VelocityPhys and AngularVelocityPhys in the Vehicle.PrepareWorker prefix, and rotate or lift it in place by rewriting position, attitude and both velocities together
category: physics
group: solver
topic: inject-forces-and-impulses-at-the-physics-safe-point
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/prepareworker-impulse-injection]
techniques: [harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, starmap StarMapAllModsLoaded]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.PhysicsStates, KSA.KinematicStates, KSA.SimStep, KSA.Orbit]
tags: [physics, vehicle, impulse, velocity, spin, prepareworker, unflip, rails, mass-properties]
---

Outcome: your own force model (here, a wheeled car's springs, tyres and engine) becomes a change of velocity
and spin that the physics step integrates, and a flipped car can be set back on its wheels. Prerequisites:
StarMap and Harmony. KSACars ("Fast & Purrious") demonstrates both: KSA has no wheel module, so the mod
computes the forces itself and hands the engine a delta.

## Background: how the game does it

KSA integrates a craft from the kinematic state in its worker snapshot. `Vehicle.PrepareWorker`
[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) ends with `PrepareFromVehicle`, so a value written to the vehicle's
`Kinematic` state immediately before it is part of the next step; a value written after it is replaced at the
next `ApplyVehicleSolvers` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2435-L2544)). `GetPhysicsStatesMutable()` hands back a
`PhysicsStates` whose fields alias the vehicle's own, so assigning through it writes the live vehicle.[^ksa-vehicle]

The engine's own use of the same recipe is `Vehicle.Split`:

```csharp
// current/decomp/KSA/Vehicle.cs L1639-L1642
		PhysicsStates physicsStatesMutable = GetPhysicsStatesMutable();
		PhysicsStates physicsStatesMutable2 = vehicle.GetPhysicsStatesMutable();
		double3 double12 = splitImpulse * double5;
		physicsStatesMutable.Kinematic.VelocityPhys = velocityPhys + double10 - double12 / TotalMass;
```

Velocity and spin live in the physics frame (`Phys`); a craft on rails is held on its analytic orbit and ignores
both (see [rails](../../solver/keep-a-pushed-craft-off-rails/overview.md)).

## Lifecycle and timing

The window and its guarantees are those of the [PrepareWorker approach](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md).
In short, for a push:

| Fact | Consequence |
|---|---|
| The prefix runs once per frame per vehicle, before the snapshot | One delta per frame; the engine integrates it through its own substeps |
| `simStep.DeltaTime` is the length of the step about to be integrated | Scale a rate by it; at 50x warp it is a second or more |
| `DeltaTime` is 0 while paused | Read inputs, write nothing |
| This frame's key presses are already applied | `GetThrusterFlags()` is current |
| Guards worth copying | `dt <= 0` write nothing; `dt > 0.1 s` skip springs and tyres (explicit impulses ring when the step is long); railed, idle and nothing queued: return at the first test |

## Steps

1. Get the mutable state and, for a craft on rails, rebuild the integrated state from its orbit first, as
   `Vehicle.Split` does. KSACars distinguishes a craft coasting on its orbit (rebuilt) from one standing on the
   ground on rails (held to the ground, only woken, below):

```csharp
// src/KSACars/Ksa/Buggies.cs L586-L605
        PhysicsStates states = craft.GetPhysicsStatesMutable();

        // A car coasting on its orbit is rebuilt from it, as KSA rebuilds a craft it is about to split;
        // one standing on the ground is on rails too, held to the ground and not to an orbit, and is
        // only woken, below.
        bool coasting = railed && !craft.Situation.HasAnyContact();
        if (coasting)
        {
            if (!burning) return;
            Orbit orbit = craft.Orbit;
            states.UpdateFromAnalytic(orbit, in orbit.StateVectors, craft.Body2Cce, craft.BodyRates, Situation.Maneuvering);
            railed = false;
        }

        PhysicsStates.GetStatesCcf(in states.Origin, in states.Kinematic,
                                   out double3 positionCcf, out double3 velocityCcf, out doubleQuat body2Ccf);
        doubleQuat ccf2Body = body2Ccf.Inverse();
        doubleQuat body2Phys = states.Kinematic.Body2Phys;
        double3 spinBody = states.Kinematic.AngularVelocityPhys.Transform(body2Phys.Inverse());
        double3 velocityBody = velocityCcf.Transform(ccf2Body);
```

2. Compute your force model in the craft's own frame, as impulses. Linear impulse over mass gives a velocity
   change; angular impulse through the inverse inertia tensor gives a spin change. Reject non-finite results
   before they reach the state:

```csharp
// src/KSACars/Ksa/Buggies.cs L689-L713
        double3 dv = impulse.Linear / mass;
        Symmetric3x3 inverse = Symmetric3x3.Invert(craft.TotalMassPropsBody.Inertia);
        double3 l = impulse.Angular;
        double3 dw = new((inverse.XX * l.X) + (inverse.YX * l.Y) + (inverse.ZX * l.Z),
                         (inverse.YX * l.X) + (inverse.YY * l.Y) + (inverse.ZY * l.Z),
                         (inverse.ZX * l.X) + (inverse.ZY * l.Y) + (inverse.ZZ * l.Z));

        // The wheels are off the ground, so the keys that drove them fly the car instead.
        // Under warp the wheels are not stepped, so the hull's own contact says whether it is down.
        bool airborne = warped ? !e.Scraping : !e.Drive.Grounded.Any(g => g);
        double3 groundUpBody = Vec.Unit(positionCcf).Transform(ccf2Body);
        LiftPush lift = Lift.Step(new LiftInput(e.Rockets, e.Input.Throttle, e.Input.Steer, e.Turn), airborne, up, forward,
                                  groundUpBody, spinBody, gravity, dt);
        dv += hovering ? Hover.Push(up, groundUpBody, e.Climb, gravity, dt) : lift.Velocity + Downforce.Push(pressed, up, dt);
        dv += Boost.Push(e.Boosting, forward, dt);
        dw += lift.Spin;
        if (!Vec.IsFinite(dv) || !Vec.IsFinite(dw)) return;

        states.Kinematic.VelocityPhys += dv.Transform(body2Phys);
        states.Kinematic.AngularVelocityPhys += dw.Transform(body2Phys);

        // Woken the way KSA wakes a landed craft something has bumped into. Not rebuilt from its orbit,
        // as a craft in flight is: a landed craft on rails is held to the ground, not to that orbit, and
        // rebuilt from it the car is left standing still in space while the planet turns away at 400 m/s.
        if (railed) craft.TakeOffRails();
```

3. Wake a landed craft with `Vehicle.TakeOffRails()` (it clears the on-rails flag in `_props`) rather than
   rebuilding it from the orbit; that is the `if (railed) craft.TakeOffRails();` line closing step 2's
   snippet, and its comment explains why.

## Rotate or lift a craft in place (unflip)

A righting move changes the pose, not just a velocity, and then the velocities must change with it. KSACars'
**Unflip** button queues a request (a static dictionary keyed by vehicle); the next prefix call consumes it.
`Righting.Solve` [src/KSACars/Sim/Righting.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Righting.cs#L14-L40) returns the shortest turn that puts the car's up
on the ground's up (a half turn about forward when it is exactly upside down) and the lift that leaves the
lowest tyre 0.15 m clear. The prefix then writes position, attitude and both velocities in one go:

```csharp
// src/KSACars/Ksa/Buggies.cs L622-L645
        if (righting)
        {
            double comRadius = Vec.Len(positionCcf);
            if (!(comRadius > 0.0)) return;
            double3 upCcf = positionCcf / comRadius;
            if (!ground.TryHeight(upCcf.Transform(ccf2Cce), out double under)) return;

            double3 groundUp = upCcf.Transform(ccf2Body);
            double comHeight = comRadius - (body.MeanRadius + under);
            if (pads.TryHeightOver(positionCcf, out double overPad)) comHeight = Math.Min(comHeight, overPad);
            RightingMove move = tip
                ? new RightingMove(doubleQuat.CreateFromAxisAngle(forward, Math.PI), 1.5 - comHeight)
                : Righting.Solve(corners, hubs, up, forward, groundUp, comHeight);

            // The turn is in the car's own frame, so it goes on first; and the car is left standing
            // still over the ground, which is what the planet-fixed velocity measures.
            states.Kinematic.PositionPhys += groundUp.Transform(body2Phys) * move.Lift;
            states.Kinematic.Body2Phys = body2Phys * move.Turn;
            states.Kinematic.VelocityPhys -= velocityBody.Transform(body2Phys);
            states.Kinematic.AngularVelocityPhys = default;
            if (railed) craft.TakeOffRails();
            Log.Info($"{KsaWorld.DisplayName(craft)} {(tip ? "tipped onto its roof" : "set back on its wheels")}, lifted {move.Lift:F2} m");
            return;
        }
```

Why all four fields change together:

| Field | If it were left alone |
|---|---|
| `PositionPhys` (lift) | The new attitude would put a tyre inside the ground |
| `Body2Phys` (turn) | The car would not be upright |
| `VelocityPhys` minus the planet-fixed velocity | The planet turns under a landed craft at hundreds of m/s; without the subtraction the car, now free of the ground contact, carries that speed |
| `AngularVelocityPhys = default` | The old tumble would continue in the new attitude |

The log shows `<craft> set back on its wheels, lifted 0.xx m` on success.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs#L33-L94) | Installs and wraps the prefix |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L519-L714) | `Physics` and `Step`: inputs, guards, frame conversions, impulse to velocity and spin, righting |
| [src/KSACars/Sim/Righting.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Righting.cs) | Turn and lift that set a car on its wheels, in the car's own frame |

## Variations and gotchas

- Hold a force by restating it every frame. A write is not a standing force; the next frame's apply replaces
  it with the integrated state.
- Explicit impulses ring when the step is long. KSACars leaves springs and tyres out above a 0.1 s step and
  lets the car rest on its colliders; rockets are plain impulses and stay valid for any step length (see
  [approach 4](inject-forces-and-impulses-at-the-physics-safe-point-4.md)).
- The prefix runs before `ClearHeldPlayerInput`, so held keys and `GetThrusterFlags()` are readable for the
  frame but cleared for the craft afterwards.
- The impulse goes through the craft's centre of mass and inertia, so a car docked to something else drives the
  whole assembly.
- Above the atmosphere KSA rails a craft with no engine of its own firing; a railed craft ignores the write.
  KSACars marks a burning car for its rails hook in the same prefix.
- Do not call `Universe.ApplyVehicleSolvers` from a hook to "apply" a write; it also advances the simulation
  clock.

## See also

- [Overview and comparison](overview.md)
- [Approach 2: blast shove](inject-forces-and-impulses-at-the-physics-safe-point-2.md), [approach 4: lift, hover, boost](inject-forces-and-impulses-at-the-physics-safe-point-4.md)
- Mod feature: [KSACars PrepareWorker impulse injection](../../../../mods/ksacars/features/prepareworker-impulse-injection.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
- [Keep a craft under full physics while your mod pushes it](../../solver/keep-a-pushed-craft-off-rails/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
