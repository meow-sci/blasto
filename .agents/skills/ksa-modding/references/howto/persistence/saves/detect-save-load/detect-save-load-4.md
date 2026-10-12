---
type: How-To
title: Postfix Universe.DeserializeSave when the CelestialSystem object survives a load
description: Clear anchors held against the old world from a postfix on DeserializeSave, because a latch on the CurrentSystem reference cannot see a load
category: persistence
group: saves
topic: detect-save-load
approach: 4
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-measuretools/features/live-body-relative-anchors]
techniques: [harmony-postfix]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.Universe.DeserializeSave, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, event:load, frame:draw-ui]
game_types: [KSA.Universe, KSA.CelestialSystem, KSA.Vehicle, KSA.Program]
tags: [saves, load, harmony, anchors, state-reset]
---

Outcome: a mod whose state is a set of points in the world (rulers, protractors, pins) clears them when a save replaces the world. KSA-MeasureTools had a per-frame "did the system change" test that worked for loading a different system but never fired for a save load; it patches `Universe.DeserializeSave` instead. Demonstrated by [Live anchors in body, orbit, surface and part frames](../../../../mods/ksa-measuretools/features/live-body-relative-anchors.md).

## Background: how the game does it

`Universe.DeserializeSave(UniverseData)` keeps `Universe.CurrentSystem` and its bodies, destroys every vehicle with `DestroyAllVehicles` and deserializes the saved system into the existing object ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478), [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L663-L720)). A check of the form `Universe.CurrentSystem != _lastSystem` therefore stays false. Vehicle anchors die by themselves (their vehicles are disposed and fail an identity check), but body, orbit and free points would survive "at offsets that mean nothing in the loaded save", in MeasureTools' words.

`DeserializeSave` has two callers in this build: `UncompressedSave.Load` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L80)) and the multiplayer client's join handler ([current/decomp/KSA.Networking/NetworkClient.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Networking/NetworkClient.cs#L127-L129)). A postfix on `DeserializeSave` itself covers both; `Program.OnGameLoaded` (the latch of [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md)) covers only the first.

## Lifecycle and timing

The postfix runs synchronously at the end of `DeserializeSave`, inside the load, after the camera has been deserialized and the speed set back to 1 (`event:load`). MeasureTools applies the patch in its `Mod.cs` startup (`startup:all-mods-loaded`) and runs `MeasureState.Prune()` first in every `[StarMapAfterGui]` call (`frame:draw-ui`), so the load reset and the per-frame validity pass are two complementary mechanisms ([MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L37), [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L57-L78)). See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the frame order.

Because the postfix clears synchronously, it must not touch anything the rest of the load frame still reads in a half-finished state; clearing a mod-owned list is safe.

## Steps

1. **Write the patch class.** The comment states why the per-frame check is insufficient; the body is a guarded clear:

   ```csharp
   // MeasureTools/Patches/Patch_LoadSave.cs L9-L29
   // Universe.DeserializeSave keeps the same CelestialSystem, so Prune's system-change
   // compare never fires on a save load. Vehicle anchors drop out by themselves; body
   // and free points would survive at offsets that mean nothing in the loaded save.
   [HarmonyPatch(typeof(Universe), nameof(Universe.DeserializeSave))]
   internal static class Patch_LoadSave
   {
       [HarmonyPostfix]
       private static void Postfix()
       {
           // A postfix throws into the loader.
           try
           {
               MeasureState.ClearAll();
               MeasureState.SetReferenceOverride(null);
           }
           catch (Exception ex)
           {
               LogHelper.ErrorOnce("loadsave-" + ex.GetType().Name,
                   $"[MeasureTools] Clearing measurements on save load failed: {ex}");
           }
       }
   ```

