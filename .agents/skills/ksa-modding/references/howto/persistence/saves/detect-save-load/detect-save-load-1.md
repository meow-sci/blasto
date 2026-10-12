---
type: How-To
title: Latch Program.OnGameLoaded and forget the old world in the next frame step
description: Postfix OnGameLoaded to set a flag only, consume it once in the mod's frame step, and drop everything that belonged to the replaced world with Discard rather than a clear that writes settings
category: persistence
group: saves
topic: detect-save-load
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/detect-save-load-ongameloaded, mods/ksagolf/features/save-load-detection]
techniques: [harmony-postfix, starmap-lifecycle]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.Program.OnGameLoaded
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
lifecycle: [startup:all-mods-loaded, event:load, frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Program, KSA.Universe, KSA.UncompressedSave]
tags: [saves, load, harmony, latch, reset, discard]
---

Outcome: a mod that holds references into the world (weapon systems, a craft mover, a course builder, cameras) notices a save load, once, and drops them before the next simulation step integrates across the new clock. Prerequisites: StarMap and Harmony. Demonstrated by KSArmory and KSAGolf, whose `WorldReloadHook` classes are the same file with different names for the dropped state: [Detect a save load with a postfix on Program.OnGameLoaded](../../../../mods/ksarmory/features/detect-save-load-ongameloaded.md) and [Save-load detection](../../../../mods/ksagolf/features/save-load-detection.md).

## Background: how the game does it

`UncompressedSave.Load` calls `Universe.DeserializeSave` and then `Program.OnGameLoaded()` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L80-L81)). `DeserializeSave` keeps the same `CelestialSystem` and destroys every vehicle, so a test such as "a system exists and the editor is closed" stays true across a load, and "the scene changed" never fires. KSArmory measured the cost: its loose weapon systems outlive their platforms by design (so rounds of a launcher shot out from under them keep flying), so after a reload they kept stepping the old world's rounds in the new one and logged shell expiries for five seconds. Details of `DeserializeSave` are in the [overview](./overview.md#engine-background).

## Lifecycle and timing

| When | What runs | Lifecycle |
|---|---|---|
| `StarMapAllModsLoaded` | `WorldReloadHook.Install` patches `Program.OnGameLoaded` | `startup:all-mods-loaded` |
| Inside the load | The postfix sets `_pending = true` and does nothing else | `event:load` (synchronous, main thread, inside the frame phase that triggered the load) |
| The mod's next step | `StepOnce` calls `ConsumeReload()` first, before anything integrates | `frame:draw-ui` (`StarMapAfterGui`), or the frame postfix on frames with the HUD hidden |

Which frame consumes it depends on who started the load. A load confirmed in the save window runs in `Popup.DrawAll`, at the top of `OnDrawUiFrame`, so the same frame's `StarMapAfterGui` consumes it. A load from the console, or from a mod's own bridge in `StarMapAfterOnFrame`, is consumed by the next frame's step. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the frame order. The reason to defer is that the postfix runs deep in the engine's load path, sometimes in the middle of a UI draw; tearing state down there would pull objects from under code that still runs later in that frame.

## Steps

1. **Write the hook class.** The postfix is a bool assignment inside an empty `try` (an exception in a postfix on the load path becomes the game's exception):

   ```csharp
   // src/KSArmory/Ksa/WorldReloadHook.cs L117-L123
   private static void AfterGameLoaded()
   {
       // Setting a bool cannot fail, but this runs in the engine's load path where an exception
       // is the game rather than a log line, and the guard costs nothing.
       try { _pending = true; }
       catch { }
   }
   ```

2. **Install it from `StarMapAllModsLoaded`, degrading if the target is missing.** The method is `public static` and parameterless, so `AccessTools.Method(..., Type.EmptyTypes)` finds it. A missing target or a refused patch is logged once and the mod runs on without load detection:

   ```csharp
   // src/KSArmory/Ksa/WorldReloadHook.cs L65-L98
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
               Log.Warn($"save loads cannot be seen: {Trouble} -- rounds in the air will outlive "
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
           Log.Warn($"could not hook save loading; rounds in the air will outlive a reload "
                    + $"({e.Message})");
       }
   }
   ```

3. **Expose a consuming read.** Whoever reads `true` owns the reset, so exactly one caller acts per load:

   ```csharp
   // src/KSArmory/Ksa/WorldReloadHook.cs L53-L63
   /// <summary>
   /// True once per save load, to whoever asks first. <b>Consuming</b> — the caller that reads it
   /// owns the reset.
   /// </summary>
   public static bool ConsumeReload()
   {
       if (!_pending) return false;

       _pending = false;
       return true;
   }
   ```

4. **Consume it first in the frame step.** KSArmory's step runs it before anything integrates:

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L329-L334
   KsaWorld.BeginFrame();
   KsaWorld.AuditBodiesOnce();

   // Before anything integrates: the roster still holds the previous world's systems, and one
   // more step of them is one more step of rounds that no longer have a world to fly in.
   if (WorldReloadHook.ConsumeReload()) ForgetTheWorld();
   ```

   KSAGolf calls the same line from `StepOnce` ([src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L113-L120)).

5. **Forget the world.** Drop or reset everything that refers to the old world, in one method. KSArmory's version:

   ```csharp
   // src/KSArmory/Ksa/KSArmoryMod.cs L362-L400
   private void ForgetTheWorld()
   {
       Log.Info("a save was loaded - forgetting the previous world");

       // Discard rather than Clear: the settings about to be read belong to the save being opened,
       // and writing the outgoing session's over them first is how a reload stops restoring them.
       _roster?.Discard();
       _armaments.Clear();
       _heads?.Clear();
       _countermeasures?.Discard();
       _icbms?.Clear();
       KsaWorld.Wreckage.Clear();

       // The clouds, the burned ground, the flash and the bang still on its way all belong to the
       // world that was replaced: anchored to its bodies, they would stand in the new one.
       NuclearClouds.Clear();
       BulletHoles.Clear();
       Leaks.Clear();
       StoreMass.Clear();

       // Keyed on live craft, every one of which DeserializeSave has just destroyed. CollectTeams
       // rebuilds it on the next step that runs, so this only matters for a world that stays
       // paused -- and a destroyed vehicle reachable from a dictionary is what KsaWorld's own
       // census note forbids however briefly.
       KsaWorld.TeamRoster.Clear();

       // Markers pin the craft they show, and every one of them has just been destroyed.
       Markers.Forget();

       // Forget, not release: DeserializeSave has already unfollowed every viewport camera, so the
       // recording describes a pose in a scene that no longer exists and there is nothing left to
       // hand it back to. Same treatment the editor gets, and for the same reason.
       _chase.Release();
       _sight.Forget();

       // The universe clock restarted at the save's own time, so the boundary last integrated
       // through belongs to a world that is gone.
       KsaWorld.ResetSimStepTracking();
   }
   ```

   The table below lists what each group is and why. KSAGolf's is smaller because it holds less: it releases the craft mover, the course builder and the putting session, then resets sim-step tracking so the next consumed step takes the new clock whatever its timestamp:

   ```csharp
   // src/KSAGolf/Ksa/KSAGolfMod.cs L138-L145
   private void ForgetTheWorld()
   {
       Log.Info("a save was loaded - forgetting the previous world");
       _mover.Release();
       _builder?.Forget();
       _golf?.Forget();
       KsaWorld.ResetSimStepTracking();
   }
   ```

6. **Reset sim-step tracking.** `DeserializeSave` sets the last and next sim steps to a zero-length step at the save's time. A mod that remembers the last universe time it integrated through (a "step gate") would otherwise compare the new clock with a stale boundary and either skip or double-count a step. KSAGolf's helper:

   ```csharp
   // src/KSAGolf/Ksa/KsaWorld.cs L122-L127
   /// <summary>Forgets which step was last integrated. For unload and scene changes.</summary>
   public static void ResetSimStepTracking()
   {
       _stepGate.Reset();
       _consumedThrough = null;
   }
   ```

### What `ForgetTheWorld` drops, and why

| Dropped | How | Why this way |
|---|---|---|
| Weapon systems, including loose ones still flying rounds | `WeaponSystems.Discard()` | `Clear()` writes the settings first; the file now belongs to the save being opened (see below) |
| Armaments, optical heads, dispensers, missile computers, wreckage | `Clear()` / `Discard()` | Keyed on vehicles `DeserializeSave` destroyed |
| Mushroom clouds, bullet holes, leaks, store-mass bookkeeping | static `Clear()` | Anchored to bodies of the old world |
| Team roster, screen markers | `TeamRoster.Clear()`, `Markers.Forget()` | Hold destroyed `Vehicle` references |
| Chase and sight cameras | `_chase.Release()`, `_sight.Forget()` | `DeserializeSave` already unfollowed every viewport camera, so there is nothing to hand the recorded pose back to |
| Step tracking | `KsaWorld.ResetSimStepTracking()` | The universe clock restarted at the save's time |

### Discard versus Clear

When a mod persists state on a clear, a reload must not use that clear. KSArmory's `WeaponSystems.Clear()` calls `WriteNow()` and then `Discard()`; the load path calls `Discard()` only:

```csharp
// src/KSArmory/Ksa/WeaponSystems.cs L886-L911
public void Clear()
{
    // Last chance: a system about to be forgotten still holds settings someone chose.
    WriteNow();
    Discard();
}

/// <summary>
/// Drops every system without writing anything down, for a world that has been replaced.
///
/// <para>The file on disk is the truth after a save load: the settings about to be read belong
/// to the save being opened, and writing the outgoing session's over them first is how
/// reloading stops restoring what was saved.</para>
///
/// <para>Loose systems go too, and that is the point of having this. A loose system outlives
/// its platform so its rounds still fly, which is right for a launcher shot out from under
/// them and wrong for the world being taken away — nothing it is holding exists any more.</para>
/// </summary>
public void Discard()
{
    foreach (Entry e in _entries.Values) e.Weapon.Reset();
    _entries.Clear();

    foreach (WeaponSystem loose in _loose) loose.Reset();
    _loose.Clear();
}
```

Writing the outgoing session's per-craft settings over the file would replace what the incoming save is about to read (how that file is keyed is in [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md)).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WorldReloadHook.cs) | Install, postfix latch, `ConsumeReload`, `PinTheSignature` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L329-L400) | Consume and `ForgetTheWorld` |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L886-L911) | `Clear` versus `Discard` |
| [src/KSAGolf/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/WorldReloadHook.cs) | KSAGolf's copy of the hook |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L113-L145) | KSAGolf's consume and `ForgetTheWorld` |

## Variations and gotchas

- **Latch, do not act, in the postfix.** The same reasoning applies to anything that touches ImGui, GPU resources or physics from inside the load path.
- **One caller today.** `Program.OnGameLoaded` is called only from `UncompressedSave.Load`. The multiplayer client's join path calls `Universe.DeserializeSave` without it ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430)), so a latch on `OnGameLoaded` misses that path; use [Postfix Universe.DeserializeSave when the CelestialSystem object survives a load](detect-save-load-4.md) if you need it.
- **Refused loads do not reach the postfix.** If the editor is open or the file is unreadable, `UncompressedSave.Load` returns before `OnGameLoaded` runs, which is what you want. A load that fails inside `DeserializeSave` with an exception aborts before `OnGameLoaded` too.
- **Where the hook is blind.** If a later KSA build adds another path that replaces the world without calling `OnGameLoaded`, the hook goes quiet without an error. Check callers after each game update, and see [Pin the OnGameLoaded signature at compile time so a game update breaks the build, not the patch](detect-save-load-2.md) for making a signature change a build error.
- **The clock can jump backwards**, so the first step after a load must not integrate "from the last time" to now.

## See also

- [Pin the OnGameLoaded signature at compile time so a game update breaks the build, not the patch](detect-save-load-2.md), [Observe UncompressedSave.Load and reset a registry of save-lifetime caches](detect-save-load-3.md), [Postfix Universe.DeserializeSave when the CelestialSystem object survives a load](detect-save-load-4.md), [overview](./overview.md)
- [Detect a save load with a postfix on Program.OnGameLoaded](../../../../mods/ksarmory/features/detect-save-load-ongameloaded.md), [Save-load detection](../../../../mods/ksagolf/features/save-load-detection.md)
- [Create and load native KSA saves from mod code](../save-and-load-the-game-from-code/overview.md), [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md)
