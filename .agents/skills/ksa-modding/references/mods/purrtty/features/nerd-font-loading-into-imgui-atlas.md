---
type: Mod Feature
title: Bundled Nerd Fonts loaded into the ImGui atlas
description: Ship monospace Nerd Fonts with a renamed extension so KSA does not auto-load them, add them to the shared ImGui atlas at mod init, and size them per draw with PushFont
mod: purrtty
category: ui
techniques: [imgui-font, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.FontManager
  - Brutal.ImGuiApi.ImFontAtlasPtr
  - Brutal.ImGuiApi.ImFontPtr
  - Brutal.ImGuiApi.ImGui
  - KSA.ImGuiBackendVulkanImpl
  - KSA.Program
  - KSA.ModLibrary
tags: [imgui, fonts, nerd-fonts, atlas, monospace]
---

purrTTY ships 15 Nerd Font files (Hack, JetBrains Mono and Space Mono in four styles; Pro Font,
Proggy Clean and Shure Tech Mono regular only) so prompts, powerline glyphs and TUI icons render
in-game. They are loaded into the same ImGui font atlas the game uses and selected per window by
family name; each draw call passes its own pixel size, so one loaded font serves every size from
4 to 72 px.

The trick that makes this a clean mod: the files are renamed from `.ttf` to `.iamttf`, so KSA's own
font scanner never picks them up, and purrTTY loads them explicitly.

## How it works

1. **Why rename.** At startup `FontManager.Initialize` collects every `*.ttf` under the game's
   `Content` folder recursively, and `RegenerateFonts` adds each one to the atlas and to the
   `FontManager.Fonts` dictionary (the game's font picker list) at the game font size.[^ksa-fontmanager]
   Mods can live under `Content/` as well as the Documents mods folder,[^ksa-modlibrary] so a mod
   shipping `.ttf` files there would have them auto-loaded. Renaming to `.iamttf` opts out
   (`docs/BRUTAL_IMGUI_NOTES.md`).[^brutal-notes]
2. **Deploy.** The csproj copies `TerminalFonts/**` beside the mod DLL on build.[^gamemod-csproj]
3. **Load once at init.** `TerminalMod.InitializeTerminal` (called from `[StarMapAllModsLoaded]`)
   calls `PurrTTYFontManager.LoadFonts()`. It finds `TerminalFonts/*.iamttf` next to
   `Assembly.GetExecutingAssembly().Location`, calls
   `ImGui.GetIO().Fonts.AddFontFromFileTTF(path, 32f)` for each, keys the `ImFontPtr` by file name,
   and also inserts it into `KSA.FontManager.Fonts` so other code can look it up by name.[^font-manager]
4. **Family registry.** A static registry maps display names ("Hack", "Jet Brains Mono", ...) to a
   base file name and the variants that exist; missing Bold/Italic/BoldItalic fall back to Regular
   (`CreateFontConfigForFamily`).
5. **Per-draw sizing.** Windows never load a font at a specific size. They resolve four
   `ImFontPtr`s (`FrameFonts`) and pass `fontSize` to `ImDrawListPtr.AddText(font, size, ...)` and to
   `ImGui.PushFont(font, size)` when measuring. This works because KSA's ImGui Vulkan backend
   declares `ImGuiBackendFlags.RendererHasTextures` (the dynamic atlas: glyphs are rasterized on
   demand at any requested size and atlas pages are uploaded by the backend).[^ksa-imgui-vulkan]
6. **Metric checks.** On a family or size change, `TerminalWindow.ResolveFontsCached` measures the
   cell from `CalcTextSize("M")` and checks each variant's printable-ASCII advance before allowing
   batched text runs (see [grid renderer](./terminal-grid-drawlist-renderer.md)). The cache also
   re-resolves when the loaded-font count changes.

## Lifecycle and timing

| Step | Hook | Why there |
|---|---|---|
| `LoadFonts` | `startup:all-mods-loaded` via `[StarMapAllModsLoaded]` -> `OnFullyLoaded` -> `InitializeTerminal` | The ImGui context and atlas already exist: `Program` calls `ImGui.CreateContext()` and `FontManager.Initialize` before `ModLibrary.PrepareAll` / `LoadAll`.[^ksa-program] |
| Resolve, measure, draw | `frame:draw-ui` via `[StarMapAfterGui]` | `PushFont`/`CalcTextSize` need an active ImGui frame; draw lists are being built |

Loading is idempotent (`s_fontsLoaded`), main-thread only, and never repeated.

## Key code

| Path | Role |
|---|---|
| [purrTTY.Display/Rendering/PurrTTYFontManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Rendering/PurrTTYFontManager.cs#L34-L88) | `.iamttf` discovery and `AddFontFromFileTTF` |
| [purrTTY.Display/Rendering/PurrTTYFontManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Rendering/PurrTTYFontManager.cs#L90-L159) | Family registry and variant fallback |
| [purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs#L20-L97) | Per-window resolution, cell metrics, monospace validation |
| [purrTTY.Display/Ghostty/FrameFonts.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/FrameFonts.cs#L57-L122) | The four variants and per-variant batching flags |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L263-L281) | Calls `LoadFonts` during init |
| [purrTTY.GameMod/purrTTY.GameMod.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/purrTTY.GameMod.csproj#L65-L70) | Copies `TerminalFonts/` into the deployed mod folder |
| [purrTTY.Display/Configuration/FontFamilyDefinition.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/FontFamilyDefinition.cs) | Family definition POCO |
| [purrTTY.Display/Controllers/LayoutConstants.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Controllers/LayoutConstants.cs) | Font size clamp (4 to 72) |

## Snippets

```csharp
// purrTTY.Display/Rendering/PurrTTYFontManager.cs L43-L78
            string? dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(dllDir))
            {
                return;
            }

            string fontsDir = Path.Combine(dllDir, "TerminalFonts");
            if (!Directory.Exists(fontsDir))
            {
                ModLog.Log.Error($"PurrTTYFontManager: fonts directory not found at: {fontsDir}");
                return;
            }

            string[] fontFiles = Directory.GetFiles(fontsDir, "*.iamttf");
            if (fontFiles.Length == 0)
            {
                ModLog.Log.Error($"PurrTTYFontManager: no font files found in {fontsDir}");
                return;
            }

            ImFontAtlasPtr atlas = ImGui.GetIO().Fonts;
            foreach (string fontPath in fontFiles)
            {
                string fontName = Path.GetFileNameWithoutExtension(fontPath);
                ImFontPtr font = atlas.AddFontFromFileTTF(new ImString(fontPath), 32.0f);
                s_loadedFonts[fontName] = font;

                try
                {
                    FontManager.Fonts[fontName] = font;
                }
                catch (Exception ex)
                {
                    ModLog.Log.Error($"PurrTTYFontManager: could not add font '{fontName}' to FontManager: {ex.Message}");
                }
            }
```

What KSA does with `.ttf` files under `Content/` (the reason for the rename):

```csharp
// current/decomp/KSA/FontManager.cs L27-L32
	public static void Initialize(Device device)
	{
		_device = device;
		_fontFiles = Directory.GetFiles(Path.Combine("Content"), "*.ttf", SearchOption.AllDirectories);
		RegenerateFonts();
	}
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `FontManager.Initialize`, `FontManager.RegenerateFonts`, `FontManager.Fonts` | [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L9-L98) | The game's font scanner and name-to-font dictionary that purrTTY avoids and then joins |
| `ImFontAtlasPtr.AddFontFromFileTTF(ImString, float, ...)` | [current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs#L18-L24) | Adds a font file to the shared atlas[^ksa-atlas-ext] |
| `ImGui.PushFont(ImFontPtr, float)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872-L5875) | Per-call font size (ImGui 1.92 style)[^ksa-imgui] |
| `ImGuiBackendVulkanImpl` ctor (`RendererHasTextures`) | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L642-L660) | Dynamic atlas support that makes late font adds and arbitrary sizes work |
| `Program` init order (`ImGui.CreateContext`, `FontManager.Initialize`, `ModLibrary.LoadAll`) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L942) | Confirms the atlas exists when `StarMapAllModsLoaded` fires |

## Pitfalls and version notes

- **Game font changes clear the atlas.** Changing the game's font settings calls
  `Program.ScheduleFontRebuild`; the next `PrepareFrame` runs `FontManager.RegenerateFonts`, which
  starts with `ImGui.GetIO().Fonts.Clear()` and `Fonts.Clear()`.[^ksa-program][^ksa-fontmanager]
  purrTTY loads its fonts once and keeps the old `ImFontPtr` values in `s_loadedFonts`, with no hook
  to reload them. From the code, terminals would then hold pointers to fonts the atlas no longer
  owns (runtime effect not verified in game). A robust mod would postfix `FontManager.RegenerateFonts`
  and reload.
- **The `.otf` is never loaded.** `TerminalFonts/` also ships `DepartureMonoNerdFont-Regular.otf`
  and the registry lists "Departure Mono", but `LoadFonts` only globs `*.iamttf`, so that family
  resolves to no font and `ResolveFonts` falls back to `ImGui.GetFont()`.[^window-fonts]
- Load at a generous base size (purrTTY uses 32 px) and size per draw; do not add one font per size.
- Do not call `PushFont` outside an ImGui frame; measurement lives in the render path for that reason.
- Nerd Fonts ship no braille glyphs; purrTTY draws braille as dots instead of relying on the font.

## See also

- [Terminal grid drawn with ImGui draw lists](./terminal-grid-drawlist-renderer.md)
- [TOML themes and settings](./toml-themes-and-settings.md) (font family and size persisted per theme)
- [Named terminal registry and theme dialog](./named-terminal-registry-and-theme-dialog.md)
- [Off-screen ImGui context render-to-texture](./offscreen-imgui-context-render-to-texture.md) (shares this atlas)
- [purrTTY overview](../overview.md)
- [Add a font family at StarMapAllModsLoaded under a renamed extension so KSA does not auto-load it](../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-3.md) - how-to article demonstrating this feature
- [Keep mod fonts alive when the game rebuilds its font atlas](../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-4.md) - how-to article demonstrating this feature

[^ksa-fontmanager]: KSA.FontManager (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^brutal-notes]: purrTTY docs/BRUTAL_IMGUI_NOTES.md
[^gamemod-csproj]: purrTTY.GameMod/purrTTY.GameMod.csproj
[^font-manager]: purrTTY.Display/Rendering/PurrTTYFontManager.cs
[^ksa-imgui-vulkan]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-atlas-ext]: Brutal.ImGuiApi.ImFontAtlasExtensions (decompiled, build 2026.10.10.5554)
[^ksa-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^window-fonts]: purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs
