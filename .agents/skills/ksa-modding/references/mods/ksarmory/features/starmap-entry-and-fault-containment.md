---
type: Mod Feature
title: StarMap entry class with per-hook fault containment
description: One [StarMapMod] class binds six StarMap hooks, wraps every frame hook in its own try/catch and switches the whole mod off after ten faults instead of letting an exception escape into KSA's frame loop
mod: ksarmory
category: getting-started
techniques: [starmap-lifecycle, harmony-prefix, harmony-postfix, fault-containment, frame-budget]
hooks:
  - starmap StarMapImmediateLoad
  - starmap StarMapAllModsLoaded
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - starmap StarMapUnload
lifecycle:
  - startup:immediate-load
  - startup:all-mods-loaded
  - frame:draw-ui
  - frame:after-on-frame
  - event:starmap-unload
game_types: [KSA.Program, KSA.App, KSA.Mod, KSA.ModLibrary, KSA.Universe]
tags: [starmap, lifecycle, entry-point, fault-tolerance, harmony, profiling, unload]
---

KSArmory's whole binding to the game is one class, `KSArmoryMod`, marked `[StarMapMod]`, with six
attributed methods. Everything the mod does each frame runs inside one of three of them, and each
of those wraps its body in a `try/catch` that feeds a shared fault counter. Ten faults and the mod
switches itself off for the session: it drops every weapon system, removes its Harmony patches and
logs `air defence disabled`, instead of letting an exception reach KSA's frame loop, which has no
handler of its own.[^ksarmory-mod][^ksa-app]

The same class carries two pieces of self-measurement a modder can copy: `FrameBudget`, which
times every named span of the mod's frame and reports mean and worst, and `OverrunLog`, which
rate-limits the warning that the simulated step was too long to integrate.

## How it works

`mod.toml` names the entry assembly; KSA reads the same file as a content mod (`assets` list) and
StarMap reads its `[StarMap]` table.[^ksarmory-modtoml] StarMap loads `KSArmory.dll` into its own
load context, takes the first type carrying an attribute named `StarMapModAttribute`, creates it
with `Activator.CreateInstance` and registers every public method whose attribute signature
validates; a method with the wrong signature is skipped without a message.[^starmap-runtimemod]

| Attribute | Method | What KSArmory does there |
|---|---|---|
| `[StarMapImmediateLoad]` | `OnImmediateLoad(Mod)` | Writes the generated shader include into `mod.DirectoryPath`; logs version, built-for and running KSA build |
| `[StarMapAllModsLoaded]` | `OnFullyLoaded()` | Registers asset-only packs, freezes the catalogue, audits packs against loaded part templates, repairs saves, installs its six Harmony patches by hand, builds the roster, heads, ICBM computers, sounds, UI; starts the developer tools when `Build.Developer` |
| `[StarMapBeforeGui]` | `OnBeforeGui(double)` | Adds the "Mods" menu-bar entry; errors swallowed without counting ("cosmetic") |
| `[StarMapAfterGui]` | `OnAfterGui(double)` | `StepOnce` (the simulation), then the panel and every world overlay; faults counted as `gui` |
| `[StarMapAfterOnFrame]` | `OnAfterFrame(double, double)` | Logs sim-speed changes, `StepOnce` fallback, developer bridge; `finally` releases the frame latch; faults counted as `frame` |
| `[StarMapUnload]` | `Unload()` | Restores held timewarp, releases cameras and controller, returns pooled emitters and audio channels, clears state, removes five patches, flushes the log |

Fault containment, in the order it is applied:

1. Every frame hook starts with `if (_disabled || _ui is null ...) return;`, so a disabled mod costs
   one branch per hook.
2. `OnAfterGui` and `OnAfterFrame` catch everything and call `Fault(where, e)`; the `finally`
   blocks still run (the GUI pass books its time, the frame hook releases the
   [frame latch](./dual-hook-simulation-step.md)).
3. `Fault` increments `_faults` and logs `"{where} failed ({n}/10)"` with the exception. At
   `FaultLimit = 10` it sets `_disabled`, clears every roster and removes the attitude, pre-render,
   world-reload and both body-draw patches.
