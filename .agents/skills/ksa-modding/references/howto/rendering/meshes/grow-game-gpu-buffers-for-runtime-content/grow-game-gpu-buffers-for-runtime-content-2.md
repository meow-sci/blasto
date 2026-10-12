---
type: How-To
title: Re-create the star instance buffer by reflection and restyle stars with a Harmony prefix
description: Grow the background star instance buffer at runtime by writing readonly fields and invoking the protected CreateInstanceBuffer, and rewrite every star's size and colour from a prefix on InstancedStarTechnique.AddInstance
category: rendering
group: meshes
topic: grow-game-gpu-buffers-for-runtime-content
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/ohmystars/features/star-instance-restyle-patch]
techniques: [harmony-prefix, reflection, vulkan-buffer]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix KSA.InstancedStarTechnique.AddInstance
  - harmony-prefix KSA.InstancedStarTechnique..ctor
  - reflection KSA.InstancedRenderTechnique._maxInstances
  - reflection KSA.InstancedStarTechnique.CreateInstanceBuffer
  - starmap StarMapAllModsLoaded
  - starmap StarMapBeforeGui
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.InstancedStarTechnique
  - KSA.InstancedRenderTechnique
  - KSA.ModLibrary
  - KSA.Mod
  - KSA.Program
  - KSA.Rendering.RetiredResourceQueue
tags: [stars, instance-buffer, sprite-instance, restyle, reflection, frames-in-flight]
---
Outcome: change how the game's background stars look (size curve, size floor, brightness, per-channel colour) and change how many of them there can be, including at runtime, without touching a shader. The same code also shows the second kind of buffer growth: the star technique's host-visible vertex buffer is sized at construction from a `readonly` field, so more stars means re-creating it.

Demonstrated by OhMyStars' Star Editor ([Star instance restyle patch](../../../../mods/ohmystars/features/star-instance-restyle-patch.md)). Prerequisites: StarMap, Harmony, reflection (the OhMyStars code is GPL-2.0; read it, do not paste it into an incompatible licence).

## Background: how the game does it

At startup the `Program` constructor creates the star technique with a capacity from `ModLibrary.GetStarCount()` (200 plus the `int32` header of every listed star binary), then fills it:

```csharp
// current/decomp/KSA/Program.cs L1047-L1054
using (Loading.Task("Star Instances"))
{
	_starStarTechnique = new InstancedStarTechnique(_renderer, _offscreenTarget, array, _pointClampedSampler, ModLibrary.GetStarCount(), _renderer.MaxFramesInFlight, TextureReference.EmptyWhite);
}
using (Loading.Task("Star Binaries"))
{
	ModLibrary.PopulateStars(_starStarTechnique);
}
```

`Mod.LoadStarBinaries` reads each record (a direction, a size byte, three colour bytes) and calls `starTechnique.AddInstance(...)` with distance 19.5 ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285)). `InstancedStarTechnique.AddInstance` packs the colour and `scale` into the one `uint` of a `SpriteInstance`, and `Star.vert` unpacks the scale as the sprite's `circleRadius` ([current/Content/Core/Shaders/Star.vert](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Star.vert#L38-L48)):

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

The base class `AddInstance` throws `"Instance buffer full"` when the per-viewport array is at `_maxInstances`, and `Instances` and `_maxInstances` are `protected readonly`. The GPU buffer is host-visible and mapped, created by a protected virtual method:

```csharp
// current/decomp/KSA/InstancedRenderTechnique.cs L16-L18, L84-L96
	protected readonly T[][] Instances;

	protected readonly int _maxInstances;
// ...
	protected abstract void CreateInstanceBuffer();

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

```csharp
// current/decomp/KSA/InstancedStarTechnique.cs L51-L61
protected override void CreateInstanceBuffer()
{
	_instanceBuffer = Renderer.Allocator.CreateBuffer(new BufferEx.CreateInfo
	{
		Name = "StarInstanceBuffer",
		BufferUsage = VkBufferUsageFlags.VertexBufferBit,
		BufferSize = ByteSize.Of<SpriteInstance>(_maxInstances),
		AllocRequiredProperties = (VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit)
	});
	_instanceMemory = _instanceBuffer.Map();
}
```

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Install the Harmony patches | `[StarMapBeforeMain]`, before `Program.Main` | `startup:starmap-before-main` |
| Read saved slider values | `[StarMapAllModsLoaded]` (inside `ModLibrary.LoadAll`, Program.cs L942) | `startup:all-mods-loaded` |
| First `PopulateStars` runs through the prefix | Program constructor L1053, after the technique is created at L1049 | still startup |
| Runtime resize and re-apply | `StarMapBeforeGui` (Star Editor window), main thread | `frame:draw-ui` |
| The stars are drawn | `_starStarTechnique.Render` in the offscreen scope | `render:scene-opaque` |

The prefix is installed in `OnBeforeMain` so it is in place before the game builds its star technique and loads star binaries; `[StarMapAllModsLoaded]` would also be early enough. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#process-start-and-loading-sequence).

A runtime resize runs in the UI phase, while earlier frames (up to `MaxFramesInFlight`) may still be executing command buffers that bind the old vertex buffer; see the gotcha below and [free Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md).

## Steps

### Part A: restyle every star as it is added

1. **Install on `StarMapBeforeMain`.** `PatchAll` would also apply the mod's other patch classes, so process only this class and patch the constructor manually:

   ```csharp
