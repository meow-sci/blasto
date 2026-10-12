---
type: Mod Feature
title: Degrading Harmony patches with per-feature ids and signature pins
description: One static class per Harmony patch, each under its own Harmony id, looking its target up defensively, logging once and standing down instead of throwing, pinned into the API record by a never-called call, and removed by a fault limit rather than by StarMapUnload
mod: ksagolf
category: patching
techniques: [harmony-prefix, harmony-postfix, reflection, starmap-lifecycle]
hooks:
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterGui
  - starmap StarMapAfterOnFrame
  - starmap StarMapUnload
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:after-on-frame]
game_types:
  - KSA.Program
  - KSA.Universe
  - KSA.SuperMeshRenderSystem
  - KSA.ConstraintSim
  - KSA.KittenEva
  - KSA.KittenLocomotion
tags: [harmony, patching, robustness, degrade, fault-limit, api-surface, starmap]
---

KSAGolf patches eleven engine methods, one of them private and three of them called on vehicle
worker threads, against a game with no modding API that changes every few weeks. It treats every patch as optional. Each one lives
in its own static class with its own Harmony id. Each looks its target up and logs once when the
target is gone, then runs without it. Each body catches everything. The mod's frame wrapper counts
faults and pulls every patch after ten. What a missing patch costs is written down per class
(CLAUDE.md, "The patches are prefixes and postfixes whose absence degrades"): a frame of lag, a
reload's worth of stale state, an undrawn ball, a kitten that walks through a course. None of them
crashes the game.[^golf-claude-md]

## How it works

| Class | Harmony id | Target (kind) | Lost if missing |
|---|---|---|---|
| `PreRenderHook` | `com.ksagolf.prerender` | private `Program.OnFrameCelestials(double)` (prefix) | Hidden-HUD frames step one frame late |
| `WorldReloadHook` | `com.ksagolf.worldreload` | `Program.OnGameLoaded()` (postfix) | Mod state outlives a save load |
| `ViewDrawHook` | `com.ksagolf.viewdraw` | `SuperMeshRenderSystem.ClearBuckets(IViewport)` (postfix) | Ball, club and course meshes not drawn |
| `FirstPersonHook` | `com.ksagolf.firstperson` | `KittenEva.UpdateRenderData`, `AnimatedRenderable.Draw`, `StaticMeshRenderable.Draw` (prefix and postfix); `KittenLocomotion.StepGrounded` (optional, prefix and postfix) | First person looks out through the kitten's head; without the walk patch the kitten turns away from the view |
| `PadSurface` | `com.ksagolf.padsurface` | `Universe.ExecuteNextVehicleSolvers` (prefix) | The ball rests on terrain alone, not on launch pads |
| `CourseCollider` | `com.ksagolf.coursecollider` | `Universe.ExecuteNextVehicleSolvers`, `ConstraintSim.DetectCollisions`, `Simulate`, `TryResetForPool` (prefixes) | Kittens walk through courses |

The same five rules hold in every class:[^golf-prerenderhook][^golf-worldreloadhook][^golf-viewdrawhook][^golf-firstpersonhook][^golf-padsurface][^golf-coursecollider]

1. **Look the target up, do not assume it.** `AccessTools.Method(type, name, argTypes)` or
   `Type.GetMethod(..., [typeof(IViewport)])`. A null result logs one `Warn` naming what is lost and
   returns with `Installed` false. `CourseCollider` throws `MissingMethodException` from a helper,
   catches it, and unpatches whatever it had already applied under its id.
2. **One Harmony id per feature.** `new Harmony(HarmonyId)` and, in `Remove()`,
   `_harmony?.UnpatchAll(HarmonyId)`. Passing the id limits the removal to this class's own patches,
   so pulling the first-person patches leaves the ball drawn.
3. **Nothing in a patch body may throw.** Every prefix and postfix wraps its work in `try`/`catch`.
   A failure that would repeat every frame nulls the delegate the patch calls and logs once
   (`_complained`). The patch then stands down, which is the same outcome as a target that never
   existed. The worker-thread prefixes in `CourseCollider` follow the same rule; their one log line
   comes from a vehicle worker thread, and `Log` takes a lock.
4. **Hand state over, never call back into the mod's object graph.** Patches are static, so the mod
   hands each one a delegate (`PreRenderHook.Install(StepOnce)`, `ViewDrawHook.Install(DrawIntoView)`)
   or a volatile snapshot (`PadSurface.Want`, `CourseCollider.Want`). `Remove()` nulls the delegate
   first, "which is what stops a stale patch reaching a dead mod".