4. Code that runs inside an engine method rather than a StarMap hook (the Harmony prefixes and
   postfixes) has its own `try/catch` that logs once and stands down, because the mod's `Fault`
   path is not on that call stack.[^ksarmory-attitudehook]

`OnFullyLoaded` is ordered on purpose: pack registration is closed before anything is crewed; save
repair runs before any save can be loaded; `AttitudeHook` is installed before anything can be
steered; the pre-render and world-reload hooks are installed before the first frame.

Self-measurement:

- `FrameBudget.Measure(name, top)` returns a `struct` span whose `Dispose` books elapsed
  milliseconds; only `top: true` spans ("sim", "draw") add to the frame total, because spans nest.
  `EndFrame(wallSeconds)` closes the frame and `Take()` prints every span dearest first, mean and
  worst, every 10 s of wall clock, only at `Debug` log level.[^ksarmory-framebudget]
- `OverrunLog.Observe(step, anyInFlight)` totals simulated seconds the round clamp discarded and
  reports the first overrun and then one in 120, counted separately for "rounds were in the air"
  (warning) and "nothing was flying" (debug), so the long first frame after a scene load does not
  use up the warning for the first real overrun.[^ksarmory-overrunlog]

## Lifecycle and timing

Where each StarMap attribute fires, verified in StarMap 4dc60f8 and KSA 2026.10.10.5554:

| Hook | Firing point | Game state at that moment |
|---|---|---|
| construction | `RuntimeMod.InitializeMod`, from `ModLoader.Init` before `Program.Main` runs[^starmap-runtimemod][^starmap-modloader] | No game. Field initializers run here, so `KSArmoryMod` creates its roster, UI, sound and smoke objects in `OnFullyLoaded` instead (KSArmoryMod.cs L209-L230) |
| `StarMapImmediateLoad` | Harmony prefix on `Mod.PrepareSystems`[^starmap-modpatches], called per mod from `ModLibrary.PrepareAll` (ModLibrary.cs L443), from the `Program` constructor at Program.cs L899[^ksa-modlibrary][^ksa-program] | Manifest read; asset bundles not loaded (`PreloadAssetBundles` is the next line, L900) |
| `StarMapAllModsLoaded` | Harmony postfix on `ModLibrary.LoadAll`[^starmap-modlibrarypatches], Program.cs L942 | Every mod's XML loaded, part templates resolvable; `AssignDefaults` (L943), `Universe.LoadDefaultSystem` (L1069) and `IsLoaded = true` (L1317) still to come |
| `StarMapBeforeGui` | Harmony prefix on `Program.OnDrawUiFrame`[^starmap-programpatcher], Program.cs L2267 inside `if (DrawUI)` | `PrepareFrame` done: physics applied, next step already on the workers; ImGui frame open |
| `StarMapAfterGui` | Harmony postfix on `Program.OnDrawUiViewports`, Program.cs L2268 inside `if (DrawUI)` | As above, after `OnDrawUiFrame` (popups, so a save load may have just happened); gizmos were reset at L2260, render is still ahead |
| `StarMapAfterOnFrame` | Harmony postfix on `Program.OnFrame` (L2238-L2334) | After `PostRender` and `FrameNumber++`; runs on every return path of `OnFrame`, including the `PrepareFrame` exit at L2243-L2248 |
| `StarMapUnload` | `ModLoader.Dispose`, from `StarMapCore.DeInit`, from `GameSurveyer.Dispose`[^starmap-modloader][^starmap-core][^starmap-surveyer] | After `App.Run` has returned and `Program` was disposed; StarMap has already called `_harmony.UnpatchAll()` |

