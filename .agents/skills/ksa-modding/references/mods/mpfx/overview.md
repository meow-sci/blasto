---
type: Mod
title: MPFX
description: Small post-processing mod by AMPW that declares a 32-pass color-grading chain as ShaderExtensions XML assets, feeds it from an ImGui settings window each frame and keeps profiles only as hand-copied XML
mod: mpfx
tags: [shaders, post-processing, color-grading, imgui, shaderextensions, kittenextensions, modmenu]
repo: MPFX
license: MIT
author: AMPW
loader: starmap
mod_id: MPFX
---

MPFX is a deliberately small post-processing mod by AMPW, the same author as ShaderExtensions and
KittenExtensions Continued. The README calls it "a very simple post processing mod" and "a small side
project of a side project", not a TUFX equivalent. Its README lists color balance (lift, gamma, gain), color
temperature, a YIQ hue shift, brightness, contrast, saturation, a color overlay, a vignette and five
experimental passes, and says every effect exists twice: once before the ImGui UI is drawn (game image
only) and once after (game plus UI).[^mpfx-readme] The asset file adds film grain and a separable
Gaussian blur that the README does not list yet, and the window keeps the experimental passes under a
"Fun stuff" header.[^mpfx-assets]

The interesting part for modders is how little engine code it needs. MPFX contains no Harmony patch and
no Vulkan code. The whole render side is one XML asset file of ShaderExtensions `PostProcessingShader`
and `GlobalPostShader` elements plus 30 fragment shaders. The C# side is one `[StarMapAfterGui]` method
that writes effect parameters into ShaderExtensions-owned uniform buffers and push constants and draws
the settings window.[^mpfx-main] [^mpfx-assets]

## Install footprint

| Item | Value |
|---|---|
| Mod folder | `Documents/My Games/Kitten Space Agency/mods/MPFX/`. The README recommends the local mods folder, where no `manifest.toml` edit is needed but a restart is needed after the first launch[^mpfx-readme] |
| Files shipped | `MPFX.dll`, `ModMenu.Attributes.dll`, `MPFXAssests.xml` (sic), `Shaders/*.frag` (30 files), generated `mod.toml` |
| `mod.toml` | Not checked in. [MPFX/CopyOutput.targets](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/CopyOutput.targets#L44-L60) writes it on every build: `name = "MPFX"`, `version = "v0.3.6"`, `author = "AMPW"`, `assets = [ "MPFXAssests.xml" ]`, `[StarMap] EntryAssembly = "MPFX"` and two `[[StarMap.ModDependencies]]` tables[^mpfx-copyoutput] |
| Manifest id | `MPFX` (`<ModName>` in [MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L4))[^mpfx-csproj] |
| Version | `VersionPrefix` 0.3.6 |
| Assets | `MPFXAssests.xml`, listed through `<ModManifestArray>assets</ModManifestArray>` on the `None` item. The README calls it `MPFXAssets.xml`, but the file name in the repo is `MPFXAssests.xml` |

## Loader model and entry class

StarMap mod. The entry class is `MPFX.MPFX`, marked `[StarMapMod]`, in
[MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L15-L16). It uses one StarMap hook and one ModMenu attribute.

| Hook | Method | Fires | What MPFX does there |
|---|---|---|---|
| `[StarMapAfterGui]` | `AfterGui(double dt)` | Harmony postfix on `Program.OnDrawUiViewports`, inside `if (DrawUI)` ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2264-L2275)) | Advances film grain time and writes push constants. When the dirty flag is set, writes every uniform buffer. Draws the settings window |
| `[ModMenuEntry("MPFX")]` | static `DrawMenu()` | Called by ModMenu when it draws its menu. The discovery mechanism lives in ModMenu, which is not in the reference set (unverified) | Draws a label and an "Open" button that sets `ShowWindow` |

The effects themselves run inside ShaderExtensions' patches of `Program.RenderGame`: a pre-ImGui
chain runs before the swapchain render pass that holds `RenderFinalComposite` and the ImGui draw, and a
post-ImGui chain runs after that pass ends ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L225)).[^sx-patches] [^ksa-program]

## Dependencies

