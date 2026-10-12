---
type: Mod Feature
title: A generated include that reaches KSA's own shader library from a mod shader
description: At StarMapImmediateLoad the mod writes Shaders/Content/CoreAtmosphere.glsl with absolute includes of the install's Global, AtmosphereLuts and TextureSet headers, so its compute shaders compile against KSA's atmosphere code without copying it, and a glslang script checks them offline the same way
mod: ksarmory
category: shaders
techniques: [starmap-lifecycle, glsl-shader, xml-asset, generated-include, offline-shader-check]
hooks: [starmap StarMapImmediateLoad]
lifecycle: [startup:immediate-load, startup:asset-load]
game_types:
  - KSA.ShaderReference
  - KSA.AssetReloader.ShaderReloader
  - RenderCore.Shaders.ShaderCompilerResolve
  - Brutal.ShaderCApi.ShaderC
  - KSA.Mod
  - KSA.ModLibrary
tags: [glsl, shaderc, include, atmosphere-luts, bindless, generated-file, glslang, ci]
---

KSArmory's cloud and fire-light compute shaders call KSA's own atmosphere code: the same
transmittance, aerial-perspective and ambient LUT lookups, the engine's global uniform block
(camera, sun, lighting) and its bindless texture array. Those headers ship in the game install
(`Content/Core/Shaders/...`), while a mod's shaders live in the player's mods folder, and there is
no relative path between the two trees.

The mod's answer is a one-file trick: at `[StarMapImmediateLoad]`, before KSA compiles any of its
shaders, it writes `Shaders/Content/CoreAtmosphere.glsl` into its own folder containing three
`#include` lines with **absolute** paths into the running install. Its `.comp` files include that
header by a fixed relative name. Nothing of the game's is copied or redistributed, and the header
is regenerated every launch so it always names the install that is running.[^ksarmory-coreinclude]

## How it works

| Step | Where | What happens |
|---|---|---|
| 1 | `KSArmoryMod.OnImmediateLoad` L132-L137 | First statement: `CoreShaderInclude.Write(mod.DirectoryPath)`[^ksarmory-mod] |
| 2 | `CoreShaderInclude.Write` | Resolves `Content/Core/Shaders/Common/Global.glsl`, `Atmosphere/AtmosphereLuts.glsl` and `Common/TextureSet.glsl` with `Path.GetFullPath`, i.e. against the process working directory, which the mod assumes is the install root; each must exist |
| 3 | same | Creates `<mod>/Shaders/Content/` and writes `CoreAtmosphere.glsl`: a two-line comment plus one `#include "<absolute path>"` per file, backslashes turned to forward slashes |
| 4 | `KSArmoryCloud.comp`, `KSArmoryFireLight.comp` | `#define SET_TEXTURE 2`, `#define SET_CLOUD_DATA 5`, then `#include "Content/CoreAtmosphere.glsl"`[^ksarmory-cloud-comp][^ksarmory-firelight-comp] |
| 5 | KSA asset load | `ShaderReference.DoLoad` compiles each `<Shader>` with shaderc; the include resolves next to the `.comp`, and the header's absolute includes then resolve, and their own relative includes resolve inside the Core tree as usual |

Why an absolute include works (build 5554). KSA sets its own include callback,
`ShaderCompilerResolve.Resolve`, which forwards to `ShaderC.Callbacks.Resolve` and then reports the
resolved path.[^ksa-compilerresolve] `ShaderC.Callbacks.ResolveInternal` builds the path as
`Utf8.Path.Combine(GetDirectoryName(requestingSource), source)` and opens it.[^ksa-shaderc]
`Utf8.Path.Combine` is `System.IO.Path.Combine` on UTF-16 strings, which returns the second argument
unchanged when it is rooted.[^ksa-utf8] So an absolute `#include` is used as-is, and a relative one
is always relative to the file that asked.

Why the header sits in a folder named `Content`. After every include resolves,
`ShaderReference.OnIncludeResolved` maps the path through `ShaderReloader.GetModPath`, which is
`fullPath.Substring(fullPath.IndexOf("Content"))`.[^ksa-shaderreference][^ksa-shaderreloader] A path
with no `Content` in it makes that `Substring(-1)` throw; the catch logs an error naming the shader on
every compile (the shader still compiles). `<mod>/Shaders/Content/CoreAtmosphere.glsl` contains the
word, and so do the install paths it includes.

What the three headers provide:

| Header | Provides | Requirement on the includer |
|---|---|---|
| `Common/Global.glsl` | `SET_GLOBAL` 0 and the `Global`/`GlobalConstants` blocks that `ComputePipelineWrapper` binds at set 0 for every compute pass[^ksa-global-glsl] | None |
| `Atmosphere/AtmosphereLuts.glsl` | The LUT lookup functions (pulls in `Common/Shared.glsl`)[^ksa-atmosphereluts-glsl] | The LUT samplers bound at the set the shader declares (the mod binds the atmosphere LUT set at 3) |
| `Common/TextureSet.glsl` | `globalTextures[]` and friends at `SET_TEXTURE` | `#define SET_TEXTURE` first, or it hits an `#error`[^ksa-textureset-glsl] |

