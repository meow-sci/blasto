---
type: How-To
title: Postfix the single non-inlined outer method instead of a per-vehicle callee
description: Run per-vehicle state machines right after the physics results are applied by patching the static, non-inlined Universe.ApplyVehicleSolvers with a prefix and a postfix, because the per-vehicle callee Vehicle.UpdateFromTaskResultsSynchronized is AggressiveInlining
category: patching
group: internals
topic: patch-around-aggressive-inlining
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick
  - mods/ksa-advancedflightcomputer/features/validated-reflection-handles
techniques: [harmony-postfix, harmony-prefix, outer-method-hook]
hooks: [harmony-prefix KSA.Universe.ApplyVehicleSolvers, harmony-postfix KSA.Universe.ApplyVehicleSolvers]
lifecycle: [startup:all-mods-loaded, frame:apply-solvers]
game_types: [KSA.Universe, KSA.Vehicle, KSA.VehicleUpdateTask, KSA.PhysicsBubble]
tags: [harmony, postfix, inlining, aggressiveinlining, apply-solvers, main-thread]
---

KSA-AdvancedFlightComputer needs one hook that runs every frame, on the main thread, after every vehicle has received the results of the physics step that just finished. A per-vehicle method looks like a better
fit, but the per-vehicle half of the apply is `AggressiveInlining`, which a detour on the callee cannot be trusted to survive. The mod hangs a prefix and a postfix on the static `Universe.ApplyVehicleSolvers`
instead, a plain method that is called once per frame.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by AFC in [Main-thread vehicle tick right after the solver apply](../../../../mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick.md) and [Validated reflection handles with per-feature tags](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md).

## Background: how the game does it

`Universe.ApplyVehicleSolvers` has no vehicle parameter. It computes the achieved speed fraction, then calls `_vehicleUpdateTask.ApplyResultsToMainThread()` and refreshes per-frame data:

```csharp
// current/decomp/KSA/Universe.cs L1922-L1952
public static void ApplyVehicleSolvers()
{
	if (CurrentSystem != null)
	{
		double num = JobSystems.VehicleSolver.GetMaxLastTickTime() * 0.001;
		double y = 1.0 / (double)GameSettings.Current.Simulation.MinTargetFrameRate;
		double num2 = double.Min(_nextPlayerDeltaTime, y);
		double num3;
		if (_nextPlayerDeltaTime.IsExactlyZero() || num.IsNearlyZero())
		{
			num3 = 1.0;
		}
		else
		{
			double num4 = 0.9;
			num3 = double.Min(1.0, num4 * num2 / num);
		}
		if (num3 < _achievedSpeedFraction)
		{
			_achievedSpeedFraction = num3;
		}
		else
		{
			_achievedSpeedFraction = 0.9 * _achievedSpeedFraction + 0.1 * num3;
		}
		_vehicleUpdateTask.ApplyResultsToMainThread();
		_lastSimStep = _nextSimStep;
		_lastPlayerDeltaTime = _nextPlayerDeltaTime;
		CurrentSystem.UpdatePerFrameData();
	}
}
```

Source: [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952)[^ksa-universe]

`ApplyResultsToMainThread` applies each physics bubble's results in a parallel batch on the vehicle worker pool, then, for each bubble, calls the per-vehicle `ApplySynchronizedResultsToVehicles`:

```csharp
// current/decomp/KSA/VehicleUpdateTask.cs L226-L259
public void ApplyResultsToMainThread()
{
	using (ParallelBatch parallelBatch = JobSystems.VehicleWorkerPool.ParallelBatch())
	{
		foreach (PhysicsBubble bubble in _bubbles)
		{
			if (bubble.IsResultsReady)
			{
				parallelBatch.Execute<BubbleApplyResultsJob, PhysicsBubble>(bubble);
			}
		}
	}
	foreach (PhysicsBubble bubble2 in _bubbles)
	{
		if (bubble2.IsResultsReady)
		{
			bubble2.RethrowApplyFailure();
		}
	}
	foreach (PhysicsBubble bubble3 in _bubbles)
	{
		if (bubble3.IsResultsReady)
		{
			bubble3.ApplySynchronizedResultsToVehicles();
		}
	}
	foreach (PhysicsBubble bubble4 in _bubbles)
	{
		if (bubble4.IsResultsReady)
		{
			bubble4.ApplyRenderEventsToVehicles();
		}
	}
}
```

