---
type: Mod Feature
title: Rocket lift, hover, boost and downforce as velocity deltas
description: Fly a car on thrust the mod applies itself by adding lift, hover, boost and downforce pushes to the vehicle's velocity and spin in the same Vehicle.PrepareWorker window as the springs, written as shares of the step so time warp cannot make them overshoot
mod: ksacars
category: physics
techniques: [harmony-prefix, custom-physics, reflection]
hooks:
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - reflection KSA.Vehicle._manualControlInputs
  - reflection KSA.Vehicle._engineFlags
  - starmap StarMapAfterGui
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.PhysicsStates
  - KSA.KinematicStates
  - KSA.Universe
  - KSA.SimStep
  - KSA.ManualControlInputs
  - KSA.EngineFlags
tags: [physics, thrust, hover, flight, time-warp, prepareworker, harmony, attitude-hold]
---

The Eldorado flies, boosts and sticks to the ground on rockets that KSA knows nothing about. Four
lift rockets under the floor give up to two weights of thrust along the car's own up (so half
throttle hovers), with an attitude hold in the air that the pitch, yaw and roll keys lean and turn.
Two tail rockets give a 12 m/s^2 boost while the sprint key is held, four downward rockets on the
bonnet and boot press the car onto its tyres with about 1 g (the RCS key), and with lift and
downforce both lit the car hovers in place.[^ksacars-claude] Each push is a change of velocity
and spin added in the same `Vehicle.PrepareWorker` prefix that applies the springs.[^ksacars-buggies]

## How it works

**Which car flies.** A `BuggyProfile` with `RocketNozzles` flies; `BoostNozzles` gives a boost,
`DownNozzles` downforce, and `Hatches` lists the iris each thruster fires through (group `Lift`,
`Boost` or `Down`). Only the Eldorado has any.[^ksacars-profile]

**Inputs, read at the top of `Buggies.Step`.**[^ksacars-buggies]

| Input | Source |
|---|---|
| Engine on/off | `_manualControlInputs.EngineOn`, read by reflection (`KsaWorld.EngineOn`) |
| Lift throttle | the mod's own `Entry.Throttle`, ramped at KSA's 0.7/s from the held keys in `_engineFlags` (`KsaWorld.ThrottleKeys`) |
| Boost | `Vehicle.GetSprintInput()` |
| Downforce | a toggle flipped by the RCS key |
| Stick | `GetThrusterFlags()`: pitch, yaw and roll |

The keys and the reflection are covered in [drive with flight keys](./drive-with-flight-keys.md).
KSA clamps its own throttle to the least any `EngineController` allows, which is 1 with no
engine, so the mod keeps its own.[^ksacars-ksaworld]

**Iris gate.** Wanting a group sets `HatchWanted[group]`; the group fires only once
`Hatch.Ready(HatchOpen[group])`, i.e. the iris is at least 0.999 open. `Hatch.Advance` opens or
closes an iris over `OpenSeconds = 0.1` and is stepped in `Buggies.Pose` with the world's time,
so slow motion slows it and pause holds it; an iris stays open while its flame is still
dying.[^ksacars-hatch][^ksacars-hatch-tests] The six blades of each iris are posed as subparts
(see [posing subparts](./pose-subparts-at-runtime.md)).

**The pushes, all in the car's body frame.**

| Push | Function | Per step |
|---|---|---|
| Lift | `Lift.Step` | `up * throttle * 2 g * dt`; when airborne also a spin toward the wanted attitude |
| Hover (lift and downforce together) | `Hover.Push` | along up, enough that its vertical share cancels gravity and brakes the step's average climb |
| Boost | `Boost.Push` | `forward * 12 * dt` |
| Downforce | `Downforce.Push` | `up * -10 * dt`, and the springs are sized for `gravity + 10` |