`SET_CLOUD_DATA 5` is defensive: none of the three headers includes `Clouds/CloudData.glsl` in
build 5554, so it binds nothing.

### Offline check: `tools/check-shaders.sh`

KSA compiles a mod's shaders only at load, so the repo compiles them in a second instead:[^ksarmory-check-shaders]

1. Finds `glslangValidator` on `PATH` or in `~/.cache/ksarmory/glslang` (`--install` downloads
   Khronos' 16.6.0 Linux release there); skips with a notice if absent.
2. Uses `$KSA_DIR/Content/Core/Shaders` as the library (default
   `/mnt/c/Program Files/Kitten Space Agency`, a WSL path); skips if missing, which is what CI does.
3. Writes a temporary `Content/CoreAtmosphere.glsl` with **relative** includes and passes the
   library with `-I`, because glslang takes a search path where shaderc took the absolute one.
4. Copies each `.comp`, inserting `#extension GL_GOOGLE_include_directive : require` after line 1,
   and compiles with `-V --target-env vulkan1.3`; errors are reported with a one-line offset note.

`tools/check-all.sh` runs it as "Shaders compile".[^ksarmory-check-all]

## Lifecycle and timing

- `[StarMapImmediateLoad]` is StarMap's Harmony prefix on `Mod.PrepareSystems`,[^starmap-modpatches]
  which `ModLibrary.PrepareAll` calls per mod (L443).[^ksa-modlibrary] That is before
  `ModLibrary.LoadAll`, where `AssetBundle` entries are loaded and every registered loader, the
  mod's `ShaderReference`s included, runs in `Parallel.ForEachAsync` (L510-L514). Writing the header
  any later (for example at `[StarMapAllModsLoaded]`, a postfix on `LoadAll`) would be after the
  compile.
- `Mod.DirectoryPath` is the folder of the mod's manifest, set when the mod is read.[^ksa-mod]
- Nothing in KSA is touched; the hook only writes a file. The compiled modules are later looked up
  by the compute pass at render time.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/CoreShaderInclude.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CoreShaderInclude.cs) | Writes the header |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L132-L147) | Calls it first thing at immediate load |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1-L19) | Defines the set macros and includes the header |
