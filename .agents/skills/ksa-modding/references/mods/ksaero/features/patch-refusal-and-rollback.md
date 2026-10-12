---
type: Mod Feature
title: Patch refusal and rollback
description: KSAero throws from its own transpiler when the target method has changed shape, then catches the failure, removes only its own Harmony patches by id and leaves the stock behaviour and its UI switched off
mod: ksaero
category: patching
techniques: [harmony-transpiler, starmap-lifecycle, fail-safe-patching]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, event:starmap-unload]
game_types: [KSA.PhysicsStates]
tags: [harmony, safety, rollback, unpatch, version-drift]
---

KSAero treats "the game method no longer looks like I expect" as a normal outcome rather than a
crash. When the transpiler cannot find exactly one atmospheric `ComputeDrag` call it throws; the mod
catches the failure, removes every patch registered under its own Harmony id, logs a single warning
with the exception, and leaves `_harmony` null so the readout window never appears. The player keeps
flying with stock aerodynamics.[^ksaero-mod] [^ksaero-readme]

The pattern is worth copying for any transpiler or any patch whose correctness depends on the IL
shape of a game method that changes between builds.

## How it works

| Layer | Where | Failure signal |
|---|---|---|
| Member lookup | `AirForcePatch.Apply` | `MissingMemberException` if `ComputeDerivatives`, `ComputeDrag` or `AtmosphericDensity` is not found ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L52-L55)) |
| IL shape | `AirForcePatch.Transpiler` | `InvalidOperationException` unless exactly one call was rewritten ([KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L76-L78)) |
| Install | `Mod.OnFullyLoaded` | Any exception from `AirForcePatch.Apply` (Harmony surfaces a transpiler exception from `harmony.Patch`) |
| Rollback | `Mod.OnFullyLoaded` catch block | `harmony.UnpatchAll(HarmonyId)`, then `Log.Warning` with the exception |
| UI gating | `Mod.DrawGui` | Draws the readout only when `_harmony != null` |
| Shutdown | `Mod.Unload` | `_harmony?.UnpatchAll(HarmonyId)`, a no-op in practice because StarMap has already removed every patch (see below) |

Details that make it work:

- **The Harmony instance is published only on success.** `_harmony = harmony` runs after `Apply`
  returns, so every consumer of `_harmony` (the readout gate and `Unload`) knows whether the physics
  patch is live ([KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L23-L35)).
- **Unpatch by id.** `UnpatchAll(HarmonyId)` with `HarmonyId = "ksaero"` removes only patches
  registered under that id. Harmony's `UnpatchAll()` with no id removes every patch in the process,
  whichever Harmony instance made it, so a mod must never call it mid-session. StarMap calls exactly
  that form at shutdown, before any `[StarMapUnload]` method runs
  ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)).[^starmap-core]
- **Clean up even after a failed patch.** The transpiler threw, so the method should not be patched,
  but the unpatch costs nothing and guarantees no partial registration is left behind.
- **Readout follows physics.** If the readout ran without the patch it would show lift and drag the
  physics is not applying. Gating it on `_harmony` keeps the UI honest.
- **One warning, full exception.** The log line carries `{ex}`, so the "found 0" or "found 2" message
  from the transpiler reaches the player's log.

## Lifecycle and timing

- **Apply and rollback: `startup:all-mods-loaded`.** `[StarMapAllModsLoaded]` fires from StarMap's
  postfix on `ModLibrary.LoadAll` ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)),
  during game construction and before any vehicle is simulated. Failing here means stock physics was
  never replaced, so there is nothing to restore in the simulation; the decision is final for the
  session.[^starmap-modlibrarypatches]