5. **Pin the public targets.** Each class with a public target has a never-called method that
   really calls it (`PinTheSignature`, `PinTheStep`, `PinThePatches`, `PinTheWalk`, `PinTheRender`).
   The compiler then emits a member reference, `tools/api-surface.sh` records the member in
   `docs/KSA-API-SURFACE.md`, and a KSA signature change becomes a build error, not a silent
   no-op. The private `OnFrameCelestials` cannot be pinned this way. `ViewDrawHook` has no pin, so
   `ClearBuckets` is absent from the record although its comment says otherwise. See
   [API surface record and assembly lock](api-surface-and-assembly-lock.md).[^golf-api-surface-md]

Each class also exposes `Installed` and, for the two `Program` hooks, a `Trouble` string. Nothing
reads them at this commit; the log is the only report.

**The fault limit.** `KSAGolfMod` wraps its `StarMapAfterGui` and `StarMapAfterOnFrame` work in
`try`/`catch` and counts exceptions in `Fault`. At ten it sets `_disabled`, releases the mover,
builder and putting session, and calls `Remove()` on all six patch classes. Every later hook returns
at its first line. It does not restore KSA's camera controller; letting go of the putting session
hands any borrowed view back first.[^golf-mod]

## Lifecycle and timing

| Step | Where it runs | Lifecycle |
|---|---|---|
| `Install()` for all six classes | `[StarMapAllModsLoaded] OnFullyLoaded`, the postfix StarMap puts on `ModLibrary.LoadAll` | `startup:all-mods-loaded` |
| Patch bodies | At their targets: `frame:dispatch-solvers` (`ExecuteNextVehicleSolvers`), `physics:vehicle-solver` and `physics:bepu` (worker prefixes), `frame:celestials`, `frame:render`, `event:save-loaded`; see each feature | various |
| `Fault` and the fault-limit `Remove()` | Inside whichever of `[StarMapAfterGui]` or `[StarMapAfterOnFrame]` caught the tenth exception | `frame:draw-ui`, `frame:after-on-frame` |
| `Remove()` from `[StarMapUnload]` | Process exit, after StarMap has already unpatched everything | (shutdown) |

- **Removal only matters for the fault limit.** StarMap calls `[StarMapUnload]` methods from
  `ModLoader.Dispose`. That runs from `StarMapCore.DeInit`, after `Program.Main` has returned and
  right after an id-less `Harmony.UnpatchAll()` that removes every patch in the process. So the
  `Remove()` calls in `KSAGolfMod.Unload` find nothing left to unpatch. The fault-limit path is the
  only one where `Remove()` takes patches out of a running game.[^starmap-core][^starmap-modloader]
- **Removing from inside the frame.** The fault-limit removal runs on the main thread from a
  StarMap hook. At that point the `ConstraintSim` prefixes may be executing on vehicle workers,
  because the next physics step was dispatched earlier in the same frame. The mod does not wait for
  the solver before unpatching; Harmony's behaviour for a method that is mid-call during an unpatch
  was not checked here.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/WorldReloadHook.cs#L61-L124) | The template: lookup, own id, logged degrade, remove, never-throwing postfix, signature pin |