2. **Register it with a failure label.** MeasureTools applies each patch class through a helper so one failing patch does not stop the mod or the other patches ([MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L32-L55)):

   ```csharp
   // MeasureTools/Mod.cs L35-L55
       ApplyPatch(typeof(Patch_MenuBar), "Measure menu");
       ApplyPatch(typeof(Patch_MouseButton), "mouse intercept");
       ApplyPatch(typeof(Patch_LoadSave), "save load reset");

       DefaultCategory.Log.Info("[MeasureTools] Loaded.");
   }

   private static void ApplyPatch(Type patchClass, string description)
   {
       try
       {
           _harmony!.CreateClassProcessor(patchClass).Patch();
           if (DebugConfig.Measure)
               DefaultCategory.Log.Debug($"[MeasureTools] Applied {description} patch ({patchClass.Name}).");
       }
       catch (Exception ex)
       {
           // A missing hook should not unload the mod or block the other patch.
           LogHelper.ErrorOnce("patch-" + patchClass.Name, $"[MeasureTools] Failed to apply {description} patch: {ex}");
       }
   }
   ```

3. **Keep a per-frame validity pass anyway.** The load reset handles the world replacement; `Prune` handles everything that can go stale mid-session (a body object replaced, a vehicle disposed). It checks object identity, not ids, so a new object with the same id does not silently re-anchor:

   ```csharp
   // MeasureTools/Features/Measure/MeasureState.cs L220-L248
       public static void Prune()
       {
   #if DEBUG
           using var perfScope = new PerfTracker.Scope("MeasureState.Prune");
   #endif
           CelestialSystem? system = Universe.CurrentSystem;
           if (!ReferenceEquals(system, _system))
           {
               // Only worth a log line when something is actually dropped (the very
               // first frame also lands here, transitioning from no system).
               if (DebugConfig.Measure && (Measurements.Count > 0 || Pending.Count > 0))
                   DefaultCategory.Log.Debug(
                       $"[MeasureTools] System changed, clearing {Measurements.Count} measurement(s) and {Pending.Count} pending point(s).");
               _system = system;
               Pending.Clear();
               Measurements.Clear();
               ReferenceOverride = null;
               StateVersion++;
               return;
           }
           if (system == null)
               return;
           if (ReferenceOverride != null && !ReferenceEquals(system.Get(ReferenceOverride.Id), ReferenceOverride))
           {
               if (DebugConfig.Measure)
                   DefaultCategory.Log.Debug($"[MeasureTools] Reference override '{ReferenceOverride.Id}' no longer resolves, back to auto.");
               ReferenceOverride = null;
               StateVersion++;
           }
   ```

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Patches/Patch_LoadSave.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_LoadSave.cs) | The `DeserializeSave` postfix |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L32-L78) | Patch registration and the per-frame `Prune` call |
| [MeasureTools/Features/Measure/MeasureState.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureState.cs#L220-L262) | `Prune` and validity rules |

## Variations and gotchas

- **Why not `OnGameLoaded`?** It only has one caller and it runs after `DeserializeSave`; both work for a single-player load. The `DeserializeSave` patch is the one that also sees a multiplayer join and fires before `OnGameLoaded` closes the menus.
- **Exception safety.** A postfix that throws throws into the loader. The `try`/`catch` and the log-once helper are not optional.
- **The signature is the only stability risk.** `DeserializeSave(UniverseData)` is `public static`; MeasureTools patches it by type and name with an attribute, so a rename fails at patch time, not at build time (see [Pin the OnGameLoaded signature at compile time so a game update breaks the build, not the patch](detect-save-load-2.md) for the build-time pin).
- **Clear is cheap; recompute is the alternative.** If the mod's state can be rebuilt from the live world (see [Skip detection: rebuild a derived registry from the live world every frame](detect-save-load-6.md)) there is nothing to clear.

## See also

- [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md), [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](detect-save-load-3.md), [overview](./overview.md)
- [Live anchors in body, orbit, surface and part frames](../../../../mods/ksa-measuretools/features/live-body-relative-anchors.md)