- **Unload: `event:starmap-unload`.** `[StarMapUnload]` methods
  ([StarMap.API/BaseAttributes.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/BaseAttributes.cs#L108-L132)) are invoked by
  `ModLoader.Dispose` ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)),
  which runs from `StarMapCore.DeInit` when StarMap's `GameSurveyer` is disposed
  ([StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L58-L61)). That happens when the `using`
  scope in StarMap's `Main` ends after `RunGame` returns
  ([StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L21-L28)), so it is process-shutdown cleanup, not a
  hot-unload while flying.[^starmap-baseattributes] [^starmap-modloader] [^starmap-gamesurveyer] [^starmap-program]
  `StarMapCore.DeInit` calls `_harmony.UnpatchAll()` with no id before `_loader.Dispose()`
  ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)), so when `Unload` runs every
  Harmony patch in the process, KSAero's included, is already gone. KSAero's `UnpatchAll("ksaero")`
  there is a harmless no-op; only the "Unloaded." log line has an effect.

## Key code

| Path | Role |
|---|---|
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L12-L14) | Harmony id constant and the nullable published instance |
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L23-L35) | Try, publish on success, unpatch by id and warn on failure |
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L38-L43) | Readout gated on the published instance |
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L45-L51) | Unpatch on StarMap unload |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L52-L81) | The two throw sites |

## Snippets

```csharp
// KSAero/Mod.cs L12-L51
    private const string HarmonyId = "ksaero";

    private static Harmony? _harmony;

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning($"[KSAero] Tested against {TestedGameVersion}, current is {gameVersion}.");

        var harmony = new Harmony(HarmonyId);
        try
        {
            AirForcePatch.Apply(harmony);
            _harmony = harmony;
            DefaultCategory.Log.Info("[KSAero] Loaded. Atmospheric forces now use the body-of-revolution model.");
        }
        catch (Exception ex)
        {
            // The transpiler refuses rather than guesses, so a failure here leaves stock aero running.
            harmony.UnpatchAll(HarmonyId);
            DefaultCategory.Log.Warning($"[KSAero] Patching failed, stock aerodynamics stay in place: {ex}");
        }
    }

    [StarMapAfterGui]
    public void DrawGui(double dt)
    {
        if (_harmony != null)
            AeroReadout.Draw();
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(HarmonyId);
        _harmony = null;
        DefaultCategory.Log.Info("[KSAero] Unloaded.");
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `PhysicsStates.ComputeDerivatives` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L956) | The method whose IL shape is checked |

The rest is Harmony (`Harmony`, `Harmony.UnpatchAll(string)`) and StarMap (`StarMapAllModsLoaded`,
`StarMapAfterGui`, `StarMapUnload`).

## Pitfalls and version notes

- **The transpiler runs again later.** Harmony re-runs every registered transpiler on
  `ComputeDerivatives` whenever any mod patches that method. If a higher-priority transpiler installed
  after KSAero's removes the atmospheric call, KSAero's throw surfaces from the other mod's `Patch` call,
  outside KSAero's `try`, and it is that mod's patch that fails (Harmony library behaviour, not
  something KSAero handles).
- Throwing from a transpiler is the only clean way to abort: returning the unmodified list would
  "succeed" and leave a patch registered that does nothing, and the readout gate would be wrong.
- Do not swallow the exception silently. The warning is the only signal a player gets that the mod is
  inert on their build.
- The version check in the same method is advisory only; this structural check is what decides. See
  [Game version check](game-version-check.md).
- `_harmony` is `static` while the StarMap entry class is an instance; that is harmless with one
  instance per process, which is how StarMap creates mod classes.

## See also

- [ComputeDrag call-site transpiler](computedrag-call-site-transpiler.md)
- [Game version check](game-version-check.md)
- [Aero readout window](aero-readout-window.md)
- [Hook the frame with StarMap lifecycle attributes](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) - how-to article demonstrating this feature
- [Throw on a bad match, UnpatchAll(own id), publish the instance on success](../../../howto/patching/safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-1.md) - how-to article demonstrating this feature
- [UnpatchAll(ownId) from a main-thread hook after nulling delegates](../../../howto/patching/safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-1.md) - how-to article demonstrating this feature

[^ksaero-mod]: KSAero/Mod.cs
[^ksaero-readme]: KSAero/README.md
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-baseattributes]: StarMap.API/BaseAttributes.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^starmap-gamesurveyer]: StarMap/GameSurveyer.cs
[^starmap-program]: StarMap/Program.cs
