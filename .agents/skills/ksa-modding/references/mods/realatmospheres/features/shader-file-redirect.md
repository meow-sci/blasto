---
type: Mod Feature
title: Shader file redirect into a patched shadow tree
description: A Harmony prefix on RenderCore.ShaderModuleUtils.FromFile points every Content/Core/Shaders path at a patched full copy of the tree in the mod folder, so stock shaders and their includes compile from the copy
mod: realatmospheres
category: shaders
techniques: [harmony-prefix, reflection, glsl-shader, assembly-load-context]
hooks: [starmap StarMapBeforeMain, harmony-prefix RenderCore.ShaderModuleUtils.FromFile]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - RenderCore.ShaderModuleUtils
  - KSA.ShaderReference
  - RenderCore.Shaders.ShaderCompilerResolve
  - Brutal.ShaderCApi.ShaderC
  - KSA.AssetReloader.ShaderReloader
  - KSA.FileReference
  - KSA.ModLibrary
  - RenderCore.Pipelines.SimpleShaderStages
  - KSA.Mod
tags: [shaders, glsl, shaderc, harmony, prefix, file-redirect, includes]
---

Real Atmospheres changes about a dozen of KSA's GLSL files (atmosphere scattering, clouds, terrain
lighting) without editing the install. At launch it copies the game's whole `Content/Core/Shaders`
tree into `<mod>/ShadowContent/Core/Shaders`, patches the copy as text, and then redirects every
shader compile whose path points into the stock tree to the same relative path in the copy. One
prefix on one method covers every shader the game compiles from that tree.[^ra-modmain]

## How it works

1. **Find the method without referencing the game.** At `[StarMapBeforeMain]` the mod looks up
   `RenderCore.ShaderModuleUtils` with `AccessTools.TypeByName`. That type lives in
   `Planet.Render.Core.dll`, which the mod's comment says is not loaded yet at that point. If the
   lookup fails, the mod loads the assembly itself into the load context that holds `KSA.dll`
   (StarMap's `CoreAssemblyLoadContext`) and takes the type from there; patching a second copy in
   another context would have no effect
   ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L46-L57)).
2. **Pick the overload.** It patches the four-parameter `FromFile` whose second parameter is a
   `string` ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L92-L99)). In the game that is
   `FromFile(Device device, string filePath, out VkShaderStageFlags shaderStage, CompileOptions? options)`;
   the two-parameter overload forwards to it
   ([current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L159)).[^rendercore-shadermoduleutils]
3. **Rewrite the path.** `ShaderFromFilePrefix(ref string filePath)` normalizes the path with
   `Path.GetFullPath` (relative paths resolve against the working directory, which StarMap sets to the
   game folder), finds `\Content\Core\Shaders\`, and swaps in the shadow root if the file exists there
   ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L226-L248)). Missing files fall through to stock.
4. **Includes follow for free.** `FromFile` passes the path as the shaderc input file name, and the
   include callback resolves every `#include` relative to the directory of the requesting file
   ([current/decomp/Brutal.ShaderCApi/ShaderC.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ShaderCApi/ShaderC.cs#L247-L255)).[^brutal-shadercapi-shaderc]
   Because the whole tree is copied, a redirected `Atmosphere.comp` pulls the patched
   `AtmosphereData.glsl` and `AtmosphereFunctions.glsl` from the copy too
   ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L7-L24)).
5. **Keep hot-reload bookkeeping valid.** `ShaderReference.Compile` registers an include callback
   that maps every resolved include through `ShaderReloader.GetModPath`, which takes the substring from
   the first `"Content"` ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L140-L166),
   [current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54-L58)).
   The folder is named `ShadowContent` so that lookup still finds `Content\Core\Shaders\...`, while the
   leading backslash in the marker means a redirected path never matches again.

The redirect target table (what is patched in the copy) is covered in
[Anchor-verified text patching of the shadow tree](shadow-tree-anchor-patching.md).

## Lifecycle and timing

- **`startup:starmap-before-main`: install.** `ModMain.BeforeMain` builds the shadow tree and installs
  the prefix before `Program.Main` runs ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L24-L35)). StarMap invokes
  `[StarMapBeforeMain]` directly from `RuntimeMod.InitializeMod`
  ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L129-L162)),
  so the patch exists before the first shader compiles. No game object exists yet; only types are
  touched.
- **`startup:asset-load`: the prefix fires.** Stock shaders are `<Shader>` entries in
  `Core/DefaultAssets.xml` (for example `AtmosphereCompute` is `Shaders/Atmosphere/Atmosphere.comp`,
  [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L235)). Each
  `FileReference` registers itself as a loader when its bundle loads outside preload
  ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L55)), and
  `ModLibrary.LoadAll` runs all loaders with `Parallel.ForEachAsync`
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L507-L519)).[^ksa-modlibrary]
  So the prefix runs on thread-pool threads, many at once; it only reads a static string set before
  the game started and calls `File.Exists`, which is safe there. `ShaderReference` keeps its compile
  options and include callback `[ThreadStatic]` for the same reason.
- Later recompiles (hot reload, `CompileVariantWithCustomOptions`) go through the same `FromFile`
  overload and are redirected the same way
  ([current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L138)).
