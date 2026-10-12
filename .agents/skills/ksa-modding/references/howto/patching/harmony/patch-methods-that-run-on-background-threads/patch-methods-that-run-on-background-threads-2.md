---
type: How-To
title: Preload in StarMapBeforeMain because shaders compile in parallel on pool threads
description: A prefix on ShaderModuleUtils.FromFile redirects shader paths while the game compiles them on thread-pool threads at load; install it in StarMapBeforeMain, preload the owning assembly into the game's load context, and keep the prefix thread-safe
category: patching
group: harmony
topic: patch-methods-that-run-on-background-threads
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/shader-file-redirect]
techniques: [harmony-prefix, assembly-load-context, background-thread]
hooks: [starmap StarMapBeforeMain, harmony-prefix RenderCore.ShaderModuleUtils.FromFile]
lifecycle: [startup:starmap-before-main, startup:asset-load, thread:background]
game_types: [KSA.ModLibrary, KSA.FileReference, KSA.ShaderReference, RenderCore.ShaderModuleUtils]
tags: [harmony, prefix, thread-pool, shaders, load-context, preload, startup]
---

KSA compiles its shaders while it loads mod data, on thread-pool threads, many at once. A patch on the shader
compile entry point therefore has to exist before the first compile, has to be thread-safe, and has to patch the
copy of the type the game will actually use. Real Atmospheres redirects every compile whose path points into the stock
`Content/Core/Shaders` tree to a patched copy with one prefix on one method. This article is about the startup and
thread side of that patch: where to install it, how to find a type whose assembly is not loaded yet, and what the
prefix may touch.

Prerequisites: a StarMap mod with `0Harmony` (and, here, no compile-time reference to the game). Demonstrated by
Real Atmospheres in [Shader file redirect into a patched shadow tree](../../../../mods/realatmospheres/features/shader-file-redirect.md).

## Background: how the game does it

Stock shaders are `<Shader>` entries in the default asset XML. Each `FileReference` registers itself as a loader
when its bundle loads outside preload, and `ModLibrary.LoadAll` runs all loaders with `Parallel.ForEachAsync`:

```csharp
// current/decomp/KSA/FileReference.cs L39-L55
public override void OnDataLoad(Mod mod)
{
	base.OnDataLoad(mod);
	if (!base.IsReferenceable)
	{
		base.Id = ModPath;
		SetHash();
	}
	if (!mod.Preload)
	{
		_isReference = string.IsNullOrEmpty(LocalPath);
		if (!_isReference)
		{
			ModLibrary.RegisterLoader(this);
		}
	}
}
```

Source: [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L55)[^ksa-filereference]

```csharp
// current/decomp/KSA/ModLibrary.cs L507-L519
Task task;
lock (Loaders)
{
	task = Parallel.ForEachAsync(Loaders, delegate(ILoader loader, CancellationToken token)
	{
		loader.Load();
		return ValueTask.CompletedTask;
	});
}
while (!task.Wait(1))
{
	Loading.Current?.OnFrame();
}
```

Source: [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L507-L519)[^ksa-modlibrary]

The compile ends in `RenderCore.ShaderModuleUtils.FromFile(Device, string filePath, out VkShaderStageFlags, CompileOptions?)`,
which reads the file and compiles it ([current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L125)[^ksa-shadermoduleutils]).

## Lifecycle and timing

- **Install: `startup:starmap-before-main`.** StarMap invokes `[StarMapBeforeMain]` from `RuntimeMod.InitializeMod`
  with a bare `action.Invoke`, before it calls the game's entry point
  ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L156-L159)[^starmap-runtimemod]). That is the only hook before shader loading, and no
  game object exists yet, only types.
- **Run: `startup:asset-load`, on pool threads.** The prefix fires from the loaders in `ModLibrary.LoadAll`
  (`thread:background`). Later recompiles (hot reload, variants) go through the same overload on whatever thread
  asks. It must therefore be allocation-light and thread-safe: here it reads a static string set before the game
  started and calls `File.Exists`.
- StarMap does not wrap `[StarMapBeforeMain]` in a `try`/`catch`; a throw there aborts the launch, so Real Atmospheres
  catches everything itself and logs "FAILED to install (game will run stock)". See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
  [Let an anchor failure stop the launch (and why that can be right)](../../safety/refuse-a-patch-and-roll-back-when-the-game-changes/refuse-a-patch-and-roll-back-when-the-game-changes-3.md).

## Steps

1. **Install in `StarMapBeforeMain`, catching everything.** The mod builds the shadow tree and patches inside one
   `try`:

```csharp
// ModMain.cs L24-L35
[StarMapBeforeMain]
public void BeforeMain()
{
    try
    {
        Install();
    }
    catch (Exception ex)
    {
        ShadowBuilder.Log("FAILED to install (game will run stock): " + ex);
    }
}
```

Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L24-L35)[^realatmospheres-modmain]

2. **Get the right copy of the target type.** `RenderCore.ShaderModuleUtils` lives in `Planet.Render.Core.dll`,
   which the mod's comment says is not loaded at this point. If `AccessTools.TypeByName` finds nothing, load the assembly
   into the load context that holds `KSA.dll` and take the type from there: patching a second copy in another
   context would have no effect:

```csharp
// ModMain.cs L41-L57
Type modType = AccessTools.TypeByName("KSA.Mod")
    ?? throw new InvalidOperationException("KSA.Mod not found");

string installRoot = Path.GetDirectoryName(modType.Assembly.Location)!;

// Planet.Render.Core loads lazily and is not in the process yet at
// BeforeMain: pre-load it into KSA's own load context so the instance
// we patch is the one the game will use.
Type? shaderUtils = AccessTools.TypeByName("RenderCore.ShaderModuleUtils");
if (shaderUtils == null)
{
    var alc = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(modType.Assembly)
        ?? System.Runtime.Loader.AssemblyLoadContext.Default;
    var prc = alc.LoadFromAssemblyPath(Path.Combine(installRoot, "Planet.Render.Core.dll"));
    shaderUtils = prc.GetType("RenderCore.ShaderModuleUtils")
        ?? throw new InvalidOperationException("RenderCore.ShaderModuleUtils not found");
}
```

Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L41-L57)

3. **Pick the overload by shape.** The mod references only StarMap.API and `0Harmony`, so it finds `FromFile` by name,
   parameter count and the type of the second parameter. The four-parameter overload is the one the two-parameter one
   forwards to:

```csharp
// ModMain.cs L92-L99
MethodInfo fromFile = shaderUtils
    .GetMethods(BindingFlags.Public | BindingFlags.Static)
    .FirstOrDefault(m => m.Name == "FromFile"
        && m.GetParameters().Length == 4
        && m.GetParameters()[1].ParameterType == typeof(string))
    ?? throw new InvalidOperationException("ShaderModuleUtils.FromFile(4) not found");
harmony.Patch(fromFile,
    prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.ShaderFromFilePrefix)));
```

Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L92-L99)

4. **Write a prefix that only reads and rewrites its argument.** `ref string filePath` lets the prefix replace the
   path; nothing else is touched, and missing files fall through to stock:

```csharp
// ModMain.cs L231-L248
public static void ShaderFromFilePrefix(ref string filePath)
{
    string? shadowRoot = ShadowBuilder.ShadowShadersRoot;
    if (shadowRoot == null) return;
    string full = Normalize(filePath);
    int i = full.IndexOf(ShadersMarker, StringComparison.OrdinalIgnoreCase);
    if (i < 0) return;
    string candidate = Path.Combine(shadowRoot, full[(i + ShadersMarker.Length)..]);
    if (File.Exists(candidate))
    {
        filePath = candidate;
        if (!_logged2DCloud && candidate.EndsWith("2DCloud.comp", StringComparison.OrdinalIgnoreCase))
        {
            ShadowBuilder.Log("redirected Clouds/2DCloud.comp (terminator fade live)");
            _logged2DCloud = true;
        }
    }
}
```

Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L231-L248)

   Include directives follow for free because shaderc resolves `#include` relative to the requesting file's
   directory, so a redirected file pulls its siblings from the copy too.

5. **Verify.** A redirected shader compiles from the copy (the mod logs the first redirect of `Clouds/2DCloud.comp`).
   A missing `Planet.Render.Core.dll` or an unknown overload is reported by the `FAILED to install` line and the
   game runs stock.

## Complete example

| File | Role |
|---|---|
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L24-L110) | `BeforeMain`, type discovery, overload pick, Harmony patches |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L226-L248) | `ShaderFromFilePrefix` |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L7-L24)[^realatmospheres-shadowbuilder] | Copy of the whole shader tree so includes resolve inside it |

## Variations and gotchas

- **Do not patch generic shared code.** The mod's comment records a crash from patching the generic `XmlLoader.Load<T>`:
  shared generic code means a patch hijacks every reference-type instantiation. Patch the non-generic consumers.
- **Two mods redirecting the same file** chain through the `ref string filePath` argument; the first to rewrite
  decides unless a later prefix also matches the rewritten path. Real Atmospheres has no coordination for shaders.
- **Windows path assumptions.** The marker is `\Content\Core\Shaders\`; the feature page infers (without testing) that
  on Linux the marker never matches and shaders stay stock.
- **Prefer the overload with the `ref string`.** Patching `FromString` would also work but would have to re-implement
  include resolution.
- **Hot reload compiles the copy.** An edit to a stock file does not show while the redirect is active.

## See also

- [Patches on planner tasks that run on pooled threads](patch-methods-that-run-on-background-threads-1.md), [Worker-side postfix merging commands from the main thread](patch-methods-that-run-on-background-threads-3.md), [ConditionalWeakTable keyed by objects the copy shares by reference](patch-methods-that-run-on-background-threads-4.md), [Look the state up through __instance.BurnPlan in a ComputeControl prefix](patch-methods-that-run-on-background-threads-5.md)
- [Patching game methods that run on worker and pool threads](overview.md)
- [Shader file redirect into a patched shadow tree](../../../../mods/realatmospheres/features/shader-file-redirect.md)
- Related topic: how-starmap-launches-ksa

[^ksa-filereference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-shadermoduleutils]: RenderCore.ShaderModuleUtils (decompiled, build 2026.10.10.5554)
[^starmap-runtimemod]: StarMap/StarMap.Core/ModRepository/RuntimeMod.cs
[^realatmospheres-modmain]: RealAtmospheres/ModMain.cs
[^realatmospheres-shadowbuilder]: RealAtmospheres/ShadowBuilder.cs
