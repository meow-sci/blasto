---
type: How-To
title: Observe UncompressedSave.Load and reset a registry of save-lifetime caches
description: Capture UniverseData in a prefix and compare it in the postfix so refused and failed loads are ignored, learn the loaded save id, and reset every cache that holds vehicle, burn or body-index references
category: persistence
group: saves
topic: detect-save-load
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries
  - mods/ohmystars/features/save-scoped-settings-sidecar
techniques: [harmony-prefix, harmony-postfix]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.UncompressedSave.Load
  - harmony-postfix KSA.UncompressedSave.Load
lifecycle: [startup:all-mods-loaded, event:load]
game_types: [KSA.UncompressedSave, KSA.GameSave, KSA.Universe, KSA.Vehicle, KSA.GameSaves]
tags: [saves, load, harmony, save-id, state-reset, caches]
---

Outcome: a mod learns that a load really completed, which save it was, and drops every cache that describes the world the load replaced - from one patch and one list. KSA-AdvancedFlightComputer (AFC) does this for its multi-pass and RCS execution registries; OhMyStars uses the same patch target to learn the current save id for its per-save settings sidecar. Demonstrated by [Save-scoped TOML registries beside the mod](../../../../mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries.md) and [Save-scoped settings sidecar](../../../../mods/ohmystars/features/save-scoped-settings-sidecar.md).

## Background: how the game does it

`UncompressedSave.Load()` returns normally in two cases that loaded nothing: the editor is open (`GameSaves.RefusedInEditor`) and `UniverseData.LoadFrom` threw (logged). On success it assigns a fresh `UniverseData` to the public field `GameSave.UniverseData` ([current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L7)) before `Universe.DeserializeSave` and `Program.OnGameLoaded` run ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L83)). So the identity of that field's value changes if and only if the file was read. A prefix can capture the old value; the postfix can compare. The save's folder name is `GameSave.Id`, which also names the save in `GameSaves` ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124)).

`Universe.DeserializeSave` destroys every vehicle, but `Vehicle.Dispose` leaves the destroyed vehicle's `FlightComputer` and `BurnPlan` intact, so a kept `Burn` still looks live; and `LookupCollection.Deregister` swap-removes, so a kept body index resolves to a different body rather than to nothing. This is AFC's recorded reason for resetting caches by name rather than relying on a disposed check.

## Lifecycle and timing

| When | What | Lifecycle |
|---|---|---|
| `StarMapAllModsLoaded` | `harmony.CreateClassProcessor(typeof(LoadPatch)).Patch()` ([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L117)) | `startup:all-mods-loaded` |
| Inside `Load`, before it runs | Prefix stores `__instance.UniverseData` in `__state` | `event:load` |
| Inside `Load`, after it returned | Postfix runs after `DeserializeSave` and `OnGameLoaded` have finished, so the new vehicles exist and can be looked up by id | `event:load` |

Both run synchronously on the main thread in whatever frame phase started the load; see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). Unlike the latch in [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md) the postfix here acts immediately, which is safe because AFC's reset only clears its own caches and reloads a file; it does not touch engine objects.

## Steps

1. **Patch the concrete class, not `GameSave`.** `GameSave.Load` is abstract; the implementation is `UncompressedSave.Load`. The prefix captures state, the postfix decides whether a load happened:

   ```csharp
   // AdvancedFlightComputer/Core/SaveLoadObserver.cs L35-L61
   [HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Load), new Type[0])]
   private static class LoadPatch
   {
       static void Prefix(UncompressedSave __instance, out UniverseData __state) => __state = __instance.UniverseData;

       static void Postfix(UncompressedSave __instance, UniverseData __state)
       {
           // UncompressedSave.Load returns normally when GameSaves.RefusedInEditor refuses the load or the universe file cannot be read.
           // Only a completed load replaces UniverseData.
           if (ReferenceEquals(__instance.UniverseData, __state))
           {
               if (DebugConfig.MultiPass)
                   DefaultCategory.Log.Debug(
                       $"[AFC] SaveLoadObserver.LoadPatch: ignored save '{__instance.Id}' because it did not load.");
               return;
           }

           string saveId = __instance.Id ?? string.Empty;
           CurrentSaveId = saveId;

           // Keep each stage independent so one failure does not prevent the others.
           RunStage(saveId, "reset the save-scoped state", SaveScopedState.ResetAll);
           RunStage(saveId, "restore the multi-pass registry", MultiPassRegistry.Load);
           RunStage(saveId, "notify the save-loaded subscribers", () => SaveLoaded?.Invoke());
           if (DebugConfig.MultiPass)
               RunStage(saveId, "log the loaded state", () => LogLoadedState(saveId));
       }
   ```

2. **Isolate each stage.** One failure must not stop the others, and an exception from a postfix would propagate into the game's load:

   ```csharp
   // AdvancedFlightComputer/Core/SaveLoadObserver.cs L63-L74
   private static void RunStage(string saveId, string stageName, Action stage)
   {
       try
       {
           stage();
       }
       catch (Exception ex)
       {
           DefaultCategory.Log.Warning(
               $"[AFC] SaveLoadObserver.LoadPatch: could not {stageName} for save '{saveId}': {ex}");
       }
   }
   ```

