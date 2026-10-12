---
type: Mod Feature
title: Save-load detection
description: Detecting that a save was loaded, which StarMap has no hook for and which never leaves the flight scene, with a latched Harmony postfix on Program.OnGameLoaded consumed by the next simulation step
mod: ksagolf
category: persistence
techniques: [harmony-postfix]
hooks: [harmony-postfix KSA.Program.OnGameLoaded, starmap StarMapAllModsLoaded]
lifecycle: [event:save-loaded, frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Program, KSA.Universe, KSA.UncompressedSave, KSA.GameSaves]
tags: [save-load, harmony, postfix, scene-state, reset, lifecycle]
---

Loading a save in KSA replaces every vehicle but does not leave the flight scene, and StarMap
offers no load hook (its list is `BeforeMain`, `ImmediateLoad`, `AllModsLoaded`, `Unload`,
`AfterOnFrame`, `BeforeGui`, `AfterGui`). A mod that holds references to crafts, a ball in flight, a
borrowed camera or a half-built course would carry them straight into the new world. KSAGolf
postfixes `Program.OnGameLoaded`, which KSA calls exactly once per load right after the world has
been rebuilt, sets a flag there, and lets the next simulation step consume the flag and drop
everything that belonged to the old world.

## How it works

1. **Install** at `[StarMapAllModsLoaded]`: `AccessTools.Method(typeof(Program),
   nameof(Program.OnGameLoaded), Type.EmptyTypes)`, then `harmony.Patch(target, postfix: ...)`
   under its own Harmony id. A missing target or a patch exception is logged once and the mod runs
   on without load detection.[^golf-worldreloadhook]
2. **Postfix** sets a static `_pending = true` inside a try/catch. It does nothing else: the load
   path is deep in the engine and the mod avoids tearing its own state down from there.
3. **Consume** in `StepOnce`: `if (WorldReloadHook.ConsumeReload()) ForgetTheWorld();`. The read
   clears the flag, so exactly one caller acts on each load.[^golf-mod]
4. **Forget**: release the craft mover, the course builder and the putting session (ball, held
   view, poses), and reset sim-step tracking so the next `StepGate` consume takes the new clock
   whatever its timestamp.[^golf-mod][^golf-stepgate]
5. **Pin**: a never-called `PinTheSignature() => Program.OnGameLoaded();` makes the compiler emit
   a reference so the member appears in the generated API surface (see
   [API surface and assembly lock](api-surface-and-assembly-lock.md)).

Engine side (build 2026.10.10.5554): `UncompressedSave.Load()` reads `UniverseData`, calls
`Universe.DeserializeSave(UniverseData)` and then `Program.OnGameLoaded()`.[^ksa-uncompressedsave]
`DeserializeSave` sets simulation speed to 0, waits for all job systems, destroys all vehicles and
physics bubbles, clears clutter exclusions, unfollows every game viewport camera, resets the last
and next sim steps to the save's time, deserializes the system and camera, then sets speed back to
1.[^ksa-universe] `OnGameLoaded` itself only closes the Esc menu, settings, save and vehicle-save
windows.[^ksa-program] Loads start from the save window or the `load` console action
(`GameSaves.LoadSaveGame`).[^ksa-gamesaves]

`KsaWorld.InFlightScene` (`Program.Editor is null && Universe.CurrentSystem is not null`) stays true
across a load, which is why a scene-change check cannot see it.[^golf-ksaworld]

## Lifecycle and timing

| Moment | What runs |
|---|---|
| `event:save-loaded` | The postfix, synchronously on the main thread inside whatever triggered the load (save window UI, console command, or the bridge's `load` command in the frame postfix), after `Universe.DeserializeSave` has returned |
| Next `StepOnce` | `frame:draw-ui` (`StarMapAfterGui`), `frame:celestials` (prefix) or `frame:after-on-frame`, whichever claims the frame; the world is already the new one and its first `ApplyVehicleSolvers` may not have run yet |

Deferring to the step keeps teardown on the mod's own schedule and in one place (`ForgetTheWorld`),
and means the reset happens before the step that would otherwise integrate across the clock jump.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/WorldReloadHook.cs) | Install, postfix, consume, signature pin |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L113-L145) | `StepOnce` consumes it, `ForgetTheWorld` resets |

## Snippets

```csharp
// src/KSAGolf/Ksa/WorldReloadHook.cs L53-L59
    public static bool ConsumeReload()
    {
        if (!_pending) return false;

        _pending = false;
        return true;
    }
```

```csharp
// src/KSAGolf/Ksa/WorldReloadHook.cs L113-L124
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

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Program.OnGameLoaded()` (public static) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | Patch target |
| `UncompressedSave.Load` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs) | The single caller of `OnGameLoaded` |
| `Universe.DeserializeSave` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | What a load destroys and resets |

## Pitfalls and version notes

- `OnGameLoaded` has one caller in this build; if a future build adds another path that rebuilds
  the world without calling it, the hook goes blind. Check callers after each KSA update.[^ksa-uncompressedsave]
- The clock can jump backwards on a load; `StepGate` treats a span shorter than the step as a clock
  disagreement and takes the step, and `ResetSimStepTracking` makes the first post-load step
  unconditional.[^golf-stepgate]
- Losing the craft being flown is not leaving flight: `Universe.DestroyVehicle` clears
  `Program.ControlledVehicle` and the scene continues, so state that must survive should be gated on
  the scene, not on having a craft.[^golf-ksaworld]

## See also

- [Dual-hook frame stepping on simulated time](dual-hook-frame-stepping.md)
- [API surface record and assembly lock](api-surface-and-assembly-lock.md)
- [Craft mover](craft-mover.md)
- [Putting session with a mouse-swung club](putting-session.md)
- [Top-down minigolf course builder](minigolf-course-builder.md)
- [Latch Program.OnGameLoaded and forget the old world in the next frame step](../../../howto/persistence/saves/detect-save-load/detect-save-load-1.md) - how-to article demonstrating this feature

[^golf-worldreloadhook]: src/KSAGolf/Ksa/WorldReloadHook.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-stepgate]: src/KSAGolf/Sim/StepGate.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-gamesaves]: KSA.GameSaves (decompiled, build 2026.10.10.5554)
