---
type: Mod Feature
title: Keep a craft with mod thrust off rails
description: Stop KSA from putting a car onto its orbit while the mod's rockets burn by answering yes to the private PhysicsBubble.KittenWantsWake, the per-craft question both rails decisions ask, and rebuild a coasting car from its orbit before pushing it
mod: ksacars
category: patching
techniques: [harmony-postfix, reflection, custom-physics, background-thread]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.PhysicsBubble.KittenWantsWake
  - harmony-prefix KSA.Vehicle.PrepareWorker
lifecycle: [startup:all-mods-loaded, frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.PhysicsBubble
  - KSA.VehicleUpdateState
  - KSA.PhysicsStates
  - KSA.Vehicle
  - KSA.Situation
  - KSA.SituationEx
  - KSA.Orbit
tags: [patching, harmony, postfix, private-method, on-rails, orbit, physics-bubble, worker-thread]
---

KSA puts a craft "on rails" whenever it can: a landed craft that has stood still is held to the
ground, and a craft coasting above the atmosphere is moved along its orbit analytically. Only a
firing engine, an active actuator and a few other reasons keep it under full physics. A car's
rockets are not engines, so above the atmosphere KSA would rail a flying car, and on rails nothing
the mod writes to its velocity is read: the car would coast and fall until the air gave it back.
`Ksa/RailsHook.cs` patches the one per-craft question both rails decisions ask, and the physics
prefix wakes or rebuilds a railed car before it pushes it.[^ksacars-railshook][^ksacars-claude]

## How it works

**The question KSA asks.** `PhysicsBubble.KittenWantsWake(VehicleUpdateState)` is a private static
method meant for a kitten on EVA: it returns false for any non-kitten, and for a kitten returns true
while thruster keys or grab are held or a ladder move is pending.[^ksa-physicsbubble] It is called
in both places the bubble decides rails status:

| Call site | When it runs | What a `true` does |
|---|---|---|
| `ApplySingleVehicleMotion` (L1291) | A bubble with one vehicle, at the start of its step | `situation.WithOnRails(false)`, so the step goes to `StepFullPhysicsTo` instead of analytic freefall or surface motion |
| `FullPhysicsStepUntil` (L1443) | Every full-physics sub-step, per vehicle | `SetOnRails(false)`; it also skips the else-chain that would rail the craft |

In `FullPhysicsStepUntil` the wake reasons (actuator commanded or active, high-fidelity ocean,
keyframe animation, `KittenWantsWake`) come first; only if none fired does the chain reach
`CanInstantlyFloatOnRails`, `TryToPutCoastingOnRails` (above the atmosphere inside the physics
radius, no predicted impact within about 3 s plus a lead) or `PhysicsStates.TryToPutOnRails`. The last one rails a craft
*outside* the body's physics radius at once, with no motionless timer: that is the separate route
beyond the physics radius, and it sits behind the same wake check.[^ksa-physicsbubble][^ksa-physicsstates]

**The patch.** `RailsHook.Install` finds the method with
`AccessTools.Method(typeof(PhysicsBubble), "KittenWantsWake")` and checks it returns `bool` and its
first parameter is `VehicleUpdateState`; otherwise it warns "KSA's wake check has moved; a car's
rockets stop pushing above the atmosphere" and does not patch. The postfix takes `__0` (matched by
position, whatever KSA calls it) and `ref bool __result`, and turns a `false` into `true` when
`__0.ReadOnlyVehicle` is in a `ConcurrentDictionary<Vehicle, byte>` of burning cars.[^ksacars-railshook]

**Marking.** `Buggies.Step` calls `RailsHook.Mark(craft, burning)` every physics window, where
`burning` is lift thrust above zero or the boost firing. Downforce alone does not count. A car that
leaves the world is `Forget`-ed in `Buggies.Sync`.[^ksacars-buggies]

**Before pushing a railed car.** In the same prefix, after marking:[^ksacars-buggies]

| Car state | What the mod does | Why |
|---|---|---|
| On rails, nobody at the wheel, no rockets | returns at once | parked: exactly where it was, costs nothing |
| On rails with no contact (coasting on its orbit), rockets burning | `states.UpdateFromAnalytic(orbit, orbit.StateVectors, craft.Body2Cce, craft.BodyRates, Situation.Maneuvering)` | the kinematic state is stale while railed; KSA does the same in `Vehicle.Split` before splitting a railed craft |
| On rails with no contact, rockets out | returns | KSA's to coast |
| On rails on the ground, being driven | pushes, then `craft.TakeOffRails()` | woken as KSA wakes a landed craft something bumped; rebuilding from the orbit would leave it standing in space while the planet turns away |

