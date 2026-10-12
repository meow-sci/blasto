---
type: How-To
title: Run code inside the physics step on worker threads
description: Patch methods the vehicle solver runs per substep or per collision pass on worker threads, and exchange data with the main thread without races
category: getting-started
group: lifecycle
topic: game-loop-and-lifecycle
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksaero/features/computedrag-call-site-transpiler
  - mods/ksaero/features/body-of-revolution-aerodynamics
  - mods/ksacars/features/keep-thrusting-craft-off-rails
  - mods/ksagolf/features/course-colliders-as-clutter
  - mods/ksa-advancedflightcomputer/features/flight-computer-command-sink
techniques: [harmony-transpiler, harmony-postfix, harmony-prefix, custom-physics, bepu-physics]
hooks:
  - harmony-transpiler KSA.PhysicsStates.ComputeDerivatives
  - harmony-postfix KSA.PhysicsBubble.KittenWantsWake
  - harmony-prefix KSA.ConstraintSim.DetectCollisions
  - harmony-prefix KSA.ConstraintSim.Simulate
  - harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers
lifecycle: [physics:vehicle-solver, physics:bepu, frame:dispatch-solvers]
game_types: [KSA.PhysicsStates, KSA.PhysicsBubble, KSA.VehicleUpdateState, KSA.ConstraintSim, KSA.Universe]
tags: [physics, worker-thread, substep, transpiler, bepu, aerodynamics, threading]
---

A force that changes within a frame (aerodynamics, buoyancy, a spring at high frame time) has to be
evaluated where the engine evaluates its own forces: inside the vehicle solver, per substep, on worker
threads. Approach 3 writes once per frame before the snapshot; this approach patches the solver itself.
It is the most powerful hook point and the least forgiving: code runs off the main thread, in parallel
across vehicles, many times per frame.

Prerequisites: approach 2 (Harmony patching), C# threading basics. Demonstrated by KSAero (aero force
model swapped inside `ComputeDerivatives`), KSACars (a worker-side rails decision fed from the main
thread) and KSAGolf (static colliders synced into each bubble's Bepu simulation).

## Background: how the game does it

`ExecuteNextVehicleSolvers` queues one `VehicleUpdateTask` on the `VehicleSolver` scheduler
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126-L2139); its `Run` fans bubbles out over `VehicleWorkerPool`
[current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298). Inside a bubble ([overview](overview.md#worker-side)):

- substeps are variable; each runs an `UpdateModulesJob` per vehicle (module updaters, active nozzles,
  active chutes) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L29-L44) and then a constrained or unconstrained integration
  step [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1631-L1645);
- `PhysicsStates.ComputeDerivatives` computes gravity, drag, buoyancy, thrust and parachute forces
  [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956-L1087); it runs twice per Verlet substep, 12 times per RK8 long step and
  once per constrained substep;
- constrained substeps call `ConstraintSim.DetectCollisions` and `ConstraintSim.Simulate`, which
  steps Bepu with a dispatcher over the worker pool [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L870-L904);
- on/off-rails decisions consult the private `PhysicsBubble.KittenWantsWake` per vehicle
  [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1291) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1443) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1800).

