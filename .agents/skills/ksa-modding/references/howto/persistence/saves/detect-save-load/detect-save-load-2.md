---
type: How-To
title: Pin the OnGameLoaded signature at compile time so a game update breaks the build, not the patch
description: The same OnGameLoaded latch with a never-called method that references the target, so a later signature change fails to compile and a missing target degrades with one logged warning
category: persistence
group: saves
topic: detect-save-load
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/save-load-detection]
techniques: [harmony-postfix]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.Program.OnGameLoaded]
lifecycle: [startup:all-mods-loaded, event:load, frame:draw-ui]
game_types: [KSA.Program, KSA.UncompressedSave, KSA.Universe]
tags: [saves, load, harmony, compile-time-pin, version-safety]
---

Outcome: the load latch of [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md), hardened against game updates. A Harmony patch looked up by name silently stops applying when the target changes; KSAStructures turns that into a build error when the target's signature changes and into one log line when the target disappears. Demonstrated by [Noticing a save load with a Program.OnGameLoaded postfix](../../../../mods/ksastructures/features/save-load-detection.md).

## Background: how the game does it

`Program.OnGameLoaded` is `public static void OnGameLoaded()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599)) and `UncompressedSave.Load` is its only caller ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L80-L81)). Because it is public, ordinary C# code can reference it, which is what the pin exploits. Harmony resolves a patch target at runtime by name and parameter types; a rename compiles fine and only logs on startup, if at all.

## Lifecycle and timing

Install in `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`); the postfix fires inside the load (`event:load`); the mod's frame step consumes the flag (normally `frame:draw-ui`, see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). The consumption timing and why the postfix only sets a flag are in [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md).

## Steps

1. **Install with a guarded lookup.** Both a missing target and a patch exception are caught, recorded in `Trouble`, and logged once, so the mod keeps running and only stale-state-after-a-load is lost:

   ```csharp
   // src/KSAStructures/Ksa/WorldReloadHook.cs L60-L93
   public static void Install()
   {
       if (Installed) return;

       try
       {
           MethodInfo? target = AccessTools.Method(typeof(Program), nameof(Program.OnGameLoaded),
                                                   Type.EmptyTypes);

           if (target is null)
           {
               Trouble = "KSA has no Program.OnGameLoaded() to hook";
               Log.Warn($"save loads cannot be seen: {Trouble} -- anything in flight will outlive "
                        + "a reload");
               return;
           }

           MethodInfo postfix = typeof(WorldReloadHook).GetMethod(
               nameof(AfterGameLoaded), BindingFlags.NonPublic | BindingFlags.Static)!;

           _harmony = new Harmony(HarmonyId);
           _harmony.Patch(target, postfix: new HarmonyMethod(postfix));

           Installed = true;
           Trouble = "";
           Log.Info("save loads are seen, via Program.OnGameLoaded");
       }
       catch (Exception e)
       {
           Trouble = e.Message;
           Log.Warn($"could not hook save loading; anything in flight will outlive a reload "
                    + $"({e.Message})");
       }
   }
   ```

2. **Add the latch and the consuming read:**

   ```csharp
   // src/KSAStructures/Ksa/WorldReloadHook.cs L52-L58
   public static bool ConsumeReload()
   {
       if (!_pending) return false;

       _pending = false;
       return true;
   }
   ```

   ```csharp
   // src/KSAStructures/Ksa/WorldReloadHook.cs L112-L118
   private static void AfterGameLoaded()
   {
       // Setting a bool cannot fail, but this runs in the engine's load path where an exception
       // is the game rather than a log line, and the guard costs nothing.
       try { _pending = true; }
       catch { }
   }
   ```

3. **Add the pin.** A private method that nothing calls but whose body names the patched member. The compiler emits a metadata reference to `Program.OnGameLoaded()`, so a signature change in a later KSA build fails the mod's build. KSArmory documents the same trick in its API-surface tooling (a generated list of the game members the mod references):

   ```csharp
   // src/KSAStructures/Ksa/WorldReloadHook.cs L120-L122
   // Never called. It exists so the compiler emits a reference to the patched method, which is
   // what turns a signature change in KSA into a build error here rather than a reload that silently stops being noticed.
   private static void PinTheSignature() => Program.OnGameLoaded();
   ```

4. **Consume the flag in the frame step.** KSAStructures' consumer logs the load and resets its sim-step tracking; the census of live craft was already invalidated on the line before, so the next lookup walks the new world:

   ```csharp
   // src/KSAStructures/Ksa/KSAStructuresMod.cs L106-L117
   private void StepOnce(double dtPlayer)
   {
       if (_disabled || _ui is null) return;
       if (!_frame.Claim()) return;

       KsaWorld.InvalidateCensus();

       if (WorldReloadHook.ConsumeReload())
       {
           Log.Info("a save was loaded - forgetting the previous world");
           KsaWorld.ResetSimStepTracking();
       }
   ```

   ```csharp
   // src/KSAStructures/Ksa/KsaWorld.cs L110-L115
   public static void ResetSimStepTracking()
   {
       _stepGate.Reset();
       _consumedThrough = null;
   }
   ```

5. **Remove the patch on unload.** `Remove()` clears `_pending` and calls `_harmony.UnpatchAll(HarmonyId)` under the mod's own Harmony id ([src/KSAStructures/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/WorldReloadHook.cs#L95-L110)). Note that StarMap's own `UnpatchAll()` has already removed every patch by the time `StarMapUnload` runs (see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md)), so this matters mainly for a stand-down in the middle of a session.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/WorldReloadHook.cs) | Install, postfix, consuming read, pin |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L106-L117) | The consuming call |
| [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L110-L115) | `ResetSimStepTracking` |

## Variations and gotchas

- **The pin only protects the signature.** If the game stops calling `OnGameLoaded` from the load path, the build still succeeds and the hook goes quiet; keep an eye on `UncompressedSave.Load` after updates.
- **Prefer a public target.** The pin works because the member is `public`. For private members use reflection or an explicit existence check at startup instead.
- **Degrade, do not break.** `Trouble` lets the UI or log say that load detection is off, which beats a mod that throws at startup on a game update.
- The mod is built against 2026.10.7.5541 and the same call chain was checked in 2026.10.10.5554.

## See also

- [Latch Program.OnGameLoaded and forget the old world in the next frame step](detect-save-load-1.md), [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](detect-save-load-3.md), [overview](./overview.md)
- [Noticing a save load with a Program.OnGameLoaded postfix](../../../../mods/ksastructures/features/save-load-detection.md)
