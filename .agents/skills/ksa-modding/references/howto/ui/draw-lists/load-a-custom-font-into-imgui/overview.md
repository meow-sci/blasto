---
type: Game Feature
title: Load a custom TTF font into KSA's ImGui atlas and keep it across font rebuilds
description: How to add a bundled font to the shared ImGui atlas at the right moment, draw it at any size, avoid KSA auto-loading it, and survive FontManager.RegenerateFonts clearing the atlas
category: ui
group: draw-lists
topic: load-a-custom-font-into-imgui
demonstrated_by:
  - mods/ksa-aircrafthud/features/custom-hud-font
  - mods/compendium/features/custom-fonts-in-imgui
  - mods/ksa-public-telemetry/features/montserrat-fonts-in-imgui-atlas
  - mods/purrtty/features/nerd-font-loading-into-imgui-atlas
lifecycle: [startup:immediate-load, startup:all-mods-loaded, frame:draw-ui, frame:prepare]
game_types:
  - Brutal.ImGuiApi.ImFontAtlasPtr
  - Brutal.ImGuiApi.ImFontConfig
  - Brutal.ImGuiApi.ImGui
  - KSA.FontManager
  - KSA.Mod
  - KSA.ImGuiBackendVulkanImpl
  - KSA.GameSettings
  - KSA.Program
tags: [font, ttf, imgui, atlas, regeneratefonts, pushfont, dynamic-atlas]
---
KSA's UI draws with Dear ImGui 1.92 and one shared font atlas. A mod that wants its own typeface (a stencil HUD face, a monospace Nerd Font, a broadcast sans) adds the TTF to that atlas itself, because KSA's own font loader looks only at the game's `Content` folder. Three decisions define a font-loading approach: when to add (the atlas must exist), how to keep the game from also loading the file, and what to do when the game rebuilds the atlas and your `ImFontPtr`s stop being valid. Four mods solve the first two; none solves the third, which is why approach 4 is a labelled, untested guard. Everything below was checked against build 2026.10.10.5554.

## Engine background

| Piece | File | What it does |
|---|---|---|
| `FontManager.Initialize(device)` | [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L27-L32)[^ksa-fontmanager] | Scans `Content/**/*.ttf` recursively, then calls `RegenerateFonts()` |
| `FontManager.RegenerateFonts()` | [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L55-L98)[^ksa-fontmanager] | `ImGui.GetIO().Fonts.Clear()`, `Fonts.Clear()`, reloads every scanned TTF plus the console font, sets `SmallFont` and the tooltip font |
| `FontManager.LoadFont` | [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L100-L114)[^ksa-fontmanager] | The game's own `ImFontConfig`: `OversampleH = 3`, `OversampleV = 2`, `GlyphMaxAdvanceX = float.MaxValue`, `RasterizerMultiply = 1`, density and pixel snap from settings |
| `FontManager.RegisterFont(name)` | [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L34-L39)[^ksa-fontmanager] | Adds a name to the interface-font combo (`FontFiles`) only; called for each `mod.toml` `fonts` entry by `Mod.LoadFonts` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L287-L311)[^ksa-mod]) |
| Program constructor | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L900)[^ksa-program] | `ImGui.CreateContext()`, `ImGuiBackend.Initialize`, `FontManager.Initialize`, then `ModLibrary.PrepareAll()` (which triggers `[StarMapImmediateLoad]`) |
| `Program.ScheduleFontRebuild` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5273-L5276)[^ksa-program] | Sets `_rebuildFontNeeded`; `PrepareFrame` consumes it |
| `PrepareFrame` font branch | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2228-L2233)[^ksa-program] | `FontManager.RegenerateFonts()`, `Device.WaitIdle()`, then `return OnFrameResult.Exit` (that frame draws no UI) |
| `GameSettings` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1405-L1414)[^ksa-gamesettings], L2362-L2373 | Calls `ScheduleFontRebuild` when interface font settings change (`RequireFontRegeneration()`) |
| `ImGuiBackendVulkanImpl` | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L656-L658)[^ksa-imguibackendvulkanimpl] | Sets `RendererHasTextures`: the dynamic atlas, glyphs rasterized on demand at the size `PushFont` asks for |
| `ImGui.PushFont(font, size)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872-L5875)[^brutal-imguiapi-imgui] | ImGui 1.92 `PushFont(ImFont*, float fontSizeBaseUnscaled)`: one atlas entry serves every size |
| `ConsoleStyle.RefreshFontCache` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L462-L470)[^ksa-consolestyle] | The game's own answer to the hazard: re-resolve fonts from `FontManager.Fonts` once per ImGui frame instead of caching them |

**The three facts that matter.**

1. By the time any mod code runs, the atlas exists and already holds the game fonts (the constructor created the context and called `FontManager.Initialize` before `PrepareAll`), so adding a font at `startup:immediate-load`, `startup:all-mods-loaded` or the first UI frame all work.
2. Anything with a `.ttf` extension under `Content/` is loaded by the game and listed in its font picker; the manifest `fonts` list only registers a name.
3. A change to interface font settings calls `Program.ScheduleFontRebuild`; the next `PrepareFrame` runs `RegenerateFonts`, which clears the whole atlas, so every `ImFontPtr` a mod cached is no longer owned by the atlas. This is Dear ImGui's `ImFontAtlas::Clear` behaviour, not something the decomp shows; none of the four reference mods was tested against it.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [AddFontFromFileTTF at StarMapImmediateLoad](load-a-custom-font-into-imgui-1.md) | KSA_AircraftHUD (and Compendium) | Add once at load, `PushFont(font, size)` per use | `startup:immediate-load`, `frame:draw-ui` | Simplest; font lost on rebuild |
| 2 | [Add lazily on the first UI frame using FontManager's config](load-a-custom-font-into-imgui-2.md) | KSA-Public-Telemetry | Same `ImFontConfig` fields as the game, tabular digits, fallback | `frame:draw-ui` | Only loads if it will draw; atlas change mid-frame |
| 3 | [Add at StarMapAllModsLoaded with a renamed extension](load-a-custom-font-into-imgui-3.md) | purrTTY | `.iamttf` files avoid the `Content` scan, insert into `FontManager.Fonts` | `startup:all-mods-loaded`, `frame:draw-ui` | Many families; manual registry; font lost on rebuild |
| 4 | [Keep mod fonts alive when the game rebuilds its atlas](load-a-custom-font-into-imgui-4.md) | All four (hazard); guard adapted and untested | Look the font up in `FontManager.Fonts` each frame and re-add when missing | `frame:prepare`, `frame:draw-ui` | Not demonstrated by any mod; relies on `RegenerateFonts` clearing `Fonts` |

## Choosing

- One font, load once, accept the rebuild hazard: approach 1.
- Fonts that must not exist unless the overlay is actually drawn: approach 2.
- A whole family you do not want in the game's picker: approach 3.
- Whichever you pick, add approach 4's guard, because any player who touches interface font settings triggers the rebuild.

## See also

- [Draw per-frame text efficiently: monospace grids and allocation-free labels](../../../../howto/ui/draw-lists/draw-a-monospace-text-grid/overview.md)
- [Draw HUD instruments: arc gauges, gradients, scrolling tapes and animated intros](../../../../howto/ui/overlays/draw-hud-instruments-with-the-draw-list/overview.md)
- [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../../howto/ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)
- [Draw per-frame text efficiently: monospace grids and allocation-free labels](../draw-a-monospace-text-grid/overview.md) - related topic

[^ksa-fontmanager]: KSA.FontManager (decompiled, build 2026.10.10.5554)
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendvulkanimpl]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-consolestyle]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