// src/OhMyStarsEntryPoint.cs L28-L32
// Star patches must be in place before the game builds its star technique and loads star binaries.
[StarMapBeforeMain]
public static void OnBeforeMain() {
    StarsEditPatcher.Patch();
}
```

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

2. **Prefix `AddInstance` by exact parameter list**, taking `ref byte scale` and `ref byte4 color`. Return `false` when the viewport's array is full, which skips the original and avoids the "Instance buffer full" exception; otherwise rewrite size and colour:

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

3. **The transforms.** Size is `pow(scale/255, gamma) * 255`, times `2^(offset/2)`, plus an additive step, clamped; below the floor it becomes 0, which collapses the triangle fan to nothing, so faint stars are hidden rather than shrunk. Each colour channel is multiplied and offset the same way:

   ```csharp
// src/StarsEditWindow.cs L1011-L1031
internal static byte TransformRenderByte(byte value, float multiplier, int additiveOffset)
{
	int scaled = (int)MathF.Round(value * multiplier) + additiveOffset;
	scaled = Math.Clamp(scaled, 0, byte.MaxValue);
	return (byte)scaled;
}

internal static byte TransformScaleByte(byte value)
{
	float normalized = value / (float)byte.MaxValue;
	float curved = MathF.Pow(normalized, _sizeCurveGamma);
	float scaled = curved * byte.MaxValue;
	return TransformRenderByte((byte)Math.Clamp((int)MathF.Round(scaled), 0, byte.MaxValue), SizeMultiplier, _renderDataAdditiveOffset);
}

internal static float SizeCurveGamma => _sizeCurveGamma;
internal static float SizeMultiplier => MathF.Pow(2f, _sizeLog2Offset * 0.5f);

internal static int RenderDataAdditiveOffset => _renderDataAdditiveOffset;

internal static int SizeFloorThreshold => _sizeFloorThreshold;
```

4. **Re-apply by re-adding.** Any slider change resets the instance counts and reloads the star binaries through `AddInstance`, so the prefix restyles every star again. The values at startup come from the saved settings read in `[StarMapAllModsLoaded]`, before the first `PopulateStars`.

### Part B: grow the buffer capacity

5. **Optional: patch the constructor.** Find the constructor whose fifth parameter is an `int` and inject `ref int __4` (Harmony's positional-argument injection) to replace `inMaxInstances`. In this build the only construction is in the `Program` constructor, when no capacity has been requested yet, so this prefix leaves the stock capacity; capacity changes actually happen at runtime (step 6):

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

6. **Resize at runtime by reflection.** Copy each viewport slot's `SpriteInstance[]` into a larger array, clamp the counts, unmap and dispose the old buffer, write the `readonly` fields `_maxInstances` and `Instances` with `FieldInfo.SetValue`, then invoke the protected `CreateInstanceBuffer`, which allocates and maps a new host-visible vertex buffer of `_maxInstances` sprites:

   ```csharp