| Dependency | How it is referenced | How it resolves at runtime |
|---|---|---|
| StarMap | `StarMap.API.dll` by HintPath from `$(KSAModsDir)StarMap/`, `Private=false` | Provided by the loader |
| KittenExtensions Continued | `KittenExtensions.dll` by HintPath (`Private=false`) and a `StarMapModDependency` `KittenExtensionsContinued` with `ImportedAssemblies = KittenExtensions`. MPFX also declares its own copy of the `KxAsset` attributes ([MPFX/Attributes.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Attributes.cs)) | Non-optional StarMap dependency. README requires >= v0.4.0 and warns that KX and KXC cannot be installed together[^mpfx-attributes] |
| ShaderExtensions | No compile-time assembly reference. A `StarMapModDependency` `ShaderExtensions` with `ImportedAssemblies = ShaderExtensions` (which MPFX's code never binds to), plus MPFX's own copies of the `SxUniformBuffer`/`SxPushConstant` attributes and lookup delegates in the `ShaderExtensions` namespace ([MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs)) | ShaderExtensions finds them by attribute name and fills the delegates. For the same reason CI never downloads ShaderExtensions[^mpfx-buffers] |
| ModMenu | NuGet `ModMenu.Attributes` 1.0.2. The DLL is copied to the output ([MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L53-L61)) | README lists ModMenu as required, but it is not declared as a StarMap dependency |
| Harmony | NuGet `Lib.Harmony` 2.4.2 | Not used in code; no `Harmony` call anywhere in the repo |
| Game assemblies | `Brutal*.dll`, `KSA.dll`, `Planet*.dll`, `Tomlet.dll` globbed from `$(KSADir)` with `Private=false` ([MPFX/KSADLLs.props](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/KSADLLs.props))[^mpfx-ksadlls] | The running game's copies |

## Architecture

| Subsystem | Files | Thread | Role |
|---|---|---|---|
| Effect chain declaration | [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) | Loaded at startup | 16 `PostProcessingShader` + 16 `GlobalPostShader` elements, `RenderPassId` 128-143, with buffer child elements |
| Shaders | `MPFX/Shaders/*.frag` | GPU | One fullscreen fragment shader per effect and side. Gaussian blur reuses one file for both sides |
| Buffer structs | `MPFX/Buffers/*.cs`, [MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs) | n/a | Blittable structs tagged `[SxUniformBuffer]`/`[SxPushConstant]` with static lookup delegates |
| Entry, upload, UI | [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs) | Main | `AfterGui`: parameter upload, settings window, profile tab, XML export |
| Profiles | [MPFX/MPFXProfile.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXProfile.cs), [MPFX/Serializer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Serializer.cs) | Main | `[KxAsset("MPFXProfile")]` asset type holding every setting, plus XML serialization for export[^mpfx-profile] [^mpfx-serializer] |

Data flow per frame: an ImGui widget edits a field of `MPFX.CurrentProfile` and sets `valuesChanged`.
At the end of the window, that ORs into the static `UpdateValues`. On the next `AfterGui`, the flag
makes MPFX write every effect's parameters, or a neutral value when the effect is off, through
`LookupSpan(KeyHash.Make("<buffer id>"))` into ShaderExtensions' persistently mapped uniform buffers.
Later in the same frame, `Program.RenderGame` records both chains, and every pass samples the previous
pass's output.

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Effect chain declared as RenderPassId-ordered post-process assets](features/effect-chain-assets.md) | shaders | startup:asset-load, render:post-process |
| [Shader contract for chain passes](features/effect-shader-contract.md) | shaders | render:post-process |
| [Uniform buffer and push constant structs bound by attribute name](features/duck-typed-buffer-structs.md) | shaders | startup:starmap-before-main, startup:asset-load |
| [Per-frame effect parameter upload](features/per-frame-parameter-upload.md) | shaders | frame:draw-ui, frame:render |
| [Effect settings window and ModMenu entry](features/effect-settings-window.md) | ui | frame:draw-ui |
| [Effect profiles as XML assets with clipboard export](features/xml-effect-profiles.md) | persistence | startup:asset-load, frame:draw-ui |
| [Color grading shader math](features/color-grading-math.md) | shaders | render:post-process |
| [Separable Gaussian blur with CPU-computed weights](features/separable-gaussian-blur.md) | shaders | frame:draw-ui, render:post-process |
| [Film grain and experimental effects](features/film-grain-and-experimental-effects.md) | shaders | frame:draw-ui, render:post-process |
| [Generated mod.toml and CI release pipeline](features/ci-release-pipeline.md) | testing-and-tooling | n/a (build time) |