The solver has no events, delegates or per-step virtual methods on modules; Harmony on concrete methods
is the only way in. An exception inside a bubble job is caught and marks the bubble failed
[current/decomp/KSA/IBubbleJob.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IBubbleJob.cs#L10-L20); a failed bubble does not publish its results
[current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1115-L1121).

## Lifecycle and timing

| Patch target | Thread | Rate | Reads | Must not touch |
|---|---|---|---|---|
| `PhysicsStates.ComputeDerivatives` (call sites inside it) | `VehicleWorkerPool` | 1, 2 or 12 times per substep per vehicle | its `in` parameters: origin, kinematics, props, environment, nozzles, chutes | anything outside its arguments and immutable mod data |
| `PhysicsBubble.KittenWantsWake` | `VehicleWorkerPool` / `VehicleSolver` runner | per vehicle per rails decision | the `VehicleUpdateState` argument | main-thread collections without synchronization |
| `ConstraintSim.DetectCollisions`, `ConstraintSim.Simulate` | worker | per collision pass / Bepu timestep, per bubble | the `ConstraintSim` instance, a snapshot published by the main thread | the shared shape registry (locked while stepping) |
| `Universe.ExecuteNextVehicleSolvers` (companion prefix) | main | once per frame | everything | - |

Rules for worker-side code: no ImGui, no Vulkan, no `InputEvents`, no allocation on the hot path, no
locks the main thread may hold, no exceptions (catch everything). Data from the main thread arrives
through an immutable snapshot published with `volatile` or a concurrent collection, written in the
settled window (approach 3) or in a `PrepareWorker` prefix.

## Steps

1. Pick the narrowest method that sees the quantity you change. For forces, that is the call site in
   `ComputeDerivatives`. `ComputeDrag` itself is private and `AggressiveInlining`
   [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089-L1090), so a patch on it would be skipped wherever the JIT inlined it;
   rewrite the call instead.

2. Resolve both methods by exact signature and fail loudly if either is missing. KSAero:

```csharp
// KSAero/AirForcePatch.cs L28-L59
private delegate void ComputeDragFn(in BubbleOrigin origin, in KinematicStates kinematic, in VehicleProperties props,
    in PhysicsEnvironment environment, ref Disturbances disturbances, double3 airVelocityBub, double density,
    double dragCdA, double surfaceArea, double dt);

private static readonly MethodInfo? ComputeDerivatives = AccessTools.Method(typeof(PhysicsStates), "ComputeDerivatives", new[]
{
    typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(),
    typeof(VehicleProperties).MakeByRefType(), typeof(PhysicsEnvironment).MakeByRefType(),
    typeof(double), typeof(double), typeof(double3), typeof(double3),
    typeof(ReadOnlySpan<ActiveNozzle>), typeof(ReadOnlySpan<ActiveChute>),
});

private static readonly MethodInfo? ComputeDrag = AccessTools.Method(typeof(PhysicsStates), "ComputeDrag", new[]
{
    typeof(BubbleOrigin).MakeByRefType(), typeof(KinematicStates).MakeByRefType(),
    typeof(VehicleProperties).MakeByRefType(), typeof(PhysicsEnvironment).MakeByRefType(),
    typeof(Disturbances).MakeByRefType(), typeof(double3),
    typeof(double), typeof(double), typeof(double), typeof(double),
});

private static readonly FieldInfo? AtmosphericDensity = AccessTools.Field(typeof(PhysicsEnvironment), nameof(PhysicsEnvironment.AtmosphericDensity));

private static ComputeDragFn? _stockDrag;

internal static void Apply(Harmony harmony)
{
    if (ComputeDerivatives == null || ComputeDrag == null || AtmosphericDensity == null)
        throw new MissingMemberException("PhysicsStates.ComputeDerivatives, PhysicsStates.ComputeDrag or PhysicsEnvironment.AtmosphericDensity not found.");

    _stockDrag = AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag);
    harmony.Patch(ComputeDerivatives, transpiler: new HarmonyMethod(typeof(AirForcePatch), nameof(Transpiler)));
}
```

3. Write a transpiler that changes exactly the call you mean and refuses otherwise. KSAero swaps only
   the atmospheric `ComputeDrag` call (the one whose density argument loads
   `PhysicsEnvironment.AtmosphericDensity`, decomp [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1013)) and leaves the ocean
   call [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1005) alone:

```csharp
// KSAero/AirForcePatch.cs L61-L93
private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
{
    List<CodeInstruction> code = instructions.ToList();
    MethodInfo replacement = AccessTools.Method(typeof(AirForcePatch), nameof(ComputeAirForces));
    int replaced = 0;

    for (int i = 0; i < code.Count; i++)
    {
        if (code[i].Calls(ComputeDrag!) && PassesAtmosphericDensity(code, i))
        {
            code[i].operand = replacement;
            replaced++;
        }
    }

    // Anything else means the method has changed shape, and stock aero is the safe answer.
    if (replaced != 1)
        throw new InvalidOperationException($"Expected one atmospheric ComputeDrag call in ComputeDerivatives, found {replaced}.");

    return code;
}

private static bool PassesAtmosphericDensity(List<CodeInstruction> code, int callIndex)
{
    for (int j = callIndex - 1; j >= Math.Max(0, callIndex - ArgumentWindow); j--)
    {
        if (code[j].LoadsField(AtmosphericDensity!))
            return true;
        if (code[j].opcode == OpCodes.Call || code[j].opcode == OpCodes.Callvirt)
            return false;
    }
    return false;
}
```

4. Give the replacement the same signature and keep it pure: it may read only its arguments and
   immutable data, because it runs concurrently for every vehicle in every bubble. KSAero's
   `ComputeAirForces` falls back to the stock delegate when its model does not apply and keeps the
   engine's semi-implicit limiter so one substep cannot reverse the airspeed
   [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L95-L133).

5. Apply it from `StarMapAllModsLoaded` and unpatch on any failure, so the game keeps stock physics
   [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L16-L36).

6. When the worker-side decision needs main-thread input, publish it through a concurrent structure
   written in the settled window. KSACars marks burning cars in its `PrepareWorker` prefix
   (approach 3) [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L564-L566) and reads the mark on the worker in a
   postfix on the private `KittenWantsWake`, matched by position because the parameter name is not
   guaranteed:

```csharp
// src/KSACars/Ksa/RailsHook.cs L21-L38, L85-L99
internal static class RailsHook
{
    private const string HarmonyId = "com.ksacars.rails";

    // Written in the physics window and read from KSA's physics worker.
    private static readonly ConcurrentDictionary<Vehicle, byte> Burning = new();

    private static Harmony? _harmony;
    private static bool _complained;

    public static bool Installed { get; private set; }

    /// <summary>Says whether a car's rockets are pushing, which is what keeps it off its orbit.</summary>
    public static void Mark(Vehicle craft, bool burning)
    {
        if (burning) Burning[craft] = 0;
        else Burning.TryRemove(craft, out _);
    }
// ...
    // Inside KSA's physics worker: nothing here may throw. The first parameter is matched by position,
    // whatever KSA names it.
    private static void AfterWantsWake(VehicleUpdateState __0, ref bool __result)
    {
        try
        {
            if (!__result && __0?.ReadOnlyVehicle is { } craft && Burning.ContainsKey(craft)) __result = true;
        }
        catch (Exception e)
        {
            if (_complained) return;
            _complained = true;
            Log.Error("keeping a burning car off its orbit failed", e);
        }
    }
```

7. When the worker needs objects that can only be created on the main thread, create them in the
   settled window and swap in an immutable snapshot. KSAGolf makes Bepu shapes in a prefix on
   `ExecuteNextVehicleSolvers` (the shape registry is writable only there) and syncs them into each
   bubble's simulation from prefixes on the worker-side collision passes:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L52-L54, L81-L85, L126-L142, L157-L168
private static Harmony? _harmony;
private static volatile Wanted? _wanted;
private static volatile Ready? _ready;
// ...
        _harmony = new Harmony(HarmonyId);
        Patch(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers), nameof(BetweenVehicleSteps));
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.DetectCollisions), nameof(BeforeCollisions));
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.Simulate), nameof(BeforeCollisions));
        Patch(typeof(ConstraintSim), nameof(ConstraintSim.TryResetForPool), nameof(BeforeRecycle));