**Why a railed craft ignores velocity writes.** On rails, a single-vehicle bubble moves a
`Freefall` craft with `ApplySingleFreefallMotion`, which takes `vehicleState.CurrentOrbit` and its
state vectors at the next time, and a `Landed`/`Floating`/`Bottomed` craft with
`ApplySingleSurfaceMotion`. Neither integrates `Kinematic.VelocityPhys`; only the off-rails
situations (`Maneuvering`, `Rolling`, `Sailing`, `Dragging`) reach `StepFullPhysicsTo`.[^ksa-physicsbubble]

## Lifecycle and timing

| Piece | Hook | Thread | Lifecycle |
|---|---|---|---|
| Install | `[StarMapAllModsLoaded]` -> `RailsHook.Install` | main | `startup:all-mods-loaded` |
| `Mark`, `UpdateFromAnalytic`, `TakeOffRails` | Harmony prefix on `Vehicle.PrepareWorker` (inside `Universe.ExecuteNextVehicleSolvers`) | main | `frame:dispatch-solvers` |
| `AfterWantsWake` | postfix on `PhysicsBubble.KittenWantsWake`, called from `PhysicsBubble.StepFrameTo` via `BubbleStepSegmentJob` | vehicle-solver worker | `physics:vehicle-solver` |

The mark is written on the main thread before the step is dispatched and read on worker threads
during it, hence the `ConcurrentDictionary`.[^ksacars-railshook][^ksa-bubblestepjob] The rebuild and
`TakeOffRails` happen in the prefix because that is where the vehicle's own state is written just
before `PrepareFromVehicle` snapshots it; `TakeOffRails` only clears the on-rails bit in the
vehicle's `_props`.[^ksa-vehicle] Once the rockets are cut the mark is removed, KSA may rail the car
again, and it coasts like any craft.

## Key code

| Path | Role |
|---|---|
| [src/KSACars/Ksa/RailsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RailsHook.cs) | Validates the private target, patches it, `Mark`/`Forget`, the postfix |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L564-L598) | Mark, early returns, `UpdateFromAnalytic` for a coasting car |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L710-L713) | `TakeOffRails` for a landed car |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L105-L117) | `RailsHook.Forget` when a car leaves |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L37-L57) | Installs the hook with the others |

## Snippets

```csharp
// src/KSACars/Ksa/RailsHook.cs L46-L61
        try
        {
            MethodInfo? target = AccessTools.Method(typeof(PhysicsBubble), "KittenWantsWake");
            ParameterInfo[]? parameters = target?.GetParameters();
            if (target is null || target.ReturnType != typeof(bool)
                || parameters is not [{ } first, ..] || first.ParameterType != typeof(VehicleUpdateState))
            {
                Log.Warn("KSA's wake check has moved; a car's rockets stop pushing above the atmosphere");
                return;
            }

            MethodInfo postfix = typeof(RailsHook).GetMethod(nameof(AfterWantsWake),
                                                             BindingFlags.NonPublic | BindingFlags.Static)!;
            _harmony = new Harmony(HarmonyId);
            _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            Installed = true;
```

```csharp
// src/KSACars/Ksa/RailsHook.cs L85-L92
    // Inside KSA's physics worker: nothing here may throw. The first parameter is matched by position,
    // whatever KSA names it.
    private static void AfterWantsWake(VehicleUpdateState __0, ref bool __result)
    {
        try
        {
            if (!__result && __0?.ReadOnlyVehicle is { } craft && Burning.ContainsKey(craft)) __result = true;
        }
```

