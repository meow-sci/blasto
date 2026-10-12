---
type: How-To
title: Postfix PhysicsBubble.KittenWantsWake from a concurrent set
description: Make the per-craft wake check answer true for craft your mod is pushing, mark them from the PrepareWorker prefix, and wake a landed or coasting craft before the first push
category: physics
group: solver
topic: keep-a-pushed-craft-off-rails
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksacars/features/keep-thrusting-craft-off-rails]
techniques: [harmony-postfix, reflection]
hooks:
  - harmony-postfix KSA.PhysicsBubble.KittenWantsWake
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - starmap StarMapAllModsLoaded
lifecycle: [startup:all-mods-loaded, frame:dispatch-solvers, physics:vehicle-solver]
game_types: [KSA.PhysicsBubble, KSA.VehicleUpdateState, KSA.Vehicle, KSA.PhysicsStates]
tags: [physics, rails, physics-bubble, wake, harmony, postfix, concurrentdictionary, rockets]
---

Outcome: a flying car (or any craft pushed by something the game does not count as an engine) stays under full
physics above the atmosphere, so velocity writes keep working, and a parked car wakes the moment it is driven.
Prerequisites: StarMap and Harmony. KSACars patches the one per-craft question both rails decisions ask and
wakes a railed car before it pushes it.

## Background: how the game does it

KSA rails a craft whenever nothing says otherwise. A railed single-vehicle bubble moves a `Freefall` craft with
`ApplySingleFreefallMotion` and a `Landed`, `Floating` or `Bottomed` craft with `ApplySingleSurfaceMotion`;
neither integrates `Kinematic.VelocityPhys`. `PhysicsBubble.KittenWantsWake(VehicleUpdateState)` is a private
static method meant for a kitten on EVA. It is called in both places the bubble decides rails status:

```csharp
// current/decomp/KSA/PhysicsBubble.cs L1800-L1807
	private static bool KittenWantsWake(VehicleUpdateState vehicleState)
	{
		if (!vehicleState.IsKitten)
		{
			return false;
		}
		int result;
```

| Call site | When it runs | What true does |
|---|---|---|
| `ApplySingleVehicleMotion` (L1291) | A bubble with one vehicle, start of its step | `situation.WithOnRails(false)`: the step goes to full physics |
| `FullPhysicsStepUntil` (L1443) | Every full-physics sub-step, per vehicle | `SetOnRails(false)`, and the else-chain that would rail the craft is skipped |

## Lifecycle and timing

| Piece | Hook | Thread | Lifecycle |
|---|---|---|---|
| Install | `[StarMapAllModsLoaded]` | main | `startup:all-mods-loaded` |
| Mark, rebuild, `TakeOffRails` | prefix on `Vehicle.PrepareWorker` | main | `frame:dispatch-solvers` |
| Postfix on `KittenWantsWake` | called from `PhysicsBubble.StepFrameTo` via `BubbleStepSegmentJob` | vehicle-solver worker | `physics:vehicle-solver` |

The mark is written on the main thread before the step is dispatched and read on worker threads during it, so
the set is a `ConcurrentDictionary`. The rebuild and `TakeOffRails` happen in the prefix because that is where the
vehicle's own state is written just before `PrepareFromVehicle` snapshots it (see
[the PrepareWorker window](../../solver/mutate-vehicles-at-a-physics-safe-point/mutate-vehicles-at-a-physics-safe-point-1.md)).
Once the rockets are cut the mark is removed, KSA may rail the car again, and it coasts like any craft.

## Steps

1. Validate the private target, then patch. `KittenWantsWake` is private, so it cannot be pinned by a compile-time
   reference; check return and parameter types at install and refuse to patch if they moved:

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

2. Answer only "yes". The postfix takes the first argument by position (`__0`), so a rename of the parameter does
   not break it, and turns a `false` into `true` for marked craft. It never turns a `true` into `false`, so the
   kitten's own logic is untouched. It runs on the worker many times per step: no throw, no allocation, one
   concurrent dictionary lookup:

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

3. Mark the craft every physics window from the `PrepareWorker` prefix. `burning` is "my push is active this step"
   (lift thrust above zero or boost firing, not downforce alone). A car that leaves the world is forgotten:

```csharp
// src/KSACars/Ksa/Buggies.cs L563-L566
        // KSA puts a craft out of the air onto its orbit unless an engine of its own is firing.
        bool burning = e.Rockets > 0.0 || e.Boosting;
        RailsHook.Mark(craft, burning);
```

4. Before the first push, wake a railed craft correctly. A parked car on rails with nobody at the wheel does
   nothing and costs nothing. A car coasting on its orbit (railed, no contact) with rockets burning is rebuilt from
   the orbit as KSA rebuilds a craft it is about to split; one standing on the ground is on rails too but held to
   the ground, so it is only woken after the push with `Vehicle.TakeOffRails()`:

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

   Do not rebuild a landed craft from its orbit: it is held to the ground, not to that orbit, and rebuilt from it
   the car would be left standing still in space while the planet turns away at hundreds of m/s.

5. Expect two install lines in the log and nothing else on success. If the signature moved you get
   `KSA's wake check has moved; a car's rockets stop pushing above the atmosphere` and the feature is simply
   off.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/RailsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RailsHook.cs) | Validates the private target, patches it, `Mark` and `Forget`, the postfix |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L564-L598) | Mark, early returns, `UpdateFromAnalytic` for a coasting car |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L37-L57) | Installs the hook with the others |

## Variations and gotchas

- The postfix is matched by name. An overload or rename disables the hook (logged), so keep the feature
  optional.
- It runs per craft per full-physics sub-step on vehicle-solver worker threads; it must not throw, allocate or touch
  main-thread state. Keep the marked set small and concurrent
  ([worker-thread patterns](../../solver/write-worker-thread-safe-solver-patches/overview.md)).
- Asserting rails (the reverse) is undone by any commanded actuator on the same sub-step, so it only works
  together with a released attitude.
- KSA's chain also forces full physics for a freefall craft inside or entering the atmosphere
  (`FreefallNeedsFullPhysics`), so the hook matters above the atmosphere or on the ground.
- Marks must be cleared when the push ends and on a save load, or the craft is held in the bubble forever.

## See also

- [Overview and comparison](overview.md)
- [Approach 2: the flight computer command sink](keep-a-pushed-craft-off-rails-2.md)
- Mod feature: [KSACars keep a thrusting craft off rails](../../../../mods/ksacars/features/keep-thrusting-craft-off-rails.md)
- [Add velocity deltas in the PrepareWorker prefix](../../solver/inject-forces-and-impulses-at-the-physics-safe-point/inject-forces-and-impulses-at-the-physics-safe-point-1.md)
