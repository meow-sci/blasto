---
type: How-To
title: Unpatch by patch method on a shared Harmony instance
description: Remove only the patch methods declared on your own class from a Harmony instance shared by many features by walking Harmony.GetAllPatchedMethods, so you never strip another feature's patches; contrast with UnpatchAll(harmony.Id), which removes every feature on the shared instance
category: patching
group: safety
topic: remove-your-patches-safely-at-runtime-and-at-exit
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/sphinx-static-colliders
  - mods/unscience/features/unscience-consolidated-patcher
techniques: [harmony-unpatch-by-method, harmony-prefix, harmony-postfix]
hooks:
  - harmony-postfix KSA.ConstraintSim.BeginStaticObjectPass
  - harmony-prefix KSA.ConstraintSim.TryResetForPool
  - harmony-transpiler KSA.NarrowPhaseCallbacks.AllowContactGeneration
lifecycle: [event:starmap-unload, physics:bepu]
game_types: [KSA.ConstraintSim, KSA.NarrowPhaseCallbacks]
tags: [harmony, unpatch, shared-instance, getpatchinfo, supermod, remove]
---

In the unscience supermod, thirty features share one `Harmony("MeowSci.Unscience")` instance. `UnpatchAll(harmony.Id)` on that instance
would strip every feature's patches. Sphinx's `Remove` therefore walks every patched method, finds the patches owned by the
shared instance whose patch method is declared on `SphinxPhysicsPatches`, and unpatches only those. This article shows the
loop, why the shared id makes it necessary, and the one class in the same mod that does the opposite.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated in [Automatic box or mesh colliders for imported statics with scoped Bepu hooks](../../../../mods/unscience/features/sphinx-static-colliders.md) and
[One Harmony instance applying 30-plus feature patch classes in isolation](../../../../mods/unscience/features/unscience-consolidated-patcher.md).

## Background: what Harmony lets you ask

`Harmony.GetAllPatchedMethods()` lists every method with any patch (from any owner), and `Harmony.GetPatchInfo(method)` returns the
prefixes, postfixes, transpilers and finalizers with their `owner` id and `PatchMethod`. A local test against Lib.Harmony 2.4.2
registered two features' prefixes on one method under one shared id, removed one by patch method, then removed the rest by id:

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
two features on one shared Harmony id:
  prefix from feature X
  prefix from feature Y
  original
remove by patch method (feature X only):
  prefix from feature Y
  original
remove by id (UnpatchAll(shared.Id)):
  original