`Lift.Step` on the ground returns only the thrust: the springs hold the attitude there, and a hold
fighting them on a slope would rock the car. In the air, the wanted up is the ground's up tilted by
up to 0.26 rad toward the stick (pitch leans the nose, roll banks), the yaw keys ask for 0.9 rad/s,
and the result is a spin delta.[^ksacars-lift] `Buggies.Step` then converts the springs' impulse
to `dv` and `dw`, adds the pushes, rejects non-finite values and writes
`states.Kinematic.VelocityPhys += dv` and `AngularVelocityPhys += dw` in the physics frame.[^ksacars-buggies]

**Warp-proof by construction.** A physics step is the frame times the simulation speed. Past
0.1 s (`warped`) the springs are skipped and the car rests on its colliders, but the rockets carry
on:[^ksacars-buggies]

- Thrust and boost are plain impulses, valid for a step of any length.
- The attitude hold and the yaw rate are applied as `(target - current) * (1 - exp(-k dt))`, the
  share of the way a step of that length covers, and the righting rate is capped at `0.8 / dt`, so
  a long step takes out part of a lean and never throws it back.[^ksacars-lift][^ksacars-lift-tests]
- Hover brakes `climb + 0.5 * g * dt`, the average climb over the step, by `1 - exp(-3 dt)`. The
  mod's reasoning: KSA takes the push at the start of its step and lets gravity act through it, so
  braking the sampled climb would leave the car rising at half a step of gravity (5 m/s at 50x).
  The push is clamped between the downforce rockets' pull and two weights.[^ksacars-hover][^ksacars-hover-tests]
- Airborne is decided by the wheels when the springs ran, and by the hull's own terrain contact
  under warp. Downforce alone does nothing under warp (there is no tyre grip to help).

## Lifecycle and timing

All of this runs in the Harmony prefix on `Vehicle.PrepareWorker`, installed in
`[StarMapAllModsLoaded]`.[^ksacars-physicshook] `Program.PrepareFrame` applies the last physics
step (`Universe.ApplyVehicleSolvers`, L2167) and then dispatches the next one
(`Universe.ExecuteNextVehicleSolvers`, L2220).[^ksa-program] `ExecuteNextVehicleSolvers` calls
`PrepareVehicleWorkers`, which calls `PrepareWorker(simStep)` on the main thread for every vehicle
before the vehicle-solver job is queued.[^ksa-universe] Inside `PrepareWorker`, after the prefix,
KSA clears held input if ImGui has the keyboard, ramps and clamps its throttle, and snapshots the
vehicle's kinematic state for the worker with `_threadWorkerUpdateState.PrepareFromVehicle`.[^ksa-vehicle]

So in the prefix:

- The vehicle holds the result of the step that was just applied.
- `GetPhysicsStatesMutable()` returns a `ref struct` over the vehicle's own fields, so a velocity
  added there is in the snapshot taken a few lines later and is integrated by this step.[^ksa-physicsstates]
- `simStep.DeltaTime` is that step's length, which is what the share-of-step formulas use.

