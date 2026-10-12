---
type: How-To
title: Register course statics as clutter on every pass
description: Register your own Bepu statics in a bubble's private clutter dictionary at the start of each constraint-sim pass, with infinite mass so they never displace, because clutter's own Clear can wipe the registration at any frame
category: physics
group: colliders
topic: give-ground-clutter-custom-colliders
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/course-colliders-as-clutter]
techniques: [reflection, worker-thread-patch]
hooks: [harmony-prefix KSA.ConstraintSim.DetectCollisions, harmony-prefix KSA.ConstraintSim.Simulate]
lifecycle: [physics:vehicle-solver, physics:bepu]
game_types: [KSA.BubbleClutterStatics, KSA.ConstraintSim, KSA.PhysicsBubble]
tags: [physics, clutter, statics, reflection, ground, golf, infinite-mass]
---

Outcome: static geometry the mod adds to a bubble is treated by the engine exactly like a rock the stock filter already
accepts, without a transpiler. The full mechanism, with its threading, is in
[add static colliders, approach 2](../add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-2.md);
this article keeps to the clutter-registration half. Prerequisites: StarMap and Harmony.

## Background: how the game does it

A static takes part in contacts if `BubbleClutterStatics.IsClutterStatic(handle)` says so. The registry is a private
`Dictionary<StaticHandle, ClutterStaticInfo>` named `_statics`; `ClutterStaticInfo` is a two-field struct of key and mass:[^ksa-bubbleclutterstatics]

A registered static displaces or breaks only when impact energy reaches `Mass * DisplaceEnergyPerKg`
(or `DestroyEnergyPerKg`):

```csharp
// current/decomp/KSA/BubbleClutterStatics.cs L210-L212
	public static float DisplaceEnergyPerKg = 75.5f;

	public static float DestroyEnergyPerKg = 1.5E+13f;
```

With mass `float.PositiveInfinity` that never happens. `BubbleClutterStatics.Clear` ends with
`_statics.Clear()` and `PhysicsBubble.SyncGroundClutterStatics` calls it on every sync whenever clutter collisions are off,
the bubble is not body-fixed or the planet has no clutter data, so the registration can be wiped on any frame.

## Lifecycle and timing

The registration happens inside the vehicle step, from prefixes on `ConstraintSim.DetectCollisions` and `Simulate`
(`physics:vehicle-solver`, `physics:bepu`), on the bubble's worker, once per pass. The engine's own clutter sync runs
on the main thread earlier in the frame, so a pass-time registration runs after any wipe and before contact generation.
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Reflect on the registry once at install. Take `BubbleClutterStatics._statics`, read its value type from the generic
   arguments, find the two-parameter constructor, and build one solid value with a default key and infinite mass. If any
   piece is missing, log a warning and do not patch:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L67-L86
        try
        {
            _clutterStatics = AccessTools.Field(typeof(BubbleClutterStatics), "_statics");
            Type? info = _clutterStatics?.FieldType.GetGenericArguments() is [_, var value] ? value : null;
            ConstructorInfo? make = info?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).FirstOrDefault(c => c.GetParameters().Length == 2);
            if (_clutterStatics is null || make is null)
            {
                Log.Warn("kittens walk through courses: KSA's ground clutter no longer has the _statics a course registers in");
                return;
            }

            ParameterInfo[] args = make.GetParameters();
            _solidClutter = make.Invoke([Activator.CreateInstance(args[0].ParameterType), float.PositiveInfinity]);

            _harmony = new Harmony(HarmonyId);
            Patch(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers), nameof(BetweenVehicleSteps));
            Patch(typeof(ConstraintSim), nameof(ConstraintSim.DetectCollisions), nameof(BeforeCollisions));
            Patch(typeof(ConstraintSim), nameof(ConstraintSim.Simulate), nameof(BeforeCollisions));
            Patch(typeof(ConstraintSim), nameof(ConstraintSim.TryResetForPool), nameof(BeforeRecycle));
            Installed = true;
```

2. In the worker prefix, after adding or moving your statics, write every handle into the bubble's registry. Use the
   non-generic `IDictionary` indexer so you need not name the private value type:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L170-L202
    private static void Sync(ConstraintSim sim)
    {
        Ready? ready = _ready;
        SimState state = States.GetOrCreateValue(sim);

        if (!TryOrigin(sim, out bool ccf, out IParentBody? parent, out double3 bub)) return;

        bool here = ready is { Boxes.Length: > 0 } && ccf && ReferenceEquals(parent, ready.Body)
                    && Vector3Distance(ready.Boxes[0].CentreCcf, bub) < ReachM;
        if (!here)
        {
            if (state.Handles.Length > 0) Clear(sim, state);
            return;
        }

        if (state.Version != ready!.Version)
        {
            Clear(sim, state);
            state.Handles = [.. ready.Boxes.Select(b => sim.Simulation.Statics.Add(Describe(b, bub), ref KSA.StaticsShouldntAwakenBodies.Shared))];
            state.Version = ready.Version;
            state.Bub = bub;
        }
        else if (state.Bub != bub)
        {
            for (int k = 0; k < state.Handles.Length; k++) sim.Simulation.Statics.ApplyDescription(state.Handles[k], Describe(ready.Boxes[k], bub), ref KSA.StaticsShouldntAwakenBodies.Shared);
            state.Bub = bub;
        }

        if (sim.ClutterStatics is { } clutter && _clutterStatics?.GetValue(clutter) is IDictionary registered)
        {
            foreach (StaticHandle handle in state.Handles) registered[handle] = _solidClutter;
        }
    }
```

3. Do it every pass. Re-registering at the start of every pass restores the registration after any wipe.
4. Drop the per-simulation state when a simulation is pooled (`TryResetForPool` prefix); the engine clears a pooled
   simulation's statics itself.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L63-L241) | Install, sync, registration, recycle |

## Variations and gotchas

- Infinite mass: nothing can knock your geometry loose. Use a finite mass instead if you want it to break away like a rock.
- `ClutterStaticInfo`'s constructor and `_statics` are private; if they change, installation logs
  "kittens walk through courses" and patches nothing.
- Each pass boxes every `StaticHandle` key through the non-generic indexer: a small steady allocation inside the worker step.
- A kitten treats clutter as ground via `IsGroundSurfaceFor`.

## See also

- [Overview and comparison](overview.md)
- [Add static colliders: worker prefixes](../add-static-colliders-to-physics-bubbles/add-static-colliders-to-physics-bubbles-2.md)
- Mod feature: [KSAGolf course colliders as clutter](../../../../mods/ksagolf/features/course-colliders-as-clutter.md)

[^ksa-bubbleclutterstatics]: KSA.BubbleClutterStatics (decompiled, build 2026.10.10.5554)
