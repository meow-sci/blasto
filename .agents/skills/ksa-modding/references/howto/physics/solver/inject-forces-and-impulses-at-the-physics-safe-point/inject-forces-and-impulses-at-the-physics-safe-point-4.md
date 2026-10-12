---
type: How-To
title: Velocity and spin deltas gated by hatches in the PrepareWorker prefix
description: Give a craft lift rockets, a gravity-cancelling hover, a boost and downforce as per-step velocity and spin deltas gated by 0.1 s hatches, expressed as exponential shares and per-step caps so they stay stable at any time warp
category: physics
group: solver
topic: inject-forces-and-impulses-at-the-physics-safe-point
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/rocket-lift-hover-boost-downforce]
techniques: [harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.PhysicsStates, KSA.KinematicStates, KSA.SimStep]
tags: [physics, vehicle, lift, hover, boost, downforce, time-warp, exponential-share, hatch, rockets]
---

Outcome: a craft with four lift rockets (up to two weights of thrust, so half throttle hovers), an attitude hold
in the air that the pitch, yaw and roll keys lean and turn, two tail rockets for a 12 m/s^2 boost, and downward
rockets that press it onto its tyres, all without any real engine. KSACars' Eldorado flies this way. Each push
is a change of velocity and spin added in the same `Vehicle.PrepareWorker` prefix that applies the springs
([approach 1](inject-forces-and-impulses-at-the-physics-safe-point-1.md) for the base recipe). Prerequisites:
StarMap and Harmony.

## Background: how the game does it

KSA integrates the velocity in the snapshot through its own substeps and takes your delta at the start of the
step, then lets gravity act through it. A step is the frame time times the simulation speed
(`SimStep.DeltaTime` [current/decomp/KSA/SimStep.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SimStep.cs)), so at 50x a single prefix call stands for seconds of flight. Any
push that is a rate must therefore be scaled by `dt`, and any feedback that corrects toward a target must not be
able to overshoot when `dt` is large. KSA also puts a craft out of the air onto its orbit unless an engine of its
own is firing, so a rocket-propelled craft must also be kept off rails (see
[keep a craft under full physics](../../solver/keep-a-pushed-craft-off-rails/overview.md)).

## Lifecycle and timing

All of it runs in the `PrepareWorker` prefix, installed from `[StarMapAllModsLoaded]`. Inside the prefix the
vehicle holds the step just applied, this frame's key presses are in, and `simStep.DeltaTime` is the step about to
run. The visual side (hatch irises) is posed later in the frame from `[StarMapAfterGui]`, so a request made in
one physics window can only fire in a later one, at least 0.1 s of world time on.

| Piece | Where | Rate |
|---|---|---|
| Hatch state, throttle ramp, inputs | prefix, top of `Buggies.Step` | once per frame |
| Lift, hover, boost, downforce deltas | prefix | once per frame, scaled by `dt` |
| Iris posing | `[StarMapAfterGui]` (`frame:draw-ui`) | once per frame |

## Steps

1. Gate each rocket group on its hatch. Wanting a group sets `HatchWanted[group]`; the group fires only once its
   iris is at least 0.999 open. `Hatch.Advance` moves an iris over `OpenSeconds` of world time, so slow motion
   slows it and pause holds it:

```csharp
// src/KSACars/Sim/Hatch.cs L31-L48
    /// <summary>How long an iris takes to open, or close.</summary>
    public const double OpenSeconds = 0.1;

    /// <summary>A blade's radius: the hole's, and a little under the skin round it.</summary>
    public const double Radius = 0.1225;

    /// <summary>How far under the skin the top blade lies, which is over the cup's rim.</summary>
    public const double BladeDepth = 0.0015;

    /// <summary>How much lower each blade lies than the one before, so no two share a plane.</summary>
    public const double BladeStep = 0.00015;

    /// <summary>How far open a hatch is after a step, from 0 closed to 1 open.</summary>
    public static double Advance(double open, bool wanted, double dt) =>
        Math.Clamp(open + ((wanted ? 1.0 : -1.0) * Math.Max(dt, 0.0) / OpenSeconds), 0.0, 1.0);

    /// <summary>Whether a hatch is open far enough for its rocket to fire.</summary>
    public static bool Ready(double open) => open >= 0.999;
```

2. Read the inputs at the top of the step and decide which rockets fire. The mod keeps its own throttle because
   KSA pins its own at full on a craft with no engine; the engine switch and held throttle keys are read by
   reflection:

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

3. Express each push as a per-step change of velocity along the craft's own axes, through the centre of mass so
   it never torques the craft. Boost and downforce are plain constants times `dt`:

```csharp
// src/KSACars/Sim/Boost.cs L15-L19
    public const double AccelMs2 = 12.0;

    /// <summary>What a step of boost adds to the car's velocity, in its own frame.</summary>
    public static double3 Push(bool lit, double3 forward, double dt) =>
        lit ? Vec.Unit(forward) * (AccelMs2 * dt) : Vec.Zero;
```

```csharp
// src/KSACars/Sim/Downforce.cs L13-L17
    public const double AccelMs2 = 10.0;

    /// <summary>What a step of downforce adds to the car's velocity, in its own frame.</summary>
    public static double3 Push(bool lit, double3 up, double dt) =>
        lit ? Vec.Unit(up) * (-AccelMs2 * dt) : Vec.Zero;
```

   Lift is `up * (throttle * MaxG * gravity * dt)` (two weights at full throttle); on the ground only that thrust
   is applied because the springs hold the attitude, and in the air an attitude hold adds a spin delta.

4. Combine the pushes and write them. Lift and downforce together make a hover, so neither pushes on its own;
   under warp the springs and tyres are skipped, so the hull's own contact says whether the car is down:

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

## Keep forces stable at any time warp

A force that was tuned at 60 Hz misbehaves when one physics step is 20 frames long. KSACars' rules, all
visible in the code above and below:

| Rule | Where | Why |
|---|---|---|
| Thrust and boost are impulses (`a * dt`) | `Lift.Step`, `Boost.Push`, `Downforce.Push` | Valid for a step of any length |
| Corrective rates use a fractional share `(target - current) * (1 - exp(-k dt))` | `Lift.Step` | The share of the way a step covers: a spring and damper for short steps, no overshoot for long ones |
| Cap the correction per step | `Math.Min(HoldStiffness / HoldDamping, MostOfTheWay / dt)` | The righting rate is capped so one step takes out at most 80 percent of a lean; the cap is derived from the step length |
| Brake the step-average climb, not the sample | `Hover.Push` | KSA takes the push at the start of the step and lets gravity act through it; braking the sampled climb leaves the car rising at half a step of gravity (5 m/s at 50x) |
| Clamp the push between sensible bounds | `Hover.Push` | Between the downforce rockets' pull and two weights |
| Skip explicit springs past 0.1 s | `Buggies.Step` (`warped`) | Explicit springs ring on long steps; the car rests on its colliders while the rockets carry on |

```csharp
// src/KSACars/Sim/Lift.cs L87-L92
        double yawRate = Vec.Dot(spin, up);
        double3 tumble = spin - (up * yawRate);
        double3 righting = Vec.Cross(up, wanted) * Math.Min(HoldStiffness / HoldDamping, MostOfTheWay / dt);
        double3 turn = ((righting - tumble) * (1.0 - Math.Exp(-HoldDamping * dt)))
                       + (up * (((Math.Clamp(input.Yaw, -1.0, 1.0) * YawRateRadPerSec) - yawRate) * (1.0 - Math.Exp(-YawDamping * dt))));
        return new LiftPush(push, turn);
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

Replace force-based damping with velocity shares that approach a target, and derive every cap from `dt`. The
mod's `HoverTests` check that the car holds height and brakes a climb whatever the step length.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L550-L708) | Inputs, iris gating, the combined write |
| [src/KSACars/Sim/Lift.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Lift.cs) | Thrust, throttle ramp, attitude hold that cannot overshoot |
| [src/KSACars/Sim/Hover.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Hover.cs) | Gravity cancel and step-average climb brake |
| [src/KSACars/Sim/Boost.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Boost.cs), [src/KSACars/Sim/Downforce.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Downforce.cs) | The two constant-acceleration pushes |
| [src/KSACars/Sim/Hatch.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/Hatch.cs#L26-L52) | Iris timing |

## Variations and gotchas

- Only the `PrepareWorker` window works: a velocity written from a StarMap hook is overwritten by the next
  `ApplyVehicleSolvers`.
- Thrust through the centre of mass means no torque whatever the crew weight; attitude comes only from the hold.
- The throttle and engine switch are read by reflection on two private `Vehicle` fields; a rename leaves them
  null and the rockets cannot be lit from the keys.
- Downforce alone does nothing under warp (no tyre grip to help).
- The prefix must not throw: `Buggies.Physics` catches, drops the car from the active set and logs once.
- Above the atmosphere KSA rails a craft with no engine firing; a railed craft ignores the deltas, so the burning
  car is marked in the same prefix.

## See also

- [Overview and comparison](overview.md)
- [Approach 1: velocity deltas in the prefix](inject-forces-and-impulses-at-the-physics-safe-point-1.md), [approach 5: PID hover with real engines](inject-forces-and-impulses-at-the-physics-safe-point-5.md)
- Mod feature: [KSACars rocket lift, hover, boost, downforce](../../../../mods/ksacars/features/rocket-lift-hover-boost-downforce.md)
- [Keep a craft under full physics while your mod pushes it](../../solver/keep-a-pushed-craft-off-rails/overview.md)
