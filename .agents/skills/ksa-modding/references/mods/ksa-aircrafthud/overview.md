---
type: Mod
title: KSA_AircraftHUD
description: Fighter-style head-up display drawn with an ImGui draw list in a fullscreen window, styled by a ShaderExtensions ImGui window shader and backed by a canopy-glass post-process chain, with a custom font, an INI settings file and a ModMenu entry
mod: ksa-aircrafthud
tags: [hud, imgui, shaders, post-processing, shaderextensions, modmenu, font, ini, starmap]
repo: KSA_AircraftHUD
license: MIT
author: UriahGnu
loader: starmap
mod_id: AircraftHUD
---

KSA_AircraftHUD (manifest id `AircraftHUD`, author UriahGnu) draws a fighter-jet style HUD over the
flight view: pitch ladder, horizon line, flight path marker, aim point, heading tape, speed and
altitude tapes and boxes, and corner readouts for eccentricity, inclination, apoapsis, periapsis,
g-load, Mach, angle of attack and vertical speed. In the IVA camera it adds a "looking through a
canopy" look: the HUD itself is barrel-curved with chromatic fringing, and the scene behind it gets a
refracting, dirt-streaked, edge-blurred glass treatment with blue-noise grain. The scene treatment is
written for ShaderExtensions v0.4.0; the pinned ShaderExtensions rejects those passes (see
Compatibility).[^ah-readme] [^ah-main]

The useful lesson for modders is the split of work. Every HUD symbol is plain ImGui draw-list
geometry built in C#; no flight data goes to the GPU. ShaderExtensions supplies two hooks into the
renderer: an `ImGuiShader` that re-renders one ImGui window through a custom fragment shader, and a
chain of `PostProcessingShader` passes that run on the scene image before ImGui composites. The C#
side only pushes an on/off flag and a time value into those passes.

## Install footprint