Source: [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L226-L259)[^ksa-vehicleupdatetask]

```csharp
// current/decomp/KSA/PhysicsBubble.cs L911-L917
public void ApplySynchronizedResultsToVehicles()
{
	foreach (VehicleUpdateState vehicleState in _vehicleStates)
	{
		vehicleState.ReadOnlyVehicle.UpdateFromTaskResultsSynchronized();
	}
}
```

Source: [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L911-L917)[^ksa-physicsbubble]

The per-vehicle method it ends in is marked for inlining:

```csharp
// current/decomp/KSA/Vehicle.cs L2546-L2547
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public void UpdateFromTaskResultsSynchronized()
```

Source: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2546-L2547)[^ksa-vehicle]

## Lifecycle and timing

Install at `startup:all-mods-loaded`. Run: once per frame in `frame:apply-solvers`, inside `Program.PrepareFrame`, which calls `Universe.ApplyVehicleSolvers` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2166-L2167)[^ksa-program]) after the solver jobs have been
joined. At the postfix the vehicles hold the finished step's state, no worker is running, and the next step has not been dispatched, so it is the safest main-thread window to mutate simulated state. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Hook the frame with StarMap lifecycle attributes](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md).

## Steps

1. **Resolve the method with an empty parameter list.** `Universe.ApplyVehicleSolvers` takes no parameters; AFC tags its handle for the features that need it and builds it with `Type.EmptyTypes`:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L204-L210
// The per-frame tick MultiPass, RcsTranslation and AutoStage drive their state machines from.
// It runs on the main thread after the solver results are applied to every vehicle and before InputEvents.ApplyInputEvents, so a driver reads fresh FlightComputer state and can still queue burn mutations for the same frame's drain.
// AutoStage takes back a worker's FlightComputer.AutoBurnStoppedOutOfPropellant here, before the input drain and before FlightComputer.RaisePendingAlerts acts on it later in Program.PrepareFrame. The RCS executor raises that flag and FlightComputer.AutoBurnCompleted here for its own burns, after AutoStage has run.
// Neither per-vehicle half of the apply is a sound host, because Vehicle.UpdateFromTaskResultsUnsynchronized runs one worker per physics bubble and Vehicle.UpdateFromTaskResultsSynchronized is aggressively inlined, which a Harmony detour on the callee cannot survive.
[UsedBy(Feature.Core | Feature.MultiPass | Feature.RcsTranslation | Feature.AutoStage)]
public static readonly MethodInfo? Universe_ApplyVehicleSolvers =
    AccessTools.Method(typeof(Universe), nameof(Universe.ApplyVehicleSolvers), Type.EmptyTypes);
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L204-L210)[^ksa-advancedflightcomputer-gamereflection]

2. **Patch it with a prefix to sample and a postfix to tick.** The prefix runs before the results overwrite the state, so it records the baseline; the postfix runs after, and walks the vehicles:

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L108-L120
// Program.PrepareFrame joins the workers before this call, then drains input events, then lets FlightComputer.RaisePendingAlerts end the burns the workers or the RCS executor finished or stopped.
// The prefix runs before the worker results overwrite the burn state the staging detector compares against.
[HarmonyPatch(typeof(Universe), nameof(Universe.ApplyVehicleSolvers), new Type[0])]
private static class ApplySolversPatch
{
    static void Prefix()
    {
        if (AutoStageEnabled)
            StagingDetector.Sample();
    }

