---
type: How-To
title: "Skip detection: rebuild a derived registry from the live world every frame"
description: Find cars by scanning the live vehicles each frame, resolve their parts once, restore state from the part's own saved fields and drop entries whose vehicle is disposed, so a load needs no special handling
category: persistence
group: saves
topic: detect-save-load
approach: 6
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/frame-step-latch-and-car-registry]
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Part, KSA.Universe, KSA.Program]
tags: [saves, load, derived-state, registry, idempotent]
---

Outcome: no load detection code at all. KSACars keeps a registry of "which vehicles are cars" that is a pure function of the live world, recomputed (idempotently) every frame. A save load destroys every vehicle; the next frame's sync notices they are gone, drops them, finds the newly built ones and reads their state back from the parts. Demonstrated by [Once-per-frame car step from two StarMap hooks, with a registry of the mod's craft](../../../../mods/ksacars/features/frame-step-latch-and-car-registry.md). Use it when the world can reproduce everything your mod needs; when it cannot, use a hook ([Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md)).

## Background: how the game does it

`Universe.DeserializeSave` disposes every vehicle and rebuilds them from the save, including each part's own saved fields ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478)). A disposed vehicle stays reachable from any list a mod kept, as a reference whose `IsDisposed` is true. A registry that is re-derived from `Universe.CurrentSystem` each frame only has to treat that flag as "gone". State that lives on a part (a light switch, for instance) comes back with the vehicle, so the registry can read it again rather than store it.

## Lifecycle and timing

The sync runs once per frame from `StepOnce`, which every frame hook calls and which a `_frame.Claim()` latch makes idempotent per frame: it normally runs from `[StarMapAfterGui]` (`frame:draw-ui`, HUD visible) and from `[StarMapAfterOnFrame]` (`frame:after-on-frame`) when the HUD is hidden. The claiming mechanism is described in [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md); the frame order is in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). Because it runs every frame, a car that appears in any way (spawned, loaded, undocked) is picked up on the next one.

## Steps

1. **Define liveness once.**

   ```csharp
   // src/KSACars/Ksa/KsaWorld.cs L289
   public static bool IsAlive(Vehicle? v) => v is { IsDisposed: false };
   ```

2. **Hold the registry keyed by the `Vehicle` object**, so a disposed vehicle is a key that can be removed, not a stale id:

   ```csharp
   // src/KSACars/Ksa/Buggies.cs L73-L74
   // Read by the physics prefix, which has only the vehicle to go on.
   private static readonly Dictionary<Vehicle, Entry> Active = [];
   ```

3. **Sync in two passes: add what is new, then drop what is gone.** The first pass adds each live vehicle that is not yet known and whose part list contains a part whose id matches a car profile, resolves its subparts (wheels) once, and restores the headlamp setting from the part's saved light switch. The second pass drops any entry whose vehicle was disposed or whose matching part is gone:

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

4. **Read persistent state from the world, not from your own file.** A "saved car comes back with its switch as it was left" - the registry reads `LightSwitch.LightIsActive` from the part (step 3, lines 95-99 of the snippet) instead of storing it.

5. **Call the sync from the frame step**, after invalidating the per-frame vehicle census so it sees the current list:

   ```csharp
   // src/KSACars/Ksa/KSACarsMod.cs L112-L130
   private void StepOnce()
   {
       if (!_frame.Claim()) return;

       if (!KsaWorld.InFlightScene)
       {
           _buggySound.StopAll();
       _rocketSound.StopAll();
           return;
       }

       // Every frame rather than on the clock: the springs step inside the engine's own physics
       // window, so this only has to find the cars and show where their wheels ended up.
       KsaWorld.InvalidateCensus();
       _buggies.Sync(KsaWorld.Vehicles);
       _buggies.Pose();
       _buggySound.Update(_buggies, 1f);
       _rocketSound.Update(_buggies);
   }
   ```

## Complete example

| File | Role |
|---|---|
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L73-L136) | `Active`, `Sync`, `Clear` |
| [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L226) and [src/KSACars/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KsaWorld.cs#L289) | Flown-craft and liveness helpers |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs#L112-L130) | The frame step that calls `Sync` |

## Variations and gotchas

- **Contrast with the hook approaches.** Everything in [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md) through [Postfix Universe.DeserializeSave when the CelestialSystem object survives a load](detect-save-load-4.md) exists because the mod holds state the world cannot reproduce (rounds in the air, a course builder, a measurement anchored at an offset). If your state is derivable, prefer this.
- **Keep the scan cheap.** A per-frame walk over `Universe.CurrentSystem` is only affordable because KSACars builds the live list at most once a frame and shares it ("freshness is a generation").
- **Per-entry mutable state still dies with the vehicle.** A held throttle or a "righting" request keyed on the old vehicle is dropped with the entry (`Held`, `ToRight` are cleared with `Clear()`); they are transient by design.
- **Do not hold a vehicle across a frame boundary without checking `IsDisposed`.** The second pass of `Sync` is what removes the disposed ones; any other cache that keys on `Vehicle` needs the same check.

## See also

- [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md), [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](detect-save-load-3.md), [overview](./overview.md)
- [Once-per-frame car step from two StarMap hooks, with a registry of the mod's craft](../../../../mods/ksacars/features/frame-step-latch-and-car-registry.md)
- [Tracking which craft carry your part, classifying them and detecting staging and loss](../../../vehicles-and-parts/craft-tracking/track-and-classify-craft/overview.md), [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)