StarMap invokes each hook with `MethodInfo.Invoke` and no handler (ProgramPatcher.cs L20-L23,
L32-L35, L44-L47), `App.Run` calls `OnFrame` in a bare `while` loop (App.cs L45-L70) and
`Program.Main` (Program.cs L1693-L1703) has no handler either, so an exception leaving a mod's
frame hook ends the process. That is why the containment lives in the mod. The `DrawUI` guard
means the GUI hooks can be skipped for whole stretches; `OnAfterFrame` is the only hook guaranteed
every frame, which is why it owns the latch release.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L132-L242) | `OnImmediateLoad` and `OnFullyLoaded`: startup order, patch installation |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L268-L305) | `OnAfterFrame`: catch, `Fault`, `finally` latch release |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L491-L666) | `OnBeforeGui`, `OnAfterGui` with the draw-time `finally` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L988-L1040) | `Unload` |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1453-L1476) | `Fault`: count, disable, unpatch |
| [src/KSArmory/Sim/FrameBudget.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FrameBudget.cs) | Span timing with top-level totals and worst-frame tracking |
| [src/KSArmory/Sim/OverrunLog.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/OverrunLog.cs) | Two-bucket rate-limited overrun reporting |
| [src/KSArmory/mod.toml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/mod.toml) | One folder that is both a content mod and a StarMap code mod |

## Snippets

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L268-L305
    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        try
        {
            if (_disabled || _roster is null) return;

            // Sim speed and pause state change what everything else in the log means, so record
            // them rather than inferring them later from frozen timestamps.
            if (KsaWorld.InFlight)
            {
                double speed = KsaWorld.SimulationSpeed;
                if (Math.Abs(speed - _lastSimSpeed) > 1e-9)
                {
                    Log.Info($"simulation speed {_lastSimSpeed:F2}x -> {speed:F2}x"
                             + (KsaWorld.IsPaused ? " (paused)" : ""));
                    _lastSimSpeed = speed;
                }
            }

            // The fallback, and a no-op on every frame the GUI pass ran. See StepOnce.
            StepOnce(dtPlayer);

            // After the step, so a command sees this frame's world and its simulated step.
            _bridge?.Update(dtPlayer, KsaWorld.InFlightScene && !KsaWorld.IsPaused ? _lastSimStep : 0.0);
        }
        catch (Exception e)
        {
            Fault("frame", e);
        }
        finally
        {
            // Unconditionally, and ahead of nothing: this is the only hook KSA always calls, so a
            // release skipped by an early return or an exception would stop the mod for the rest of
            // the session rather than for a frame.
            _frame.EndFrame();
        }
    }
```

```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L1453-L1476
    private void Fault(string where, Exception e)
    {
        _faults++;
        Log.Error($"{where} failed ({_faults}/{FaultLimit})", e);

        if (_faults < FaultLimit) return;

        _disabled = true;
        _roster?.Clear();
        _armaments.Clear();
        _heads?.Clear();
        _heads = null;
        _countermeasures?.Discard();
        _countermeasures = null;
        _icbms?.Clear();
        _icbms = null;
        KsaWorld.Wreckage.Clear();
        AttitudeHook.Remove();
        PreRenderHook.Remove();
        WorldReloadHook.Remove();
        RoundBodyDrawHook.Remove();
        LooseBodyDrawHook.Remove();
        Log.Error("too many faults - air defence disabled for this session");
    }
```

```csharp
// StarMap.Core/Patches/ProgramPatcher.cs L38-L48
        [HarmonyPatch(OnFrameMethodName)]
        [HarmonyPostfix]
        public static void AfterOnFrame(double currentPlayerTime, double dtPlayer)
        {
            var methods = StarMapCore.Instance?.Loader.ModRegistry.Get<StarMapAfterOnFrameAttribute>() ?? [];

            foreach (var (_, @object, method) in methods)
            {
                method.Invoke(@object, new object[] { currentPlayerTime, dtPlayer });
            }
        }
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Program.OnFrame`, `Program.OnDrawUiFrame`, `Program.OnDrawUiViewports` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334) | Patched by StarMap to deliver the three frame hooks |
| `Mod.PrepareSystems`, `Mod.Id`, `Mod.DirectoryPath` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L82-L91) | `ImmediateLoad` carrier; the mod folder for the generated shader include[^ksa-mod] |
| `ModLibrary.PrepareAll`, `ModLibrary.LoadAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L520) | Startup phases the two load hooks hang off |
| `App.Run` | [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L38-L73) | The loop with no exception handler around `OnFrame` |
| `Universe.SimulationSpeed`, `Universe.IsPaused` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L122-L131) | Logged on change from the frame hook (through `KsaWorld`)[^ksa-universe] |