## Build, test and tooling

- .NET 10 class library (`net10.0`), `AllowUnsafeBlocks`, nullable enabled. There are no tests.
- [MPFX/CopyOutput.targets](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/CopyOutput.targets) rebuilds `Release/MPFX/` after every build and writes `mod.toml` from MSBuild
  items. KittenExtensions Continued uses the same approach (see
  [MSBuild-generated mod.toml and deploy](../kittenextensions/features/msbuild-mod-toml-generation.md)).
- [MPFX/Properties/launchSettings.json](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Properties/launchSettings.json) starts `StarMap.exe` from the mods folder for debugging.[^mpfx-launch]
- Debug builds add a "Rebuild renderers" button that calls `Program.ScheduleRendererRebuild()`. That
  triggers ShaderExtensions' `RebuildRenderer` postfix, which rebuilds both chains.
- CI (GitHub Actions) builds against KSA DLLs cloned from a private repository and publishes GitHub and
  SpaceDock releases ([.github/workflows/build.yml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/.github/workflows/build.yml)).[^mpfx-ci]

## Compatibility notes

- No targeted KSA build is stated in the repo. CI writes the KSA version from the private DLL repo's
  `KSA_VERSION.txt` into the release body.
- MPFX depends on ShaderExtensions' `RenderGame` transpiler. That transpiler finds
  `ImGuiBackendVulkanImpl.RenderDrawData`, then the preceding `CommandBuffer.BeginRenderPass`, then
  the last `EndRenderPass`. In build 2026.10.10.5554, `RenderGame` still ends with
  `BeginRenderPass`, `RenderFinalComposite`, `RenderDrawData`, `EndRenderPass`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4948-L4951)), and the
  transpiler's hard-coded `ldloc.1`/`ldloc.0` (command buffer, frame resources) still match the IL
  local slots; a reordering of those locals would not make the patch fail. The base game has no
  generic post-process chain of its own.
- With CMAA2 anti-aliasing selected, `RenderAaPreparePasses` tonemaps the offscreen image into CMAA2's
  own target before the pre-ImGui hook runs, and the final blit reads that target, so the pre-ImGui
  half of every MPFX effect is computed but not displayed. Only the post-ImGui effects show
  ([current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs#L166-L177)). Read from the decomp,
  not confirmed in game.[^ksa-cmaa2]
- Only `RenderGame` is patched, so effects do not run in the vehicle editor (`RenderEditor`). Only the
  main window is processed ([README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md#L35-L40)).[^sx-readme]
- The README text about reordering "by changing the subpass" is stale. ShaderExtensions has removed
  subpasses, and order is now set only by `RenderPassId`.
- Settings are not saved. See [Effect profiles as XML assets with clipboard export](features/xml-effect-profiles.md).

## Secondary sources

- [README.md](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/README.md): effect list, default order, install notes, persistence warning.
- [README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md) of ShaderExtensions: the authoritative description of the asset types,
  bindings, time buffer, uniform buffers and push constants MPFX uses.
- [.github/copilot-instructions.md](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/.github/copilot-instructions.md): generic LLM coding guidelines only, nothing KSA-specific.[^mpfx-copilot]

[^mpfx-readme]: MPFX README.md
[^mpfx-main]: MPFX/MPFX.cs
[^mpfx-assets]: MPFX/MPFXAssests.xml
[^mpfx-copyoutput]: MPFX/CopyOutput.targets
[^mpfx-csproj]: MPFX/MPFX.csproj
[^sx-patches]: ShaderExtensions/Patches.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^mpfx-attributes]: MPFX/Attributes.cs
[^mpfx-buffers]: MPFX/Buffers.cs
[^mpfx-ksadlls]: MPFX/KSADLLs.props
[^mpfx-profile]: MPFX/MPFXProfile.cs
[^mpfx-serializer]: MPFX/Serializer.cs
[^mpfx-launch]: MPFX/Properties/launchSettings.json
[^mpfx-ci]: .github/workflows/build.yml
[^sx-readme]: ShaderExtensions README.md
[^ksa-cmaa2]: KSA.Rendering.PostProcessing.Cmaa2Renderer (decompiled, build 2026.10.10.5554)
[^mpfx-copilot]: .github/copilot-instructions.md
