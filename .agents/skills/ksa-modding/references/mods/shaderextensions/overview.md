---
type: Mod
title: ShaderExtensions
description: Shader infrastructure mod adding XML-declared fragment shaders with extra texture, uniform-buffer and push-constant bindings, per-window ImGui shaders, and RenderPassId-ordered post-processing chains before and after the ImGui pass via a Program.RenderGame transpiler
mod: shaderextensions
tags: [shaders, post-processing, vulkan, imgui, harmony, transpiler, kittenextensions, inter-mod, starmap]
repo: ShaderExtensions
license: MIT
author: AMPW
loader: starmap
mod_id: ShaderExtensions
---

ShaderExtensions (by AMPW, GitHub `AMPW-german`; based on tsholmes' KittenExtensions) is the shader
layer other KSA mods build on. It adds no visible content of its own. Instead it gives mods four XML
asset types and a C# contract:[^sx-readme]

| Asset | What a mod gets |
|---|---|
| `<ShaderEx>` | A fragment shader with extra `<TextureBinding>` textures, mod-defined uniform buffers and one push constant |
| `<PostProcessingShader>` | A fullscreen pass on the HDR 3D scene, before tonemapping and before ImGui |
| `<GlobalPostShader>` | A fullscreen pass on the final swapchain image, after ImGui |
| `<ImGuiShader>` | A fragment shader applied to one ImGui window |

MPFX (an effects stack) and KSA_AircraftHUD (an ImGui HUD with shader effects) are the known
consumers ([MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L1-L12),
[HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L1-L27)).[^mpfx-assets] [^hud-assets]

## Install footprint

| Item | Value |
|---|---|
| Manifest id | `ShaderExtensions` (README asks players to add it to `manifest.toml`) |
| Folder | `ShaderExtensions/`, extracted into the game's `Content` folder per the README; StarMap also accepts the local mods folder ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L50))[^sm-runtimemod] |
| Files | `ShaderExtensions.dll`, its symbols and a generated `mod.toml`; no XML, shaders or textures |
| `mod.toml` | Generated at build time: `name`, `version = "v1.0.5"`, `author = "AMPW"`, `[StarMap] EntryAssembly = "ShaderExtensions"`, and `[[StarMap.ModDependencies]] ModId = "KittenExtensionsContinued"`, `Optional = false`, `ImportedAssemblies = [ "KittenExtensions" ]` ([ShaderExtensions/CopyOutput.targets](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/CopyOutput.targets#L45-L97), [ShaderExtensions/ShaderExtensions.csproj](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.csproj#L68-L72))[^sx-copyoutput] [^sx-csproj] |
| Harmony id | `ShaderExtensions` |


## Loader model and entry class

StarMap mod. `ShaderExtensions.ShaderExtensions` is the `[StarMapMod]` class; its only lifecycle method
is `[StarMapBeforeMain] Setup()`, which runs `Harmony.PatchAll()`
([ShaderExtensions/ShaderExtensions.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderExtensions.cs#L6-L15)).[^sx-entry] All
behavior hangs off the Harmony patches in
[ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs):[^sx-patches]

| Hook | Target | Fires | What it does |
|---|---|---|---|
| `[StarMapBeforeMain]` | `Setup()` | While StarMap initializes the mod in `RuntimeMod.InitializeMod`, before the game's entry point ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L156-L159), [StarMap/GameSurveyer.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/GameSurveyer.cs#L25-L56)) | `PatchAll` |
| Harmony prefix | `Program.Main` | Game entry, after every mod's `[StarMapBeforeMain]` and StarMap's own patches, before the `Program` constructor (closest lifecycle value `startup:starmap-before-main`) | `AssetEx.Init`: XML element injection, consumer struct registration, serializer rebuild |
| Harmony prefix | `Program.RenderGame` | Each flight frame, start of command recording (`frame:render`) | Updates the shared time buffer |
| Harmony transpiler | `Program.RenderGame` | Each flight frame | Replaces the swapchain `BeginRenderPass` with `ImGuiPreRender` (ImGui window shaders, pre-ImGui chain) and inserts the post-ImGui chain after `EndRenderPass` |
| Harmony postfix | `Program.RebuildRenderer` | After any renderer rebuild (`frame:prepare`, `frame:render` or `frame:post-render`) | Rebuilds both chains |

No `[StarMapAllModsLoaded]`, GUI or unload hooks are used; there is no shutdown cleanup. At exit StarMap's
`StarMapCore.DeInit` calls `Harmony.UnpatchAll()` with no id, which removes these patches along with
every other mod's before any `[StarMapUnload]` method runs
([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)).[^sm-gamesurveyer] [^sm-core]

## Dependencies

| Dependency | How it is referenced | How it resolves |
|---|---|---|
| StarMap | `StarMap.API.dll` hint path under `$(KSAModsDir)StarMap/`, `Private=False` | Provided by the loader; StarMap's load context returns already-loaded assemblies ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs)) |
| Harmony | NuGet `Lib.Harmony` 2.4.2 | Provided by StarMap at runtime |
| KittenExtensions Continued | `KittenExtensions.dll` hint path, StarMap dependency `KittenExtensionsContinued` | Required: its `Program.Main` prefix registers the four asset types from the `[KxAsset]` attributes ShaderExtensions declares itself ([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L13-L18), [ShaderExtensions/Attributes.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Attributes.cs#L8-L15)); KXC replaces the original KittenExtensions under a new mod id but keeps the `KittenExtensions` assembly name (KittenExtensions [README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L6)); the ShaderExtensions README warns that the two cannot be installed together |
| Game assemblies | `Brutal*.dll`, `KSA.dll`, `Planet*.dll`, `Tomlet.dll` from `$(KSADir)`, `Private=False` | The running game |
| ModMenu, native libraries | None | None |

Dependents link to ShaderExtensions only through XML and copied attribute declarations, not by
assembly reference (MPFX declares a StarMap dependency on it,
[MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L74-L81); KSA_AircraftHUD copies the helper and
attributes) ([ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs#L12-L46)).[^sm-modalc] [^kx-patches] [^sx-attributes] [^kx-readme] [^hud-helpers] [^mpfx-csproj]

## Architecture

| Subsystem | Files | Thread | Role |
|---|---|---|---|
| Asset and XML layer | `AssetEx.cs`, `Attributes.cs`, `ShaderEx.cs`, `ImGuiShader/ImGuiShaderReference.cs`, `PostProcessing/*/*Asset.cs` | Main (startup) | Element registration, `ShaderEx` and asset classes |
| Binding layer | `ShaderBinding.cs`, `UniformBufferEx.cs`, `PushConstantEx.cs`, `SharedTimeBuffer.cs` | Thread pool (`ModLibrary.Bind`), main (updates) | Textures, mod buffers, push constants, time buffer; descriptor pool, layout and writes |
| Post-processing | `PostProcessing/PostProcessingChain.cs`, `PostProcessingPassRenderer.cs`, input/output references, `PreImgui/PostProcessingHandler.cs`, `PostImgui/GlobalPostShaderHandler.cs` | Main (recording) | Two chains of fullscreen passes, cross-pass references, copies |
| ImGui window shaders | `ImGuiShader/ImGuiRenderers.cs`, `ImGuiBaseRenderer.cs`, `ImGuiPostRenderer.cs` | Main (recording) | Offscreen replay of a tagged window, custom pass, draw-list swap |
| Patches | `Patches.cs`, `ShaderExtensions.cs` | Main | Entry, transpiler, rebuild and time hooks |
| Deprecated | `GaugeShader/*.cs` (commented out) | | `<GaugeCanvasEx>` gauge post-processing, removed for lack of a test case ([Deprecated.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/Deprecated.md), [ShaderExtensions/GaugeShader/GaugeCanvasEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/GaugeShader/GaugeCanvasEx.cs#L8-L31)) |

Where each piece lands in a flight frame, all inside `Program.RenderGame` command recording
(`frame:render`) unless noted
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646-L4955)):[^sx-deprecated] [^sx-gaugecanvasex] [^ksa-program]

| Order | Game step | ShaderExtensions |
|---|---|---|
| `frame:draw-ui` | Mod ImGui code | Consumers tag windows and write buffers / push constants |
| `frame:imgui-render` | `ImGui.Render()` | |
| start of `RenderGame` | | Prefix: time buffer update |
| scene passes | opaque scene, atmosphere, translucency, bloom, orbit lines, gizmos and grid end up in `_offscreenTarget` (RGBA16F) | |
| line 4947 | `RenderAaPreparePasses` (CMAA2 LDR copy and edge passes) | |
| line 4948 | `BeginRenderPass(_mainPass)` | **Replaced:** ImGui window shaders, pre-ImGui chain on `_offscreenTarget`, then the original call |
| line 4949 | `RenderFinalComposite` (tonemap or CMAA2 blit) | |
| line 4950 | `ImGuiBackend.Vulkan.RenderDrawData` | Draws the substituted window images |
| line 4951 | `EndRenderPass` | **Inserted after:** post-ImGui chain on the swapchain image |
| `frame:post-render` | submit, present; `RebuildRenderer` if requested | Postfix: chain rebuild |

The base game has no generic post-processing chain; its final composite precedes the ImGui draw in the
same render pass, so both mod stages sit just outside that pass.

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [ShaderEx asset with extra descriptor bindings](features/shaderex-extra-bindings.md) | shaders | startup:asset-load, frame:render |
| [Shared shader time uniform buffer](features/shared-time-uniform-buffer.md) | shaders | frame:render |
| [Mod-defined uniform buffer bindings](features/custom-uniform-buffers.md) | shaders | startup:starmap-before-main, startup:asset-load, frame:draw-ui, frame:render |
| [Mod-defined push constants](features/push-constants.md) | shaders | startup:starmap-before-main, startup:asset-load, frame:draw-ui, frame:render |
| [RenderPassId-ordered fullscreen pass chain](features/post-processing-pass-chain.md) | shaders | frame:render, render:post-process |
| [Cross-pass image references](features/cross-pass-image-references.md) | shaders | frame:render, render:post-process |
| [Pre-ImGui post-processing on the HDR scene image](features/pre-imgui-post-processing.md) | shaders | frame:render, render:post-process |
| [Post-ImGui global post shaders on the swapchain image](features/post-imgui-global-post-shaders.md) | shaders | frame:render, render:post-process |
| [Per-window ImGui post shader](features/imgui-window-post-shader.md) | shaders | frame:draw-ui, frame:render, render:imgui |
| [RenderGame transpiler around the swapchain render pass](features/rendergame-render-pass-transpiler.md) | patching | startup:starmap-before-main, frame:render, render:post-process, render:imgui |
| [Rebuilding mod GPU resources with the game renderer](features/renderer-rebuild-hook.md) | rendering | frame:prepare, frame:render, frame:post-render |
| [Tagging an ImGui window with a draw-list marker callback](features/imgui-draw-list-marker-callback.md) | ui | frame:draw-ui, frame:render, render:imgui |
| [Custom XML asset types and injected child elements](features/xml-asset-types-and-element-injection.md) | assets | startup:starmap-before-main, startup:asset-load |
| [Attribute duck typing as an inter-mod API](features/duck-typed-attribute-api.md) | external-io | startup:starmap-before-main |
| [mod.toml generated by MSBuild with a StarMap dependency](features/generated-mod-toml.md) | getting-started | none (build time) |
| [CI build against private game DLLs with GitHub and SpaceDock releases](features/ci-build-and-release.md) | testing-and-tooling | none (CI) |

## Build, test and tooling

- .NET 10 class library, `AllowUnsafeBlocks`, nullable enabled, version `1.0.5` with a git-hash
  informational version. `dotnet build` needs `/p:KSADir=` and `/p:KSAModsDir=`; output and `mod.toml`
  go to `Release/ShaderExtensions/` (see
  [mod.toml generated by MSBuild](features/generated-mod-toml.md)).[^sx-csproj]
- GitHub Actions builds every push and PR against privately hosted game DLLs and releases to GitHub and
  SpaceDock on a version bump (see [CI build and release](features/ci-build-and-release.md)).[^sx-buildyml]
- No tests. `Properties/launchSettings.json` launches StarMap for debugging.
- `.github/copilot-instructions.md` records the project rules, including the single shared time buffer
  at binding 1.[^sx-copilot]

## Compatibility

- **Target build.** The README names no KSA build. The pinned commit merges the branch
  `fix/KSA_v2026.10.7.5541`, so this ref targets 2026.10.7.5541; earlier branches name
  2026.9.7.5402 and 2026.9.10.5438 (git history). Claims here were checked against the decompiled build
  2026.10.10.5554, where the transpiler anchors and the local indices it hard-codes still hold.
- **Breaking change: no subpasses.** Since the switch to dynamic rendering for KSA 2026.8.5.5168,
  `SubpassId` and `RequiresUniqueRenderpass` are rejected (shader skipped with an error) and every
  shader samples its input through a `sampler2D`; `subpassInput` / `subpassLoad` are gone. KSA_AircraftHUD
  at its pinned ref still declares both attributes on its post-processing shaders, so those are skipped
  ([HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L7-L19)); MPFX uses only `RenderPassId`.
- **Breakage pattern on game updates:** the `RenderGame` IL shape (anchors, locals 0 and 1), private
  members read by reflection (`Program._offscreenTarget`, four `ImGuiBackendVulkanImpl` members,
  `ShaderReference.Shader` setter), and `GlobalShaderBindings`, whose API moved from a dynamic offset to per-frame descriptor sets
  (the 5541 fix branch replaced `DynamicOffset` and the `DescriptorSet` property) ([current/decomp/KSA/GlobalShaderBindings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GlobalShaderBindings.cs#L47-L59)).
  The compatibility plan lists the same risks.[^sx-compatplan] [^ksa-globalshaderbindings]
- **Vulkan correctness notes from reading build 5554** (not reproduced in game): the post-pass
  dynamic-offset count is one too many; the pre-ImGui copy-back leaves the offscreen image in a transfer
  layout when the composite samples it; the post-ImGui copy writes into swapchain images created without
  `TRANSFER_DST` usage ([current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L715)); and with
  CMAA2 enabled the pre-ImGui result is not what the final blit reads
  ([current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.PostProcessing/Cmaa2Renderer.cs#L166-L177)).
  Two more: the reflection helper meant to read `GlobalShaderBindings._currentFrame` asks for a field
  named `currentFrame`, so it always yields 0; that zero lands on the first dynamic buffer of set 1
  (set 0 has none), which needs offset 0, so the bug cancels out;
  and an `<ImGuiShader>` fragment push constant is laid out at offset 0, overlapping the 32-byte vertex
  range that carries the window rectangles. Details are in the feature pages.[^core-renderer] [^ksa-cmaa2]
- **Scope:** only frames recorded by `RenderGame` (flight scene); the vehicle editor (`RenderEditor`) is
  not patched. The post-processing chains cover the main window only: secondary game viewports and ImGui
  platform windows (drawn after submit in `PostRender`) are not post-processed. ImGui window shaders
  scan every ImGui viewport, so a tagged window in a platform window is rewritten too.
- **Deprecated:** `<GaugeCanvasEx>` gauge post-processing, commented out after the game's gauge rework
  into `GaugeBundle`.[^sx-deprecated]

## Secondary sources

- [README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md): asset formats, binding numbers, time buffer, push constant and uniform
  buffer contracts, ImGui helper, limitations and the subpass breaking change.
- [KSA-2026.9.7-compatibility-plan.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/KSA-2026.9.7-compatibility-plan.md): the author's analysis of
  StarMap timing, the `Program.Main` prefix, transpiler fragility and missing shutdown cleanup.
- [Deprecated.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/Deprecated.md): the removed `<GaugeCanvasEx>` asset and its binding layout.
- [.github/copilot-instructions.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/.github/copilot-instructions.md): project rules for contributors
  and agents.
- MPFX's [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L120-L221) is the most complete consumer example of
  per-frame buffer and push-constant updates.[^mpfx-main]

[^sx-readme]: ShaderExtensions/README.md
[^mpfx-assets]: MPFX/MPFXAssests.xml
[^hud-assets]: KSA_AircraftHUD/HudAssests.xml
[^sm-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^sx-copyoutput]: ShaderExtensions/CopyOutput.targets
[^sx-csproj]: ShaderExtensions/ShaderExtensions.csproj
[^sx-entry]: ShaderExtensions/ShaderExtensions.cs
[^sx-patches]: ShaderExtensions/Patches.cs
[^sm-gamesurveyer]: StarMap/GameSurveyer.cs
[^sm-modalc]: StarMap.Core/ModRepository/ModAssemblyLoadContext.cs
[^kx-patches]: KittenExtensions/Patches.cs
[^sx-attributes]: ShaderExtensions/Attributes.cs
[^kx-readme]: KittenExtensions/README.md
[^hud-helpers]: KSA_AircraftHUD/ShaderExtensionsHelpers.cs
[^mpfx-csproj]: MPFX/MPFX.csproj
[^sx-deprecated]: ShaderExtensions/Deprecated.md
[^sx-gaugecanvasex]: ShaderExtensions/GaugeShader/GaugeCanvasEx.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^sx-buildyml]: ShaderExtensions/.github/workflows/build.yml
[^sx-copilot]: ShaderExtensions/.github/copilot-instructions.md
[^sx-compatplan]: ShaderExtensions/KSA-2026.9.7-compatibility-plan.md
[^ksa-globalshaderbindings]: KSA.GlobalShaderBindings (decompiled, build 2026.10.10.5554)
[^core-renderer]: Core.Renderer (decompiled, build 2026.10.10.5554)
[^ksa-cmaa2]: KSA.Rendering.PostProcessing.Cmaa2Renderer (decompiled, build 2026.10.10.5554)
[^mpfx-main]: MPFX/MPFX.cs
[^sm-core]: StarMap.Core/StarMapCore.cs