// ...
// Main thread, between vehicle steps: the one moment the shared shape registry can be written.
private static void BetweenVehicleSteps()
{
    if (_wanted is not { } wanted || _ready?.Version == wanted.Version) return;

    try
    {
        using ShapesUnlock unlocked = ConstraintSim.UnlockShapes();
        Box[] boxes = [.. wanted.Boxes.Select(b => new Box(b.Centre, ToBepu(b.Orientation), ShapeOf(unlocked.Shapes, b.Size)))];
        _ready = new Ready(wanted.Body, boxes, wanted.Version);
        Log.Info($"course is ground: {boxes.Length} slab(s), {ShapesBySize.Count} shape(s) made so far");
    }
    catch (Exception e)
    {
        Complain($"could not make the course's shapes yet, will try again: {e.GetBaseException().Message}");
    }
}
// ...
// A worker thread, before a bubble's collision pass. Nothing here may throw.
private static void BeforeCollisions(ConstraintSim __instance)
{
    try
    {
        Sync(__instance);
    }
    catch (Exception e)
    {
        Complain($"the course's ground could not be synced: {e.GetBaseException().Message}");
    }
}
```

8. Show results on the main thread by recomputing from applied state, not by reading worker
   variables. KSAero's readout re-evaluates the same model from the controlled vehicle's applied
   kinematics and environment in `StarMapAfterGui` [KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs#L31-L56).

## Complete example

- KSAero: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs), [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs), [KSAero/AeroReadout.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AeroReadout.cs).
- KSACars: [src/KSACars/Ksa/RailsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/RailsHook.cs) with its main-thread writer
  [src/KSACars/Ksa/PhysicsHook.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/PhysicsHook.cs).
- KSAGolf: [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs).

## Variations and gotchas

- Inlining: `ComputeDrag`, `ComputeBuoyancy`, `VehicleUpdateState.UpdateModules` and `UpdateActiveNozzles`
  are `AggressiveInlining` [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1089)
  [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1113-L1114)
  [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L380-L381)
  [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L410-L411); patch their callers.
  `UpdateActiveChutes` carries only `SkipLocalsInit`, not an inlining hint
  [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L440-L441). Bepu's integrator callbacks
  (`PoseIntegratorCallbacks`) are generic struct callbacks specialized into Bepu and are not practical
  Harmony targets.
- Rate: a per-substep patch can run thousands of times per frame at high warp or with many vehicles.
  Profile it; the vehicle solver's tick time feeds `_achievedSpeedFraction`, so slow patches slow
  simulated time rather than the frame rate [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1926-L1946).
- Exceptions: a throw inside a bubble job marks the bubble failed and its step is not applied; the
  player sees vehicles freeze, not a crash, and the error is logged again every frame it recurs. Catch,
  log once, and fall back to stock. KSAero takes the other route: it has no `try`/`catch` on the hot
  path and avoids throwing by guarding its divisions; its stock `ComputeDrag` delegate covers vehicles
  whose bounding box it cannot model, not exceptions.
- Ordering: the worker reads module states and props through copy-on-write
  [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L183-L239); never write the live `Vehicle` from the worker.
- Updates: transpilers break when the method changes shape. Count the matches and throw when the count
  is not exactly what you expect, as KSAero does; the mod then runs without the feature instead of with
  a wrong one.
- Kittens on EVA are vehicles too (`IsKitten`); filter in hooks that should only touch craft.

## See also

- [Game loop and mod lifecycle overview](overview.md)
- [Approach 2: Harmony patches on Program and Universe](game-loop-and-lifecycle-2.md)
- [Approach 3: mutate simulation state safely](game-loop-and-lifecycle-3.md)
