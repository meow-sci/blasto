---
type: How-To
title: Throw on a bad match, UnpatchAll(own id), publish the instance on success
description: "Treat a changed game method as a normal outcome: validate lookups, make the transpiler throw unless exactly one call matched, catch around Patch, unpatch by your own Harmony id and publish the instance only on success so the UI stays honest"
category: patching
group: safety
topic: refuse-a-patch-and-roll-back-when-the-game-changes
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksaero/features/patch-refusal-and-rollback
  - mods/ksaero/features/computedrag-call-site-transpiler
techniques: [harmony-transpiler, harmony-unpatch-by-id]
hooks: [starmap StarMapAllModsLoaded, harmony-transpiler KSA.PhysicsStates.ComputeDerivatives]
lifecycle: [startup:all-mods-loaded, event:starmap-unload]
game_types: [KSA.PhysicsStates, KSA.PhysicsEnvironment, HarmonyLib.Harmony]
tags: [harmony, transpiler, rollback, refusal, unpatch, safety, game-update]
---

A game update can change the method your patch depends on. KSAero treats that as a normal outcome rather than a
crash: if its transpiler cannot find exactly one atmospheric `ComputeDrag` call it throws, the mod catches the
failure, removes every patch registered under its own Harmony id, logs one warning with the exception, and
leaves its `_harmony` field null so its readout window never appears. The player keeps flying with stock
aerodynamics. The pattern applies to any transpiler or any patch whose correctness depends on the IL shape of a game
method.

Prerequisites: a StarMap mod with `0Harmony` and a transpiler
([Writing a Harmony transpiler for a KSA method](../../transpilers/write-a-harmony-transpiler/overview.md)). Demonstrated by KSAero in [Patch refusal and rollback](../../../../mods/ksaero/features/patch-refusal-and-rollback.md) and
[ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md).

## Background: how the game does it

`PhysicsStates.ComputeDerivatives` calls the private static `ComputeDrag` twice, once for the ocean with
`environment.OceanDensity` and once for the air with `environment.AtmosphericDensity`
([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1002-L1017)[^ksa-physicsstates]). KSAero retargets only the second call. If a future build merges, reorders or
removes those calls, replacing zero or two call sites would give wrong aerodynamics silently, so the transpiler
counts what it replaced and refuses unless the count is one.

## Lifecycle and timing

- **Apply and roll back: `startup:all-mods-loaded`.** `[StarMapAllModsLoaded]` fires from StarMap's postfix on
  `ModLibrary.LoadAll` ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)[^starmap-modlibrarypatches]), during game construction and
  before any vehicle is simulated. Failing here means stock physics was never replaced; there is nothing to restore in
  the simulation and the decision is final for the session.
- **Unload: `event:starmap-unload`.** `[StarMapUnload]` methods run from `ModLoader.Dispose`, which runs from
  `StarMapCore.DeInit` when StarMap's `GameSurveyer` is disposed after `RunGame` returns. `DeInit` calls
  `_harmony.UnpatchAll()` with **no id** first, so by the time `Unload` runs every Harmony patch in the process,
  KSAero's included, is already gone:

```csharp
// StarMap.Core/StarMapCore.cs L31-L35
public void DeInit()
{
    _harmony.UnpatchAll();
    _loader.Dispose();
}
```

Source: [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)[^starmap-starmapcore]

```csharp
// StarMap.Core/ModRepository/ModLoader.cs L143-L151
public void Dispose()
{
    foreach (var (_, @object, method) in _modRegistry.Get<StarMapUnloadAttribute>())
    {
        method.Invoke(@object, []);
    }

    _modRegistry.Dispose();
}
```

Source: [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)[^starmap-modloader]

  KSAero's own `UnpatchAll("ksaero")` there is a harmless no-op; only its log line has an effect. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
  [Removing Harmony patches safely at runtime and at exit](../remove-your-patches-safely-at-runtime-and-at-exit/overview.md).

## Steps

1. **Resolve every lookup and fail if any is missing.** `Apply` checks the three members the transpiler needs and
   throws `MissingMemberException` naming them:

