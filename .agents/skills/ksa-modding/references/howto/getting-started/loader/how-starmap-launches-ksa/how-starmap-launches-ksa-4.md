---
type: How-To
title: Preload a lazily loaded game assembly into the core load context before patching it
description: Patch code in a game DLL the game has not loaded yet by loading it into the context that holds KSA.dll from StarMapBeforeMain, so the patched copy is the one the game calls
category: getting-started
group: loader
topic: how-starmap-launches-ksa
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/starmap/features/per-mod-assembly-load-context
  - mods/realatmospheres/features/shader-file-redirect
techniques: [assembly-load-context, harmony-prefix, reflection]
hooks: [starmap StarMapBeforeMain, reflection KSA.Mod, harmony-prefix RenderCore.ShaderModuleUtils.FromFile]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.Program]
tags: [assembly-load-context, lazy-load, harmony, reflection, preload, shaders]
---

Some game assemblies are loaded lazily: `Planet.Render.Core.dll` (home of `RenderCore.ShaderModuleUtils`) is
not in the process when `StarMapBeforeMain` runs. If your mod needs to patch a type in such an assembly
*before* the game first uses it, you must load that assembly yourself, and you must load it into the
**same** `AssemblyLoadContext` that holds `KSA.dll`. Loading it into your own mod context produces a second
copy, and the patch lands on code the game never calls. RealAtmospheres does exactly this to redirect shader
compiles.[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L44-L57) Background on the contexts: [Host process, contexts and hook binding walkthrough](./how-starmap-launches-ksa-1.md); the use case in
full is [Shader file redirect into a patched shadow tree](../../../../mods/realatmospheres/features/shader-file-redirect.md).

## Background: how the game does it

StarMap loads `KSA.dll` and its dependencies into `CoreAssemblyLoadContext`. Its `Load` override resolves
through the game's `AssemblyDependencyResolver`, so any game-folder assembly KSA references is loaded on
demand into that context the first time a JIT-compiled method needs it.[StarMap/CoreAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/CoreAssemblyLoadContext.cs#L21-L36) A mod's own
context resolves assembly names by asking, in order, `Default`, the core context's already-loaded
assemblies, then `coreContext.LoadFromAssemblyName`.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L47) So a type you reference
*statically* resolves to the shared copy, but only at the moment the runtime needs it; and a patch needs the
`MethodInfo` earlier than the game does.

The target here is the shader compile entry point. The four-parameter overload is the real work; the
two-parameter one forwards to it:[current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L228)

```csharp
// current/decomp/RenderCore/ShaderModuleUtils.cs L117-L118
	public static VkShaderModule FromFile(Device device, string filePath, out VkShaderStageFlags shaderStage, CompileOptions? options = null)
```

## Lifecycle and timing

Patch at `startup:starmap-before-main`. Shaders listed as `<Shader>` assets are compiled by `ILoader`s that
`ModLibrary.LoadAll` runs with `Parallel.ForEachAsync` on thread-pool threads
(`startup:asset-load`), so the prefix fires many times, concurrently, long before `StarMapAllModsLoaded`
(whose postfix on `LoadAll` is too late for the first compiles).[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L507-L519) The prefix must
be thread-safe. No game object exists at `BeforeMain`; only types are touched.

## Steps

1. **Find the folder that holds the game's DLLs and a game type to anchor the context.** Resolve a type from
   `KSA.dll` by name (no compile-time reference needed) and read its assembly location:

   ```csharp
   // RealAtmospheres/ModMain.cs L38-L44
           string modDir = Path.GetDirectoryName(typeof(ModMain).Assembly.Location)!;

           Type modType = AccessTools.TypeByName("KSA.Mod")
               ?? throw new InvalidOperationException("KSA.Mod not found");

           string installRoot = Path.GetDirectoryName(modType.Assembly.Location)!;
   ```

   `AccessTools.TypeByName` searches every loaded assembly in every context, so it finds `KSA.Mod` in the core
   context.
2. **Try the cheap lookup first.** If the target assembly is already loaded, the type is found and nothing
   needs preloading.
3. **Otherwise preload into the context that owns `KSA.dll`**, then take the type from the returned
   assembly:[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L44-L57)

   ```csharp
   // RealAtmospheres/ModMain.cs L44-L57
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

   `GetLoadContext(modType.Assembly)` returns the core context because that is where `KSA.dll` lives.
   Later, when the game's own code asks for `Planet.Render.Core`, the context already has that assembly
   loaded by name and hands back the same instance, so your patch applies to the game's copy.
4. **Pick the overload by shape, not by index.** Select by name, parameter count and parameter types so a
   new overload does not silently change the target:[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L92-L99)

   ```csharp
   // RealAtmospheres/ModMain.cs L92-L99
           MethodInfo fromFile = shaderUtils
               .GetMethods(BindingFlags.Public | BindingFlags.Static)
               .FirstOrDefault(m => m.Name == "FromFile"
                   && m.GetParameters().Length == 4
                   && m.GetParameters()[1].ParameterType == typeof(string))
               ?? throw new InvalidOperationException("ShaderModuleUtils.FromFile(4) not found");
           harmony.Patch(fromFile,
               prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.ShaderFromFilePrefix)));
   ```

5. **Write the prefix with a `ref` argument** so it can rewrite the input; keep it free of shared mutable
   state because it runs on several threads:

   ```csharp
   // RealAtmospheres/ModMain.cs L231-L248
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

6. **Verify it worked.** Log from the prefix once (the sample logs the first redirect); if the game
   still uses stock shaders, you patched a second copy (check `GetLoadContext(...)` of the patched method's
   declaring assembly equals the core context).

## Complete example

[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs) is the whole mod entry: `Install()` builds a shadow copy of `Content/Core/Shaders`, patches the
XML consumers and `FromFile`, and the prefix above redirects. The rest of the technique (shadow tree, anchor
patching) belongs to other topics; here only the preload matters.

## Variations and gotchas

- **Never preload into your own context.** `GetLoadContext(typeof(MyMod).Assembly)` is the mod context;
  loading there makes a separate `Planet.Render.Core` whose statics and methods are not the game's.
- **Resolve the folder from a game type's location**, not from a hard-coded install path; the install root
  differs per machine.
- **Wrap `Install()` in a try/catch** (the sample does) so a layout change leaves the game stock instead of
  aborting the launch; an exception out of `StarMapBeforeMain` escapes `ModLoader.Init`.
- **Prefer a later patch when you can.** If the code you patch first runs after `StarMapAllModsLoaded`,
  `AccessTools.TypeByName` will find the assembly by then and no preload is needed.
- **Platform caveat in this sample.** The shader marker is `\Content\Core\Shaders\` with Windows separators; on
  Linux the redirect likely never matches (inferred from the code, untested).
- **Hot reload recompiles go through the same overload**, so they hit the same prefix.[current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L138)

## See also

- [Host process, contexts and hook binding walkthrough](./how-starmap-launches-ksa-1.md), [How StarMap launches KSA and loads your mod](./overview.md)
- [Shader file redirect into a patched shadow tree](../../../../mods/realatmospheres/features/shader-file-redirect.md), [Isolate each mod in its own AssemblyLoadContext](../../../../mods/starmap/features/per-mod-assembly-load-context.md)
- [StarMapBeforeMain - patch code that runs during startup](../../lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-1.md), [Reflection-only mod with visible breakage](../../build/build-a-mod-without-game-references/build-a-mod-without-game-references-1.md)