| Item | Value |
|---|---|
| Mod folder | `Documents\My Games\Kitten Space Agency\mods\AircraftHUD\` (the build writes there directly) |
| Files shipped | `AircraftHUD.dll`, `mod.toml`, `HudAssests.xml`, `hud.ini`, `ModMenu.Attributes.dll`, `Fonts/HornetDisplay-Bold.ttf` and its license, `Shaders/*`, `Textures/*` ([AircraftHUD.csproj](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.csproj#L11-L36)) |
| `mod.toml` | `name="AircraftHUD"`, `version="0.2.1"`, `author="UriahGnu"`, `assets = [ "HudAssests.xml" ]`; no `[StarMap]` table and no dependency list ([mod.toml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/mod.toml))[^ah-modtoml] |
| Manifest id | `AircraftHUD`; the README says a first-run popup enables it[^ah-readme] |
| Harmony id | `AircraftHUD` |

With no `[StarMap]` table, StarMap uses the manifest id as `EntryAssembly` and loads
`AircraftHUD.dll` from the mod folder into its own load context
([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L48-L63)).[^starmap-runtimemod]
The game itself reads the `assets` key (`Mod.Assets`, `[TomlField("assets")]`) and loads each listed
file as an `AssetBundle` in `Mod.LoadAssetBundles`
([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L168)), which is how
`HudAssests.xml` (spelling as in the repo) reaches ShaderExtensions' custom XML element types.[^ksa-mod]

## Loader model and entry class

StarMap mod. The entry class is `AircraftHUD.HUD`, marked `[StarMapMod]`, in
[AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L18-L20).[^ah-main]

| Hook | Method | Fires | What the mod does there |
|---|---|---|---|
| `[StarMapImmediateLoad]` | `Init(Mod)` | Harmony prefix on `Mod.PrepareSystems`, during `ModLibrary.PrepareAll` in the `Program` constructor ([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22)) | `PatchAll`, reads `hud.ini`, sizes the layout from the main viewport, adds the TTF to the ImGui font atlas |
| `[StarMapBeforeGui]` | `DrawHudMenu(double)` | Prefix on `Program.OnDrawUiFrame`, only while the game UI is shown ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L14-L24)) | Settings window |
| Harmony postfix | `ProgramPatches.AfterOnFrameViewports` | Postfix on `Program.OnFrameEditor`: every frame that gets past `PrepareFrame` (not on its font-rebuild or window-close `Exit` frames), after the next physics step has been dispatched and after `ImGui.NewFrame`, before `OnFrameViewports` | Builds the HUD window, selects the ImGui shader, writes the post-process push constants |
| `[ModMenuEntry("Aircraft HUD")]` | `CreateModMenu()` | Called by the ModMenu mod inside its menu | Enable/disable and settings toggles |

`Program.OnFrame` calls `PrepareImGui`, `OnFrameEditor`, `OnFrameViewports` and only then
`OnDrawUiFrame`/`OnDrawUiViewports` under `if (DrawUI)`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2273)), so the HUD is
submitted before every game window and keeps drawing when the player hides the UI; see
[Always-on ImGui hook from a Program.OnFrameEditor postfix](features/always-on-onframeeditor-hook.md).[^ksa-program]
Both StarMap methods have the signatures StarMap's attributes require (`Init(Mod)` for
`[StarMapImmediateLoad]`, `DrawHudMenu(double)` for `[StarMapBeforeGui]`). The mod has no
`[StarMapUnload]`; StarMap's id-less `UnpatchAll` at shutdown removes the postfix anyway
([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L34)).[^starmap-core]

## Dependencies

| Dependency | How it is referenced | How it resolves at runtime |
|---|---|---|
| StarMap (`StarMap.API` 0.3.6) | NuGet in [AircraftHUD.props](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUD.props#L11-L13) | Supplied by the loader; `ModAssemblyLoadContext.Load` returns assemblies already in the default or game context first ([StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L24-L47))[^starmap-alc] |
| Harmony (`Lib.Harmony` 2.4.2) | NuGet in the props file | Supplied by StarMap |
| Game assemblies | `Brutal*.dll`, `KSA.dll`, `Planet*.dll`, `Tomlet.dll` from `$(KSADir)` with `Private="False"` | The running game's copies |
| ShaderExtensions (README: v0.4.0) | `<Reference Include="ShaderExtensions">` with a sibling-checkout `HintPath` | Not needed at runtime: no type from `ShaderExtensions.dll` is used. Coupling is by attribute name and asset XML (see [ShaderExtensions push constants and uniform buffers from C#](features/sx-push-constants-and-buffers.md)) |
| KittenExtensions (README: v0.4.0) | None in code | Required by ShaderExtensions, which registers `PostProcessingShader` and `ImGuiShader` as XML asset types through KittenExtensions' `KxAsset` mechanism (see [KittenExtensions custom XML asset types](../kittenextensions/features/custom-xml-asset-types.md)). The pinned ShaderExtensions requires the KittenExtensions Continued fork (mod id `KittenExtensionsContinued`), which cannot be installed next to the original KittenExtensions that the AircraftHUD README links[^sx-readme] |
| ModMenu (README: v0.2.1) | `ModMenu.Attributes.dll` committed in the repo, referenced by `HintPath` | Shipped next to `AircraftHUD.dll` (see [ModMenu entry with a vendored attributes DLL](features/modmenu-entry-vendored-attributes.md)) |
| Native libraries | None | |

None of these are declared in `mod.toml`, so StarMap does not order or gate AircraftHUD after them.

## Architecture

| Subsystem | File | Thread | Role |
|---|---|---|---|
| Entry, settings, menu | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L576-L934) | Main | INI load/save, layout constants, font load, settings window, ModMenu callback |
| Flight data | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L220-L263) | Main | Surface attitude, alpha/beta, vertical speed, gravity; Mach in `DrawHud` |
| HUD drawing | [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L956-L1341) | Main (ImGui) | Fullscreen window, draw-list primitives, clip rects, shader selection |
| ShaderExtensions glue | [ShaderExtensionsHelpers.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/ShaderExtensionsHelpers.cs) | Main | Duck-typed attributes, buffer structs, `SxImGui.CustomShader` |
| Asset bundle | [HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml) | Load time | Three post-process passes and one ImGui window shader |
| Shaders | `Shaders/*.frag`, `Shaders/*.glsl` | GPU | Glass blur, refraction, dirt, chromatic aberration, blue-noise dither, HUD curvature |

Data flow per frame: `PrepareFrame` applies the finished vehicle step and dispatches the next one;
the `OnFrameEditor` postfix reads the controlled `Vehicle` (the applied step) and its parent body, emits HUD geometry into the window's draw list,
tags that draw list with the `HudShader` marker, and writes `enabled`/`frame` into three push
constant structs. During `Program.RenderGame`, ShaderExtensions renders the tagged window offscreen,
runs `UI_PP_HUD.frag` over it, runs the three glass passes on the scene image, copies the result back,
and then the game draws its final composite and ImGui
([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L197-L225)).[^sx-patches]

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Fullscreen draw-list HUD](features/fullscreen-drawlist-hud.md) | ui | startup:immediate-load, frame:viewports |
| [Always-on ImGui hook from a Program.OnFrameEditor postfix](features/always-on-onframeeditor-hook.md) | patching | startup:immediate-load, frame:viewports |
| [Surface attitude and air data from the controlled vehicle](features/flight-data-from-vehicle.md) | flight-and-orbits | frame:viewports |
| [Curved, chromatic HUD window via an ImGuiShader](features/imgui-window-hud-shader.md) | shaders | frame:viewports, render:imgui |
| [Canopy-glass post-process chain](features/glass-post-process-chain.md) | shaders | startup:asset-load, frame:viewports, render:post-process |
| [Spatiotemporal blue-noise dithering](features/blue-noise-dithering.md) | shaders | frame:viewports, render:post-process |
| [ShaderExtensions push constants and uniform buffers from C#](features/sx-push-constants-and-buffers.md) | shaders | startup:starmap-before-main, frame:viewports, render:post-process |
| [Custom TTF font in the ImGui atlas](features/custom-hud-font.md) | ui | startup:immediate-load, frame:viewports |
| [INI settings file and settings window](features/ini-settings-and-settings-window.md) | persistence | startup:immediate-load, frame:draw-ui |
| [ModMenu entry with a vendored attributes DLL](features/modmenu-entry-vendored-attributes.md) | ui | frame:draw-ui |
| [Props-based build straight into the mods folder](features/props-based-build.md) | getting-started | none (build time) |

## Build, test and tooling

- `AircraftHUD.csproj` targets `net10.0` with unsafe code and `CopyLocalLockFileAssemblies`, imports
  `AircraftHUD.props`, and marks every content file `CopyToOutputDirectory=Always`.[^ah-csproj]
- The props file hard-codes `KSADir` (`E:\Games\Kitten Space Agency\`) and sets `OutputPath` to the
  user's mods folder, so a build is a deploy. ShaderExtensions is expected as a sibling checkout
  (`..\ShaderExtensions\Content\ShaderExtensions\ShaderExtensions.dll`). See
  [Props-based build straight into the mods folder](features/props-based-build.md).[^ah-props]
- No tests, CI or packaging scripts. The README lists .NET 10 and Visual Studio 2026.

## Compatibility

- The README targets KSA `ksa_v2026.7.8.4980` with StarMap 0.4.5, ModMenu 0.2.1, KittenExtensions
  0.4.0 and ShaderExtensions 0.4.0; `mod.toml` says `version="0.2.1"` while the README title says
  v0.3.0.[^ah-readme] [^ah-modtoml]
- Every game member the C# code touches still exists in build 2026.10.10.5554 (checked per feature).
- The three `PostProcessingShader` passes in the asset XML do not run on the pinned ShaderExtensions
  (`6a2eb66`, KSA 2026.10.7 compatible). Since its dynamic-rendering rewrite, `SubpassId` and
  `RequiresUniqueRenderpass` are rejected: `GlassBlurH` and `GlassBlurV` declare
  `RequiresUniqueRenderpass="true"` and `PostProcess` declares `SubpassId="0"`, so each pass still
  loads as an asset but is skipped with a logged error when the chain is built
  ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs#L14-L48)),
  and a shared time buffer now occupies binding 1, which shifts every extra binding the AircraftHUD
  shaders declare.[^sx-ppasset] [^sx-readme] Details in
  [Canopy-glass post-process chain](features/glass-post-process-chain.md#pitfalls-and-version-notes).
  The `ImGuiShader` asset is not subject to that check, so the curved HUD window shader still loads;
  its unused `HudTestBuffer` block is declared on a set the ImGui shader pipeline does not have (see
  [Curved, chromatic HUD window via an ImGuiShader](features/imgui-window-hud-shader.md#pitfalls-and-version-notes)).
- The HUD drawing itself (ImGui only) does not depend on ShaderExtensions and would still render
  without it, minus the shader effects (unverified at runtime).
- Even with a ported asset XML, the glass passes would not show with CMAA2 anti-aliasing selected on
  build 2026.10.10.5554, because the CMAA2 path composites the scene before ShaderExtensions' pre-ImGui
  chain runs (see [Canopy-glass post-process chain](features/glass-post-process-chain.md#pitfalls-and-version-notes)).
- Data bugs that hold on any build are listed in
  [Surface attitude and air data from the controlled vehicle](features/flight-data-from-vehicle.md#pitfalls-and-version-notes):
  the g-load subtracts gravity twice, a vehicle orbiting the sun makes the parent cast throw on every
  frame, inclination is shown in radians, imperial Ap/Pe mix feet and meters, speeds are scaled by
  0.07, and alpha/beta use inertial instead of surface velocity.

## Secondary sources

- [README.md](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/README.md): dependency versions, install steps and credits (AMPW helped with the
  post-process shaders; Dejvid's Avionics mod was the initial code reference).
- ShaderExtensions [README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md): the reference for `ImGuiShader`,
  `PostProcessingShader`, buffer and push-constant declarations; the copy of `SxImGui` and the
  attribute shims in this repo come from it.

[^ah-readme]: KSA_AircraftHUD/README.md
[^ah-modtoml]: KSA_AircraftHUD/mod.toml
[^ah-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^ah-csproj]: KSA_AircraftHUD/AircraftHUD.csproj
[^ah-props]: KSA_AircraftHUD/AircraftHUD.props
[^sx-readme]: ShaderExtensions/README.md
[^sx-ppasset]: ShaderExtensions PostProcessingShaderAsset.cs
[^sx-patches]: ShaderExtensions Patches.cs
[^starmap-runtimemod]: StarMap RuntimeMod.cs
[^starmap-alc]: StarMap ModAssemblyLoadContext.cs
[^starmap-core]: StarMap StarMapCore.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
