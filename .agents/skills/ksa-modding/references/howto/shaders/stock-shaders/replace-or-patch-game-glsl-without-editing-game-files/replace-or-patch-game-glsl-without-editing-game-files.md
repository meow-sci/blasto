---
type: How-To
title: "Replace or text-patch the game's GLSL: a shadow tree, a FromFile redirect and anchor-verified edits"
description: Copy Content/Core/Shaders into the mod folder at launch, redirect ShaderModuleUtils.FromFile to the copies and patch them with exact-count, idempotent text edits that log and skip on drift
category: shaders
group: stock-shaders
topic: replace-or-patch-game-glsl-without-editing-game-files
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/realatmospheres/features/shader-file-redirect
  - mods/realatmospheres/features/shadow-tree-anchor-patching
techniques: [harmony-prefix, glsl-shader, reflection]
hooks: [starmap StarMapBeforeMain, harmony-prefix RenderCore.ShaderModuleUtils.FromFile]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types:
  - RenderCore.ShaderModuleUtils
  - KSA.ShaderReference
  - RenderCore.Shaders.ShaderCompilerResolve
  - KSA.AssetReloader.ShaderReloader
  - KSA.ModLibrary
tags: [glsl, shader-patching, redirect, shadow-tree, anchor, idempotent, realatmospheres, thread-pool]
---
Real Atmospheres changes about a dozen of KSA's GLSL files (atmosphere scattering, clouds, terrain lighting) without editing the install. At launch it
copies the game's whole shader tree into its own folder, patches the copy as text, and redirects every shader compile that points into the stock tree to
the copy. This article is the recipe, including the part that matters most for survival across game updates: edits that find their anchor exactly or do
nothing. You need StarMap, Harmony and a willingness to read the stock GLSL; nothing here references game assemblies at compile time.

## Background: how the game does it

See the topic overview for the compile seam. The facts that shape the recipe:

| Fact | Consequence |
|---|---|
| Stock shaders compile in `ModLibrary.LoadAll` via `Parallel.ForEachAsync` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522)) | The redirect prefix runs on many thread-pool threads at once; keep it read-only |
| `FromFile(Device, string, out VkShaderStageFlags, CompileOptions?)` is the compile entry ([current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L117-L159)) | Patch the four-parameter overload; the two-parameter one forwards to it |
| Includes resolve relative to the requesting file | A copied tree needs no include rewriting |
| `ShaderReloader.GetModPath` keys on the substring from `"Content"` ([current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54-L58)) | Name the copy folder so its path still contains `Content` after the first occurrence |
| `FileReference.Load` catches a `ShaderCompilationException` and logs; the reference keeps no module ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L67-L108)) | A bad patch is not fail-soft: the renderer that later uses the shader throws |

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Build the shadow tree, apply text patches, install the prefix | `[StarMapBeforeMain]`, before `Program.Main`; file IO only | `startup:starmap-before-main` |
| The prefix fires | Each stock shader compile in `ModLibrary.LoadAll`, on thread-pool threads | `startup:asset-load` |
| Later recompiles (hot reload, variants) and CACAO passes | Same `FromFile` overload, so also redirected | after load |

`[StarMapBeforeMain]` is the only hook that runs before shader loading, and no game object exists yet; only types are touched. See the
[game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Install in `[StarMapBeforeMain]` with a catch-all.** If anything throws, log it and let the game run stock:

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

   Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L24-L35).

2. **Resolve the patched type in the game's own load context.** `RenderCore.ShaderModuleUtils` lives in `Planet.Render.Core.dll`, which is not loaded yet at
   this point. Pre-load it into the context that holds `KSA.dll`, or the instance you patch is not the one the game uses:

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

   Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L46-L57). `modType` is `KSA.Mod`, found by name.

3. **Copy the stock tree into a folder whose path contains `Content`.** The mod uses `<mod>/ShadowContent/Core/Shaders`, deleting and re-copying it every launch
   so a game update simply changes the source of the copy:

   ```csharp
   // ShadowBuilder.cs L41-L49
           string shadowCore = Path.Combine(modDir, "ShadowContent", "Core");
           string assetsDir = Path.Combine(modDir, "assets");

           // ---- Shaders tree ----
           string srcShaders = Path.Combine(coreDir, "Shaders");
           string dstShaders = Path.Combine(shadowCore, "Shaders");
           if (Directory.Exists(dstShaders))
               Directory.Delete(dstShaders, recursive: true);
           CopyTree(srcShaders, dstShaders);
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L41-L49). `coreDir` is `<install>/Content/Core`. Keeping `Content` in the folder name keeps
   `ShaderReloader.GetModPath` valid; the leading backslash in the redirect marker means a redirected path never matches again.

4. **Patch the copy as text with anchors that must match exactly.** Read with CRLF turned into LF, count occurrences, and apply a pair only when the stock text
   occurs exactly once; skip silently when the corrected text is already present:

   ```csharp
   // ShadowBuilder.cs L348-L364
       private static string PatchAtmosphereValues(
           string s, (string Stock, string Corrected, string Marker)[] pairs, string label)
       {
           foreach (var (stock, corrected, marker) in pairs)
           {
               if (s.Contains(marker))
                   continue; // already applied (e.g. hand-patched install)
               int n = CountOf(s, stock);
               if (n != 1)
               {
                   Log($"WARN: {label} atmosphere anchor found {n}x (expected 1), skipping: {Truncate(stock)}");
                   continue;
               }
               s = s.Replace(stock, corrected);
           }
           return s;
       }
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L348-L364). Shader edits use `(file, stock, patch, expectedCount)` because some anchors legitimately appear twice or four times,
   for example the density sites ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2283-L2289)):

   ```csharp
   // Payloads.cs L2283-L2289
       public static readonly (string File, string Stock, string Patch, int Count)[] MarsDensitySites =
       {
           ("Atmosphere/AtmosphereFunctions.glsl", DensAStock, DensAPatch, 2),
           ("Atmosphere/AerialPerspectiveLut.comp", DensAStock, DensAPatch, 1),
           ("Atmosphere/Atmosphere.comp", DensBStock, DensBPatch, 1),
           ("Atmosphere/Godrays.glsl", DensBStock, DensBPatch, 2),
       };
   ```