    static void Postfix() => TickVehicles(LoadedVehicles.All);
}
```

Source: [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L108-L120)[^ksa-advancedflightcomputer-sharedvehiclehooks]

3. **Enumerate the live registry, not a cache.** `Program.VehiclesInFrame` is refreshed after the solver apply, so it is one frame stale here; AFC walks `Universe.CurrentSystem.All`:

```csharp
// AdvancedFlightComputer/Core/LoadedVehicles.cs L14-L27
/// </summary>
internal static class LoadedVehicles
{
    /// <summary><c>Vehicle.Dispose(bool)</c> sets IsDisposed before it deregisters, so callers test
    /// the flag rather than rely on a disposed vehicle being gone from here.</summary>
    public static ReadOnlySpan<Astronomical> All
    {
        get
        {
            CelestialSystem? system = Universe.CurrentSystem;
            return system == null ? default : system.All.AsSpan();
        }
    }
}
```

Source: [AdvancedFlightComputer/Core/LoadedVehicles.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LoadedVehicles.cs#L14-L27)[^ksa-advancedflightcomputer-loadedvehicles]

4. **Tick each vehicle, skipping disposed ones.**

```csharp
// AdvancedFlightComputer/Core/SharedVehicleHooks.cs L38-L55
internal static void TickVehicles(ReadOnlySpan<Astronomical> bodies)
{
    // Staging first, so a staging that keeps an Auto burn alive has taken back the stock out-of-propellant stop before anything else reads it.
    if (AutoStageEnabled)
        StagingDetector.Evaluate();

    foreach (Astronomical body in bodies)
    {
        if (body is not Vehicle vehicle || vehicle.IsDisposed)
            continue;

        // The pass state machine reads the burn mode before the RCS driver can change it.
        if (MultiPassEnabled)
            PassCompletionPatch.TickVehicle(vehicle);
        if (RcsEnabled)
            RcsDriverPatch.TickVehicle(vehicle);
    }
}
```

Source: [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L38-L55)

5. **Guard against a callee that goes silent.** AFC records, next to another handle, that a private method with one call site would leave a resolving handle and a silent postfix if a game update added an inlining attribute, and names the behavioural test that checks the effect arrives:

```csharp
// AdvancedFlightComputer/Core/GameReflection.cs L324-L329
// Stock builds AttitudeTarget here and reads it in UpdateAttitudeError right after, so a postfix can add the commanded turning rate without changing where the craft points.
// It is a private method with one call site, so an inlining attribute added on a game update would leave this handle resolving and the postfix silent. afc-guidance-driver checks that the rate arrives.
[UsedBy(Feature.Guidance)]
public static readonly MethodInfo? FlightComputer_UpdateAttitudeTarget =
    AccessTools.Method(typeof(FlightComputer), "UpdateAttitudeTarget",
        new[] { typeof(FlightComputerNavigation).MakeByRefType() });
```

Source: [AdvancedFlightComputer/Core/GameReflection.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/GameReflection.cs#L324-L329)

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/SharedVehicleHooks.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SharedVehicleHooks.cs#L38-L120) | The prefix, postfix and per-vehicle tick |
| [AdvancedFlightComputer/Core/LoadedVehicles.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/LoadedVehicles.cs) | The registry walk |

## Variations and gotchas

- **No per-vehicle detail.** The outer hook does not say which vehicle changed; the mod enumerates all of them. Do not do expensive work per vehicle per frame here, because it lengthens the frame before the next step is dispatched.
- **`UpdateFromTaskResultsUnsynchronized` runs on workers,** once per bubble, so it is not a sound host for main-thread code either.
- **A check you can run.** The method is static and not marked, so a prefix counter on it is a reliable "did the frame reach the apply" test ([Changing behaviour of a game method marked AggressiveInlining](overview.md)).
- **Shared owner.** The prefix and postfix are one patch class; a failing feature block leaves the shared patch installed and only disables its own driver.

## See also

- [Rewrite the one caller's call site instead of the inlined callee](patch-around-aggressive-inlining-1.md), [Patch the non-inlined entry points that reach the inlined method too](patch-around-aggressive-inlining-2.md), [Bracket the non-inlined methods that enclose an inlined struct callback](patch-around-aggressive-inlining-4.md)
- [Changing behaviour of a game method marked AggressiveInlining](overview.md)
- [Main-thread vehicle tick right after the solver apply](../../../../mods/ksa-advancedflightcomputer/features/post-solver-vehicle-tick.md), [Validated reflection handles with per-feature tags](../../../../mods/ksa-advancedflightcomputer/features/validated-reflection-handles.md)
- [Validating patch targets and detecting game-update breakage](../../safety/validate-patch-targets-and-detect-game-update-breakage/overview.md)
- Related topic: drive-the-flight-computer-and-attitude-modes-from-a-mod

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: KSA.VehicleUpdateTask (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-advancedflightcomputer-gamereflection]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/GameReflection.cs
[^ksa-advancedflightcomputer-sharedvehiclehooks]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/SharedVehicleHooks.cs
[^ksa-advancedflightcomputer-loadedvehicles]: KSA-AdvancedFlightComputer/AdvancedFlightComputer/Core/LoadedVehicles.cs