| [src/KSArmory/Shaders/KSArmoryFireLight.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryFireLight.comp#L1-L20) | The second includer |
| [tools/check-shaders.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/check-shaders.sh) | Offline glslang compile against the install's library |

## Snippets

```csharp
// src/KSArmory/Ksa/CoreShaderInclude.cs L55-L100
public static bool Write(string modFolder)
{
    Available = false;

    try
    {
        // The game's own working directory is its install root, which is what makes the Content
        // path resolvable without anybody being asked where the game is.
        List<string> found = [];

        foreach (string[] parts in Wanted)
        {
            string core = Path.GetFullPath(Path.Combine(parts));

            if (!File.Exists(core))
            {
                // ...
                return false;
            }

            found.Add(core);
        }

        string shaders = Path.Combine(modFolder, Folder);
        // ...
        string body = "// Generated at load. Points at this machine's own KSA install;\n"
                      + "// nothing of the game's is copied here. See Ksa/CoreShaderInclude.cs.\n";

        foreach (string core in found) body += $"#include \"{core.Replace('\\', '/')}\"\n";

        string into = Directory.CreateDirectory(Path.Combine(shaders, Subfolder)).FullName;
        File.WriteAllText(Path.Combine(into, Generated), body);

        Available = true;
        // ...
        return true;
    }
```

```glsl
// src/KSArmory/Shaders/KSArmoryCloud.comp L1-L19
#version 450

// KSA's own atmosphere, through a header this mod writes at load pointing at the player's install:
// shaderc resolves an include against the requesting file, and Core's shader tree and a mod's do
// not meet by any relative path. Ksa/CoreShaderInclude.cs writes it; nothing of the game's is
// copied here.
// ...
#define SET_TEXTURE 2
#define SET_CLOUD_DATA 5

#include "Content/CoreAtmosphere.glsl"
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

```bash
# tools/check-shaders.sh L43-L66
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

mkdir -p "$WORK/Content"
printf '#include "Common/Global.glsl"\n#include "Atmosphere/AtmosphereLuts.glsl"\n#include "Common/TextureSet.glsl"\n' \
    > "$WORK/Content/CoreAtmosphere.glsl"

failed=0
for shader in "$REPO_ROOT"/src/KSArmory/Shaders/*.comp; do
    name="$(basename "$shader")"

    # shaderc takes #include without being asked; glslang wants the extension named.
    awk 'NR==1 { print; print "#extension GL_GOOGLE_include_directive : require"; next } 1' \
        "$shader" > "$WORK/$name"

    if out="$("$GLSLANG" -V --target-env vulkan1.3 "-I$LIBRARY" -o /dev/null "$WORK/$name" 2>&1)"; then
        echo "  $name compiles"
    else
        failed=1
        # ...
    fi
done
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `ShaderReference.DoLoad` / `Compile` / `OnIncludeResolved` | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L181) | Compiles the mod's `.comp` with the include callback and records include paths |
| `ShaderCompilerResolve.Resolve` | [current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Shaders/ShaderCompilerResolve.cs#L28-L36) | KSA's include hook: resolve, then notify |
| `ShaderC.Callbacks.ResolveInternal` | [current/decomp/Brutal.ShaderCApi/ShaderC.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ShaderCApi/ShaderC.cs#L247-L272) | Directory-of-requester plus include path; depth limit 200 |
| `Utf8.Path.Combine` | [current/decomp/Brutal.Strings/Utf8.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Strings/Utf8.cs#L260-L263) | `System.IO.Path.Combine`, so rooted includes win |
| `ShaderReloader.GetModPath` | [current/decomp/KSA.AssetReloader/ShaderReloader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.AssetReloader/ShaderReloader.cs#L54-L58) | Why the header lives under a `Content` folder |
| `ModLibrary.PrepareAll` / `LoadAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L522) | Immediate load first, shader compile after |
| `Mod.DirectoryPath` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L91-L109) | Where to write the header |

## Pitfalls and version notes

- **No fallback in this checkout.** The class comment says that without the header "the cloud
  still draws, on the approximated sky", but both `.comp` files include it unconditionally and GLSL
  has no conditional include. A failed `Write` on a fresh install is a compile failure of the cloud
  and fire-light shaders; with the shader pass on, the cloud pipeline build then throws and the
  compute pass stands down.[^ksarmory-coreinclude]
- **It is per machine.** The header holds an absolute install path, so it must never be committed
  or shipped; it is regenerated each launch. `.gitignore` does not list it (the file is written into
  the deployed mod folder, not the repo).[^ksarmory-gitignore]
- **Working directory assumption.** `Path.GetFullPath("Content/...")` relies on the game's working
  directory being its install root; the decomp does not show how KSA sets its working directory, so
  this is the mod's assumption (unverified here). A launcher that changes it breaks the lookup.
- **Coupled to KSA's shader internals.** Any rename or signature change in `AtmosphereLuts.glsl`,
  `Global.glsl` or `TextureSet.glsl` breaks the mod's shaders at load. The offline check catches it
  only on a machine with the install present.
- **KSA's hot reload is not involved.** `GetModPath` runs on every compile through
  `OnIncludeResolved`, even though `ShaderReloader.EnableHotReloading` has no caller in build 5554.
- **glslang is not shaderc.** The check script inserts the include extension, uses a search path
  instead of absolute includes, and reports line numbers one past the file's own.

## See also

- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [Fireball light injection](./fireball-light-injection.md)
- [High-altitude burst sky](./high-altitude-burst-sky.md)
- [Repo static checks](./repo-static-checks.md)
- [StarMap entry and fault containment](./starmap-entry-and-fault-containment.md)
- [Check shaders offline: check-shaders.sh in a pre-push hook and CI, skipped without an install](../../../howto/shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/check-mod-shaders-offline-with-glslang-1.md) - how-to article demonstrating this feature
- [Include the game's shader library: a generated Shaders/Content/CoreAtmosphere.glsl pulling in atmosphere and texture headers](../../../howto/shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/include-the-game-shader-library-in-mod-glsl-1.md) - how-to article demonstrating this feature
- [Reload a mod shader: a reload command in a developer file bridge that an agent can drive](../../../howto/shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/reload-a-mod-shader-in-a-running-game-2.md) - how-to article demonstrating this feature

[^ksarmory-coreinclude]: src/KSArmory/Ksa/CoreShaderInclude.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-cloud-comp]: src/KSArmory/Shaders/KSArmoryCloud.comp
[^ksarmory-firelight-comp]: src/KSArmory/Shaders/KSArmoryFireLight.comp
[^ksarmory-check-shaders]: tools/check-shaders.sh
[^ksarmory-check-all]: tools/check-all.sh
[^ksarmory-gitignore]: .gitignore
[^starmap-modpatches]: StarMap.Core/Patches/ModPatches.cs
[^ksa-shaderreference]: KSA.ShaderReference (decompiled, build 2026.10.10.5554)
[^ksa-shaderreloader]: KSA.AssetReloader.ShaderReloader (decompiled, build 2026.10.10.5554)
[^ksa-compilerresolve]: RenderCore.Shaders.ShaderCompilerResolve (decompiled, build 2026.10.10.5554)
[^ksa-shaderc]: Brutal.ShaderCApi.ShaderC (decompiled, build 2026.10.10.5554)
[^ksa-utf8]: Brutal.Strings.Utf8 (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-textureset-glsl]: Core/Shaders/Common/TextureSet.glsl (build 2026.10.10.5554)
[^ksa-atmosphereluts-glsl]: Core/Shaders/Atmosphere/AtmosphereLuts.glsl (build 2026.10.10.5554)
[^ksa-global-glsl]: Core/Shaders/Common/Global.glsl (build 2026.10.10.5554)