3. **Keep the reset list in one place** so the load path and the unload path cannot disagree. AFC's class comment is the design note; the method body is the list:

   ```csharp
   // AdvancedFlightComputer/Core/SaveScopedState.cs L10-L20
   /// <summary>
   /// Every piece of mod state whose lifetime is one save game, in one list, so the save-load path
   /// (<see cref="SaveLoadObserver"/>'s load postfix) and <c>Mod.Unload</c> cannot disagree.
   ///
   /// A save load replaces the world. <c>Universe.DeserializeSave</c> runs
   /// <c>CelestialSystem.DestroyAllVehicles</c> and rebuilds every vehicle, so anything keyed on a
   /// vehicle id, a body id or a LookupIndex, and anything holding a Burn, Vehicle, BurnPlan or
   /// FlightPlan reference, describes a world that no longer exists. None of it self-heals.
   /// <c>Vehicle.Dispose</c> leaves the destroyed vehicle's FlightComputer and BurnPlan intact, so a
   /// kept Burn still looks live, and <c>LookupCollection.Deregister</c> swap-removes, so a kept
   /// index resolves to a different body rather than to nothing.
   ```

   ```csharp
   // AdvancedFlightComputer/Core/SaveScopedState.cs L32-L47
   internal static class SaveScopedState
   {
       public static void ResetAll()
       {
           MultiPassPreviewCache.Reset();
           HohmannMultiPassPlanner.ResetShiftCache();
           MultiPassUI.Reset();
           HohmannMultiPassUI.Reset();
           HohmannFlybyUI.Reset();
           Patch_DrawPlanWindow.Reset();
           ManeuverToolsWindow.Reset();
           RcsExecutor.ResetUiCache();
           RcsCommandChannel.Reset();
           // Also disarms the AUTOSTAGE switch, so a loaded save does not stage until the player arms it again.
           StagingDetector.Reset();
       }
   ```

   Three kinds of state are deliberately left out of that list (registries reloaded from disk right after, per-feature enable flags, and dedup sets that live for the whole mod load); the comment on the class says which and why. Write the same kind of comment for your list.

4. **Notify the rest of the mod with an event, after the reset.** `SaveLoaded` is raised last; the RCS registry and the guidance release subscribe in `Mod.cs`:

   ```csharp
   // AdvancedFlightComputer/Mod.cs L196-L197
   SaveLoadObserver.SaveLoaded += RcsExecRegistry.Load;
   SaveLoadObserver.SaveWritten += OnRcsSaveWritten;
   ```

5. **Reset on unload with the same list.** `SaveLoadObserver.Reset()` clears the save id and the event handlers ([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L243)).

### OhMyStars: the same target, a smaller job

OhMyStars only needs the id of the save being loaded, so it uses a plain postfix without the identity comparison:

```csharp
// src/SaveLoadObserver.cs L24-L36
[HarmonyPatch(typeof(UncompressedSave), nameof(UncompressedSave.Load))]
private static class LoadPatch {
    static void Postfix(UncompressedSave __instance) {
        try {
            CurrentSaveId = __instance.Id ?? string.Empty;

            OhMyStarsSettingsStore.LoadForSave(CurrentSaveId);
        } catch(Exception ex) {
            DefaultCategory.Log.Warning(
                $"[OhMyStars] SaveLoadObserver Load Postfix: {ex}");
        }
    }
}
```

This sets `CurrentSaveId` and loads that save's settings even for a refused or failed load, since the postfix always runs when `Load` returns. It is the contrast case for step 1: add the `UniverseData` check if wrong-save state matters.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Core/SaveLoadObserver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SaveLoadObserver.cs) | Load and write postfixes, save id, `SaveLoaded` / `SaveWritten` events |
| [AdvancedFlightComputer/Core/SaveScopedState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Core/SaveScopedState.cs) | The save-lifetime cache list |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L117) | Where the patches are applied |
| [src/SaveLoadObserver.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SaveLoadObserver.cs) | The smaller postfix-only variant |

## Variations and gotchas

- **Never keep a `Vehicle`, `Burn` or `FlightPlan` across a load.** Store ids and resolve again (see [Identify vessels and parts stably across saves](../identify-vessels-and-parts-across-saves/overview.md) for which ids survive).
- **A load that throws inside `DeserializeSave`** never reaches the postfix (Harmony does not run postfixes when the original throws, unless a finalizer is used); the world is then half-replaced and not detected. Add a Harmony finalizer if that matters (see [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../save-mod-state-with-the-scene/overview.md) for a mod that does).
- **A load is not the only way to start a session.** The default world never goes through `UncompressedSave.Load`, which is why AFC also loads its registry file at patch time and treats an empty `CurrentSaveId` as "default world".
- **The multiplayer join path** replaces the world with `Universe.DeserializeSave` and does not call `UncompressedSave.Load`; this patch misses it (see [Postfix Universe.DeserializeSave when the CelestialSystem object survives a load](detect-save-load-4.md)).
- **Pair with the write side.** `SaveLoadObserver` also patches `UncompressedSave.Write`; that half is described in [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md).

## See also

- [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md), [Postfix Universe.DeserializeSave when the CelestialSystem object survives a load](detect-save-load-4.md), [overview](./overview.md)
- [Save-scoped TOML registries beside the mod](../../../../mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries.md), [Save-scoped settings sidecar](../../../../mods/ohmystars/features/save-scoped-settings-sidecar.md)
- [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md), [Identify vessels and parts stably across saves](../identify-vessels-and-parts-across-saves/overview.md)