- Some renderers compile straight from a hard-coded path on the main thread when they are built after
  loading, through `SimpleShaderStages.SetShaderFromFile`, for example the CACAO ambient-occlusion passes
  (`"Content/Core/Shaders/PostProcess/Cacao/..."`). These go through the same `FromFile` overload, so
  they are redirected too, to unpatched copies
  ([current/decomp/RenderCore.Pipelines/SimpleShaderStages.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Pipelines/SimpleShaderStages.cs#L58-L66),
  [current/decomp/KSA/AmbientOcclusionRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AmbientOcclusionRenderer.cs#L597)).

## Key code

| Path | Role |
|---|---|
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L46-L57) | Resolve `ShaderModuleUtils`, pre-loading `Planet.Render.Core.dll` into the game's load context |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L92-L99) | Select the 4-parameter `FromFile` overload and patch it |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L226-L248) | `ShaderFromFilePrefix`: marker search and path swap |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L558-L564) | `Normalize`: full path with backslashes |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L44-L81) | Copy the tree, apply shader patches, publish `ShadowShadersRoot` |

## Snippets

```csharp
// ModMain.cs L46-L57
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

```csharp
// current/decomp/Brutal.ShaderCApi/ShaderC.cs L247-L255
		private unsafe static void ResolveInternal(ref IncludeResult result, void* data, ReadOnlySpan<byte> source, IncludeType type, ReadOnlySpan<byte> requestingSource, nuint depth)
		{
			if (depth > 200)
			{
				throw new InvalidOperationException($"Include depth {depth} exceeded the maximum of {200u}; possible #include cycle.");
			}
			String8 @string = Utf8.Path.Combine(Utf8.Path.GetDirectoryName(requestingSource), source);
			string text = Utf8.GetString(@string);
			using SafeFileHandle handle = File.OpenHandle(text, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.None, 0L);
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ShaderModuleUtils.FromFile(Device, string, out VkShaderStageFlags, CompileOptions?)` | [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L159) | Patched; reads the file and compiles it with shaderc |
| `ShaderCompilerResolve.GetCompileOptionsWithIncludeCallback` | [current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs#L16-L36) | Wraps the shaderc include resolver and reports each include |
| `ShaderC.Callbacks.ResolveInternal` | [current/decomp/Brutal.ShaderCApi/ShaderC.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ShaderCApi/ShaderC.cs#L247-L255) | Includes resolve relative to the requesting file |
| `ShaderReference.Compile`, `OnIncludeResolved` | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L166) | Caller with `base.ModPath`; include bookkeeping |
| `ShaderReloader.GetModPath` | [current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54-L58) | Substring from `"Content"`; why the folder is `ShadowContent` |
| `FileReference.ModPath`, `OnDataLoad` | [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L24-L55) | Shader path source; loader registration |
| `ModLibrary.LoadAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522) | Runs shader loaders in parallel |

## Pitfalls and version notes

- **Windows path assumptions.** `Normalize` replaces `/` with `\` and the marker is
  `\Content\Core\Shaders\` ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L153)). A shader's `ModPath` comes from
  `Mod.GetPath`, which already uses `/` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392)).
  On Linux `Path.GetFullPath` treats the inserted backslashes as ordinary characters and roots the
  path with `/`, so the marker never matches and the shaders stay stock. The XML redirect is not
  affected. This is inferred from the code and was not tested on Linux.
- **Whole-tree copy every launch.** The copy is deleted and rebuilt at each start (412 files under
  `Content/Core/Shaders` in build 2026.10.10.5554), so a game update simply changes what is copied.
  Files the game adds later are copied too; files it removes simply stop existing in the copy.
- **Shaders compiled from other roots are untouched.** Only paths containing `\Content\Core\Shaders\`
  are rewritten; the top-level `Content/Shaders` folder and other mods' shaders compile as usual.
- **Two mods redirecting the same file** would chain through the `ref string filePath` argument. The
  first prefix to rewrite the path decides, unless a later prefix also matches the rewritten path.
  Real Atmospheres' marker never matches a path inside another mod's copy unless that path contains
  `\Content\Core\Shaders\`. Neither mod sees the other's edits. Real Atmospheres has no
  coordination for shaders (it has one for XML, see
  [Shared shadow for two cooperating mods](cross-mod-shared-shadow.md)).
- **Hot reload compiles the copy.** `ShaderReloader.ReloadShader` recompiles through
  `ShaderReference.DoLoad`, so the prefix redirects it. An edit to a stock file under `Content/Core/Shaders`
  therefore does not show while the mod is installed, because the copy that is compiled is the one
  under `ShadowContent`. This is inferred from the code; hot reload is a developer setting
  (`HotReloadingEnabled`) ([current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L102-L110)).
- **Prefer the overload with the `ref string`.** Patching `FromString` would also work but would need
  to re-implement include resolution; `FromFile` keeps shaderc's own include handling.

## See also

- [Anchor-verified text patching of the shadow tree](shadow-tree-anchor-patching.md)
- [Core XML entry redirect](core-xml-entry-redirect.md)
- [Reflection-only mod build](reflection-only-mod-build.md)
- [Startup lifecycle hooks (StarMap)](../../starmap/features/startup-lifecycle-hooks.md)
- [Preload a lazily loaded game assembly into the core load context before patching it](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-4.md) - how-to article demonstrating this feature
- [Preload in StarMapBeforeMain because shaders compile in parallel on pool threads](../../../howto/patching/harmony/patch-methods-that-run-on-background-threads/patch-methods-that-run-on-background-threads-2.md) - how-to article demonstrating this feature
- [Replace or text-patch the game's GLSL: a shadow tree, a FromFile redirect and anchor-verified edits](../../../howto/shaders/stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/replace-or-patch-game-glsl-without-editing-game-files.md) - how-to article demonstrating this feature

[^ra-modmain]: ModMain.cs
[^rendercore-shadermoduleutils]: RenderCore.ShaderModuleUtils (decompiled, build 2026.10.10.5554)
[^brutal-shadercapi-shaderc]: Brutal.ShaderCApi.ShaderC (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
