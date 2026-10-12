---
type: How-To
title: Pin patched members with a never-called method that really calls them
description: Make a signature change in a public patch target a build error by adding a never-called method that calls it, because a call emits a member reference in the assembly and nameof does not; private targets cannot be pinned this way
category: patching
group: safety
topic: validate-patch-targets-and-detect-game-update-breakage
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksagolf/features/degrading-harmony-patches]
techniques: [reflection, compile-time-pin]
hooks: [harmony-postfix KSA.Program.OnGameLoaded, harmony-prefix KSA.Universe.ExecuteNextVehicleSolvers]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Program, KSA.Universe, KSA.ConstraintSim]
tags: [harmony, pin, compile-time, api-surface, game-update, memberref]
---

Run-time validation finds breakage when a player starts the game. KSAGolf also makes breakage visible when *you* build: for each
public member it patches, it adds a method that is never called but really calls the target. The compiler then emits a member
reference for the target in the mod's assembly, so a tool that lists a mod's external member references can record the surface,
and a KSA signature change becomes a build error instead of a silent no-op patch. Private targets cannot be pinned this way; those
need the run-time checks in [Feature-tagged handle registry with a Validate per feature](validate-patch-targets-and-detect-game-update-breakage-1.md) and [Non-throwing static constructor with fatal and degraded lists](validate-patch-targets-and-detect-game-update-breakage-2.md).

Prerequisites: a StarMap mod with `0Harmony` that references the game assemblies at build time. Demonstrated by KSAGolf in
[Degrading Harmony patches with per-feature ids and signature pins](../../../../mods/ksagolf/features/degrading-harmony-patches.md).

## Background: why a call and not `nameof`

`nameof(G.Target)` compiles to a string literal; only a call emits a reference to the member. A local test with a stub game
assembly and a mod assembly showed the difference in the IL printed by `ilspycmd -il`:

```text
// observed output of a local test (ilspycmd -il on a throwaway mod assembly, not part of any mod repository)
string ByName () cil managed
    IL_0000: ldstr "PublicTarget"
    IL_0005: ret
void PinTheSignature () cil managed
    IL_0000: call void [Game]G::PublicTarget()
    IL_0005: ret
```

The `call` is a member reference to `[Game]G::PublicTarget()` with its full signature, so changing the game's signature breaks the
mod's compile. A string name never breaks at compile time.

## Lifecycle and timing

The pins run at build time, not at run time; the patches they protect are installed at `[StarMapAllModsLoaded]`
(`startup:all-mods-loaded`). Nothing about the pin methods executes in the game: they are private and never called. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write a never-called method that really calls the public target.** KSAGolf's `PadSurface` pins the solver dispatch it patches:

```csharp
// src/KSAGolf/Ksa/PadSurface.cs L316
private static void PinTheStep(double dtPlayer, SimStep step) => Universe.ExecuteNextVehicleSolvers(dtPlayer, step);
```

Source: [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs#L316)[^ksagolf-padsurface]

   `WorldReloadHook` pins `Program.OnGameLoaded`:

```csharp
// src/KSAGolf/Ksa/WorldReloadHook.cs L124
private static void PinTheSignature() => Program.OnGameLoaded();
```

Source: [src/KSAGolf/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/WorldReloadHook.cs#L124)[^ksagolf-worldreloadhook]

2. **Pin the whole signature, including the parameters.** The pin must call the target with arguments of the exact types, so a
   changed parameter list fails to compile. `CourseCollider` pins its three physics targets together:

```csharp
// src/KSAGolf/Ksa/CourseCollider.cs L253-L258
private static void PinThePatches(ConstraintSim sim, double dt, SimStep step)
{
    sim.DetectCollisions(dt);
    sim.Simulate(dt, in step);
    sim.TryResetForPool();
}
```

Source: [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L253-L258)[^ksagolf-coursecollider]

3. **Record the surface.** The member references land in the mod's metadata; `tools/api-surface.sh` records them in
   `docs/KSA-API-SURFACE.md`, which is diffed in review. (This is the build-level drift detection that the
   testing-and-tooling category covers: a hashed assembly lock and an external member surface.)

4. **Do not rely on the pin for private members.** The private `Program.OnFrameCelestials` cannot be pinned this way; it is
   looked up by name and degrades ([Static patch classes that look up the target, log once and stand down](../isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-3.md)).

5. **Check that the pin is a call.** `ViewDrawHook` names its target with `nameof` and has no pin, so `ClearBuckets` is absent
   from the API record even though the comments say otherwise. A pin that is not a real call protects nothing.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/PadSurface.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PadSurface.cs#L316) | Pin for `Universe.ExecuteNextVehicleSolvers` |
| [src/KSAGolf/Ksa/WorldReloadHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/WorldReloadHook.cs#L24-L30) | Why the target is public and pinned |
| [src/KSAGolf/Ksa/CourseCollider.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseCollider.cs#L253-L258) | Pin for the `ConstraintSim` targets |

## Variations and gotchas

- **Only public members.** A private target has no member reference to emit; pin the public callers or validate at run time.
- **The pin is only as good as the call.** If the call and the patch target differ (an overload, an interface method), the pin can
  stay green while the patch binds to something else; keep the pin's call and the patch's `AccessTools` lookup identical in
  parameter types.
- **Comments rot.** Review the API record for each patched member; a class that forgot its pin looks identical until the day it
  matters.
- **Pair with run-time degrade.** A pinned build can still meet a different game build in the field; the patch class still
  looks the target up and logs once when it is gone.

## See also

- [Feature-tagged handle registry with a Validate per feature](validate-patch-targets-and-detect-game-update-breakage-1.md), [Non-throwing static constructor with fatal and degraded lists](validate-patch-targets-and-detect-game-update-breakage-2.md)
- [Validating patch targets and detecting game-update breakage](overview.md)
- [Degrading Harmony patches with per-feature ids and signature pins](../../../../mods/ksagolf/features/degrading-harmony-patches.md)
- Related topics: pin-the-game-build-with-a-hashed-lock, upgrade-a-mod-to-a-new-ksa-build, check-the-game-version

[^ksagolf-padsurface]: KSAGolf/src/KSAGolf/Ksa/PadSurface.cs
[^ksagolf-worldreloadhook]: KSAGolf/src/KSAGolf/Ksa/WorldReloadHook.cs
[^ksagolf-coursecollider]: KSAGolf/src/KSAGolf/Ksa/CourseCollider.cs
