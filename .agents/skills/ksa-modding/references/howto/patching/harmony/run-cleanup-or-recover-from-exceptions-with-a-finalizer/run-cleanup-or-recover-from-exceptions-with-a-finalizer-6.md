---
type: How-To
title: Thread-static construction context scoping a transpiler and a prefix
description: Pebbles redirects the ground clutter renderer's material buffer lookups to a private buffer and swaps a fragment shader variant only while a ThreadStatic construction context is active, with a finalizer that leaves the context on every path
category: patching
group: harmony
topic: run-cleanup-or-recover-from-exceptions-with-a-finalizer
approach: 6
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/pebbles-private-clutter-materials]
techniques: [harmony-transpiler, harmony-prefix, harmony-finalizer, thread-static-scope]
hooks:
  - harmony-transpiler KSA.ClutterEcotypeRenderData.SortMaterialIds
  - harmony-transpiler KSA.ClutterEcotypeRenderData.CreateColorRenderer
  - harmony-prefix KSA.ClutterEcotypeRenderData.RebuildFrameResources
  - harmony-finalizer KSA.ClutterEcotypeRenderData.RebuildFrameResources
  - harmony-prefix KSA.ShaderReference.CompileVariantWithCustomOptions
lifecycle: [startup:asset-load, frame:render]
game_types: [KSA.GroundClutterRenderer, KSA.ClutterEcotypeRenderData, KSA.ShaderReference, KSA.Universe]
tags: [harmony, transpiler, finalizer, thread-static, scoped-override, ground-clutter]
---

Stock ground clutter shares one material buffer per `GroundClutterRenderer`. Pebbles wants a private buffer
for one planet without touching any other. It does so by patching the game's own *callers* of the shared
members, in a way that only changes behaviour while the current thread holds a construction context. The
context is opened by a `using` around Pebbles' own build loop, re-entered by a prefix when the game rebuilds
frame resources later, and left by a finalizer that also rethrows. This is the "scoped override" pattern with
a transpiler and a prefix instead of a single prefix.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by unscience in
[Private clutter material buffer and source-color shader variant scoped to owned construction](../../../../mods/unscience/features/pebbles-private-clutter-materials.md).

## Background: how the game does it

`GroundClutterRenderer.MaterialBuffer` is a one-line property and `GetMaterialIndex` looks up a material in the
shared table ([current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L320)[^ksa-groundclutterrenderer], [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L591-L600)). Each
`ClutterEcotypeRenderData` binds that buffer into its pipelines. `SortMaterialIds` calls `GetMaterialIndex`
for every primitive, and `CreateColorRenderer`, `CreateDepthPrePassRenderer` and `CreateShadowDepthRenderer`
each read `MaterialBuffer` once:

```csharp
// current/decomp/KSA/ClutterEcotypeRenderData.cs L334-L336, L459
				GroundClutterMaterialReference groundClutterMaterialReference = groundClutterLodReference.ResolveMaterial(meshReference.PrimitiveMaterialIds[l]).Get();
				uint materialIndex = _planetRenderer.GroundClutterRenderer.GetMaterialIndex(groundClutterMaterialReference);
				int num2 = _pipelineIdLookup[groundClutterMaterialReference.ColorPipelineFlags];
// ...
simpleGraphicsPipeline.SetBuffer(3, _planetRenderer.GroundClutterRenderer.MaterialBuffer.VkBuffer, VK.WHOLE_SIZE, ByteSize.Zero, VkDescriptorType.StorageBuffer);
```

