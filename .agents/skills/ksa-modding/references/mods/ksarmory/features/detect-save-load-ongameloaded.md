---
type: Mod Feature
title: Detect a save load with a postfix on Program.OnGameLoaded
description: A one-line Harmony postfix on the public static Program.OnGameLoaded latches a flag that the next simulation step consumes to drop every round, system, effect and clock boundary belonging to the world the save replaced
mod: ksarmory
category: persistence
techniques: [harmony-postfix, deferred-reset, signature-pinning]
hooks: [starmap StarMapAllModsLoaded, harmony-postfix KSA.Program.OnGameLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, startup:game-loaded, event:save-load, frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.Universe
  - KSA.UncompressedSave
  - KSA.GameSaves
  - KSA.GameSave
  - KSA.CelestialSystem
  - KSA.Popup
tags: [save-load, harmony, postfix, scene-change, reset, latch, universe-time]
---

StarMap has no save-load hook, and KSArmory never leaves the flight scene across a load: KSA builds
its one `CelestialSystem` at startup and a save only replaces what is inside it. Without help the
mod would keep stepping the previous world's rounds in the new one (the author logged shell expiries
for five seconds after a reload). `WorldReloadHook` patches `Program.OnGameLoaded`, which KSA calls
from exactly one place, immediately after `Universe.DeserializeSave` rebuilt the world. The postfix
only sets a flag; the next `StepOnce` consumes it and calls `ForgetTheWorld` before anything
integrates.[^ksarmory-worldreloadhook]

## How it works

1. **Install** (from `[StarMapAllModsLoaded]`): `AccessTools.Method(typeof(Program),
   nameof(Program.OnGameLoaded), Type.EmptyTypes)`, patched as a postfix under the id
   `com.kesslersystems.ksarmory.worldreload`. A missing target logs once that rounds will outlive a
   reload; nothing else depends on it.
2. **Postfix** `AfterGameLoaded`: `_pending = true` inside an empty `try/catch`.
3. **Consume**: `WorldReloadHook.ConsumeReload()` returns true once and clears the flag. `StepOnce`
   calls it right after `KsaWorld.BeginFrame()` and before `StepSimulation`.[^ksarmory-mod]
4. **`ForgetTheWorld`** drops the old world:

| Dropped | How | Why this way |
|---|---|---|
| Weapon systems, including loose ones still flying rounds | `WeaponSystems.Discard()` | Not `Clear()`, which first writes settings; the file now belongs to the save being opened[^ksarmory-weaponsystems] |
| Armaments, optical heads, dispensers, ICBM computers, wreckage | `Clear` / `Discard` | All keyed on vehicles `DeserializeSave` destroyed |
| Mushroom clouds, bullet holes, leaks, store-mass bookkeeping | static `Clear()` | Anchored to bodies of the old world |
| Team roster, screen markers | `TeamRoster.Clear()`, `Markers.Forget()` | Hold destroyed `Vehicle` references |
| Chase and sight cameras | `_chase.Release()`, `_sight.Forget()` | `DeserializeSave` already unfollowed every viewport camera, so nothing is handed back |
| Step tracking | `KsaWorld.ResetSimStepTracking()` | The universe clock restarted at the save's time |

5. `PinTheSignature()` (never called) references `Program.OnGameLoaded()` so a signature change in
   KSA is a compile error rather than a reload that silently stops being noticed.

`KsaWorld.InFlightScene` (`Program.Editor is null && Universe.CurrentSystem is not null`) stays true
throughout, which is why the mod's ordinary "left flight" paths never run on a load.[^ksarmory-ksaworld]

## Lifecycle and timing

Who calls what, build 2026.10.10.5554:[^ksa-uncompressedsave][^ksa-universe][^ksa-program]

| Step | Where |
|---|---|
| Load requested | Save browser row double-click or context "Load" (UncompressedSave.cs L228-L245) -> `GameSave.LoadOnConfirm` creates a `ConfirmActionPopup` (GameSave.cs L53-L56); console `load` (`GameSaves.LoadSaveGame`, GameSaves.cs L282-L289); `ProfilerCapture.OnFrame` (ProfilerCapture.cs L233)[^ksa-gamesave][^ksa-gamesaves][^ksa-profilercapture] |
| Popup confirm runs `Load` | `Popup.DrawAll` in `OnDrawUiFrame` (Program.cs L3134), i.e. after `[StarMapBeforeGui]` and before `[StarMapAfterGui]` in the same frame |
| `UncompressedSave.Load` | reads `universe.xml`, then `Universe.DeserializeSave(UniverseData)` (L80), then `Program.OnGameLoaded()` (L81) |
| `DeserializeSave` | speed 0, `JobSystems.WaitAll()`, emitters/trails/explosion volumes reset, `CurrentSystem.DestroyAllVehicles()`, physics bubbles destroyed, viewport cameras unfollowed, `_lastSimStep = _nextSimStep =` a zero step at the save's `GameTime`, celestials re-placed at that time, system and camera deserialized, speed 1.0 (Universe.cs L2430-L2478) |
| `Program.OnGameLoaded` | closes the Esc menu, settings, save and vehicle-save windows; clears the menu pause (Program.cs L2599-L2606) |
| **Postfix** | sets `_pending` inside that call chain |
| **Consumed** | popup load: the same frame's `OnAfterGui` -> `StepOnce`. Console `load` (run from the console's own input handling) or `ProfilerCapture` (L2295, after `ImGui.Render`): the first `StepOnce` after it, the next frame's at the latest |