## Pitfalls and version notes

- **Load-time hooks are not wrapped.** `OnImmediateLoad`, `OnFullyLoaded` and `Unload` have no
  `try/catch`; each patch installer catches its own failure, but anything else that throws there
  propagates into `ModLibrary.PrepareAll`, the `LoadAll` postfix or StarMap's shutdown.
- **`OnBeforeGui` swallows without counting**, so a menu entry that throws every frame never trips
  `FaultLimit`. The counter never resets: ten faults per session, not ten in a row.
- **A fault inside `StepOnce` reached from the pre-render prefix is not counted.** `PreRenderHook`
  catches it, nulls its delegate and leaves stepping to the frame postfix for the rest of the session.
- **`CloudPassHook` is never removed.** `Unload` and `Fault` remove five patches; `CloudPassHook.Remove`
  exists (CloudPassHook.cs L89) and has no caller.[^ksarmory-cloudpasshook]
- **`AttitudeHook.Remove` clears only `Wanted`, `Staging` and `Shoves`**, leaving the quiet, rails,
  pulse and throttle-key sets holding vehicle references.
- **Unload runs at process exit under StarMap 4dc60f8.** `StarMapCore.DeInit` unpatches everything and
  then calls `[StarMapUnload]`, after `Program.Main` returned. The warp and camera hand-back in
  `Unload` only matters to a loader that unloads mid-session.[^starmap-core]
- **Docs vs StarMap:** `docs/KSA-MODDING-NOTES.md` (StarMap section) says `ImmediateLoad` fires "as
  this mod finishes loading". It fires as a prefix on `Mod.PrepareSystems`, before the mod's asset
  bundles are preloaded.[^ksarmory-moddingnotes]
- **Reporting is switched off on build 5554.** `ksa-assemblies.lock` at this commit says
  2026.10.7.5541, so `ReportDraft.GameIsSupported` compares unequal and the startup line reads
  `reporting off` (see [own log file and developer switch](./own-log-file-and-developer-switch.md)).[^ksarmory-lock][^ksarmory-reportdraft]

## See also

- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Consume the applied simulation step](./consume-applied-sim-step.md)
- [PrepareWorker prefix command window](./prepareworker-prefix-command-window.md)
- [Detect a save load via OnGameLoaded](./detect-save-load-ongameloaded.md)
- [Startup save repair](./startup-save-repair.md)
- [Own log file and developer switch](./own-log-file-and-developer-switch.md)
- [Timewarp policy during engagements](./timewarp-policy-during-engagements.md)
- [StarMapAllModsLoaded: work that needs the full mod set](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-3.md) - how-to article demonstrating this feature
- [Diagnose a StarMap hook that never fires with a signature checklist](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-4.md) - how-to article demonstrating this feature
- [Wrap every frame hook in try/catch and disable the mod after ten faults](../../../howto/getting-started/lifecycle/contain-exceptions-in-starmap-hooks/contain-exceptions-in-starmap-hooks-1.md) - how-to article demonstrating this feature
- [Hook the frame with StarMap lifecycle attributes](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) - how-to article demonstrating this feature
- [Nested budget spans with top-level totals, the worst frame and rate-limited overrun warnings](../../../howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-3.md) - how-to article demonstrating this feature

[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-modtoml]: src/KSArmory/mod.toml
[^ksarmory-framebudget]: src/KSArmory/Sim/FrameBudget.cs
[^ksarmory-overrunlog]: src/KSArmory/Sim/OverrunLog.cs
[^ksarmory-attitudehook]: src/KSArmory/Ksa/AttitudeHook.cs
[^ksarmory-cloudpasshook]: src/KSArmory/Ksa/CloudPassHook.cs
[^ksarmory-lock]: ksa-assemblies.lock
[^ksarmory-reportdraft]: src/KSArmory/Sim/ReportDraft.cs
[^ksarmory-moddingnotes]: docs/KSA-MODDING-NOTES.md
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-modpatches]: StarMap.Core/Patches/ModPatches.cs
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^starmap-surveyer]: StarMap/GameSurveyer.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-app]: KSA.App (decompiled)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled)
[^ksa-mod]: KSA.Mod (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
