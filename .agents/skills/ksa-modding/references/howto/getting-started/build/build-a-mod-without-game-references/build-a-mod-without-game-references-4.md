---
type: How-To
title: Rebuild the vehicle set each frame from Universe.CurrentSystem.All and sync a registry
description: Read the set of vehicles from the system collection instead of Program.VehiclesInFrame, which is empty after PostRender, and keep a Vehicle-keyed registry that drops disposed craft
category: getting-started
group: build
topic: build-a-mod-without-game-references
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/frame-step-latch-and-car-registry]
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Program, KSA.Universe, KSA.Vehicle, KSA.CelestialSystem]
tags: [vehicles, census, registry, framelatch, isolation, ksacars]
---

A mod that acts on specific craft (here, cars) needs to know which vehicles exist this frame. The tempting source,
`Program.VehiclesInFrame`, is a per-frame scratch buffer that is **empty at the point a StarMap hook runs after
`PostRender`**. KSACars wraps all game-state access in a `KsaWorld` class that builds its own census from
`Universe.CurrentSystem.All` once per pass, then syncs a `Dictionary<Vehicle, Entry>` registry that drops disposed
craft. This is "centralize access behind a thin provider" at frame granularity. Compare the static providers of
[Static provider library](./build-a-mod-without-game-references-3.md).

## Background: how the game does it

`Program.RefreshVehiclesInFrame()` fills `_vehiclesInFrame` from `Universe.CurrentSystem.All` during `PrepareFrame`, and
`PostRender` clears it again:[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L582-L589)

```csharp
// current/decomp/KSA/Program.cs L582-L589
	public static void RefreshVehiclesInFrame()
	{
		_vehiclesInFrame.Clear();
		if (Universe.CurrentSystem != null)
		{
			_vehiclesInFrame.FillFromOfType(Universe.CurrentSystem.All.AsSpan());
		}
	}
```

So `VehiclesInFrame` is full during the UI pass (`StarMapAfterGui`) and empty in `StarMapAfterOnFrame`. Reading
`Universe.CurrentSystem.All` directly gives the same set from either hook.

## Lifecycle and timing

Both StarMap hooks run on the main thread after `PrepareFrame` has dispatched the next physics step, so the step is in
flight during the whole census: the registry is for reading and for posing, never for writing simulated state.
`StarMapAfterGui` only runs when `DrawUI` is true; `StarMapAfterOnFrame` runs on every return path of `OnFrame`. KSACars
therefore lets whichever hook comes first run the step, with a latch that the always-called hook releases
([Per-hook try/catch feeding a shared fault counter, unpatch at ten](../../lifecycle/contain-exceptions-in-starmap-hooks/contain-exceptions-in-starmap-hooks-1.md), [StarMap lifecycle attributes](../../lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md)).

## Steps

1. **Build a census per pass, not per call.** `KsaWorld.Vehicles` is computed at most once between
   `InvalidateCensus()` calls, from the system collection with disposed craft skipped, falling back to
   `Program.VehiclesInFrame` only if that comes back empty:[src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L315-L352)

   ```csharp
   // src/KSACars/Ksa/KsaWorld.cs L315-L352
       // Universe.CurrentSystem.All rather than Program.VehiclesInFrame: the latter is a per-frame scratch
       // buffer refilled at a point in the tick a StarMap hook does not line up with, and reads back empty
       // from there. The system's collection is valid from any hook.
       private static void CollectVehicles(List<Vehicle> into)
       {
           into.Clear();

           try
           {
               if (Universe.CurrentSystem is { } system)
               {
                   ReadOnlySpan<Astronomical> all = system.All.AsSpan();
                   for (int i = 0; i < all.Length; i++)
                   {
                       if (all[i] is Vehicle { IsDisposed: false } v) into.Add(v);
                   }
               }
           }
           catch (Exception e)
           {
               Log.Warn($"vehicle enumeration failed: {e.Message}");
           }

           if (into.Count != 0) return;

           try
           {
               ReadOnlySpan<Vehicle> inFrame = Program.VehiclesInFrame;
               for (int i = 0; i < inFrame.Length; i++)
               {
                   if (inFrame[i] is { IsDisposed: false } v) into.Add(v);
               }
           }
           catch
           {
               // Nothing more to try.
           }
       }
   ```

2. **Invalidate at the top of each pass.** Freshness is a generation rather than a frame count, because a destroyed
   vehicle stays in a held list as a disposed reference.