| [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs#L55-L130) | A private target by name, and a body that stands down on its first exception |
| [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L63-L110) | Four patches applied all-or-nothing, with a throwing lookup helper |
| [src/KSAGolf/Ksa/FirstPersonHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/FirstPersonHook.cs#L82-L119) | One optional target inside an otherwise required set |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L191-L240) | `Unload` and the fault limit |

## Snippets

```csharp
// src/KSAGolf/Ksa/WorldReloadHook.cs L61-L94
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

```csharp
// src/KSAGolf/Ksa/PreRenderHook.cs L113-L130
    private static void BeforeOnFrameCelestials(double deltaTime)
    {
        try
        {
            _step?.Invoke(deltaTime);
        }
        catch (Exception e)
        {
            // Stand down rather than throw again next frame from inside the engine's loop. The
            // frame postfix has the mod's own fault handling, and it is one hook later -- so
            // giving up here is the same degradation a refused patch gives, arrived at differently.
            _step = null;

            if (_complained) return;
            _complained = true;
            Log.Error("the pre-render step failed; the frame postfix takes it from here", e);
        }
    }
```

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L97-L101, L252-L258
    private static void Patch(Type type, string method, string prefix)
    {
        MethodInfo target = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.Name, method);
        _harmony!.Patch(target, prefix: new HarmonyMethod(typeof(CourseCollider).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
    }
    // ...
    // Never called. Puts the patched methods in this assembly's metadata for the API record.
    private static void PinThePatches(ConstraintSim sim, double dt, SimStep step)
    {
        sim.DetectCollisions(dt);
        sim.Simulate(dt, in step);
        sim.TryResetForPool();
    }
```

```csharp
// src/KSAGolf/Ksa/KSAGolfMod.cs L222-L240
    private void Fault(string where, Exception e)
    {
        _faults++;
        Log.Error($"{where} failed ({_faults}/{FaultLimit})", e);

        if (_faults < FaultLimit) return;

        _disabled = true;
        _mover.Release();
        _builder?.Release();
        _golf?.Release();
        ViewDrawHook.Remove();
        FirstPersonHook.Remove();
        PadSurface.Remove();
        CourseCollider.Remove();
        PreRenderHook.Remove();
        WorldReloadHook.Remove();
        Log.Error("too many faults - KSAGolf disabled for this session");
    }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Program.OnFrameCelestials` (private), `Program.OnGameLoaded` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599-L2732) | The two `Program` targets: one private and unpinnable, one public and pinned |
| Patch targets of the other classes | see [Mod mesh draw hook](mod-mesh-draw-hook.md), [First-person kitten view](first-person-kitten-view.md), [Launch-site surface raycast](launch-site-surface-raycast.md), [Course colliders as clutter](course-colliders-as-clutter.md) | Covered with their decomp links in each feature |
| StarMap `StarMapCore.DeInit` | [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35) | `_harmony.UnpatchAll()` with no id, then `ModLoader.Dispose` invokes `[StarMapUnload]` |

## Pitfalls and version notes

- **Do not rely on `StarMapUnload` to unpatch.** It runs only at process exit and after every patch
  is already gone; a mod that needs to remove its patches during play (a fault limit, a settings
  toggle) has to do it itself, under its own id.[^starmap-core]
- **A swallowed exception is a silent feature.** The degrade is logged once and then nothing shows
  it. `Installed` and `Trouble` exist for a UI to report, but at this commit the panel does not.
- **The pin is only as good as the call.** `ViewDrawHook` names its target with `nameof`, which
  compiles to a string and emits no member reference. So `ClearBuckets` is missing from the API
  record even though CLAUDE.md says it is "pinned the same way".[^golf-api-surface-md][^golf-claude-md]
- **Version.** All eleven targets keep their names and signatures in build 2026.10.10.5554.

## See also

- [Dual-hook frame stepping on simulated time](dual-hook-frame-stepping.md)
- [API surface record and assembly lock](api-surface-and-assembly-lock.md)
- [Save-load detection](save-load-detection.md)
- [Drawing a mod's static mesh into every viewport](mod-mesh-draw-hook.md)
- [First-person kitten view that keeps the shadow](first-person-kitten-view.md)
- [Course colliders registered as ground clutter](course-colliders-as-clutter.md)
- [Launch-site surface by raycast into a copied collider](launch-site-surface-raycast.md)
- [Manual AccessTools lookup with a degrade path when the method is gone](../../../howto/patching/internals/patch-a-private-game-method-by-name/patch-a-private-game-method-by-name-2.md) - how-to article demonstrating this feature
- [Static patch classes that look up the target, log once and stand down](../../../howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-3.md) - how-to article demonstrating this feature
- [Fault counter that pulls every patch after a limit](../../../howto/patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-5.md) - how-to article demonstrating this feature
- [UnpatchAll(ownId) from a main-thread hook after nulling delegates](../../../howto/patching/safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-1.md) - how-to article demonstrating this feature
- [Pin patched members with a never-called method that really calls them](../../../howto/patching/safety/validate-patch-targets-and-detect-game-update-breakage/validate-patch-targets-and-detect-game-update-breakage-3.md) - how-to article demonstrating this feature

[^golf-worldreloadhook]: src/KSAGolf/Ksa/WorldReloadHook.cs
[^golf-prerenderhook]: src/KSAGolf/Ksa/PreRenderHook.cs
[^golf-viewdrawhook]: src/KSAGolf/Ksa/ViewDrawHook.cs
[^golf-firstpersonhook]: src/KSAGolf/Ksa/FirstPersonHook.cs
[^golf-padsurface]: src/KSAGolf/Ksa/PadSurface.cs
[^golf-coursecollider]: src/KSAGolf/Ksa/CourseCollider.cs
[^golf-mod]: src/KSAGolf/Ksa/KSAGolfMod.cs
[^golf-claude-md]: CLAUDE.md
[^golf-api-surface-md]: docs/KSA-API-SURFACE.md
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
