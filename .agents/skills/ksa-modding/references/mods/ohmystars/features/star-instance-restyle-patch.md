---
type: Mod Feature
title: Star instance restyle patch
description: Restyle every background star by prefixing InstancedStarTechnique.AddInstance to rewrite its size and colour bytes, and resize the instance buffer at runtime by reflecting into the technique
mod: ohmystars
category: rendering
techniques: [harmony-prefix, reflection, vulkan-buffer]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix KSA.InstancedStarTechnique.AddInstance
  - harmony-prefix KSA.InstancedStarTechnique..ctor
  - reflection KSA.InstancedRenderTechnique._maxInstances
  - reflection KSA.InstancedStarTechnique.CreateInstanceBuffer
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.InstancedStarTechnique
  - KSA.InstancedRenderTechnique
  - KSA.ModLibrary
  - KSA.Mod
  - KSA.Program
  - KSA.Rendering.RetiredResourceQueue
tags: [stars, instanced-rendering, harmony, prefix, reflection, vulkan-buffer, star-binary]
---

The Star Editor window (formerly a standalone mod called StarsEdit, per the class comment) lets the
player change how big and how bright the game's background stars are: a size curve, a size offset,
an additive size step, a size floor that hides faint stars, and overall and per-channel colour
multipliers. It does not touch shaders. A Harmony prefix on `InstancedStarTechnique.AddInstance`
rewrites the per-star size byte and colour bytes as each star is added, and reflection re-creates
the instance buffer at a new size whenever an apply needs a capacity other than the current
one.[^oms-starseditpatcher-cs]

## How it works

1. **The stock pipeline.** At start-up the `Program` constructor creates
   `new InstancedStarTechnique(..., ModLibrary.GetStarCount(), ...)` and then calls
   `ModLibrary.PopulateStars`, which runs `Mod.LoadStarBinaries` for every mod
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1047-L1054),
   [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L551-L572)).[^ksa-program-cs] [^ksa-modlibrary-cs]
   `GetStarCount` is 200 plus the `int32` header of every listed binary. Each record becomes
   `starTechnique.AddInstance(Program.MainViewport, forward, scale, color, 19.5f)`
   ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285)).[^ksa-mod-cs] `AddInstance` packs `scale` into the
   low byte and RGB into the upper bytes of `SpriteInstance.PackedData`
   ([current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L83)),[^ksa-instancedstartechnique-cs]
   and `Star.vert` uses that byte directly as the sprite radius
   ([current/Content/Core/Shaders/Star.vert](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Star.vert)).[^ksa-content-core-shaders-star-vert]
2. **Prefix on `AddInstance`.** Matched by its exact parameter list
   `(IViewport, float3, byte, byte4, float)`, the prefix takes `ref byte scale` and
   `ref byte4 color` ([src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L86-L128)):
   - If the viewport's instance array is already full it returns `false`, skipping the original,
     whose base `AddInstance` would otherwise throw `"Instance buffer full"`
     ([current/decomp/KSA/InstancedRenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedRenderTechnique.cs#L86-L96)).[^ksa-instancedrendertechnique-cs]
   - Size: `pow(scale/255, gamma) * 255`, times `2^(offset/2)`, plus the additive step, clamped; below
     the floor it becomes 0, which collapses the `Star.vert` triangle fan to nothing
     ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1011-L1027)).
   - Colour: each channel times `ColorMultiplier * channelMultiplier`, plus the same additive step.
   - Counters (`_prefixSinceLastApply`, `_droppedSinceLastApply`) are bumped with `Interlocked`
     during an apply cycle for the status line.
3. **Constructor prefix.** `PatchInstancedStarTechniqueConstructor` finds the constructor whose
   fifth parameter is an `int` (`inMaxInstances`) and patches it manually with a prefix taking
   `ref int __4`, Harmony's positional-argument injection, replacing the capacity with the requested
   one ([src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L130-L158)).
4. **Runtime resize by reflection.** `TryAdaptCapacityNow` copies the `SpriteInstance[]` of every
   one of the eight shader slots into a new array of the requested size (larger or smaller, allocated
   even for unused slots), clamps `InstanceCount`, unmaps `_instanceMemory`, disposes
   `_instanceBuffer`, writes the `readonly` fields `_maxInstances` and `Instances` through
   `FieldInfo.SetValue`, and invokes the protected `CreateInstanceBuffer` to allocate and map a new
   host-visible vertex buffer ([src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L160-L237),
   [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L51-L60),
   [current/decomp/KSA/InstancedRenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedRenderTechnique.cs#L16-L22)).
   It runs whenever the requested capacity differs from `_maxInstances`. The request is the selected
   files' star count plus 1,000 (`CapacityPadding`), while the stock capacity is the header counts plus
   200 (`ModLibrary.GetStarCount`), so the first apply of every session re-creates the buffer even with
   the stock selection, and later applies do so whenever the selection's total changes
   ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L821-L824)).
5. **Re-apply.** Any slider change in the Star Editor calls `ApplySelection`, which resets and
   reloads the instances through `AddInstance`, so the prefix restyles every star again (see
   [Star binary switcher](star-binary-switcher.md)).

## Lifecycle and timing

- **Install at `startup:starmap-before-main`.** `OnBeforeMain` is `[StarMapBeforeMain]` and calls
  `StarsEditPatcher.Patch()` on its own Harmony id `nanonestor.ohmystars.starsedit`, applying only
  this class plus the manual constructor patch
  ([src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L28-L32), [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L57-L72)).[^oms-ohmystarsentrypoint-cs]
  StarMap calls a mod's before-main method from `RuntimeMod.InitializeMod`, before KSA's entry point
  ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162)).[^starmap-modrepository-runtimemod-cs]
  `StarsEditWindow.OnAllModsLoaded` calls `Patch()` again, which returns early.