The hatch opening (`Buggies.Pose`) runs later in the frame, from `[StarMapAfterGui]` (or the
`StarMapAfterOnFrame` fallback), so a request made in one physics window can only fire in a later
one, at least 0.1 s of world time on.[^ksacars-buggies]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L550-L566) | Inputs, iris gating, `Rockets`/`Boosting`/`Pressed`, `RailsHook.Mark` |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L668-L708) | Gravity, hover vs lift vs downforce, boost, write to `VelocityPhys` / `AngularVelocityPhys` |
| [src/KSACars/Sim/Lift.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Lift.cs) | Thrust, throttle ramp, attitude hold that cannot overshoot |
| [src/KSACars/Sim/Hover.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Hover.cs) | Gravity cancel and step-average climb brake |
| [src/KSACars/Sim/Boost.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Boost.cs) | 12 m/s^2 along forward |
| [src/KSACars/Sim/Downforce.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Downforce.cs) | 10 m/s^2 along down, and the load the springs carry |
| [src/KSACars/Sim/Hatch.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Hatch.cs#L28-L48) | Iris timing: `Advance`, `Ready`, `OpenSeconds` |
| [src/KSACars/Sim/BuggyProfile.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/BuggyProfile.cs#L88-L113) | `RocketNozzles`, `BoostNozzles`, `DownNozzles`, `Hatches`; Eldorado values at L231-L282 |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L20-L28) | Reflection fields for the engine switch and throttle keys |
| [tests/KSACars.Tests/HoverTests.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tests/KSACars.Tests/HoverTests.cs) | Holds height and brakes a climb whatever the step length |

## Snippets

```csharp
// src/KSACars/Ksa/Buggies.cs L550-L566
        // A rocket fires once its hatch is open; until then the driver only wants it.
        e.HatchWanted[(int)ThrusterGroup.Boost] = boost && e.Drive.Profile.HasBoost;
        e.Boosting = e.HatchWanted[(int)ThrusterGroup.Boost] && e.Ready(ThrusterGroup.Boost);

        // KSA's own engine keys: start and shutdown light and cut the rockets, and the throttle keys move
        // the mod's throttle, because KSA pins its own at full on a craft with no engine.
        (bool more, bool less) = KsaWorld.ThrottleKeys(craft);
        if (dt > 0.0) e.Throttle = Lift.Ramp(e.Throttle, more, less, dt);
        e.Lit = e.Drive.Profile.HasRockets && (KsaWorld.EngineOn(craft) ?? e.Lit);
        e.HatchWanted[(int)ThrusterGroup.Lift] = e.Lit;
        e.HatchWanted[(int)ThrusterGroup.Down] = e.Downforce && e.Drive.Profile.HasDownforce;
        e.Rockets = e.Lit && e.Ready(ThrusterGroup.Lift) ? e.Throttle : 0.0;
        e.Pressed = e.HatchWanted[(int)ThrusterGroup.Down] && e.Ready(ThrusterGroup.Down);

        // KSA puts a craft out of the air onto its orbit unless an engine of its own is firing.
        bool burning = e.Rockets > 0.0 || e.Boosting;
        RailsHook.Mark(craft, burning);
```

```csharp
// src/KSACars/Ksa/Buggies.cs L672-L708
        // Both sets of rockets at once hold the car where it is, so neither pushes on its own.
        bool hovering = e.Pressed && e.Rockets > 0.0;
        // Pressing the car down is for the tyres' grip, which under warp there is none of.
        bool pressed = e.Pressed && !hovering && !warped;
        DriveImpulse impulse = warped
            ? default
            : e.Drive.Step(e.Input, contacts, hubs, up, forward, mass, Downforce.Load(pressed, gravity), air, dt);
        // ...
        double3 dv = impulse.Linear / mass;
        // ...
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
```

```csharp
// src/KSACars/Sim/Hover.cs L24-L35
    public static double3 Push(double3 up, double3 groundUp, double climb, double gravity, double dt)
    {
        up = Vec.Unit(up);
        double upright = Math.Max(Vec.Dot(up, Vec.Unit(groundUp)), LeastUpright);
        // The engine takes the push at the start of its step and lets gravity act through it, so the
        // car's climb over the step is half a step of gravity less than its climb right after the push.
        // That average is what is braked to nothing; braking the sampled climb leaves the car rising.
        double mean = climb + (0.5 * gravity * dt);
        double braked = mean * (1.0 - Math.Exp(-ClimbDamping * dt));
        double push = Math.Clamp(((gravity * dt) - braked) / upright, -Downforce.AccelMs2 * dt, Lift.MaxG * gravity * dt);
        return up * push;
    }
```

```csharp
// src/KSACars/Sim/Lift.cs L87-L92
        double yawRate = Vec.Dot(spin, up);
        double3 tumble = spin - (up * yawRate);
        double3 righting = Vec.Cross(up, wanted) * Math.Min(HoldStiffness / HoldDamping, MostOfTheWay / dt);
        double3 turn = ((righting - tumble) * (1.0 - Math.Exp(-HoldDamping * dt)))
                       + (up * (((Math.Clamp(input.Yaw, -1.0, 1.0) * YawRateRadPerSec) - yawRate) * (1.0 - Math.Exp(-YawDamping * dt))));
        return new LiftPush(push, turn);
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Vehicle.PrepareWorker(SimStep)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423) | Prefix target; snapshot taken at its end |
| `Universe.ExecuteNextVehicleSolvers` / `PrepareVehicleWorkers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2155) | Calls `PrepareWorker` per vehicle on the main thread, then queues the solver |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2236) | Apply (L2167) before dispatch (L2220) |
| `Vehicle.GetPhysicsStatesMutable()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L922-L931) | `ref struct PhysicsStates` aliasing the vehicle's kinematic state |
| `PhysicsStates` (`Kinematic.VelocityPhys`, `AngularVelocityPhys`, `Body2Phys`, `GetStatesCcf`) | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L10) | Where the deltas are written |
| `Vehicle.GetSprintInput()`, `GetThrusterFlags()` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6089-L6105) | Boost key and stick |
| `Vehicle._manualControlInputs.EngineOn`, `Vehicle._engineFlags` (private) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2403-L2414) | Engine switch and held throttle keys, read by reflection |

## Pitfalls and version notes

- **Only this window works.** A velocity written from a StarMap hook is overwritten by the next
  `ApplyVehicleSolvers` before anything reads it; see
  [PrepareWorker impulse injection](./prepareworker-impulse-injection.md).
- **Above the atmosphere KSA rails a craft with no engine firing**, and a railed car ignores the
  velocity written here. The mod marks a burning car for `RailsHook` in the same prefix; see
  [keep a thrusting craft off rails](./keep-thrusting-craft-off-rails.md).
- **Thrust through the centre of mass.** Every push is a velocity delta, so it never torques the
  car whatever the crew's weight; the attitude comes only from the hold.[^ksacars-lift]
- **Reflection on two private fields.** If KSA renames `_manualControlInputs` or `_engineFlags`,
  `KsaWorld` leaves the fields null and the rockets cannot be lit from the keys; only the dev bridge
  can still fly them.[^ksacars-ksaworld][^ksacars-claude]
- **The prefix must not throw.** `Buggies.Physics` catches, drops the car from the active set and
  logs once; `PhysicsHook` catches again.[^ksacars-physicshook]

## See also

- [PrepareWorker impulse injection](./prepareworker-impulse-injection.md)
- [Wheel, suspension and tyre model](./wheel-suspension-tyre-model.md)
- [Drive with flight keys](./drive-with-flight-keys.md)
- [Keep a thrusting craft off rails](./keep-thrusting-craft-off-rails.md)
- [Exhaust plumes without an engine](./exhaust-plumes-without-engine.md)
- [Engine HUD for an engineless craft](./engine-hud-for-engineless-craft.md)
- [Pose subparts at runtime](./pose-subparts-at-runtime.md)
- [KSACars overview](../overview.md)
- [Velocity and spin deltas gated by hatches in the PrepareWorker prefix](../../../howto/physics/solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-4.md) - how-to article demonstrating this feature

[^ksacars-lift]: src/KSACars/Sim/Lift.cs
[^ksacars-hover]: src/KSACars/Sim/Hover.cs
[^ksacars-hatch]: src/KSACars/Sim/Hatch.cs
[^ksacars-profile]: src/KSACars/Sim/BuggyProfile.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-physicshook]: src/KSACars/Ksa/PhysicsHook.cs
[^ksacars-ksaworld]: src/KSACars/Ksa/KsaWorld.cs
[^ksacars-hover-tests]: tests/KSACars.Tests/HoverTests.cs
[^ksacars-lift-tests]: tests/KSACars.Tests/LiftTests.cs
[^ksacars-hatch-tests]: tests/KSACars.Tests/HatchTests.cs
[^ksacars-claude]: CLAUDE.md, "The cars"
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled)