// src/StarsEditPatcher.cs L160-L237
internal static int TryAdaptCapacityNow(InstancedStarTechnique starTechnique, int requestedCapacity)
{
    int currentCapacity = GetMaxInstances(starTechnique);
    if (requestedCapacity <= 0 || requestedCapacity == currentCapacity)
    {
        return currentCapacity;
    }

    try
    {
        Type baseType = typeof(InstancedRenderTechnique<SpriteInstance>);
        _maxInstancesField ??= baseType.GetField("_maxInstances", BindingFlags.Instance | BindingFlags.NonPublic);
        _instancesField ??= baseType.GetField("Instances", BindingFlags.Instance | BindingFlags.NonPublic);
        _instanceBufferField ??= typeof(InstancedStarTechnique).GetField("_instanceBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
        _instanceMemoryField ??= typeof(InstancedStarTechnique).GetField("_instanceMemory", BindingFlags.Instance | BindingFlags.NonPublic);
        _createInstanceBufferMethod ??= typeof(InstancedStarTechnique).GetMethod("CreateInstanceBuffer", BindingFlags.Instance | BindingFlags.NonPublic);

        if (_maxInstancesField is null || _instancesField is null || _createInstanceBufferMethod is null)
        {
            return currentCapacity;
        }

        if (_instancesField.GetValue(starTechnique) is not SpriteInstance[][] oldInstances)
        {
            return currentCapacity;
        }

        SpriteInstance[][] resized = new SpriteInstance[oldInstances.Length][];
        int[] counts = starTechnique.InstanceCount;
        for (int i = 0; i < oldInstances.Length; i++)
        {
            SpriteInstance[] source = oldInstances[i] ?? Array.Empty<SpriteInstance>();
            SpriteInstance[] target = new SpriteInstance[requestedCapacity];
            int copyCount = Math.Min(source.Length, target.Length);
            if (copyCount > 0)
            {
                Array.Copy(source, target, copyCount);
            }

            resized[i] = target;

            if (i < counts.Length && counts[i] > requestedCapacity)
            {
                counts[i] = requestedCapacity;
            }
        }

        object? oldMemory = _instanceMemoryField?.GetValue(starTechnique);
        oldMemory?.GetType().GetMethod("Unmap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(oldMemory, null);

        object? oldBuffer = _instanceBufferField?.GetValue(starTechnique);
        if (oldBuffer is IDisposable disposableBuffer)
        {
            disposableBuffer.Dispose();
        }
        else
        {
            oldBuffer?.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(oldBuffer, null);
        }

        _maxInstancesField.SetValue(starTechnique, requestedCapacity);
        _instancesField.SetValue(starTechnique, resized);
        _createInstanceBufferMethod.Invoke(starTechnique, null);

        int adapted = GetMaxInstances(starTechnique);
        if (adapted == requestedCapacity)
        {
            Console.WriteLine($"StarsEdit adapted star capacity from {currentCapacity} to {adapted}.");
        }

        return adapted;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"StarsEdit could not adapt star capacity at runtime: {ex.Message}");
        return currentCapacity;
    }
}
```

7. **Request enough capacity before re-adding.** The Star Editor asks for the selected files' star count plus 1,000 and adapts the technique before it adds anything:

   ```csharp
// src/StarsEditWindow.cs L821-L828
int requestedCapacity = totalAvailable + CapacityPadding;
StarsEditPatcher.SetRequestedStarCapacity(requestedCapacity);

int maxInstances = StarsEditPatcher.TryAdaptCapacityNow(starTechnique, requestedCapacity);
if (maxInstances <= 0)
{
	maxInstances = GetMaxInstances(starTechnique);
}
```

   Because the stock capacity is the header counts plus 200 and the request adds 1,000, the first apply of every session re-creates the buffer even with the stock selection.

8. **Safer disposal (adapted).** The game defers its own frees through `RetiredResourceQueue`, which disposes a resource once the frame it was retired in is no longer in flight (`CheckQueued` runs in `Program.OnFrame`). Replacing the immediate `Dispose` in step 6 with a retire is the safer pattern:

   ```csharp
   // adapted from OhMyStars/src/StarsEditPatcher.cs L207-L218 (unmap, then retire instead of Dispose)
   object? oldMemory = _instanceMemoryField?.GetValue(starTechnique);
   oldMemory?.GetType().GetMethod("Unmap", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(oldMemory, null);

   if (_instanceBufferField?.GetValue(starTechnique) is IDisposable oldBuffer)
   {
       KSA.Rendering.RetiredResourceQueue.Retire(oldBuffer);
   }
   ```

   Frames already submitted keep the old buffer alive until the queue releases it, and later frames bind the new one.

## Complete example

| File | Role |
|---|---|
| [src/StarsEditPatcher.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditPatcher.cs#L57-L260) | Patch install, `AddInstance` prefix, constructor prefix, runtime capacity change, full test |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L1011-L1039) | Size and colour byte transforms |
| [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L28-L32) | `[StarMapBeforeMain]` install |

## Variations and gotchas

- **Immediate dispose is a risk.** OhMyStars unmaps and disposes the old buffer from UI code while earlier frames may still bind it, and rewrites the one host-visible buffer those frames read on every apply. This is an unverified risk in the mod; the retire pattern in step 8 avoids it.
- **`readonly` fields.** Reflection allows `FieldInfo.SetValue` on instance `readonly` fields, which is what makes the resize possible; the same call on a `static readonly` field throws on modern .NET.
- **The additive step also shifts colour.** The same offset is added to the RGB bytes, so a size step brightens or darkens stars as a side effect.
- **Every `AddInstance` is affected.** Any other mod that adds stars through this method is restyled too.
- **Positional `__4`.** If the constructor's parameter order changes, the search (`p.Length >= 5 && p[4].ParameterType == typeof(int)`) may silently patch the wrong `int`.
- **Instances live on the CPU first.** Stars are kept in `Instances[slot]` and copied into the mapped buffer by `UpdateInstanceBuffer`, which the game calls once per viewport at the end of `ModLibrary.PopulateStars` and OhMyStars calls again after each apply. Re-creating the buffer therefore loses nothing as long as the arrays are carried over and `UpdateInstanceBuffer` is called afterwards.
- **A different kind of growth.** The star buffer is a plain re-allocation; the shared mesh buffer in approach 1 cannot be re-allocated like this because bound meshes hold absolute offsets into it.

## See also

- [Overview](overview.md)
- [Approach 1: shared mesh buffer headroom](grow-game-gpu-buffers-for-runtime-content-1.md)
- [Star instance restyle patch](../../../../mods/ohmystars/features/star-instance-restyle-patch.md)
- [Draw lines and labels on the sky](../../overlays/draw-lines-on-the-sky/overview.md), [Free Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