Source: [current/decomp/KSA/ClutterEcotypeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypeRenderData.cs#L334-L459)[^ksa-clutterecotyperenderdata]

A graphics settings change runs `ClutterEcotypeRenderData.RebuildFrameResources`, which recompiles the clutter
shader ([current/decomp/KSA/ClutterEcotypeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypeRenderData.cs#L932-L934), [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L1799-L1808)).

## Lifecycle and timing

| Moment | Hook | State |
|---|---|---|
| Commit in the `Universe.ExecuteNextClothSolvers` prefix | the context is entered in `ClutterResources.Build` and the transpiled methods and shader prefix fire inside the native constructors | main thread, solvers and GPU idle |
| Graphics rebuild | prefix and finalizer on `ClutterEcotypeRenderData.RebuildFrameResources` | main thread, inside the renderer rebuild |
| Any other time | the transpiled calls see no context and behave like stock | other mods unaffected |

`[ThreadStatic]` matters because the target methods can in principle be reached from other threads; a context
opened on the main thread never leaks to them. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the frame.

## Steps

1. **Declare the context per thread and give it a disposable scope.** `Enter` stores the previous value and
   `Dispose` restores it, so scopes nest:

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L21-L28
private static ClutterController? _controller;
private static readonly Dictionary<ClutterEcotypeRenderData, ClutterResources> Owned = [];
[ThreadStatic] private static ClutterResources? _context;
public static IDisposable Enter(ClutterResources resources)
{ var old = _context; _context = resources; return new Scope(() => _context = old); }
private sealed class Scope(Action leave) : IDisposable { public void Dispose() => leave(); }
public static void Track(ClutterResources resources) { foreach (var render in resources.Render) Owned.Add(render, resources); }
public static void Forget(ClutterResources resources) { foreach (var render in resources.Render) Owned.Remove(render); }
```

Source: [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L21-L28)[^unscience-clutterhooks]

2. **Open the scope around your own construction.** `ClutterResources.Build` does the work inside
   `using var context = ClutterHooks.Enter(this)`:

```csharp
// pebbles.lib/Runtime/ClutterResources.cs L64-L71
using var context = ClutterHooks.Enter(this);
for (var i = 0; i < count; i++)
{
    var e = Graph.Reference.Ecotypes[i];
    Placement[i] = new GroundClutterPlacementData(renderer, body, e);
    Placement[i].BuildResources(pool, command);
    Render[i] = new ClutterEcotypeRenderData(renderer, planet, pass, body, e, Placement[i]);
    Render[i].BuildRenderResources(pool, command);
```

Source: [pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs#L64-L71)[^unscience-clutterresources]

3. **Patch the callers, not the getter, and make the replacement conditional.** The transpiler retargets the
   single `MaterialBuffer` or `GetMaterialIndex` call in each of four methods to a static helper with the same
   stack shape. The helper returns the private value only when the renderer is the context's owner, otherwise it
   forwards to the stock member. The code comment gives the reason for patching callers: "which the JIT may
   inline". The transpiler throws unless exactly one call was replaced, which aborts the patch set on an
   incompatible build (see [Writing a Harmony transpiler for a KSA method](../../transpilers/write-a-harmony-transpiler/overview.md)):

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L36-L38, L65-L81
    // Patch callers rather than tiny getters, which the JIT may inline.
    foreach (var name in new[] { "SortMaterialIds", "CreateColorRenderer", "CreateDepthPrePassRenderer", "CreateShadowDepthRenderer" })
        harmony.Patch(Required(typeof(ClutterEcotypeRenderData), name), transpiler: Method(nameof(MaterialCalls)));
// ...
private static IEnumerable<CodeInstruction> MaterialCalls(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
{
    var buffer = AccessTools.PropertyGetter(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.MaterialBuffer));
    var index = Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.GetMaterialIndex));
    var replaced = 0;
    foreach (var instruction in input)
    {
        if (instruction.Calls(buffer)) { instruction.opcode = OpCodes.Call; instruction.operand = Required(typeof(ClutterHooks), nameof(Buffer)); replaced++; }
        else if (instruction.Calls(index)) { instruction.opcode = OpCodes.Call; instruction.operand = Required(typeof(ClutterHooks), nameof(Index)); replaced++; }
        yield return instruction;
    }
    if (replaced != 1) throw new InvalidOperationException($"Pebbles expected one material binding in {__originalMethod.Name}, found {replaced}. Game build is incompatible.");
}
private static BufferEx Buffer(GroundClutterRenderer renderer)
    => ReferenceEquals(_context?.Owner, renderer) ? _context.MaterialBuffer!.Value : renderer.MaterialBuffer;
private static uint Index(GroundClutterRenderer renderer, GroundClutterMaterialReference material)
    => ReferenceEquals(_context?.Owner, renderer) ? _context.MaterialIndices[material.Hash] : renderer.GetMaterialIndex(material);
```

Source: [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L36-L81)

4. **Re-enter the context for deferred rebuilds, with `__state` and a rethrowing finalizer.** Descriptors capture the
   buffer at pipeline creation, so drawing never needs the context, but a settings-triggered rebuild recompiles
   shaders. The prefix looks the instance up in the table of owned render data and re-enters; the finalizer disposes
   the scope on every path and returns `__exception`:

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L89-L91
private static void BeforeRebuild(ClutterEcotypeRenderData __instance, out IDisposable? __state)
    => __state = Owned.TryGetValue(__instance, out var resources) ? Enter(resources) : null;
private static Exception? AfterRebuild(Exception? __exception, IDisposable? __state) { __state?.Dispose(); return __exception; }
```

Source: [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L89-L91)

5. **Skip the stock shader compile only inside the context.** A prefix on
   `ShaderReference.CompileVariantWithCustomOptions` returns `true` (stock runs) unless a context with at least one
   source-colour material is active and the shader is `ClutterSolidFrag`; otherwise it edits the GLSL at an exact
   marker (present once at the cited line of `Solid.frag`), compiles it and returns `false`:

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L93-L103
private static bool CompileColor(ShaderReference __instance, CompileOptions __0, ref VkShaderModule? __result)
{
    if (_context == null || _context.Graph.SourceColorMaterials.Count == 0 || __instance.Id != "ClutterSolidFrag") return true;
    var source = File.ReadAllText(__instance.ModPath);
    const string marker = "diffuseSample.rgb *= (groundColor / averageLuminosity);";
    if (source.Split(marker).Length != 2) throw new InvalidOperationException("Clutter color shader changed; source-color adaptation needs review.");
    source = source.Replace(marker, marker + "\n    if ((materialData.flags & 0x80000000u) != 0u) diffuseSample.rgb = pow(texture(sampler2D(globalTextures[materialData.diffuseTextureId], textureSampler), inUv).rgb, vec3((materialData.flags & 0x40000000u) != 0u ? 1.0 : 2.2));", StringComparison.Ordinal);
    ShaderCompilerResolve.SetIncludeCallback(__0);
    __result = ShaderModuleUtils.FromString(Program.GetRenderer().Device, Encoding.UTF8.GetBytes(source), VkShaderStageFlags.FragmentBit, __0, Encoding.UTF8.GetBytes(__instance.ModPath + "\0"));
    return false;
}
```

Source: [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L93-L103)

6. **Capture construction for cleanup.** A prefix on every public constructor of the six native ownership types
   adds `__instance` to the context's list, so a build that throws halfway can free what was already allocated
   ([pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L34-L35), [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L82)).

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs) | Context, scope, transpiler, prefixes, finalizer |
| [pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs) | Opens the scope around the native construction loop |
| [pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs#L25-L58)[^unscience-clutterretirement] | Frees what a failed build had already allocated |

## Variations and gotchas

- **Exact-count transpiler.** A game update that adds a second `MaterialBuffer` read makes the patch set fail
  to apply; it does not fall back to mutating the shared buffer. All four counts are one in build 5554.
- **Shader marker.** The shader edit keys on the literal line
  `diffuseSample.rgb *= (groundColor / averageLuminosity);`, present exactly once in
  [current/Content/Core/Shaders/Planet/GroundClutter/Solid.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Planet/GroundClutter/Solid.frag#L338)[^ksa-solid]; a reworded line throws "Clutter color shader
  changed".
- **Return `__exception` from the finalizer.** Returning `null` here would swallow a real rebuild failure.
- **Partial cleanup only.** Constructor capture cannot see allocations a native constructor holds in locals.
- **Key by the exact instance.** The table of owned render data is keyed by the `ClutterEcotypeRenderData`
  instance, so the stock render data of the same body is rebuilt with stock bindings.

## See also

- [Restore what a prefix changed around one game call](run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md), [Swallow an exception and finish the method's remaining work](run-cleanup-or-recover-from-exceptions-with-a-finalizer-2.md), [Prefix with __state and a finalizer that restores an edited input](run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md), [Prefix, postfix and finalizer as a transaction around loads](run-cleanup-or-recover-from-exceptions-with-a-finalizer-4.md), [ThreadStatic override set in try/finally around the mod's own submission](run-cleanup-or-recover-from-exceptions-with-a-finalizer-5.md), [Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer](run-cleanup-or-recover-from-exceptions-with-a-finalizer-7.md)
- [Harmony finalizers, cleanup and scoped overrides](overview.md)
- [Private clutter material buffer and source-color shader variant scoped to owned construction](../../../../mods/unscience/features/pebbles-private-clutter-materials.md)
- [Writing a Harmony transpiler for a KSA method](../../transpilers/write-a-harmony-transpiler/overview.md)

[^ksa-groundclutterrenderer]: KSA.GroundClutterRenderer (decompiled, build 2026.10.10.5554)
[^ksa-clutterecotyperenderdata]: KSA.ClutterEcotypeRenderData (decompiled, build 2026.10.10.5554)
[^unscience-clutterhooks]: unscience/pebbles.lib/Runtime/ClutterHooks.cs
[^unscience-clutterresources]: unscience/pebbles.lib/Runtime/ClutterResources.cs
[^unscience-clutterretirement]: unscience/pebbles.lib/Runtime/ClutterRetirement.cs
[^ksa-solid]: current/Content/Core/Shaders/Planet/GroundClutter/Solid.frag (build 2026.10.10.5554)