3. **Sync a registry from the census.** Add each live vehicle not yet known whose part list contains one of yours,
   resolve its subparts once, and drop every entry whose vehicle is disposed or no longer holds the part:[src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L84-L117)

   ```csharp
   // src/KSACars/Ksa/Buggies.cs L84-L117
       public void Sync(IReadOnlyList<Vehicle> vehicles)
       {
           for (int i = 0; i < vehicles.Count; i++)
           {
               Vehicle v = vehicles[i];
               if (!KsaWorld.IsAlive(v) || Active.ContainsKey(v)) continue;
               if (Find(v) is not { } found) continue;

               Entry entry = new(v, found.Part, found.Profile);
               Resolve(entry);
               // A saved car comes back with its switch as it was left.
               if (found.Part.LightSwitch is { LightIsActive: true })
               {
                   entry.Beam = BeamSetting.Low;
                   entry.SwitchSeen = true;
               }
               Active[v] = entry;
               Log.Info($"{found.Profile.DisplayName} on {KsaWorld.DisplayName(v)}: "
                        + $"{entry.Wheels.Count(w => w is not null)} wheel(s) found");
           }

           List<Vehicle>? gone = null;
           foreach ((Vehicle v, Entry e) in Active)
           {
               if (!KsaWorld.IsAlive(v) || Find(v)?.Part != e.Part) (gone ??= []).Add(v);
           }
           if (gone is not null)
           {
               foreach (Vehicle v in gone)
               {
                   Active.Remove(v);
                   RailsHook.Forget(v);
               }
           }
   ```

4. **Use a frame latch so the step runs exactly once per frame** from whichever hook comes first (`Claim()` returns
   true to the first asker after `EndFrame()`), and release it in a `finally` on the hook that cannot be skipped:[src/KSACars/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/FrameLatch.cs)

   ```csharp
   // src/KSACars/Sim/FrameLatch.cs L15-L36
   internal sealed class FrameLatch
   {
       private bool _taken;

       /// <summary>Whether the frame's work has already been claimed.</summary>
       public bool Claimed => _taken;

       /// <summary>
       /// Whether this caller owns the frame's work. True to the first asker after each
       /// <see cref="EndFrame"/>, and false to everyone after it.
       /// </summary>
       public bool Claim()
       {
           if (_taken) return false;

           _taken = true;
           return true;
       }

       /// <summary>End the frame, whatever happened during it.</summary>
       public void EndFrame() => _taken = false;
   }
   ```

5. **Make hot paths cheap when the registry is empty.** The Harmony patches on hot paths return at once when `Active` is
   empty and otherwise look the vehicle up (`Buggies.Physics` from the `Vehicle.PrepareWorker` prefix); a craft that is
   not a car costs a lookup or a short loop. The registry is main-thread only; the one worker-side patch uses its own
   `ConcurrentDictionary`.
6. **Do not write simulated state from the census step.** Pose wheels and play sounds here; write forces from the
   `PrepareWorker` prefix window ([Mutate simulation state safely](../../lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)).

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs) | `KsaWorld` census and invalidation |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs) | Registry sync and removal |
| [src/KSACars/Sim/FrameLatch.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Sim/FrameLatch.cs) | The latch |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs) | `StepOnce` called from both hooks, `EndFrame` in `finally` |

## Variations and gotchas

- **Release the latch on the hook that always runs**, in a `finally`, before any early return.
- **The fallback is a frame late.** From `StarMapAfterOnFrame` the poses are drawn by the next frame's render, and
  gizmos submitted there are reset before they are drawn.
- **`VehiclesInFrame` depends on the hook**; `CurrentSystem.All` is the stable choice.
- **A dictionary keyed by `Vehicle` is main-thread only**; a disposed vehicle must be removed or it leaks and may be
  touched after disposal.
- **Debris vehicles appear in `All`** too; filter by `IsDebris` if you do not want them (see approach 3).

## See also

- [Static provider library](./build-a-mod-without-game-references-3.md), [Reflection-only mod with visible breakage](./build-a-mod-without-game-references-1.md), [Build a KSA mod that references only StarMap.API and Harmony](./overview.md)
- [Once-per-frame car step from two StarMap hooks, with a registry of the mod's craft](../../../../mods/ksacars/features/frame-step-latch-and-car-registry.md)
- [Keep mod exceptions out of KSA's frame loop](../../lifecycle/contain-exceptions-in-starmap-hooks/overview.md), [Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md)