The startup scene is not a load: the `Program` constructor calls `Universe.LoadDefaultSystem()`
(L1069), the only caller of `LoadSystem`, and `OnGameLoaded` is not called on that path.

At consume time the mod may safely drop anything keyed on vehicles (all disposed) and reset its
clock tracking. The first `GetLastSimStep()` it then reads is the zero-length step at the save's time
(either still the one `DeserializeSave` wrote, or promoted to `_lastSimStep` by the next
`ApplyVehicleSolvers`), so after `ResetSimStepTracking` the new world starts with a zero step instead
of a span across the difference between the two worlds' clocks.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WorldReloadHook.cs) | Install, postfix latch, `ConsumeReload`, `PinTheSignature` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L332-L334) | Consumed at the top of `StepOnce` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L357-L400) | `ForgetTheWorld` |
| [src/KSArmory/Ksa/WeaponSystems.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystems.cs#L886-L911) | `Clear` (writes settings) vs `Discard` (does not) |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L35-L56) | `InFlightScene` |

## Snippets

```csharp
// src/KSArmory/Ksa/WorldReloadHook.cs L117-L128
    private static void AfterGameLoaded()
    {
        // Setting a bool cannot fail, but this runs in the engine's load path where an exception
        // is the game rather than a log line, and the guard costs nothing.
        try { _pending = true; }
        catch { }
    }

    // Never called. It exists so the compiler emits a reference to the patched method, which is
    // what puts Program.OnGameLoaded in docs/KSA-API-SURFACE.md and turns a signature change in KSA
    // into a build error here rather than a reload that silently stops being noticed.
    private static void PinTheSignature() => Program.OnGameLoaded();
```

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

        // ...
        KsaWorld.TeamRoster.Clear();

        // Markers pin the craft they show, and every one of them has just been destroyed.
        Markers.Forget();

        // ...
        _chase.Release();
        _sight.Forget();

        // The universe clock restarted at the save's own time, so the boundary last integrated
        // through belongs to a world that is gone.
        KsaWorld.ResetSimStepTracking();
    }
```

```csharp
// current/decomp/KSA/UncompressedSave.cs L61-L81
		try
		{
			UniverseData = UniverseData.LoadFrom(this);
		}
		catch (Exception exception)
		{
			// ...
			return;
		}
		Universe.DeserializeSave(UniverseData);
		Program.OnGameLoaded();
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Program.OnGameLoaded()` (public static) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599-L2606) | Postfixed |
| `Universe.DeserializeSave` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478) | What the postfix is reacting to |
| `UncompressedSave.Load` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L92) | The only caller of `OnGameLoaded` |
| `Universe.LoadDefaultSystem` / `LoadSystem` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L223-L239) | Startup builds the system once |
| `Program.Editor`, `Universe.CurrentSystem` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L227) | `InFlightScene` test |

## Pitfalls and version notes

- **Latch, do not act, in the postfix.** It runs inside KSA's load path (inside the UI pass for a
  popup load); tearing mod state down there would race whatever the rest of that frame does.
- **Use `Discard`, not `Clear`, for anything that persists on clear.** `WeaponSystems.Clear()` calls
  `WriteNow()` first and would write the outgoing world's settings into the incoming save's file.
- **Loads that happen after the GUI pass** (`ProfilerCapture` at Program.cs L2295, and the console
  `load` if it executes after `OnDrawUiViewports`) leave one render with the old roster; the reset
  waits for the next frame's `StepOnce`.
- **`DeserializeSave` sets the speed to 1.0 at the end** (Universe.cs L2477). `ForgetTheWorld` does
  not clear `WarpPolicy`; see [timewarp policy](./timewarp-policy-during-engagements.md).
- **Docs out of date:** `docs/KSA-FRAME-ORDER.md` §6 and §10 say nothing resets `StepGate` on a load
  and propose a heuristic (a zero `DeltaTime` with a new `NextTime`). The code uses this hook instead.[^ksarmory-frame-order]
- The `WorldReloadHook` summary lists StarMap's hooks as BeforeMain, ImmediateLoad, AllModsLoaded,
  Unload, AfterOnFrame, BeforeGui and AfterGui; that matches StarMap 4dc60f8.

## See also

- [Consume the applied simulation step](./consume-applied-sim-step.md)
- [Per-craft settings JSON sidecar](./per-craft-settings-json-sidecar.md)
- [Startup save repair](./startup-save-repair.md)
- [StarMap entry and fault containment](./starmap-entry-and-fault-containment.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [Hook any frame point with Harmony patches on Program and Universe](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) - how-to article demonstrating this feature
- [Latch Program.OnGameLoaded and forget the old world in the next frame step](../../../howto/persistence/saves/detect-save-load/detect-save-load-1.md) - how-to article demonstrating this feature

[^ksarmory-worldreloadhook]: src/KSArmory/Ksa/WorldReloadHook.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-weaponsystems]: src/KSArmory/Ksa/WeaponSystems.cs
[^ksarmory-frame-order]: docs/KSA-FRAME-ORDER.md
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled)
[^ksa-gamesave]: KSA.GameSave (decompiled)
[^ksa-gamesaves]: KSA.GameSaves (decompiled)
[^ksa-profilercapture]: KSA.ProfilerCapture (decompiled)