```

Sphinx patches `ConstraintSim` methods and, with a transpiler, the internal `NarrowPhaseCallbacks.AllowContactGeneration`
(`AggressiveInlining`, on a struct used as a generic parameter). In its stock body a vehicle collides with a static only if the
static is a clutter static, a terrain block or the vehicle's own ground surface:

```csharp
// current/decomp/KSA/NarrowPhaseCallbacks.cs L23-L25, L46-L56
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
{
// ...
	BubbleClutterStatics clutterStatics = Sim.ClutterStatics;
	if (clutterStatics != null && clutterStatics.IsClutterStatic(collidableReference.StaticHandle))
	{
		return true;
	}
	bool flag = Sim.TerrainBlocks?.IsTerrainBlock(collidableReference.StaticHandle) ?? false;
	if (!Sim.HandleToState.TryGetValue(collidableReference2.BodyHandle, out VehicleUpdateState value))
	{
		return Sim.ClutterStatics?.IsClutterBody(collidableReference2.BodyHandle) ?? false;
	}
	return flag || value.BepuHandles.IsGroundSurface(collidableReference.StaticHandle);
```

Source: [current/decomp/KSA/NarrowPhaseCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NarrowPhaseCallbacks.cs#L23-L56)[^ksa-narrowphasecallbacks]

## Lifecycle and timing

Sphinx's `Apply` runs from the supermod's `Patcher.Patch()` at `[StarMapAllModsLoaded]`; its hooks run on vehicle workers inside the
bubble's collision step (`physics:bepu`). `Remove` runs from `Patcher.Unload()` at `[StarMapUnload]`, which under StarMap is after
the id-less `UnpatchAll()` has already removed every patch, so the loop usually finds nothing; it matters to a standalone host
that removes one feature while the game keeps running ([UnpatchAll(ownId) from a main-thread hook after nulling delegates](remove-your-patches-safely-at-runtime-and-at-exit-1.md)). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Apply patches through the caller's instance with a helper.** Each hook is a prefix or postfix on a `ConstraintSim` method; the
   transpiler goes on the internal callback struct, found by name:

```csharp
// sphinx.lib/SphinxPhysicsPatches.cs L15-L34
public static void Apply(Harmony harmony)
{
    Patch(typeof(ConstraintSim), nameof(ConstraintSim.BeginStaticObjectPass), nameof(Sync));
    Patch(typeof(ConstraintSim), nameof(ConstraintSim.UpdateSimForSnappedOrigin), nameof(Sync));
    Patch(typeof(ConstraintSim), nameof(ConstraintSim.IsGroundSurfaceFor), nameof(Ground));
    Patch(typeof(ConstraintSim), nameof(ConstraintSim.TryResetForPool), nameof(Clear), prefix: true);
    Patch(typeof(ConstraintSim), nameof(ConstraintSim.Dispose), nameof(Clear), prefix: true);
    var callback = typeof(ConstraintSim).Assembly.GetType("KSA.NarrowPhaseCallbacks", throwOnError: true)!;
    var filter = AccessTools.Method(callback, "AllowContactGeneration",
        new[] { typeof(int), typeof(CollidableReference), typeof(CollidableReference), typeof(float).MakeByRefType() })
        ?? throw new MissingMethodException(callback.FullName, "AllowContactGeneration");
    harmony.Patch(filter, transpiler: new HarmonyMethod(typeof(SphinxPhysicsPatches), nameof(ContactFilter)));

    void Patch(Type type, string name, string hook, bool prefix = false)
    {
        var target = AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
        var method = new HarmonyMethod(typeof(SphinxPhysicsPatches), hook);
        harmony.Patch(target, prefix: prefix ? method : null, postfix: prefix ? null : method);
    }
}
```

Source: [sphinx.lib/SphinxPhysicsPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysicsPatches.cs#L15-L34)[^unscience-sphinxphysicspatches]

2. **Remove by walking the patch info.** For every patched method, take the prefixes, postfixes and transpilers, keep those owned
   by this Harmony id *and* declared on this class, and unpatch that patch method:

```csharp
// sphinx.lib/SphinxPhysicsPatches.cs L35-L45
public static void Remove(Harmony harmony)
{
    foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
    {
        var patches = Harmony.GetPatchInfo(original);
        if (patches == null) continue;
        foreach (var patch in patches.Prefixes.Concat(patches.Postfixes).Concat(patches.Transpilers)
            .Where(p => p.owner == harmony.Id && p.PatchMethod.DeclaringType == typeof(SphinxPhysicsPatches)))
            harmony.Unpatch(original, patch.PatchMethod);
    }
}
```

Source: [sphinx.lib/SphinxPhysicsPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysicsPatches.cs#L35-L45)

3. **Make the filter specific.** `owner == harmony.Id` alone would also match every other feature on the shared id; the
   `DeclaringType == typeof(SphinxPhysicsPatches)` test is what scopes the removal to this class. Other mods' patches are never
   touched, because the owner filter excludes them.

4. **Know the opposite case.** `CameraControllerOverridePatches.Remove` calls `harmony.UnpatchAll(harmony.Id)`; on the shared
   instance that strips every unscience patch, so every `TryRemove` after it in `Unload()` is a no-op:

```csharp
// camera-controller-override.lib/CameraControllerOverridePatches.cs L36-L40
public static void Remove(Harmony harmony)
{
    harmony.UnpatchAll(harmony.Id);
    Console.WriteLine("camera-controller-override.lib: patches removed");
}
```

Source: [camera-controller-override.lib/CameraControllerOverridePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverridePatches.cs#L36-L40)[^unscience-cameracontrolleroverridepatches]

   The consolidated patcher's `Unload` runs `TryRemove` per feature in an explicit order ([unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L117-L130)[^unscience-patcher]).

5. **Guard the transpiler as usual.** Sphinx's filter transpiler counts replacements and throws unless exactly one stock call was
   replaced, which aborts `Apply` and is contained by `TryApply` ([Consolidated patcher with TryApply and ordered shared seams](../isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-2.md)):

```csharp
// sphinx.lib/SphinxPhysicsPatches.cs L51-L69
private static IEnumerable<CodeInstruction> ContactFilter(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
{
    var original = AccessTools.Method(typeof(BepuHandles), nameof(BepuHandles.IsGroundSurface));
    var replacement = AccessTools.Method(typeof(SphinxPhysicsPatches), nameof(IsSurface));
    var sim = AccessTools.Field(__originalMethod.DeclaringType, "Sim")
        ?? throw new MissingFieldException("KSA.NarrowPhaseCallbacks", "Sim");
    int replaced = 0;
    foreach (var instruction in input)
    {
        if (!instruction.Calls(original)) { yield return instruction; continue; }
        var load = new CodeInstruction(OpCodes.Ldarg_0);
        load.MoveLabelsFrom(instruction); load.MoveBlocksFrom(instruction);
        yield return load;
        yield return new CodeInstruction(OpCodes.Ldfld, sim);
        yield return new CodeInstruction(OpCodes.Call, replacement);
        replaced++;
    }
    if (replaced != 1) throw new InvalidOperationException("Sphinx expected one stock ground filter call; collision hooks need a game-update review.");
}
```

Source: [sphinx.lib/SphinxPhysicsPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysicsPatches.cs#L51-L69)

## Complete example

| File | Role |
|---|---|
| [sphinx.lib/SphinxPhysicsPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/SphinxPhysicsPatches.cs) | `Apply`, `Remove` by patch method, hooks, transpiler |
| [camera-controller-override.lib/CameraControllerOverridePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverridePatches.cs#L36-L40) | The contrasting `UnpatchAll(harmony.Id)` removal |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L117-L181) | Ordered, isolated unload |

## Variations and gotchas

- **Include finalizers if you patch with them.** Sphinx's loop covers prefixes, postfixes and transpilers because that is all it
  installs; extend the `Concat` with `patches.Finalizers` if your class has any.
- **Prefer by-method removal on a shared instance, by-id removal on a private one.** Per-feature owners (as in
  [One Harmony owner per feature with TryApply, validated handles and runtime flags](../refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-2.md)) make `UnpatchAll(id)` safe because the id is the feature.
- **The cost.** `GetAllPatchedMethods()` enumerates every patched method in the process, which is fine for an unload path and
  wrong for a hot one.
- **Drop per-bubble state.** Sphinx's `TryResetForPool` and `Dispose` prefixes clear its handles for a simulation before KSA
  reuses it; removing the patches does not by itself remove the statics already added to a running simulation, so clear that state too
  (`SphinxPhysics.Clear`).

## See also

- [UnpatchAll(ownId) from a main-thread hook after nulling delegates](remove-your-patches-safely-at-runtime-and-at-exit-1.md), [Lazy install and removal of one patch while it is in use](remove-your-patches-safely-at-runtime-and-at-exit-2.md)
- [Removing Harmony patches safely at runtime and at exit](overview.md)
- [Automatic box or mesh colliders for imported statics with scoped Bepu hooks](../../../../mods/unscience/features/sphinx-static-colliders.md), [One Harmony instance applying 30-plus feature patch classes in isolation](../../../../mods/unscience/features/unscience-consolidated-patcher.md)
- [Bracket the non-inlined methods that enclose an inlined struct callback](../../internals/patch-around-aggressive-inlining/patch-around-aggressive-inlining-4.md) for why Sphinx patches the transpiled callback and `ConstraintSim`

[^ksa-narrowphasecallbacks]: KSA.NarrowPhaseCallbacks (decompiled, build 2026.10.10.5554)
[^unscience-sphinxphysicspatches]: unscience/sphinx.lib/SphinxPhysicsPatches.cs
[^unscience-cameracontrolleroverridepatches]: unscience/camera-controller-override.lib/CameraControllerOverridePatches.cs
[^unscience-patcher]: unscience/Patcher.cs
