---
type: How-To
title: Rewrite by-ref arguments and skip when a limit would throw
description: A prefix on InstancedStarTechnique.AddInstance edits scale and color through ref parameters, returns false instead of letting a full buffer throw, and a manual constructor patch rewrites an argument by position with ref int __4
category: patching
group: harmony
topic: skip-the-original-method-with-a-prefix
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ohmystars/features/star-instance-restyle-patch]
techniques: [harmony-prefix, harmony-ref-arguments]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix KSA.InstancedStarTechnique.AddInstance
  - harmony-prefix KSA.InstancedStarTechnique..ctor
lifecycle: [startup:starmap-before-main, frame:draw-ui]
game_types: [KSA.InstancedStarTechnique, KSA.InstancedRenderTechnique, KSA.ModLibrary, KSA.Program]
tags: [harmony, prefix, ref-arguments, skip-original, stars, instancing]
---

Not every skipping prefix replaces a result. This one lets the original run in the normal case and
only edits its inputs: OhMyStars changes the size byte and the colour of every background star as the
game adds it, and returns `false` in the one case where the original would throw. Two Harmony
features carry it: `ref` parameters that write back into the original's arguments, and a positional
`ref int __4` for a constructor argument that has no stable name to bind to.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by OhMyStars in
[Star instance restyle patch](../../../../mods/ohmystars/features/star-instance-restyle-patch.md).

## Background: how the game does it

At start-up `Program` creates the star technique with a capacity of `ModLibrary.GetStarCount()` (200
plus each mod's star-binary count) and then calls `ModLibrary.PopulateStars`, which asks every mod to
add its stars ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1047-L1054)[^ksa-program], [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L551-L573)[^ksa-modlibrary]). Each record ends up in
`InstancedStarTechnique.AddInstance(IViewport, float3, byte, byte4, float)`, which packs the size
byte and the RGB bytes into `SpriteInstance.PackedData` and forwards to the generic
`InstancedRenderTechnique<T>.AddInstance(IViewport, T)`. That generic method throws
`Exception("Instance buffer full")` when the per-viewport count reaches `_maxInstances`:

```csharp
// current/decomp/KSA/InstancedStarTechnique.cs L68-L84
public void AddInstance(IViewport viewport, float3 forward, byte scale, byte4 color, float distance)
{
	float3 float5 = float3.NormalizeOrZero(forward) * distance;
	uint num = ((uint)color >> 16) & 0xFF;
	uint num2 = ((uint)color >> 8) & 0xFF;
	uint num3 = (uint)color & 0xFF;
	uint num4 = 0u;
	num4 |= num3 << 24;
	num4 |= num2 << 16;
	num4 |= num << 8;
	num4 |= scale;
	AddInstance(viewport, new SpriteInstance
	{
		Position = new float3(float5.X, float5.Y, float5.Z),
		PackedData = num4
	});
}
```

Source: [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L84)[^ksa-instancedstartechnique]

```csharp
// current/decomp/KSA/InstancedRenderTechnique.cs L86-L96
public void AddInstance(IViewport viewport, T inInstance)
{
	int shaderSlot = viewport.ShaderSlot;
	if (InstanceCount[shaderSlot] >= _maxInstances)
	{
		throw new Exception("Instance buffer full");
	}
	int num = InstanceCount[shaderSlot];
	Instances[shaderSlot][num] = inInstance;
	InstanceCount[shaderSlot]++;
}
```

Source: [current/decomp/KSA/InstancedRenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedRenderTechnique.cs#L86-L96)[^ksa-instancedrendertechnique]

The constructor takes `inMaxInstances` as its fifth parameter ([current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L20-L23)).

## Lifecycle and timing

- **Install: `startup:starmap-before-main`.** OhMyStars calls `StarsEditPatcher.Patch()` from a
  `[StarMapBeforeMain]` method, with a comment that the star patches must be in place before the game
  builds its star technique and loads star binaries
  ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L28-L32)[^ohmystars-ohmystarsentrypoint]). The `Program` constructor creates the technique at line
  1049 and populates it at line 1053, so the very first `PopulateStars` already runs through the prefix.
  `StarMapAllModsLoaded` would also have been early enough; before-main is simply earlier.
- **Run: wherever `AddInstance` is called.** At load that is the `Program` constructor
  (`startup:*`, main thread). At runtime the mod re-applies its selection from its Star Editor window in
  `frame:draw-ui`, which calls `AddInstance` again for every star
  ([Star instance restyle patch](../../../../mods/ohmystars/features/star-instance-restyle-patch.md) documents the apply cycle). The prefix keeps its
  status counters (`_prefixSinceLastApply`, `_droppedSinceLastApply`) with `Interlocked` operations.