5. **Make multi-site edits all-or-nothing per file.** Where several sites must agree (Rayleigh extinction and scattering in one shader), count every anchor first and
   write nothing unless all match:

   ```csharp
   // ShadowBuilder.cs L667-L689
       private static void PatchRayleighColour(string shadersDir)
       {
           int done = 0, files = 0;
           foreach (var group in Payloads.RayleighColourSites.GroupBy(site => site.File))
           {
               files++;
               string path = Path.Combine(shadersDir, group.Key.Replace('/', Path.DirectorySeparatorChar));
               if (!File.Exists(path)) { Log($"WARN: {group.Key} missing, skipping its Rayleigh colour"); continue; }
               string s = ReadLf(path);
               if (s.Contains(Payloads.RayleighColourCall)) { done++; continue; } // already applied
               var off = group.Where(site => CountOf(s, site.Stock) != site.Count).ToList();
               if (off.Count > 0)
               {
                   Log($"WARN: {group.Key} Rayleigh anchor '{off[0].Stock}' found {CountOf(s, off[0].Stock)}x (expected {off[0].Count}), leaving its colour stock");
                   continue;
               }
               foreach (var site in group)
                   s = s.Replace(site.Stock, site.Patch);
               File.WriteAllText(path, s);
               done++;
           }
           Log($"Rayleigh colour by altitude ({done}/{files} files)");
       }
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L667-L689). Prefer anchors on stable structure (`void main()\n{`, an include guard's `#endif`) for insertions, and keep payload literals at
   column 0 so indentation equals the stock file byte for byte.

6. **Install the redirect.** Select the four-parameter `FromFile` whose second parameter is a `string`, and prefix it:

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

   Sources: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L92-L99) and [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L231-L248). The prefix only reads a static string set before the game started and calls `File.Exists`,
   which is safe on many threads. Files missing from the copy fall through to stock.

7. **Check the log.** Output goes to the console prefixed `RealAtmospheres - `, for example `shader tree ready (n/2 patches applied) -> <path>`. Every
   `WARN: ... anchor found Nx (expected M)` line means the game changed under that patch and the file stays stock.

## Complete example

| File | Role |
|---|---|
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs) | Install, type resolution, `FromFile` prefix |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) | Copy, `PatchFile`, `ReadLf`, `CountOf`, patch engines |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | The stock/corrected text pairs and site lists |

## Variations and gotchas

- **Fixed locale.** Format every number written into text with `CultureInfo.InvariantCulture`; a decimal comma once made a patched XML fail to load and the game would not start
  ([CHANGELOG.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CHANGELOG.md#L40-L44)).
- **A patch that compiles badly breaks the renderer, not the patch.** The anchors only guarantee the text was found. A shader that fails to compile logs an error from
  `FileReference.Load` and leaves the `ShaderReference` without a module; the first pipeline that converts it to a `VkShaderModule` then throws
  (inferred from the code). Compile your patched tree offline with glslang ([Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md)).
- **Windows path assumptions.** `Normalize` replaces `/` with `\` and the marker is `\Content\Core\Shaders\`. On Linux the marker never matches and shaders stay stock
  (inferred, not tested).
- **Two mods redirecting the same file** chain through the `ref string filePath`; the first prefix to rewrite the path decides. There is no shader coordination.
- **Hot reload compiles the copy.** An edit to a stock file does not show while the mod is installed.
- **Whole-tree copy every launch.** 412 files in build 2026.10.10.5554; the cost is paid before the window opens.
- **Pin to a build.** Every anchor in the mod was verified against build 2026.10.10.5554; re-check after each game update.

## See also

- Overview: [overview](overview.md)
- [Shader file redirect into a patched shadow tree](../../../../mods/realatmospheres/features/shader-file-redirect.md), [Anchor-verified text patching of the shadow tree](../../../../mods/realatmospheres/features/shadow-tree-anchor-patching.md)
- [Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md), [Patch Core or another mod's XML data without copying files](../../../assets/xml-patching/patch-game-xml-data/overview.md), [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md)