- **Stock stars are restyled at load.** Slider values are read from the `[Sliders]` block of
  `starsedit_settings.ini` in `[StarMapAllModsLoaded]` ([src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L169-L181)). That block
  holds the values of the last preset saved or used, not the last slider positions, because moving a
  slider does not write the file (see [Star binary switcher](star-binary-switcher.md)). The hook fires inside
  `ModLibrary.LoadAll` at `Program.cs` line 942, before the technique is created (line 1049) and
  populated (line 1053), so the first `PopulateStars` already runs through the prefix with the saved
  values. `[StarMapAllModsLoaded]` would have been early enough too; before-main is simply earlier.
- **The constructor prefix is effectively dormant in this build.** The only construction happens in
  the `Program` constructor, when `RequestedStarCapacity` is still 0, so the prefix leaves the stock
  capacity. Capacity changes actually happen through `TryAdaptCapacityNow` during an apply.
- **Runtime applies run in `frame:draw-ui`** (Star Editor window, `[StarMapBeforeGui]`, or a popup
  button inside `Popup.DrawAll` right after it). The first one is the deferred initial apply, which
  waits until the Star Editor window is drawn. The mod then calls `UpdateInstanceBuffer` for every
  viewport, which copies a slot's instances into the single mapped buffer; `Render` later in the same
  frame draws `InstanceCount[0]` instances
  ([current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L30-L49)).

## Key code

| Path | Role |
|---|---|
| [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L86-L128) | `AddInstance` prefix |
| [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L130-L158) | Constructor prefix with `__4` |
| [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L160-L260) | Runtime capacity change, buffer-full test |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1011-L1039) | Size and colour byte transforms |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `InstancedStarTechnique.AddInstance(IViewport, float3, byte, byte4, float)` | [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L83) | Harmony prefix target |
| `InstancedStarTechnique` constructor | [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L20-L23) | Harmony prefix target (`inMaxInstances`) |
| `InstancedStarTechnique._instanceBuffer`, `_instanceMemory`, `CreateInstanceBuffer` | [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L14-L60) | Reflection for resize |
| `InstancedRenderTechnique<T>._maxInstances`, `Instances`, `InstanceCount` | [current/decomp/KSA/InstancedRenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedRenderTechnique.cs#L16-L22) | Reflection for resize and full test |
| `ModLibrary.GetStarCount`, `PopulateStars` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L551-L572) | Stock sizing and load |
| `Program._starStarTechnique` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L207) | The single star technique instance |

## Pitfalls and version notes

- **Disposing a buffer the GPU may still read (unverified risk).** The resize unmaps and disposes
  the old vertex buffer immediately from UI code, while command buffers of earlier frames that bind it
  may still be executing (`_renderer.MaxFramesInFlight` frames), and every apply rewrites the one
  host-visible buffer those frames read. The game itself defers such frees:
  `KSA.Rendering.RetiredResourceQueue.Retire(IDisposable)` records the frame and disposes the resource
  only once that frame is no longer in flight, polled by `RetiredResourceQueue.CheckQueued` in
  `Program.OnFrame` ([current/decomp/KSA.Rendering/RetiredResourceQueue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RetiredResourceQueue.cs#L26-L86)).[^ksa-rendering-retiredresourcequeue-cs]
  Retiring the old buffer there (after unmapping) instead of disposing it is the safer pattern.
- **Writing `readonly` fields.** .NET reflection allows `FieldInfo.SetValue` on instance
  `readonly` fields, which is what makes the resize possible; the same call on a `static readonly`
  field throws on modern .NET.
- **The additive size step also shifts colour.** The same `RenderDataAdditiveOffset` is added to the
  RGB bytes, so "Size +/- Scale" brightens or darkens stars as a side effect.
- **Every `AddInstance` is affected.** Any other mod that adds star instances through this method is
  restyled too.
- **Positional `__4`.** If the constructor's parameter order changes, the search
  (`p.Length >= 5 && p[4].ParameterType == typeof(int)`) may silently patch the wrong `int`.

## See also

- [Star binary switcher](star-binary-switcher.md)
- [Attitude-mode hand-off patches](attitude-mode-handoff-patches.md)
- [Constellation lines and labels on the sky](sky-overlay-lines-and-labels.md)
- [Rewrite by-ref arguments and skip when a limit would throw](../../../howto/patching/harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-2.md) - how-to article demonstrating this feature
- [Re-create the star instance buffer by reflection and restyle stars with a Harmony prefix](../../../howto/rendering/meshes/grow-game-gpu-buffers-for-runtime-content/grow-game-gpu-buffers-for-runtime-content-2.md) - how-to article demonstrating this feature

[^oms-starseditpatcher-cs]: OhMyStars src/StarsEditPatcher.cs
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-rendering-retiredresourcequeue-cs]: KSA.Rendering.RetiredResourceQueue (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary-cs]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-mod-cs]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-instancedstartechnique-cs]: KSA.InstancedStarTechnique (decompiled, build 2026.10.10.5554)
[^ksa-content-core-shaders-star-vert]: Content/Core/Shaders/Star.vert (game content, build 2026.10.10.5554)
[^ksa-instancedrendertechnique-cs]: KSA.InstancedRenderTechnique (decompiled, build 2026.10.10.5554)
[^oms-ohmystarsentrypoint-cs]: OhMyStars src/OhMyStarsEntryPoint.cs
[^starmap-modrepository-runtimemod-cs]: StarMap StarMap.Core/ModRepository/RuntimeMod.cs