```csharp
// src/KSACars/Ksa/Buggies.cs L577-L598
        bool railed = craft.Situation.IsOnRails();
        e.Railed = railed;
        bool idle = e.Input.Throttle == 0.0 && e.Input.Steer == 0.0 && !e.Lit && !e.HatchWanted[(int)ThrusterGroup.Boost] && !e.Downforce;

        // KSA only rails a car that has stood still, so one on rails with nobody at the wheel is parked:
        // it is exactly where it was, and costs nothing. Its speed from before it stopped says nothing.
        bool righting = ToRight.Remove(craft, out bool tip);
        if (railed && idle && !righting) return;

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
```

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1434-L1457 (KSA, build 2026.10.10.5554)
		for (int j = 0; j < _vehicleStates.Count; j++)
		{
			VehicleUpdateState vehicleUpdateState2 = _vehicleStates[j];
			PhysicsStates newStates = vehicleUpdateState2.GetNewStates();
			vehicleUpdateState2.UpdateActiveNozzles(context.IntraStepTime);
			bool anyActuatorCommanded = vehicleUpdateState2.FlightComputerOutput.AnyActuatorCommanded;
			bool flag = vehicleUpdateState2.AnyActuatorActive();
			bool flag2 = vehicleUpdateState2.UseHighFidelityOceanPhysics && newStates.Props.Situation.IsFreeInOcean();
			bool flag3 = KeyframeAnimationModule.AnyAnimating(vehicleUpdateState2.ReadOnlyVehicle.Parts.States);
			bool flag4 = KittenWantsWake(vehicleUpdateState2);
			if (_forceOffRails)
			{
				newStates.Props.SetOnRails(isOnRails: false);
			}
			else if (anyActuatorCommanded || flag || flag2 || flag3 || flag4)
			{
				// ...
				newStates.Props.SetOnRails(isOnRails: false);
			}
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `PhysicsBubble.KittenWantsWake(VehicleUpdateState)` (private static) | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1800-L1820) | Patch target |
| `PhysicsBubble.ApplySingleVehicleMotion` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1264-L1351) | Call site 1; the rails/full-physics switch |
| `PhysicsBubble.FullPhysicsStepUntil` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1434-L1478) | Call site 2; the rails else-chain |
| `PhysicsBubble.TryToPutCoastingOnRails` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1180-L1213) | Rails a coasting craft above the atmosphere inside the physics radius |
| `PhysicsBubble.StepFrameTo`, `ApplySingleFreefallMotion` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L688-L714) | Single vs multi-vehicle dispatch; railed freefall follows the orbit (L2640-L2659) |
| `PhysicsStates.TryToPutOnRails` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L927-L949) | Rails at once outside `InPhysicsRadius` |
| `PhysicsStates.UpdateFromAnalytic(Orbit, in StateVectors, doubleQuat, double3, Situation)` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L835-L840) | Rebuild kinematic state from the orbit |
| `Vehicle.TakeOffRails()`; `Vehicle.Split` rebuilding a railed craft | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L933-L936) | Clear the on-rails bit; KSA's own `UpdateFromAnalytic(..., Maneuvering)` at L1546-L1549 |
| `VehicleUpdateState.ReadOnlyVehicle` | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L14) | From the worker's state back to the `Vehicle` key |
| `SituationEx.IsOnRails`, `HasAnyContact`, `WithOnRails` | [current/decomp/KSA/SituationEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SituationEx.cs#L18-L95) | Rails bit and contact tests |

## Pitfalls and version notes

- **A private target cannot be pinned.** The public patches in this mod each have a never-called
  `PinTheSignature` so a KSA change is a build error; this one validates return and parameter types
  at install instead, and if they moved the car's rockets simply stop pushing above the atmosphere.[^ksacars-railshook]
- **Runs on the worker, many times per step.** The postfix is called per craft per full-physics
  sub-step on vehicle-solver worker threads: it must not throw, allocate or touch main-thread state;
  it reads one concurrent dictionary.
- **Answer only "yes".** The postfix never turns KSA's `true` into `false`, so a kitten's own wake
  logic is untouched.
- **Do not rebuild a landed car from its orbit.** A railed landed craft is held to the ground, not
  to its orbit; `UpdateFromAnalytic` would leave it in space while the planet turned away at
  hundreds of m/s. The mod only calls it when there is no contact.[^ksacars-buggies]
- **Name-based lookup.** `KittenWantsWake` is matched by name; an overload or rename disables the
  hook (logged).

## See also

- [Rocket lift, hover, boost and downforce](./rocket-lift-hover-boost-downforce.md)
- [PrepareWorker impulse injection](./prepareworker-impulse-injection.md)
- [API surface pinning and diff](./api-surface-pinning-and-diff.md)
- [KSACars overview](../overview.md)
- [Run code inside the physics step on worker threads](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md) - how-to article demonstrating this feature
- [Worker-side postfix merging commands from the main thread](../../../howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-3.md) - how-to article demonstrating this feature
- [Postfix PhysicsBubble.KittenWantsWake from a concurrent set](../../../howto/physics/solver/keep-a-pushed-craft-off-rails/keep-a-pushed-craft-off-rails-1.md) - how-to article demonstrating this feature

[^ksacars-railshook]: src/KSACars/Ksa/RailsHook.cs
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-claude]: CLAUDE.md, "The cars"
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-bubblestepjob]: KSA.BubbleStepSegmentJob (decompiled)