```csharp
// KSAero/AirForcePatch.cs L52-L59
internal static void Apply(Harmony harmony)
{
    if (ComputeDerivatives == null || ComputeDrag == null || AtmosphericDensity == null)
        throw new MissingMemberException("PhysicsStates.ComputeDerivatives, PhysicsStates.ComputeDrag or PhysicsEnvironment.AtmosphericDensity not found.");

    _stockDrag = AccessTools.MethodDelegate<ComputeDragFn>(ComputeDrag);
    harmony.Patch(ComputeDerivatives, transpiler: new HarmonyMethod(typeof(AirForcePatch), nameof(Transpiler)));
}
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L52-L59)[^ksaero-airforcepatch]

2. **Make the transpiler refuse.** Count the rewrites and throw unless exactly one. Returning the unmodified list
   would "succeed" and leave a registered patch that does nothing, and the UI gate would be wrong:

```csharp
// KSAero/AirForcePatch.cs L61-L81
private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
{
    List<CodeInstruction> code = instructions.ToList();
    MethodInfo replacement = AccessTools.Method(typeof(AirForcePatch), nameof(ComputeAirForces));
    int replaced = 0;

    for (int i = 0; i < code.Count; i++)
    {
        if (code[i].Calls(ComputeDrag!) && PassesAtmosphericDensity(code, i))
        {
            code[i].operand = replacement;
            replaced++;
        }
    }

    // Anything else means the method has changed shape, and stock aero is the safe answer.
    if (replaced != 1)
        throw new InvalidOperationException($"Expected one atmospheric ComputeDrag call in ComputeDerivatives, found {replaced}.");

    return code;
}
```

Source: [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs#L61-L81)

3. **Catch around `Patch` and unpatch by your own id.** Harmony surfaces a transpiler exception from `harmony.Patch`.
   The catch block removes only patches registered under `HarmonyId`:

```csharp
// KSAero/Mod.cs L14-L36
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
```

Source: [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L14-L36)[^ksaero-mod]

   A local test against Lib.Harmony 2.4.2 (the version StarMap references) confirms the shape of the failure: a
   transpiler that throws makes `Patch` throw a wrapped exception (`TargetInvocationException` for a direct
   `Patch` call, `HarmonyException` through `PatchAll`), the original method keeps running unpatched, and no patch
   owner remains. Use `GetBaseException()` or log the whole exception so the transpiler's message ("found 0")
   reaches the log.

4. **Publish the instance only after success.** `_harmony = harmony` runs after `Apply` returns, so every consumer of
   `_harmony` knows whether the physics patch is live. The readout window checks it:

```csharp
// KSAero/Mod.cs L38-L43
[StarMapAfterGui]
public void DrawGui(double dt)
{
    if (_harmony != null)
        AeroReadout.Draw();
}
```

Source: [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs#L38-L43)

5. **Warn exactly once, with the exception.** The log line carries `{ex}`. The player's only signal that the mod is inert
   on their build is that warning.

6. **Never call `UnpatchAll()` without an id mid-session.** The same test shows an id-less `UnpatchAll()` on any
   Harmony instance removes patches owned by every other id:

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
both installed:
  prefix from mod a
  prefix from mod b
  original
mod.a calls UnpatchAll("mod.a"):
  prefix from mod b
  original
mod.a re-installed; a third instance calls UnpatchAll() with no id:
  original
```

## Complete example

| File | Role |
|---|---|
| [KSAero/Mod.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/Mod.cs) | `OnFullyLoaded` with version check, apply, rollback; `Unload` |
| [KSAero/AirForcePatch.cs](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/AirForcePatch.cs) | Member lookups, counted transpiler |

## Variations and gotchas

- **The transpiler runs again later.** Harmony re-runs every registered transpiler on `ComputeDerivatives` whenever any
  mod patches that method. If a transpiler installed after KSAero's removes the atmospheric call, KSAero's throw
  surfaces from the *other* mod's `Patch` call, outside KSAero's `try`. See
  [Anchors that one mod can consume (and what the next transpiler then sees)](../../harmony/coexist-with-other-mods-patching-the-same-method/coexist-with-other-mods-patching-the-same-method-2.md).
- **The version check is advisory.** KSAero warns when the build differs from `TestedGameVersion`, but the structural check decides
  ([Game version check](../../../../mods/ksaero/features/game-version-check.md)).
- **Dependent features gate on the result.** Anything that would lie without the patch, such as a readout of drag the
  physics is not applying, must be gated on the same success flag.
- **Do not swallow silently.** A silent catch hides the only message a player can send you.

## See also

- [One Harmony owner per feature with TryApply, validated handles and runtime flags](refuse-a-patch-and-roll-back-when-the-game-changes-2.md), [Let an anchor failure stop the launch (and why that can be right)](refuse-a-patch-and-roll-back-when-the-game-changes-3.md)
- [Refusing a patch and rolling back when the game changes](overview.md)
- [Patch refusal and rollback](../../../../mods/ksaero/features/patch-refusal-and-rollback.md), [ComputeDrag call-site transpiler](../../../../mods/ksaero/features/computedrag-call-site-transpiler.md)
- [Isolating patch failures and keeping patch bodies from throwing](../isolate-patch-failures-and-fault-proof-patch-bodies/overview.md), [Validating patch targets and detecting game-update breakage](../validate-patch-targets-and-detect-game-update-breakage/overview.md)
- Related topic: check-the-game-version

[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^starmap-modlibrarypatches]: StarMap/StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-starmapcore]: StarMap/StarMap.Core/StarMapCore.cs
[^starmap-modloader]: StarMap/StarMap.Core/ModRepository/ModLoader.cs
[^ksaero-airforcepatch]: KSAero/AirForcePatch.cs
[^ksaero-mod]: KSAero/Mod.cs