- **The constructor prefix is dormant here.** The only construction is the `Program` constructor, when the
  requested capacity is still 0 and the prefix leaves the stock value alone. The mod grows the buffer
  later by reflection (see the feature page); this article is about the Harmony side only.

See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for how the startup sequence is ordered.

## Steps

1. **Attach to the right overload.** `InstancedStarTechnique` has two `AddInstance` methods, one
   inherited with an `(IViewport, T)` signature and the one above. The patch names the parameter types
   so Harmony picks the five-argument overload. The class carries a bare `[HarmonyPatch]` and the
   mod applies *only this class* with `CreateClassProcessor` so that `PatchAll` does not re-apply its other
   patch classes a second time:

```csharp
// src/StarsEditPatcher.cs L8-L12
[HarmonyPatch]
internal static class StarsEditPatcher
{
    private static Harmony? _harmony;

```

Source: [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L8-L12)[^ohmystars-starseditpatcher]

```csharp
// src/StarsEditPatcher.cs L57-L72
internal static void Patch()
{
    if (_harmony is not null)
    {
        return;
    }

    _harmony = new Harmony("nanonestor.ohmystars.starsedit");
    // Only this class: PatchAll would re-apply OhMyStars' other patch classes a second time.
    _harmony.CreateClassProcessor(typeof(StarsEditPatcher)).Patch();
    PatchInstancedStarTechniqueConstructor(_harmony);
    _prefixSinceLastApply = 0;
    _droppedSinceLastApply = 0;
    _isApplyCycleActive = 0;
    Console.WriteLine("StarsEdit Harmony patch applied.");
}
```

Source: [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L57-L72)

2. **Declare `ref` parameters for what you want to rewrite.** A prefix parameter named like an
   original parameter is bound by name; declaring it `ref` makes the write go back into the original's
   argument. Here the prefix rewrites `scale` (a `byte`) and `color` (a `byte4`) and, before touching
   anything, returns `false` if the viewport's instance array is already full. Returning `false`
   skips the original, so the base `AddInstance` never reaches its `throw`:

```csharp
// src/StarsEditPatcher.cs L86-L128
[HarmonyPatch(typeof(InstancedStarTechnique), nameof(InstancedStarTechnique.AddInstance),
    new[] { typeof(IViewport), typeof(float3), typeof(byte), typeof(byte4), typeof(float) })]
[HarmonyPrefix]
private static bool InstancedStarTechniqueAddInstancePrefix(InstancedStarTechnique __instance, IViewport viewport, ref byte scale, ref byte4 color)
{
    bool isActiveCycle = Volatile.Read(ref _isApplyCycleActive) == 1;

    if (IsBufferFull(__instance, viewport))
    {
        if (isActiveCycle)
        {
            Interlocked.Increment(ref _droppedSinceLastApply);
        }

        return false;
    }

    int additiveOffset = StarsEditWindow.RenderDataAdditiveOffset;
    byte transformedScale = StarsEditWindow.TransformScaleByte(scale);
    if (transformedScale < StarsEditWindow.SizeFloorThreshold)
    {
        transformedScale = 0;
    }

    scale = transformedScale;

    float redScale = StarsEditWindow.ColorMultiplier * StarsEditWindow.RedMultiplier;
    float greenScale = StarsEditWindow.ColorMultiplier * StarsEditWindow.GreenMultiplier;
    float blueScale = StarsEditWindow.ColorMultiplier * StarsEditWindow.BlueMultiplier;

    color = new byte4(
            StarsEditWindow.TransformRenderByte(color.X, redScale, additiveOffset),
            StarsEditWindow.TransformRenderByte(color.Y, greenScale, additiveOffset),
            StarsEditWindow.TransformRenderByte(color.Z, blueScale, additiveOffset),
            color.W);

    if (isActiveCycle)
    {
        Interlocked.Increment(ref _prefixSinceLastApply);
    }

    return true;
}
```

Source: [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L86-L128)

3. **Make the full-buffer guard fail open.** The capacity lives in the private `_maxInstances` field of
   the generic base. The guard reads it by reflection and, if the field cannot be found, answers
   `int.MaxValue`, so a rename disables the guard instead of dropping every star:

```csharp
// src/StarsEditPatcher.cs L239-L260
private static bool IsBufferFull(InstancedStarTechnique starTechnique, IViewport viewport)
{
    int[] instanceCount = starTechnique.InstanceCount;
    if (viewport.ShaderSlot < 0 || viewport.ShaderSlot >= instanceCount.Length)
    {
        return false;
    }

    int maxInstances = GetMaxInstances(starTechnique);
    return instanceCount[viewport.ShaderSlot] >= maxInstances;
}

private static int GetMaxInstances(InstancedStarTechnique starTechnique)
{
    _maxInstancesField ??= typeof(InstancedRenderTechnique<SpriteInstance>).GetField("_maxInstances", BindingFlags.Instance | BindingFlags.NonPublic);
    if (_maxInstancesField?.GetValue(starTechnique) is int value)
    {
        return value;
    }

    return int.MaxValue;
}
```

Source: [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L239-L260)

4. **Reach an argument by position when it cannot be named.** The constructor is matched by shape,
   not by name: the first constructor whose fifth parameter is an `int`. Harmony injects arguments as
   `__0`, `__1`, ... in the original's parameter order, and `ref int __4` rewrites `inMaxInstances`.
   Because this method is patched by hand with `harmony.Patch(ctor, prefix: ...)`, the prefix needs no
   attribute:

```csharp
// src/StarsEditPatcher.cs L130-L158
private static void PatchInstancedStarTechniqueConstructor(Harmony harmony)
{
    ConstructorInfo? ctor = typeof(InstancedStarTechnique)
        .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        .FirstOrDefault(static c =>
        {
            ParameterInfo[] p = c.GetParameters();
            return p.Length >= 5 && p[4].ParameterType == typeof(int);
        });

    if (ctor is null)
    {
        Console.WriteLine("StarsEdit could not find InstancedStarTechnique constructor to patch.");
        return;
    }

    harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(StarsEditPatcher), nameof(InstancedStarTechniqueCtorPrefix)));
}

private static void InstancedStarTechniqueCtorPrefix(ref int __4)
{
    int currentMax = __4;
    int requestedMax = RequestedStarCapacity;
    if (requestedMax > 0 && requestedMax != currentMax)
    {
        __4 = requestedMax;
        Console.WriteLine($"StarsEdit set star capacity from {currentMax} to {requestedMax}.");
    }
}
```

Source: [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L130-L158)

5. **Verify in the console.** The patch writes `StarsEdit Harmony patch applied.` at install; a
   missing constructor writes `StarsEdit could not find InstancedStarTechnique constructor to patch.`
   and the rest keeps working.

## Complete example

| File | Role |
|---|---|
| [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs) | `AddInstance` prefix, constructor prefix, capacity change, full test |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1011-L1039)[^ohmystars-starseditwindow] | The size and colour byte transforms the prefix calls |
| [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L28-L32) | `[StarMapBeforeMain]` install |

## Variations and gotchas

- **Prefer `ref` edits over replacing the method.** Only the *cases that must not run* need
  `return false`. Everything else is a plain argument rewrite that composes with other mods' patches on
  the same method, because the original still runs.
- **Every caller is affected.** Any other mod that adds star instances through this method is restyled
  too; the patch has no notion of who is calling.
- **Positional injection is fragile.** `__4` is only right while the fifth parameter is the capacity.
  The search `p.Length >= 5 && p[4].ParameterType == typeof(int)` would silently pick a different `int` if
  parameters are reordered. Prefer a named parameter whenever the original has a stable one.
- **A skipped call is dropped, not retried.** When the prefix returns `false` for a full buffer the
  star is simply not added; the mod counts those drops for its status line.
- **The skip does not run later `bool` prefixes (`void` prefixes still run).** See [Skipping Harmony prefixes](overview.md).

## See also

- [Reproduce the original body for marked objects and return false](skip-the-original-method-with-a-prefix-1.md), [Re-inject a private field before a rebuild so nothing can change it back](skip-the-original-method-with-a-prefix-3.md), [Mouse prefix that pairs presses with releases and repairs private latches](skip-the-original-method-with-a-prefix-4.md), [Keyboard and mouse-binding gates with a held-key model](skip-the-original-method-with-a-prefix-5.md)
- [Skipping Harmony prefixes](overview.md)
- [Star instance restyle patch](../../../../mods/ohmystars/features/star-instance-restyle-patch.md)
- The topic grow-game-gpu-buffers-for-runtime-content covers the buffer resize half of this feature
- [Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md) for string-named and positional targets

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-instancedstartechnique]: KSA.InstancedStarTechnique (decompiled, build 2026.10.10.5554)
[^ksa-instancedrendertechnique]: KSA.InstancedRenderTechnique (decompiled, build 2026.10.10.5554)
[^ohmystars-ohmystarsentrypoint]: OhMyStars/src/OhMyStarsEntryPoint.cs
[^ohmystars-starseditpatcher]: OhMyStars/src/StarsEditPatcher.cs
[^ohmystars-starseditwindow]: OhMyStars/src/StarsEditWindow.cs
