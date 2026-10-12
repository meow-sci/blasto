---
type: Mod Feature
title: Custom TTF font in the ImGui atlas
description: Adds a bundled TTF from the mod folder straight to ImGui's font atlas at StarMapImmediateLoad, then draws at several sizes from that single font with ImGui 1.92's PushFont(font, size), bypassing KSA's FontManager
mod: ksa-aircrafthud
category: ui
techniques: [imgui-font, starmap-lifecycle]
hooks: [starmap StarMapImmediateLoad, harmony-postfix KSA.Program.OnFrameEditor]
lifecycle: [startup:immediate-load, frame:viewports]
game_types: [Brutal.ImGuiApi.ImFontAtlasExtensions, Brutal.ImGuiApi.ImGui, KSA.FontManager, KSA.Mod, KSA.Program]
tags: [imgui, font, ttf, font-atlas, pushfont, hud]
---

The HUD text uses Hornet Display Bold, a stencil-like display face shipped in the mod folder under
its MIT license. The mod loads it once into ImGui's shared font atlas and keeps the returned
`ImFontPtr`; per frame it pushes that font at a large size for the speed and altitude boxes and a
medium size for everything else.[^ah-main] [^ah-font] [^ah-fontlicense]

## How it works

1. **Locate.** The font path is built from the mod DLL's own folder:
   `Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)` + `Fonts/HornetDisplay-Bold.ttf`
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L677)). The csproj copies the TTF to the output
   folder.
2. **Size.** Three sizes are derived from screen height and the INI text scale:
   `round(20|26|32 * textScale * viewportHeight / 1440)`
   ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L688-L692)).
3. **Load.** `ImGui.GetIO()->Fonts.AddFontFromFileTTF(new ImString(path), fontSizeLrg)`, guarded by
   `File.Exists` and a `fontLoaded` flag ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L701-L713)).
   The Brutal binding forwards to `ImFontAtlas_AddFontFromFileTTF` with an optional config and glyph
   ranges ([current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs#L18-L27)).[^imgui-fontatlasext]
4. **Use at any size.** `ImGui.PushFont(hudFont, fontSizeLrg)` for the boxes, then
   `PopFont(); PushFont(hudFont, fontSizeMed)` for tapes and corner text, and one `PopFont()` at the
   end ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1090-L1108)). The two-argument overload is ImGui
   1.92's dynamic-size `PushFont(ImFont*, float font_size_base_unscaled)`
   ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872)), so
   one atlas entry serves every size and the "Text Scale" slider takes effect immediately without
   reloading the font.[^imgui-imgui]

Why not KSA's own font support: `FontManager.Initialize` collects `*.ttf` only from the game's
`Content` folder, and `RegenerateFonts` loads those into the atlas
([current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L27-L32)). A mod's
`mod.toml` `fonts` list only reaches `FontManager.RegisterFont`, which adds the name to the font
choice list in settings ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L287-L311),
[current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L34-L39)).[^ksa-fontmanager]
[^ksa-mod] A mod in the user mods folder that wants a private font therefore adds it to the atlas
itself.

## Lifecycle and timing

- **`startup:immediate-load`.** The `Program` constructor creates the ImGui context, initializes the
  backend and runs `FontManager.Initialize` before `ModLibrary.PrepareAll`, which triggers
  `[StarMapImmediateLoad]` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842-L899)).
  The atlas already holds the game fonts and accepts additions.[^ksa-program]
- **`frame:viewports`.** `PushFont`/`PopFont` happen inside the HUD window in the
  `Program.OnFrameEditor` postfix, between `ImGui.NewFrame` and `ImGui.Render`.

## Key code

| Path | Role |
|---|---|
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L100-L106) | Font fields and default sizes |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L701-L713) | Atlas load |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L877-L882) | Size update from the settings slider |
| [AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1090-L1108) | Push and switch sizes |

## Snippets

```csharp
// AircraftHUDMain.cs L701-L713
            if (!fontLoaded)
            {
                ImGuiIO* io = ImGui.GetIO();
                ImFontAtlasPtr atlas = io->Fonts;

                string fontPathStr = Path.Combine(dllDir, fontFile);
                if (File.Exists(fontPathStr))
                {
                    ImString fontPath = new ImString(fontPathStr);
                    hudFont = atlas.AddFontFromFileTTF(fontPath, fontSizeLrg);
                    fontLoaded = true;
                }
            }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ImFontAtlasExtensions.AddFontFromFileTTF(ImFontAtlasPtr, ImString, float, ...)` | [current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImFontAtlasExtensions.cs#L18-L27) | Load the TTF |
| `ImGui.PushFont(ImFontPtr, float)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872) | Draw at a chosen size |
| `FontManager.RegenerateFonts()` | [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L55-L60) | Clears the atlas (see pitfalls) |
| `Program.ScheduleFontRebuild()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5273-L5276) | Triggers that clear on the next frame |

## Pitfalls and version notes

- **Font settings wipe it.** Changing font options in the game settings calls
  `Program.ScheduleFontRebuild()`; the next `PrepareFrame` runs `FontManager.RegenerateFonts()`,
  which starts with `ImGui.GetIO().Fonts.Clear()`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2228-L2233)). That
  frame then returns `Exit` before `OnFrameEditor`, so the HUD skips it; on the next frame the mod's
  `hudFont` points at a font the atlas no longer holds (Dear ImGui's `ImFontAtlas::Clear` destroys
  the fonts it owns; library behaviour) while `fontLoaded` stays true, and `PushFont` receives a
  dangling pointer. A robust mod re-adds its font after a rebuild, for example from a Harmony
  postfix on `FontManager.RegenerateFonts`, which is public and also runs once from
  `FontManager.Initialize` before any mod loads.
- **Unbalanced pop.** `DrawHud` ends with an unconditional `ImGui.PopFont()` even when the font was
  never loaded and nothing was pushed
  ([AircraftHUDMain.cs](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/AircraftHUDMain.cs#L1327)), which unbalances ImGui's font stack.
  Only call `PopFont` when `fontLoaded`.
- The atlas entry is created at `fontSizeLrg` from startup; with dynamic font sizing that is only
  the base size, so later size changes render correctly.

## See also

- [Fullscreen draw-list HUD](fullscreen-drawlist-hud.md)
- [INI settings file and settings window](ini-settings-and-settings-window.md)
- [Add a bundled TTF to the ImGui atlas at StarMapImmediateLoad and draw it at any size](../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-1.md) - how-to article demonstrating this feature
- [Keep mod fonts alive when the game rebuilds its font atlas](../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/load-a-custom-font-into-imgui-4.md) - how-to article demonstrating this feature

[^ah-main]: KSA_AircraftHUD/AircraftHUDMain.cs
[^ah-font]: KSA_AircraftHUD/Fonts/HornetDisplay-Bold.ttf
[^ah-fontlicense]: KSA_AircraftHUD/Fonts/HornetDisplay-license.txt
[^imgui-fontatlasext]: Brutal.ImGuiApi.ImFontAtlasExtensions (decompiled, build 2026.10.10.5554)
[^imgui-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-fontmanager]: KSA.FontManager (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
