---
type: Mod
title: purrTTY
description: In-game terminal emulator for KSA that hosts real shells (ConPTY, POSIX pty, in-game console, other mods' shells) on libghostty-vt and renders them as ImGui windows or as textures on world-space quads
mod: purrtty
tags: [terminal, imgui, vulkan, pty, native, inter-mod, render-to-texture, harmony]
repo: purrtty
license: MIT
author: purrTTY Project (meow sci)
loader: starmap
mod_id: purrTTY
---

purrTTY puts real terminals inside Kitten Space Agency. Press F12 (rebindable) or use the purrTTY
menu and a terminal window opens over the game, running PowerShell, cmd or a WSL distribution on
Windows, any shell from `/etc/shells` on Linux and macOS, the KSA developer console as a shell, or a
shell another mod provides (gatOS adds an SSH session into its Linux VM). Windows have tabs, 18
color themes, Nerd Fonts, translucency, click-through lock mode and saved layouts. Terminals can also
be placed in the 3D world on a part of the rocket or on a camera-fixed billboard, rendered into a
texture. purrTTY does not emulate a terminal itself: all VT parsing is delegated to libghostty-vt,
Ghostty's engine, through a vendored native binding.[^readme][^claude-md]

The mod is published by meow sci (`mod.toml` author "purrTTY Project") under the MIT license. It is
large: 184 C# files and about 38k lines in seven production projects and four test projects, plus prebuilt native libraries.

## Install footprint

`purrTTY.GameMod/mod.toml` (version stamped by CI on release):[^mod-toml]

```toml
# purrTTY.GameMod/mod.toml L1-L12
name = "purrTTY"
description = "A terminal emulator for KSA with mostly VT100/xterm compatibility"
version = "0.1.0"
author = "purrTTY Project"

[StarMap]
EntryAssembly = "purrTTY.GameMod"
# ...
ExportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
```

The `ExportedAssemblies` line documents the inter-mod ABI but has no effect at the pinned StarMap,
which never reads it; consumers get the shared assemblies by listing them in their own
`ImportedAssemblies` (see Compatibility notes).

The deployed `purrTTY/` folder, written by the GameMod build's
`CopyCustomContent` target:[^gamemod-csproj]

| Item | Purpose |
|---|---|
| `mod.toml`, `purrTTY.GameMod.deps.json` | StarMap manifest and dependency map |
| `purrTTY.GameMod.dll`, `purrTTY.Display.dll`, `purrTTY.Terminal.dll`, `purrTTY.CustomShells.dll`, `purrTTY.CustomShellContract.dll`, `purrTTY.Logging.dll` | The mod's assemblies |
| `Ghostty.Vt.dll`, `ModMenu.Attributes.dll`, `Tomlyn.dll`, `Microsoft.Extensions.Logging.Abstractions.dll`, `StbImageSharp.dll` | Third-party managed dependencies |
| `ghostty-vt.dll`, `libghostty-vt.so`, `libghostty-vt.dylib` | libghostty-vt for win-x64, linux-x64, osx-arm64 |
| `TerminalFonts/` (15 `.iamttf` Nerd Fonts and one `.otf`), `TerminalThemes/` (18 alacritty TOML schemes) | Assets |
| `LICENSE`, `THIRD-PARTY-NOTICES.md`, `third-party-licenses/` | License obligations |

No XML game assets, GLB models or `mod.toml` `assets` entries. Runtime state lives in
`Documents/My Games/Kitten Space Agency/.purrTTY/` (`purrtty.toml`, `themes/`, `layouts/`,
`startup-command-history.toml`).

## Loader model and entry class

purrTTY is a StarMap mod with one entry class, `purrTTY.GameMod.TerminalMod` (`[StarMapMod]`).[^terminal-mod]

| Hook | When it fires (verified) | What purrTTY does |
|---|---|---|
| `[StarMapImmediateLoad]` `OnImmediateLoad()` | Would fire from a prefix on `KSA.Mod.PrepareSystems`, but the pinned StarMap only registers methods with the signature `void M(KSA.Mod)`; this parameterless method fails `IsValidSignature` and is never called[^starmap-api-attrs] | Logs only, so nothing is lost |
| `[StarMapAllModsLoaded]` `OnFullyLoaded` | Harmony postfix on `ModLibrary.LoadAll`[^starmap-library-patches]. The renderer and the ImGui context already exist; the main viewport and `Program.OffscreenTarget` are created only after `LoadAll` returns, so no in-world GPU object is built here[^ksa-program] | `Patcher.patch()` (six patches, each isolated), `InitializeTerminal` (config path, toggle hotkey, fonts, custom-shell discovery, background shell detection, controller, menus, theme dialog), in-world coordinator, layout manager |
| `[StarMapAfterGui]` `OnAfterUi(dt)` | Postfix on `Program.OnDrawUiViewports`, inside `if (DrawUI)` between `ImGui.NewFrame` and `ImGui.Render`, after the next physics step has been dispatched; skipped in frames where the HUD is hidden[^starmap-program-patcher][^ksa-program] | Hotkey modal, theme dialog, layouts dialog, toggle hotkey, `controller.Update` + `Render` (every HUD-visible frame, also while the terminal itself is hidden), in-world `OnAfterGui` (off-screen render and submit) |
| `[StarMapUnload]` `Unload` | `ModLoader.Dispose` at game exit, after the game's own teardown (device destroyed) and after `StarMapCore.DeInit` has already removed every Harmony patch with `UnpatchAll()`[^starmap-loader][^starmap-core] | `UnpatchAll("purrTTY")` (a no-op by then), reset patch statics, dispose in-world (no GPU calls) and controller |
| `[ModMenuEntry("purrTTY")]` `DrawMenu` (static) | Called by the ModMenu mod when installed | Same menu content as the fallback |

Harmony patches (all applied in `OnFullyLoaded`):[^patcher]

| Patch | Target | Kind |
|---|---|---|
| `Patch01` | `KSA.Program.OnKey` | prefix, required |
| `Patch03_HotkeyGuard` | `KSA.GameSettings.OnKeyAll` | prefix, required |
| `Patch04_MouseBindingGate` | private `KSA.Program.DispatchKeyEvent` | prefix, required |
| `Patch02` | `KSA.Program.DrawProgramMenusHook` | postfix, optional |
| `ConsoleWindowPrintPatch` | `Brutal.ImGuiApi.Abstractions.ConsoleWindow.Print(ReadOnlySpan<char>, ImColor8, int)` | postfix, optional |
| `RenderTranslucencyPassPatch` | `KSA.SuperMeshRenderSystem.RenderTranslucencyPass` | postfix, optional |

## Dependencies

| Dependency | How it is resolved |
|---|---|
| StarMap (`StarMap.API` 0.3.6, `PrivateAssets=all`) | Loader-supplied; not shipped |
| Harmony (`Lib.Harmony` 2.4.2, `PrivateAssets=all`) | Loader-supplied; not shipped |
| ModMenu (optional; `ModMenu.Attributes` 1.0.1) | Attribute assembly shipped; menu falls back to a `DrawProgramMenusHook` postfix when `ModLibrary.Find("ModMenu")` is null |
| libghostty-vt (native, pinned ghostty commit plus three patches) | Shipped for three RIDs; loaded by a module-initializer `DllImportResolver`[^vt-readme] |
| Tomlyn 2.3.0, StbImageSharp 2.30.15, Microsoft.Extensions.Logging.Abstractions 10.0.0 | Shipped beside the DLLs |
| KSA reference assemblies (`KSA`, `Brutal.*` incl. `Brutal.Vulkan*`, `Brutal.ShaderC`, `Planet.Render.Core`) | Compile-time only (`Private=false`) |

No KittenExtensions or ShaderExtensions dependency. gatOS declares an optional dependency on
purrTTY with `ImportedAssemblies` for the shell contract.[^gatos-mod-toml]

## Architecture

Three layers with a renderer-neutral seam, plus game integration:[^claude-md]

| Project | Role |
|---|---|
| `vendor/Ghostty.Vt` | Vendored, pruned, patched libghostty-vt binding and the three native libraries |
| `purrTTY.Terminal` | Headless backend: `ITerminalSurface`/`TerminalFrame`, `GhosttyTerminalSurface`, sessions, ConPTY and POSIX pty process managers, custom-shell bridge, OSC sidecar, kitty image decoding |
| `purrTTY.CustomShellContract` | Published inter-mod ABI: `ICustomShell`, `CustomShellRegistry`, base shells |
| `purrTTY.CustomShells` | `GameConsoleShell` |
| `purrTTY.Logging` | `ModLog` Brutal log category |
| `purrTTY.Display` | ImGui frontend: multi-window controller, `TerminalWindow`, `FrameGridRenderer`, fonts, themes, layouts data, kitty textures, the in-world content renderer |
| `purrTTY.GameMod` | Entry class, Harmony patches, menus, dialogs, in-world Vulkan subsystem, layout orchestration |

**Data flow.** PTY reader threads hand bytes to `Surface.Write` (bounded inbox). Every frame in
which the HUD is drawn, inside `StarMapAfterGui`, each window's `Surface.BuildFrame()` feeds the engine, produces a dirty-aware
`TerminalFrame`, and `FrameGridRenderer` paints it into the window's ImGui draw list. Keyboard and
mouse are read from ImGui IO, encoded by the engine and queued to a PTY writer thread.

**Threads.** Main thread for all engine, ImGui and Vulkan work; per session an output pump, an
input writer, and on Unix an exit waiter; pool threads for session start and teardown; one
background task for shell detection. See [PTY threads to frame tick handoff](features/pty-threads-to-frame-tick-handoff.md).

**Rendering.** 2D terminals use only ImGui draw lists; the one Vulkan path in 2D is kitty image
textures registered with KSA's ImGui backend. In-world terminals own the rest of the Vulkan code: an
off-screen image, render pass and framebuffer, a second ImGui context and `ImGuiBackendVulkanImpl`,
a command pool with a fence ring and an extra queue submit (all in `frame:draw-ui`), a
dynamic-rendering quad pipeline with a runtime-compiled fragment shader, and a postfix on
`SuperMeshRenderSystem.RenderTranslucencyPass` that records the quads into the scene command buffer
(`frame:render`, `render:translucency`). That postfix fires at every call site of the pass: once per
visible secondary viewport (`Program.RenderViewport`), then for the main view (`Program.RenderGame`),
or in the editor (`Program.RenderEditor`).
The grid is not drawn with a custom glyph pipeline: `plans/LIBGHOSTTY_VULKAN_PLAN.md` describes one,
conditional on go-ahead, and it is not implemented.[^vulkan-plan]

## Features

| Feature | Category | Lifecycle |
|---|---|---|
| [Terminal grid drawn with ImGui draw lists](features/terminal-grid-drawlist-renderer.md) | ui | frame:draw-ui, frame:imgui-render, render:imgui |
| [Bundled Nerd Fonts loaded into the ImGui atlas](features/nerd-font-loading-into-imgui-atlas.md) | ui | startup:all-mods-loaded, frame:draw-ui |
| [Multi-window terminals with tabs, hidden chrome and grid snap](features/terminal-window-chrome-tabs-and-resize-snap.md) | ui | frame:draw-ui |
| [ImGui keyboard and mouse to terminal input](features/imgui-keyboard-mouse-to-vt-input.md) | ui | frame:input, frame:draw-ui |
| [Gate game input while a terminal is focused](features/gate-game-input-while-terminal-focused.md) | patching | frame:input, frame:draw-ui |
| [Rebindable toggle hotkey with a capture modal](features/rebindable-toggle-hotkey.md) | ui | frame:draw-ui |
| [Game menu with a ModMenu entry and a DrawProgramMenusHook fallback](features/game-menu-with-modmenu-fallback.md) | ui | frame:draw-ui, startup:all-mods-loaded, thread:background |
| [Click-through lock mode with a refocus hot zone](features/click-through-lock-mode-hot-zone.md) | ui | frame:draw-ui |
| [Kitty graphics images as ImGui Vulkan textures](features/kitty-graphics-as-imgui-vulkan-textures.md) | rendering | frame:draw-ui, render:imgui |
| [Filterable combo and form-table widgets for Brutal ImGui](features/imgui-filter-combo-and-form-widgets.md) | ui | frame:draw-ui |
| [Vendored libghostty-vt binding as the terminal engine](features/vendored-libghostty-vt-binding.md) | external-io | frame:draw-ui |
| [Per-platform native library resolver for a mod's AssemblyLoadContext](features/per-platform-native-library-resolver.md) | getting-started | startup:all-mods-loaded |
| [Headless terminal surface with a renderer-neutral frame snapshot](features/headless-terminal-surface-and-frame-snapshot.md) | external-io | frame:draw-ui, thread:background |
| [Hosting Windows shells in a ConPTY from inside the game](features/conpty-shell-hosting.md) | external-io | thread:background, frame:draw-ui |
| [POSIX pty shells spawned without fork](features/posix-pty-spawn-without-fork.md) | external-io | thread:background, frame:draw-ui |
| [Handing PTY threads' output to the game frame](features/pty-threads-to-frame-tick-handoff.md) | external-io | frame:draw-ui, thread:background |
| [The KSA game console as a terminal shell](features/game-console-shell.md) | external-io | frame:draw-ui, thread:background |
| [Shell detection and launch command resolution](features/shell-detection-and-command-resolution.md) | external-io | startup:all-mods-loaded, thread:background, frame:draw-ui |
| [OSC 52 clipboard writes into the game clipboard](features/osc52-clipboard-sidecar.md) | external-io | frame:draw-ui |
| [Inter-mod shell contract shared through StarMap assembly imports](features/custom-shell-contract-via-assembly-imports.md) | external-io | startup:starmap-before-main, startup:all-mods-loaded |
| [Custom shell registry, lifecycle and PTY bridge](features/custom-shell-registry-and-lifecycle.md) | external-io | startup:all-mods-loaded, frame:draw-ui, thread:background |
| [Logging through a Brutal LogCategory in a shared assembly](features/brutal-logging-category.md) | getting-started | startup:all-mods-loaded |
| [TOML settings and alacritty-format themes](features/toml-themes-and-settings.md) | persistence | startup:all-mods-loaded, frame:draw-ui |
| [Named terminal registry and per-target theme dialog](features/named-terminal-registry-and-theme-dialog.md) | ui | frame:draw-ui |
| [Saved terminal layouts with startup commands](features/saved-terminal-layouts.md) | persistence | frame:draw-ui |
| [Failure-isolated Harmony patching with reload-safe statics](features/failure-isolated-harmony-patching.md) | patching | startup:all-mods-loaded |
| [One-zip cross-platform build, deploy and release pipeline](features/cross-platform-build-and-release.md) | testing-and-tooling | event:build |
| [Headless test suites for a KSA mod](features/headless-test-suites.md) | testing-and-tooling | event:test |
| [KSA upgrade coupling review skill](features/ksa-upgrade-coupling-review-skill.md) | testing-and-tooling | event:game-update |
| [Render-to-texture with a second ImGui context and Vulkan backend](features/offscreen-imgui-context-render-to-texture.md) | rendering | frame:draw-ui, startup:all-mods-loaded |
| [World-space textured quad pipeline with a runtime-compiled shader](features/world-space-quad-pipeline-custom-shader.md) | rendering | frame:draw-ui, render:translucency |
| [Injecting world-space draws after KSA's translucency pass](features/draw-after-translucency-pass-postfix.md) | rendering | frame:render, render:translucency |
| [Anchoring a world-space quad to a part or to the camera](features/anchor-quad-to-part-or-camera.md) | rendering | frame:render, frame:draw-ui |
| [Click-to-focus and input routing for world-space panels](features/ray-pick-focus-for-world-quads.md) | ui | frame:draw-ui, frame:input |
| [Safe Vulkan teardown and live resize for mod-owned GPU resources](features/vulkan-resource-teardown-discipline.md) | rendering | frame:draw-ui, frame:render |

## Build, test and tooling

- `dotnet build purrtty.slnx`; `dotnet build purrTTY.GameMod` also deploys to the KSA mods folder
  (override with `PURRTTY_DIST_DIR`). Game assemblies come from `KSA_DLL_DIR`, a sibling
  `ksa-game-assemblies` checkout, or per-OS defaults, with a fail-fast check.[^build-props][^build-and-test]
- .NET 10, C# 13, nullable, warnings as errors (relaxed for the vendored binding).
- `dotnet test purrtty.slnx` runs four NUnit projects (FsCheck for the contract); rules: silent on
  success, no fixed sleeps. CI tests on Ubuntu, Windows and macOS, then stamps `mod.toml` and
  publishes `purrTTY-<version>.zip` (tip prereleases from `main`, versioned releases from `release/<v>`).
- Native libraries are cross-compiled with zig 0.15 from a pinned ghostty commit; see the vendored README.
- The repo carries Claude Code skills (`ksa`, `imgui`, `harmony`, `tomlyn`, `bun`, `upgrade-ksa`) used to
  develop and maintain the mod.[^ksa-skill][^upgrade-skill]

## Compatibility notes

- The latest commit at the pinned ref is "upgrade(ksa): for version 2026.9.22.5482". This bundle was
  checked against build 2026.10.10.5554: every Harmony target exists with compatible parameters.[^ksa-version]
- Known breakage history recorded in the code: KSA 2026.8.5.5168 (rev 5154) replaced classic render
  passes with dynamic rendering and deleted `KSA.RenderTarget`/`Framebuffer`/`OffscreenTarget`
  (the in-world pipeline and render target were rebuilt); KSA 5402 reworked viewports
  (`IGameViewport`, `IGameViewportLifecycle.SetMenuBarInUse`, removal of `Cursor.InputRay`); KSA 5482
  added mouse-button bindings through `DispatchKeyEvent` (hence `Patch04`) and moved bloom after the
  translucency pass; `ConsoleWindow.Print` moved from `(string, uint, int, ConsoleLineType)` to
  `(ReadOnlySpan<char>, ImColor8, int)`.
- In 5554, `RenderTranslucencyPass` lists `(IViewport, CommandBuffer, bool)`; purrTTY's postfix uses a
  different parameter order, which Harmony tolerates because it binds by name.
- Typical failure pattern: a drifted optional patch removes one feature (menu fallback, console
  capture, in-world quads) with a logged error; a drifted required patch lets typing leak into game
  controls. See [the upgrade skill](features/ksa-upgrade-coupling-review-skill.md).
- StarMap at the pinned ref (4dc60f8): attribute methods are registered only when
  `IsValidSignature` passes (`RuntimeMod` -> `ModRegistry.AddModMethod`), which is why
  `OnImmediateLoad()` is skipped; and `ExportedAssemblies` is parsed from `mod.toml` but never copied
  into `RuntimeMod.ExportedAssemblies`, so contract sharing works through gatOS's
  `ImportedAssemblies` alone (see [the contract feature](features/custom-shell-contract-via-assembly-imports.md)).[^starmap-runtime-mod][^starmap-registry]
  It also never reloads a mod in-process, and `StarMapCore.DeInit` removes every Harmony patch
  before calling `[StarMapUnload]`, so purrTTY's unpatch and static resets in `Unload` are defensive
  (see [failure-isolated patching](features/failure-isolated-harmony-patching.md)).[^starmap-core]
- Platforms: win-x64, linux-x64, osx-arm64 (the native library set).

## Secondary sources

| File | What it is |
|---|---|
| [docs/gotchas.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/gotchas.md) | 37 numbered behaviors and pitfalls; the best single read[^gotchas] |
| [docs/code-navigation.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/code-navigation.md) | File-by-file map of every layer[^code-navigation] |
| [docs/how-to.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/how-to.md) | Recipes: themes, binding extensions, custom shells, in-world terminals, layouts[^how-to] |
| [docs/build-and-test.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/build-and-test.md) | Paths, CI, test rules, native rebuild |
| [docs/BRUTAL_IMGUI_NOTES.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/BRUTAL_IMGUI_NOTES.md) | Font loading and `PushFont` notes for Brutal ImGui[^brutal-notes] |
| [docs/KSA_IMGUI_NOTES_LINKS.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/KSA_IMGUI_NOTES_LINKS.md) | Links to community ImGui examples[^imgui-links] |
| [docs/KSA_CAMERA_CODE_ANALYSIS.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/KSA_CAMERA_CODE_ANALYSIS.md) | General KSA camera API analysis (camera control is not a purrTTY feature at this ref)[^camera-analysis] |
| [.claude/skills/upgrade-ksa/SKILL.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/.claude/skills/upgrade-ksa/SKILL.md) | Coupling catalog for KSA updates |
| [vendor/Ghostty.Vt/README.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/README.md) | Binding provenance and native build |
| [plans/TERM_MANAGER_PLAN.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/plans/TERM_MANAGER_PLAN.md) | Layouts design (implemented)[^term-manager-plan] |
| [plans/GAME_SPACE_QUAD_PLAN.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/plans/GAME_SPACE_QUAD_PLAN.md) | In-world quad design; its status line says "not implemented" but the code implements it[^quad-plan] |

Not implemented at this ref, documented only as plans: a direct-Vulkan glyph renderer
(`LIBGHOSTTY_VULKAN_PLAN.md`), terminal audio through FMOD (`AUDIO_PLAN.md`), sixel graphics
(`SIXEL_PLAN.md`), kitty virtual placements and animation (`KITTY_PLAN.md` phases 2-3), and the
purrOS VM (`OS_PLAN.md`, which became the separate gatOS mod).[^audio-plan][^sixel-plan][^kitty-plan][^os-plan]
An earlier socket and escape-sequence RPC to drive the game from shells was removed from the code;
`docs/RPC_TERM_SEQUENCES.md` and `userland/` are leftovers, and `docs/FEATURE_TRACKING.md` describes
the retired built-in emulator.[^rpc-sequences][^rpc-client-readme][^feature-tracking]

[^readme]: purrTTY README.md
[^claude-md]: purrTTY CLAUDE.md
[^mod-toml]: purrTTY.GameMod/mod.toml
[^gamemod-csproj]: purrTTY.GameMod/purrTTY.GameMod.csproj
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^starmap-library-patches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-program-patcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-loader]: StarMap.Core/ModRepository/ModLoader.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^starmap-api-attrs]: StarMap.API/BaseAttributes.cs
[^starmap-runtime-mod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-registry]: StarMap.Core/ModRepository/ModRegistry.cs
[^patcher]: purrTTY.GameMod/Patcher.cs
[^vt-readme]: vendor/Ghostty.Vt/README.md
[^gatos-mod-toml]: gatOS.GameMod/mod.toml
[^vulkan-plan]: purrTTY plans/LIBGHOSTTY_VULKAN_PLAN.md
[^build-props]: Directory.Build.props
[^build-and-test]: purrTTY docs/build-and-test.md
[^ksa-skill]: purrTTY .claude/skills/ksa/SKILL.md
[^upgrade-skill]: purrTTY .claude/skills/upgrade-ksa/SKILL.md
[^ksa-version]: ksa-game-assemblies current/version.json (build 2026.10.10.5554)
[^gotchas]: purrTTY docs/gotchas.md
[^code-navigation]: purrTTY docs/code-navigation.md
[^how-to]: purrTTY docs/how-to.md
[^brutal-notes]: purrTTY docs/BRUTAL_IMGUI_NOTES.md
[^imgui-links]: purrTTY docs/KSA_IMGUI_NOTES_LINKS.md
[^camera-analysis]: purrTTY docs/KSA_CAMERA_CODE_ANALYSIS.md
[^term-manager-plan]: purrTTY plans/TERM_MANAGER_PLAN.md
[^quad-plan]: purrTTY plans/GAME_SPACE_QUAD_PLAN.md
[^audio-plan]: purrTTY plans/AUDIO_PLAN.md
[^sixel-plan]: purrTTY plans/SIXEL_PLAN.md
[^kitty-plan]: purrTTY plans/KITTY_PLAN.md
[^os-plan]: purrTTY plans/OS_PLAN.md
[^rpc-sequences]: purrTTY docs/RPC_TERM_SEQUENCES.md
[^rpc-client-readme]: purrTTY userland/ksa-rpc-client/README.md
[^feature-tracking]: purrTTY docs/FEATURE_TRACKING.md
